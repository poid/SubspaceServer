using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    public sealed class ArenaLoop : IModule, IArenaLoop, IArenaTimer, IDisposable
    {
        private readonly IComponentBroker _broker;
        private readonly IConfigManager _configManager;
        private readonly ILogManager _logManager;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPlayerData _playerData;

        private InterfaceRegistrationToken<IArenaLoop>? _iArenaLoopToken;
        private InterfaceRegistrationToken<IArenaTimer>? _iArenaTimerToken;

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
        /// Which worker the running thread is, offset by one, or 0 on any other thread.
        /// </summary>
        /// <remarks>
        /// Offset because a <see cref="ThreadStaticAttribute"/> field's initializer runs on one thread
        /// only, so every other thread sees <see langword="default"/> whatever the initializer says. A
        /// sentinel of -1 would therefore read as worker 0 on the mainloop and every network thread.
        /// </remarks>
        [ThreadStatic]
        private static int _currentWorkerPlusOne;

        /// <summary>
        /// Whether the calling thread is one of the arena workers.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="IArenaLoop.IsArenaThread(Arena)"/> this asks nothing about which arena, so it
        /// can be answered without a broker or an arena in hand. That is what the generated callback
        /// assertions need, since they run wherever a callback is fired.
        /// </remarks>
        public static bool IsArenaWorkerThread => _currentWorkerPlusOne > 0;

        /// <summary>
        /// The most work items allowed to wait in one worker's queue at once, or 0 for no limit.
        /// </summary>
        private int _maxQueueDepth;

        public ArenaLoop(
            IComponentBroker broker,
            IConfigManager configManager,
            ILogManager logManager,
            IObjectPoolManager objectPoolManager,
            IPlayerData playerData)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));
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
                // Deliberately a small fraction of the machine, because more workers is not better here
                // and measurably worse past a point. The work they do reads player state under a lock
                // shared with the mainloop and the send threads, so every extra worker adds contention on
                // it rather than capacity.
                //
                // Measured at two thousand players over twenty arenas: two workers cost 1.94 cores in
                // total and left the mainloop at 10%, four cost 2.56, and sixteen cost 4.84 and pushed
                // the mainloop back up to 30% through that contention. Sixteen threads each doing a
                // sixteenth of the work still each pay the per pass overhead.
                workerCount = Math.Clamp(Environment.ProcessorCount / 8, 2, 8);
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
            _iArenaTimerToken = broker.RegisterInterface<IArenaTimer>(this);

            _logManager.LogM(LogLevel.Info, nameof(ArenaLoop), $"Started {workerCount} arena worker threads.");

            return true;
        }

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iArenaTimerToken) != 0)
                return false;

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
                // Drop any timers the arena still has, so that a module which did not clear its own cannot
                // keep a destroyed arena running. Queued before the assignment is dropped, so that it goes
                // to the worker that actually holds them rather than to whichever one the fallback in
                // GetWorker would pick afterwards.
                Worker worker = GetWorker(arena);
                worker.TryQueue(new ClearTimersWorkItem(worker, arena, null, null), sheddable: false);

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

            if (_currentWorkerPlusOne == worker.Index + 1)
                throw new InvalidOperationException("Cannot wait for an arena's work to drain from the thread that runs it.");

            worker.Drain();
        }

        bool IArenaLoop.IsArenaThread(Arena arena)
        {
            ArgumentNullException.ThrowIfNull(arena);

            return _currentWorkerPlusOne > 0 && _currentWorkerPlusOne == GetWorker(arena).Index + 1;
        }

        int IArenaLoop.WorkerCount => _workers.Length;

        #endregion

        #region IArenaTimer Members

        void IArenaTimer.SetTimer<TState>(Arena arena, TimerDelegate<TState> callback, int initialDelay, int interval, TState state, object? key)
        {
            ArgumentNullException.ThrowIfNull(arena);
            ArgumentNullException.ThrowIfNull(callback);
            ArgumentOutOfRangeException.ThrowIfNegative(initialDelay);

            if (interval <= 0 && interval != Timeout.Infinite)
                throw new ArgumentOutOfRangeException(nameof(interval), "must be > 0 or Timeout.Infinite");

            // The timer itself is only ever touched by the arena's own thread, so setting it is handed over
            // to that thread rather than done here under a lock. That is what makes it impossible for a
            // timer to be cancelled while it is being invoked.
            Worker worker = GetWorker(arena);
            worker.TryQueue(new SetTimerWorkItem(worker, new ArenaTimer<TState>(arena, callback, initialDelay, interval, state, key)), sheddable: false);
        }

        void IArenaTimer.ClearTimer<TState>(Arena arena, TimerDelegate<TState> callback, object? key)
        {
            ArgumentNullException.ThrowIfNull(arena);
            ArgumentNullException.ThrowIfNull(callback);

            Worker worker = GetWorker(arena);
            worker.TryQueue(new ClearTimersWorkItem(worker, arena, callback, key), sheddable: false);
        }

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
        /// One scheduled call on an arena's thread.
        /// </summary>
        /// <remarks>
        /// Only ever read or written by the worker that owns the arena, which is why none of this is
        /// synchronized. It reaches that worker as a <see cref="SetTimerWorkItem"/> and leaves it as a
        /// <see cref="ClearTimersWorkItem"/>.
        /// </remarks>
        private abstract class ArenaTimer(Arena arena, int initialDelay, int interval, object? key)
        {
            public Arena Arena { get; } = arena;
            public int InitialDelay { get; } = initialDelay;
            public int Interval { get; } = interval;
            public object? Key { get; } = key;

            /// <summary>
            /// When the timer is next meant to run.
            /// </summary>
            public DateTime WhenDue { get; set; }

            /// <summary>
            /// Whether the timer has been cleared and should be discarded rather than run.
            /// </summary>
            /// <remarks>
            /// A cleared timer is left in the queue rather than removed from it, since a
            /// <see cref="PriorityQueue{TElement, TPriority}"/> cannot remove from the middle. It is dropped
            /// when it next comes due, so a cleared timer outlives its clearing by at most one interval.
            /// </remarks>
            public bool IsStopped { get; set; }

            public abstract bool Invoke();

            public abstract bool Matches(Delegate callback, object? key);
        }

        private sealed class ArenaTimer<TState>(
            Arena arena,
            TimerDelegate<TState> callback,
            int initialDelay,
            int interval,
            TState state,
            object? key) : ArenaTimer(arena, initialDelay, interval, key)
        {
            private readonly TimerDelegate<TState> _callback = callback;
            private readonly TState _state = state;

            public override bool Invoke()
            {
                return _callback(_state);
            }

            public override bool Matches(Delegate callback, object? key)
            {
                // Delegate equality rather than reference equality, because converting a method group
                // produces a new delegate object each time. ClearTimer is invariably passed a different
                // instance than SetTimer was.
                return _callback.Equals(callback) && (key is null || Equals(Key, key));
            }
        }

        /// <summary>
        /// A work item that starts a timer on the thread that will run it.
        /// </summary>
        private sealed class SetTimerWorkItem(Worker worker, ArenaTimer timer) : IArenaWorkItem
        {
            public void Process()
            {
                worker.AddTimer(timer);
            }
        }

        /// <summary>
        /// A work item that stops timers on the thread that runs them.
        /// </summary>
        private sealed class ClearTimersWorkItem(Worker worker, Arena arena, Delegate? callback, object? key) : IArenaWorkItem
        {
            public void Process()
            {
                worker.ClearTimers(arena, callback, key);
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

            /// <summary>
            /// The timers for this worker's arenas, ordered by when they are next due.
            /// </summary>
            /// <remarks>
            /// Only touched by this worker's own thread. Everything that adds to or removes from it arrives
            /// through <see cref="_queue"/>, which is also what wakes the thread when a timer is added that
            /// is due sooner than whatever it is currently waiting for.
            /// </remarks>
            private readonly PriorityQueue<ArenaTimer, DateTime> _timers = new();

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

            /// <summary>
            /// Starts a timer. Runs on this worker's thread.
            /// </summary>
            public void AddTimer(ArenaTimer timer)
            {
                // The initial delay runs from here rather than from when SetTimer was called, so that a
                // timer handed over from another thread is started late rather than being immediately due.
                timer.WhenDue = DateTime.UtcNow.AddMilliseconds(timer.InitialDelay);
                _timers.Enqueue(timer, timer.WhenDue);
            }

            /// <summary>
            /// Stops timers. Runs on this worker's thread.
            /// </summary>
            /// <param name="arena">The arena to stop timers for.</param>
            /// <param name="callback">The delegate to stop timers for, or <see langword="null"/> for all of the arena's timers.</param>
            /// <param name="key">The key to match, or <see langword="null"/> to match any key.</param>
            public void ClearTimers(Arena arena, Delegate? callback, object? key)
            {
                foreach ((ArenaTimer timer, DateTime _) in _timers.UnorderedItems)
                {
                    if (timer.Arena != arena)
                        continue;

                    if (callback is not null && !timer.Matches(callback, key))
                        continue;

                    timer.IsStopped = true;
                }
            }

            /// <summary>
            /// Gets how long to wait for work before a timer needs to run.
            /// </summary>
            private int GetTimerWaitMilliseconds()
            {
                if (!_timers.TryPeek(out _, out DateTime whenDue))
                    return Timeout.Infinite;

                double milliseconds = (whenDue - DateTime.UtcNow).TotalMilliseconds;

                if (milliseconds <= 0)
                    return 0;

                return (int)double.Min(milliseconds, int.MaxValue);
            }

            /// <summary>
            /// Runs whichever timers are due. Runs on this worker's thread.
            /// </summary>
            private void ProcessTimers()
            {
                DateTime now = DateTime.UtcNow;

                while (_timers.TryPeek(out ArenaTimer? timer, out DateTime whenDue) && whenDue <= now)
                {
                    _timers.Dequeue();

                    if (timer.IsStopped)
                        continue;

                    // Arena objects are pooled and reused, and a reused one can be assigned to a different
                    // worker. Dropping the timer rather than running it keeps an entry left over from the
                    // previous life of the object from firing against an arena this thread does not own.
                    if (_owner.GetWorker(timer.Arena) != this)
                        continue;

                    bool isContinuing;

                    try
                    {
                        isContinuing = timer.Invoke();
                    }
                    catch (Exception ex)
                    {
                        _owner._logManager.LogM(LogLevel.Warn, nameof(ArenaLoop), $"Caught an exception while processing a timer. {ex}");
                        isContinuing = false;
                    }

                    // Re-read the clock rather than reusing now, so that a callback which takes longer than
                    // its own interval falls behind instead of becoming permanently due and spinning.
                    if (isContinuing && !timer.IsStopped && timer.Interval != Timeout.Infinite)
                    {
                        timer.WhenDue = DateTime.UtcNow.AddMilliseconds(timer.Interval);
                        _timers.Enqueue(timer, timer.WhenDue);
                    }
                }
            }

            private void Run()
            {
                _currentWorkerPlusOne = Index + 1;

                CancellationToken cancellationToken = _owner._cancellationTokenSource.Token;

                try
                {
                    while (!_queue.IsCompleted)
                    {
                        if (_queue.TryTake(out IArenaWorkItem? workItem, GetTimerWaitMilliseconds(), cancellationToken))
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

                            // Arena work runs whatever a module registered, so this is where a hold left
                            // behind would go unnoticed and be inherited by the next item.
                            ThreadingAssert.WarnIfPlayerLockLeaked(_owner._playerData, _owner._logManager, $"{nameof(ArenaLoop)}-{Index}");
                        }

                        ProcessTimers();
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
