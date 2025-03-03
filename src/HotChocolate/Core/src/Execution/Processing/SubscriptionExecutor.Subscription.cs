using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using HotChocolate.Execution.Instrumentation;
using HotChocolate.Execution.Processing.Plan;
using HotChocolate.Fetching;
using HotChocolate.Resolvers;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.ObjectPool;

namespace HotChocolate.Execution.Processing;

internal sealed partial class SubscriptionExecutor
{
    private sealed class Subscription : ISubscription, IAsyncDisposable
    {
        private readonly ulong _id;
        private readonly ObjectPool<OperationContext> _operationContextPool;
        private readonly QueryExecutor _queryExecutor;
        private readonly IExecutionDiagnosticEvents _diagnosticEvents;
        private IDisposable? _subscriptionScope;
        private readonly IRequestContext _requestContext;
        private readonly QueryPlan _queryPlan;
        private readonly ObjectType _subscriptionType;
        private readonly ISelectionSet _rootSelections;
        private readonly Func<object?> _resolveQueryRootValue;
        private ISourceStream _sourceStream = default!;
        private object? _cachedRootValue;
        private IImmutableDictionary<string, object?> _scopedContextData =
            ImmutableDictionary<string, object?>.Empty;
        private bool _disposed;

        private Subscription(
            ObjectPool<OperationContext> operationContextPool,
            QueryExecutor queryExecutor,
            IRequestContext requestContext,
            QueryPlan queryPlan,
            ObjectType subscriptionType,
            ISelectionSet rootSelections,
            Func<object?> resolveQueryRootValue,
            IExecutionDiagnosticEvents diagnosticEvents)
        {
            unchecked
            {
                _id++;
            }

            _operationContextPool = operationContextPool;
            _queryExecutor = queryExecutor;
            _requestContext = requestContext;
            _queryPlan = queryPlan;
            _subscriptionType = subscriptionType;
            _rootSelections = rootSelections;
            _resolveQueryRootValue = resolveQueryRootValue;
            _diagnosticEvents = diagnosticEvents;
        }

        /// <summary>
        /// Subscribes to the pub/sub-system and creates a new <see cref="Subscription"/>
        /// instance representing that subscriptions.
        /// </summary>
        /// <param name="operationContextPool">
        /// The operation context pool to rent context pools for execution.
        /// </param>
        /// <param name="queryExecutor">
        /// The query executor to process event payloads.
        /// </param>
        /// <param name="requestContext">
        /// The original request context.
        /// </param>
        /// <param name="queryPlan">
        /// The subscription query plan.
        /// </param>
        /// <param name="subscriptionType">
        /// The object type that represents the subscription.
        /// </param>
        /// <param name="rootSelections">
        /// The operation selection set.
        /// </param>
        /// <param name="resolveQueryRootValue">
        /// A delegate to resolve the subscription instance.
        /// </param>
        /// <param name="executionDiagnosticsEvents">
        /// The internal diagnostic events to report telemetry.
        /// </param>
        /// <returns>
        /// Returns a new subscription instance.
        /// </returns>
        public static async ValueTask<Subscription> SubscribeAsync(
            ObjectPool<OperationContext> operationContextPool,
            QueryExecutor queryExecutor,
            IRequestContext requestContext,
            QueryPlan queryPlan,
            ObjectType subscriptionType,
            ISelectionSet rootSelections,
            Func<object?> resolveQueryRootValue,
            IExecutionDiagnosticEvents executionDiagnosticsEvents)
        {
            var subscription = new Subscription(
                operationContextPool,
                queryExecutor,
                requestContext,
                queryPlan,
                subscriptionType,
                rootSelections,
                resolveQueryRootValue,
                executionDiagnosticsEvents);

            subscription._subscriptionScope =
                executionDiagnosticsEvents.ExecuteSubscription(subscription);

            subscription._sourceStream =
                await subscription.SubscribeAsync().ConfigureAwait(false);

            return subscription;
        }

        public IAsyncEnumerable<IQueryResult> ExecuteAsync()
            => new SubscriptionEnumerable(
                _sourceStream,
                OnEvent,
                this,
                _diagnosticEvents);

        /// <inheritdoc />
        public ulong Id => _id;

        /// <inheritdoc />
        public IPreparedOperation Operation => _requestContext.Operation!;

        public async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                await _sourceStream.DisposeAsync().ConfigureAwait(false);
                _subscriptionScope?.Dispose();
                _disposed = true;
            }
        }

        /// <summary>
        /// OnEvent is called whenever the event stream yields a payload and triggers an
        /// execution of the subscription query.
        /// </summary>
        /// <param name="payload">
        /// The event stream payload.
        /// </param>
        /// <returns>
        /// Returns a query result which will be enqueued to the response stream.
        /// </returns>
        private async Task<IQueryResult> OnEvent(object payload)
        {
            using IDisposable es = _diagnosticEvents.OnSubscriptionEvent(new(this, payload));
            using IServiceScope serviceScope = _requestContext.Services.CreateScope();

            OperationContext operationContext = _operationContextPool.Get();

            try
            {
                IServiceProvider? eventServices = serviceScope.ServiceProvider;
                IBatchDispatcher? dispatcher = eventServices.GetRequiredService<IBatchDispatcher>();

                // we store the event payload on the scoped context so that it is accessible
                // in the resolvers.
                IImmutableDictionary<string, object?> scopedContext =
                    _scopedContextData.SetItem(WellKnownContextData.EventMessage, payload);

                // next we resolve the subscription instance.
                var rootValue = RootValueResolver.Resolve(
                    _requestContext,
                    eventServices,
                    _subscriptionType,
                    ref _cachedRootValue);

                // last we initialize a standard operation context to execute
                // the subscription query with the standard query executor.
                operationContext.Initialize(
                    _requestContext,
                    eventServices,
                    dispatcher,
                    _requestContext.Operation!,
                    _queryPlan,
                    _requestContext.Variables!,
                    rootValue,
                    _resolveQueryRootValue);

                operationContext.Result.SetContextData(
                    WellKnownContextData.EventMessage,
                    payload);

                IQueryResult result = await _queryExecutor
                    .ExecuteAsync(operationContext, scopedContext)
                    .ConfigureAwait(false);

                _diagnosticEvents.SubscriptionEventResult(new(this, payload), result);

                return result;
            }
            catch (Exception ex)
            {
                _diagnosticEvents.SubscriptionEventError(
                    new SubscriptionEventContext(this, payload),
                    ex);
                throw;
            }
            finally
            {
                _operationContextPool.Return(operationContext);
            }
        }

        // subscribe will use the subscribe resolver to create a source stream that yields
        // the event messages from the underlying pub/sub-system.
        private async ValueTask<ISourceStream> SubscribeAsync()
        {
            OperationContext operationContext = _operationContextPool.Get();

            try
            {
                // first we will create the root value which essentially
                // is the subscription object. In some cases this object is null.
                var rootValue = RootValueResolver.Resolve(
                    _requestContext,
                    _requestContext.Services,
                    _subscriptionType,
                    ref _cachedRootValue);

                // next we need to initialize our operation context so that we have access to
                // variables services and other things.
                // The subscribe resolver will use a noop dispatcher and all DataLoader are
                // dispatched immediately.
                operationContext.Initialize(
                    _requestContext,
                    _requestContext.Services,
                    NoopBatchDispatcher.Default,
                    _requestContext.Operation!,
                    _queryPlan,
                    _requestContext.Variables!,
                    rootValue,
                    _resolveQueryRootValue);

                // next we need a result map so that we can store the subscribe temporarily
                // while executing the subscribe pipeline.
                ResultMap resultMap = operationContext.Result.RentResultMap(1);
                ISelection rootSelection = _rootSelections.Selections[0];

                // we create a temporary middleware context so that we can use the standard
                // resolver pipeline.
                var middlewareContext = new MiddlewareContext();
                middlewareContext.Initialize(
                    operationContext,
                    rootSelection,
                    resultMap,
                    1,
                    rootValue,
                    Path.New(rootSelection.ResponseName),
                    _scopedContextData);

                // it is important that we correctly coerce the arguments before
                // invoking subscribe.
                if (!rootSelection.Arguments.TryCoerceArguments(
                    middlewareContext,
                    out IReadOnlyDictionary<NameString, ArgumentValue>? coercedArgs))
                {
                    // the middleware context reports errors to the operation context,
                    // this means if we failed, we need to grab the coercion errors from there
                    // and just throw a GraphQLException.
                    throw new GraphQLException(operationContext.Result.Errors);
                }

                // if everything is fine with the arguments we still need to assign them.
                middlewareContext.Arguments = coercedArgs;

                // last but not least we can invoke the subscribe resolver which will subscribe
                // to the underlying pub/sub-system yielding the source stream.
                ISourceStream sourceStream =
                    await rootSelection.Field.SubscribeResolver!.Invoke(middlewareContext)
                        .ConfigureAwait(false);
                _scopedContextData = middlewareContext.ScopedContextData;

                if (operationContext.Result.Errors.Count > 0)
                {
                    // again if we have any errors we will just throw them and not opening
                    // any subscription context.
                    throw new GraphQLException(operationContext.Result.Errors);
                }

                return sourceStream;
            }
            catch
            {
                // if there is an error we will just dispose our instrumentation scope
                // the error is reported in the request level in this case.
                _subscriptionScope?.Dispose();
                _subscriptionScope = null;
                throw;
            }
            finally
            {
                operationContext.Result.DropResult();
                _operationContextPool.Return(operationContext);
            }
        }
    }

    private sealed class SubscriptionEnumerable : IAsyncEnumerable<IQueryResult>
    {
        private readonly ISourceStream _sourceStream;
        private readonly Func<object, Task<IQueryResult>> _onEvent;
        private readonly Subscription _subscription;
        private readonly IExecutionDiagnosticEvents _diagnosticEvents;

        public SubscriptionEnumerable(
            ISourceStream sourceStream,
            Func<object, Task<IQueryResult>> onEvent,
            Subscription subscription,
            IExecutionDiagnosticEvents diagnosticEvents)
        {
            _sourceStream = sourceStream;
            _onEvent = onEvent;
            _subscription = subscription;
            _diagnosticEvents = diagnosticEvents;
        }

        public IAsyncEnumerator<IQueryResult> GetAsyncEnumerator(
            CancellationToken cancellationToken = default)
        {
            try
            {
                IAsyncEnumerator<object> eventStreamEnumerator =
                    _sourceStream.ReadEventsAsync()
                        .GetAsyncEnumerator(cancellationToken);

                return new SubscriptionEnumerator(
                    eventStreamEnumerator,
                    _onEvent,
                    _subscription,
                    _diagnosticEvents,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _diagnosticEvents.SubscriptionEventError(_subscription, ex);
                return new ErrorSubscriptionEnumerator();
            }
        }
    }

    private sealed class SubscriptionEnumerator : IAsyncEnumerator<IQueryResult>
    {
        private readonly IAsyncEnumerator<object> _eventEnumerator;
        private readonly Func<object, Task<IQueryResult>> _onEvent;
        private readonly Subscription _subscription;
        private readonly IExecutionDiagnosticEvents _diagnosticEvents;
        private readonly CancellationToken _requestAborted;
        private bool _disposed;

        public SubscriptionEnumerator(
            IAsyncEnumerator<object> eventEnumerator,
            Func<object, Task<IQueryResult>> onEvent,
            Subscription subscription,
            IExecutionDiagnosticEvents diagnosticEvents,
            CancellationToken requestAborted)
        {
            _eventEnumerator = eventEnumerator;
            _onEvent = onEvent;
            _subscription = subscription;
            _diagnosticEvents = diagnosticEvents;
            _requestAborted = requestAborted;
        }

        public IQueryResult Current { get; private set; } = default!;

        public async ValueTask<bool> MoveNextAsync()
        {
            if (_requestAborted.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                if (await _eventEnumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    Current = await _onEvent(_eventEnumerator.Current).ConfigureAwait(false);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _diagnosticEvents.SubscriptionEventError(_subscription, ex);
                throw;
            }

            return false;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                await _eventEnumerator.DisposeAsync();
                _disposed = true;
            }
        }
    }

    private sealed class ErrorSubscriptionEnumerator : IAsyncEnumerator<IQueryResult>
    {
        public IQueryResult Current => default!;

        public ValueTask<bool> MoveNextAsync() => new(false);

        public ValueTask DisposeAsync() => default;
    }
}
