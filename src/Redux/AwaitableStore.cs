﻿namespace Redux
{
    using System;
    using System.Reactive;
    using System.Reactive.Disposables;
    using System.Reactive.Linq;
    using System.Reactive.Subjects;
    using System.Threading.Tasks;

    public delegate void Saga<TState, in TAction>(TAction action, IStore<TState> store);

    public delegate Task AsyncSaga<TState, in TAction>(TAction action, IStore<TState> store);

    public static class ObservableExtensions
    {
        public static IDisposable RunsSaga<TState, TAction>(
            this IObservable<TAction> source,
            IStore<TState> store,
            Saga<TState, TAction> saga)
        {
            return source.Subscribe(action => saga(action, store));
        }

        public static IDisposable RunsAsyncSaga<TState, TAction>(
            this IObservable<TAction> source,
            IStore<TState> store,
            AsyncSaga<TState, TAction> saga)
        {
            // Using SelectMany is the standard way of running async subscribers, otherwise they become
            // async void and exceptions are swallowed. E.g. http://stackoverflow.com/a/24844934/2978652,
            // http://stackoverflow.com/a/37412422/2978652, http://stackoverflow.com/a/23011084/2978652.
            // 
            // Note that this will not block the queue while the saga runs; a new action can trigger
            // the saga while the previous saga invocation still runs. This should be the expected
            // behavior since otherwise, the sagas would have no control over cancellation of existing
            // tasks when they are invoked again.
            return source.SelectMany(
                    async action =>
                    {
                        if (store is AwaitableStore<TState> s)
                        {
                            using (s.AsyncOperation())
                            {
                                await saga(action, store);
                            }
                        }
                        else
                        {
                            await saga(action, store);
                        }
                        return Unit.Default;
                    })
                .Subscribe();
        }
    }

    public interface IObservableActionStore
    {
        IObservable<object> Actions { get; }
    }

    public class ObservableActionStore<TState> : Store<TState>, IObservableActionStore
    {
        private readonly ISubject<object> actionsSubject = new Subject<object>();

        /// <inheritdoc />
        public ObservableActionStore(
            Reducer<TState> reducer,
            TState initialState = default(TState),
            params Middleware<TState>[] middlewares) : base(reducer, initialState, middlewares)
        {
        }

        public IObservable<object> Actions => this.actionsSubject;

        protected override object InnerDispatch(object action)
        {
            object ret = base.InnerDispatch(action);
            this.actionsSubject.OnNext(action);
            return ret;
        }
    }

    public interface IAwaitableStore<TState>
    {
        Task<object> DispatchAsync(object action);
    }

    public class AwaitableStore<TState> : ObservableActionStore<TState>, IAwaitableStore<TState>
    {
        private int numOperations;
        private readonly ISubject<int> numOperationsSubject = new BehaviorSubject<int>(0);

        /// <inheritdoc />
        public AwaitableStore(
            Reducer<TState> reducer,
            TState initialState = default(TState),
            params Middleware<TState>[] middlewares) : base(reducer, initialState, middlewares)
        {
        }

        private IObservable<int> OngoingOperations => this.numOperationsSubject;

        internal IDisposable AsyncOperation()
        {
            this.numOperationsSubject.OnNext(++this.numOperations);
            return Disposable.Create(() => this.numOperationsSubject.OnNext(--this.numOperations));
        }

        public async Task<object> DispatchAsync(object action)
        {
            object ret = this.Dispatch(action);
            await this.OngoingOperations.FirstAsync(i => i == 0);
            return Task.FromResult(ret);
        }
    }
}