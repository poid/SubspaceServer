using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// Runs arena work on a small pool of threads, so that game logic is not confined to the mainloop.
    /// </summary>
    /// <remarks>
    /// There is no ASSS equivalent of this. ASSS runs all game logic on its mainloop, as this server did
    /// until arenas were given somewhere else to run.
    /// <para>
    /// Each arena belongs to exactly one worker for its whole life, and each worker drains one queue on
    /// one thread, so everything queued for an arena runs in order and never concurrently with itself.
    /// That is what lets arena state keep a single writer after it stops being the mainloop.
    /// </para>
    /// </remarks>
    [CoreModuleInfo]
    public sealed class ArenaLoop : IModule, IArenaLoop, IDisposable
    {
        private readonly IComponentBroker _broker;
        private readonly IConfigManager _configManager;
        private readonly ILogManager _logManager;
        private readonly IObjectPoolManager _objectPoolManager;

        private InterfaceRegistrationToken<IArenaLoop>? _iArenaLoopToken;

        // Optional dependency. Retrieved in Load, which is after the module that provides it.
        private IServerMetrics? _serverMetrics;

        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private Worker[] _workers = [];

        /// <summary>
        /// Which worker owns each arena.
        /// </summary>
        /// <remarks>
        /// An entry is added when the arena is created and removed after it is destroyed, so an arena
        /// never changes worker while it is alive. Work for an arena that has no entry runs on the
        /// arena's own thread anyway, because the fallback is derived from the arena rather than from a
        /// counter, and is therefore just as stable.
        /// </remarks>
        private readonly ConcurrentDictionary<Arena, int> _arenaWorkers = new();

        /// <summary>
        /// Rotated through when assigning arenas to workers, so that arenas are spread evenly rather than
        /// by whatever a hash of their names happens to do.
        /// </summary>
        private int _nextWorker = -1;

        /// <summary>
        /// Which worker the running thread is, or -1 on any other thread.
        /// </summary>
        [ThreadStatic]
        private static int _currentWorker = -1;

        /// <summary>
        /// The most work items allowed to wait in one worker's queue at once, or 0 for no limit.
        /// </summary>
        private int _maxQueueDepth;

        public ArenaLoop(
            IComponentBroker broker,
            IConfigManager configManager,
            ILogManager logManager,
            IObjectPoolManager objectPoolManager)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
        }

        #region Module Members

        [ConfigHelp<int>("ArenaLoop", "Workers", ConfigScope.Global, Default = 0, Min = 0,
            Description = """
                How many threads to run arena work on, or 0 to pick a number from the processor count.
                Arenas are spread over the workers and each stays on the one it was given, so raising this
                only helps when there are more arenas than workers and they are actually busy.
                """)]
        [ConfigHelp<int>("ArenaLoop", "MaxQueueDepth", ConfigScope.Global, Default = 0, Min = 0,
            Description = """
                The most work items allowed to wait in one worker's queue at once, or 0 for no limit.
                When the limit is reached, work the caller has said it can lose is refused rather than
                queued. Refusals are counted and reported by ?metrics.
                """)]
        bool IModule.Load(IComponentBroker broker)
        {
            _serverMetrics = broker.GetInterface<IServerMetrics>();

            _maxQueueDepth = _configManager.GetInt(_configManager.Global, "ArenaLoop", "MaxQueueDepth", ConfigHelp.Constants.Global.ArenaLoop.MaxQueueDepth.Default);

            int workerCount = _configManager.GetInt(_configManager.Global, "ArenaLoop", "Workers", ConfigHelp.Constants.Global.ArenaLoop.Workers.Default);
            if (workerCount <= 0)
            {
                // Leave room for the mainloop, the receive thread, and the send threads, which are all
                // busy at the point this starts to matter. Two workers is the least that is worth having.
                // The upper bound is because these are dedicated threads rather than pool threads, and
                // past a point more of them only costs stacks: a zone that could keep more than sixteen
                // busy would be far past what any other part of the server can feed them.
                workerCount = Math.Clamp(Environment.ProcessorCount - 4, 2, 16);
            }

            _workers = new Worker[workerCount];

            for (int i = 0; i < workerCount; i++)
            {
                _workers[i] = new Worker(this, i);
            }

            foreach (Worker worker in _workers)
            {
                worker.Start();
            }

            ArenaActionCallback.Register(broker, Callback_ArenaAction);

            _iArenaLoopToken = broker.RegisterInterface<IArenaLoop>(this);

            _logManager.LogM(LogLevel.Info, nameof(ArenaLoop), $"Started {workerCount} arena worker threads.");

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iArenaLoopToken) != 0)
                return false;

            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);

            _cancellationTokenSource.Cancel();

            foreach (Worker worker in _workers)
            {
                worker.Stop();
            }

            _workers = [];
            _arenaWorkers.Clear();

            if (_serverMetrics is not null)
            {
                broker.ReleaseInterface(ref _serverMetrics);
            }

            return true;
        }

        #endregion

        #region Callbacks

        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (action == ArenaAction.PreCreate)
            {
                // Assign here rather than on first use, so that the assignment is made once, on the
                // mainloop, before anything can queue work for the arena.
                _arenaWorkers[arena] = (int)((uint)Interlocked.Increment(ref _nextWorker) % (uint)_workers.Length);
            }
            else if (action == ArenaAction.PostDestroy)
            {
                _arenaWorkers.TryRemove(arena, out _);
            }
        }

        #endregion

        #region IArenaLoop Members

        bool IArenaLoop.QueueArenaWorkItem<TState>(Arena arena, Action<TState> callback, TState state)
        {
            return QueueArenaWorkItem(arena, callback, state, sheddable: false);
        }

        bool IArenaLoop.QueueSheddableArenaWorkItem<TState>(Arena arena, Action<TState> callback, TState state)
        {
            return QueueArenaWorkItem(arena, callback, state, sheddable: true);
        }

        void IArenaLoop.WaitForArenaWorkItemDrain(Arena arena)
        {
            ArgumentNullException.ThrowIfNull(arena);

            Worker worker = GetWorker(arena);

            if (_currentWorker == worker.Index)
                throw new InvalidOperationException("Cannot wait for an arena's work to drain from the thread that runs it.");

            worker.Drain();
        }

        bool IArenaLoop.IsArenaThread(Arena arena)
        {
            ArgumentNullException.ThrowIfNull(arena);

            return _currentWorker >= 0 && _currentWorker == GetWorker(arena).Index;
        }

        int IArenaLoop.WorkerCount => _workers.Length;

        #endregion

        private bool QueueArenaWorkItem<TState>(Arena arena, Action<TState> callback, TState state, bool sheddable)
        {
            ArgumentNullException.ThrowIfNull(arena);
            ArgumentNullException.ThrowIfNull(callback);

            ArenaWorkItem<TState> workItem = _objectPoolManager.GetPool<ArenaWorkItem<TState>>().Get();
            workItem.Set(callback, state);

            return GetWorker(arena).TryQueue(workItem, sheddable);
        }

        /// <summary>
        /// Gets the worker that owns an arena.
        /// </summary>
        /// <remarks>
        /// The fallback for an arena with no assignment is derived from the arena itself rather than from
        /// the rotating counter, so that it is the same answer every time it is asked. An arena that gets
        /// work queued for it outside its life cycle would otherwise be able to land on two workers at
        /// once, which is the one thing this whole arrangement exists to prevent.
        /// </remarks>
        private Worker GetWorker(Arena arena)
        {
            if (!_arenaWorkers.TryGetValue(arena, out int index))
            {
                index = (int)((uint)arena.Name.GetHashCode(StringComparison.OrdinalIgnoreCase) % (uint)_workers.Length);
            }

            return _workers[index];
        }

        public void Dispose()
        {
            _cancellationTokenSource.Dispose();

            foreach (Worker worker in _workers)
            {
                worker.Dispose();
            }
        }

        #region Helper Types

        private interface IArenaWorkItem
        {
            void Process();
        }

        private sealed class ArenaWorkItem<TState> : PooledObject, IArenaWorkItem
        {
            private Action<TState>? _callback;
            private TState? _state;

            public void Set(Action<TState> callback, TState state)
            {
                _callback = callback ?? throw new ArgumentNullException(nameof(callback));
                _state = state;
            }

            public void Process()
            {
                _callback?.Invoke(_state!);
            }

            protected override void Dispose(bool isDisposing)
            {
                if (isDisposing)
                {
                    _callback = null;
                    _state = default;
                }

                base.Dispose(isDisposing);
            }
        }

        /// <summary>
        /// A work item that signals whoever queued it once it has been reached.
        /// </summary>
        /// <remarks>
        /// The queue is drained in order by one thread, so a barrier running means everything queued
        /// ahead of it has already run.
        /// </remarks>
        private sealed class BarrierWorkItem : IArenaWorkItem
        {
            private readonly ManualResetEventSlim _reached = new(false);

            public void Process()
            {
                _reached.Set();
            }

            public void Wait()
            {
                _reached.Wait();
            }
        }

        /// <summary>
        /// One thread and the queue it drains.
        /// </summary>
        private sealed class Worker(ArenaLoop owner, int index) : IDisposable
        {
            private readonly ArenaLoop _owner = owner;
            private readonly BlockingCollection<IArenaWorkItem> _queue = [];
            private Thread? _thread;

            public int Index { get; } = index;

            public void Start()
            {
                _thread = new Thread(Run) { Name = $"{nameof(ArenaLoop)}-{Index}", IsBackground = false };
                _thread.Start();
            }

            public void Stop()
            {
                _queue.CompleteAdding();
                _thread?.Join();
                _thread = null;
            }

            public bool TryQueue(IArenaWorkItem workItem, bool sheddable)
            {
                if (_queue.IsAddingCompleted)
                {
                    if (workItem is IDisposable disposable)
                        disposable.Dispose();

                    return false;
                }

                // Only work the caller has said it can lose is refused, for the same reason as on the
                // mainloop: dropping a life cycle step or an await continuation wedges state permanently,
                // which is worse than the latency it would save.
                if (sheddable)
                {
                    int maxQueueDepth = _owner._maxQueueDepth;
                    if (maxQueueDepth > 0 && _queue.Count >= maxQueueDepth)
                    {
                        _owner._serverMetrics?.Increment(ServerMetric.ArenaLoopWorkItemsDropped);

                        if (workItem is IDisposable disposable)
                            disposable.Dispose();

                        return false;
                    }
                }

                try
                {
                    if (_queue.TryAdd(workItem))
                        return true;
                }
                catch (InvalidOperationException)
                {
                    // The queue was marked complete for adding between the check above and here.
                }

                if (workItem is IDisposable toDispose)
                    toDispose.Dispose();

                return false;
            }

            public void Drain()
            {
                if (_queue.IsAddingCompleted)
                    return;

                BarrierWorkItem barrier = new();

                try
                {
                    if (!_queue.TryAdd(barrier))
                        return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                barrier.Wait();
            }

            private void Run()
            {
                _currentWorker = Index;

                CancellationToken cancellationToken = _owner._cancellationTokenSource.Token;

                try
                {
                    foreach (IArenaWorkItem workItem in _queue.GetConsumingEnumerable(cancellationToken))
                    {
                        try
                        {
                            workItem.Process();
                        }
                        catch (Exception ex)
                        {
                            _owner._logManager.LogM(LogLevel.Warn, nameof(ArenaLoop), $"Caught an exception while processing a work item. {ex}");
                        }
                        finally
                        {
                            if (workItem is IDisposable disposable)
                                disposable.Dispose();
                        }

                        IServerMetrics? serverMetrics = _owner._serverMetrics;
                        if (serverMetrics is not null && serverMetrics.IsEnabled)
                        {
                            serverMetrics.Increment(ServerMetric.ArenaLoopWorkItemsProcessed);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Shutting down.
                }

                // Release anything still queued, so that pooled work items and whatever they hold go back
                // rather than being dropped on the floor at shutdown.
                while (_queue.TryTake(out IArenaWorkItem? remaining))
                {
                    if (remaining is IDisposable disposable)
                        disposable.Dispose();
                }
            }

            public void Dispose()
            {
                _queue.Dispose();
            }
        }

        #endregion
    }
}
