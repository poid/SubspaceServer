using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PersistSettings = SS.Core.ConfigHelp.Constants.Global.Persist;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that provides functionality to persist information to a database.
    /// </summary>
    [CoreModuleInfo]
    public sealed class Persist : IAsyncModule, IPersist, IPersistExecutor, IDisposable
    {
        private readonly IComponentBroker _broker;
        private readonly IArenaManager _arenaManager;
        private readonly IConfigManager _configManager;
        private readonly ILogManager _logManager;
        private readonly IMainloop _mainloop;
        private readonly IObjectPoolManager _objectPoolManager;
        private readonly IPersistDatastore _persistDatastore;
        private readonly IPlayerData _playerData;

        private InterfaceRegistrationToken<IPersist>? _iPersistToken;
        private InterfaceRegistrationToken<IPersistExecutor>? _iPersistExecutorToken;

        private readonly List<PersistentData<Player>> _playerRegistrations = [];
        private readonly List<PersistentData<Arena>> _arenaRegistrations = [];
        private readonly SemaphoreSlim _registrationSemaphore = new(1, 1);

        private readonly Pool<PlayerWorkItem> _playerWorkItemPool;
        private readonly Pool<ArenaWorkItem> _arenaWorkItemPool;
        private readonly Pool<IntervalWorkItem> _intervalWorkItemPool;
        private readonly Pool<ResetGameIntervalWorkItem> _resetGameIntervalWorkItemPool;
        private readonly Pool<PutAllWorkItem> _putAllWorkItemPool;
        private readonly ObjectPool<MemoryStream> _memoryStreamPool = ObjectPool.Create(new MemoryStreamPooledObjectPolicy()); // Note: This creates a DisposableObjectPool.

        private readonly BlockingCollection<PersistWorkItem> _workQueue = [];
        private Thread? _workerThread;
        private TimeSpan _syncTimeSpan;

        private ArenaDataKey<ArenaData> _adKey;

        // Optional dependency. Retrieved in LoadAsync, which is after the module that provides it.
        private IServerMetrics? _serverMetrics;

        /// <summary>
        /// The most work items to put through one database transaction.
        /// </summary>
        /// <remarks>
        /// A bound rather than a target. Only work already waiting is ever taken, so this is reached only
        /// when the queue is genuinely deep, and it exists so that one transaction cannot grow without
        /// limit and so that the callbacks for the first item in a batch are not held up by the last.
        /// </remarks>
        private const int MaxWorkItemBatch = 128;

        /// <summary>
        /// How many players a periodic sync writes before committing and letting anything waiting through.
        /// </summary>
        /// <remarks>
        /// Measured at ten thousand players, one sweep took 5.2 seconds. Entering an arena waits on this
        /// thread, so that was every player arriving during a sync waiting for all of it. At roughly half
        /// a millisecond per player this keeps one chunk near a tenth of a second.
        /// <para>
        /// Smaller is not automatically better: each chunk is a commit, and commits are the expensive part
        /// of a sync, so cutting it finer trades total sync time for how long any one player waits.
        /// </para>
        /// </remarks>
        private const int SyncPlayersPerChunk = 256;

        private static int _maxRecordLength = PersistSettings.MaxRecordLength.Default;

        private readonly Action<PersistWorkItem> _mainloopWorkItem_ExecuteCallbacksAndDispose;

        public Persist(
            IComponentBroker broker,
            IArenaManager arenaManager,
            IConfigManager configManager,
            ILogManager logManager,
            IMainloop mainloop,
            IObjectPoolManager objectPoolManager,
            IPersistDatastore persistDatastore,
            IPlayerData playerData)
        {
            _broker = broker;
            _arenaManager = arenaManager ?? throw new ArgumentNullException(nameof(arenaManager));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _objectPoolManager = objectPoolManager ?? throw new ArgumentNullException(nameof(objectPoolManager));
            _persistDatastore = persistDatastore ?? throw new ArgumentNullException(nameof(persistDatastore));
            _playerData = playerData ?? throw new ArgumentNullException(nameof(playerData));

            _playerWorkItemPool = _objectPoolManager.GetPool<PlayerWorkItem>();
            _arenaWorkItemPool = _objectPoolManager.GetPool<ArenaWorkItem>();
            _intervalWorkItemPool = _objectPoolManager.GetPool<IntervalWorkItem>();
            _resetGameIntervalWorkItemPool = _objectPoolManager.GetPool<ResetGameIntervalWorkItem>();
            _putAllWorkItemPool = _objectPoolManager.GetPool<PutAllWorkItem>();

            _mainloopWorkItem_ExecuteCallbacksAndDispose = MainloopWorkItem_ExecuteCallbacksAndDispose;
        }

        #region Module memebers

        [ConfigHelp<int>("Persist", "SyncSeconds", ConfigScope.Global, Default = 180, Min = 10,
            Description = "The interval at which all persistent data is synced to the database.")]
        [ConfigHelp<int>("Persist", "MaxRecordLength", ConfigScope.Global, Default = 4096,
            Description = "The maximum # of bytes to store per record.")]
        async Task<bool> IAsyncModule.LoadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            if (!await Task.Run(_persistDatastore.Open))
                return false;

            _serverMetrics = broker.GetInterface<IServerMetrics>();

            _adKey = _arenaManager.AllocateArenaData<ArenaData>();
            ArenaActionCallback.Register(broker, Callback_ArenaAction);

            _syncTimeSpan = TimeSpan.FromSeconds(
                _configManager.GetInt(_configManager.Global, "Persist", "SyncSeconds", PersistSettings.SyncSeconds.Default)).Duration();

            TimeSpan minSyncTimeSpan = TimeSpan.FromSeconds(PersistSettings.SyncSeconds.Min);
            if (_syncTimeSpan < minSyncTimeSpan)
                _syncTimeSpan = minSyncTimeSpan;

            _maxRecordLength = _configManager.GetInt(_configManager.Global, "Persist", "MaxRecordLength", PersistSettings.MaxRecordLength.Default);

            _workerThread = new Thread(PersistWorkerThread);
            _workerThread.Name = nameof(Persist);
            _workerThread.Start();

            _iPersistToken = broker.RegisterInterface<IPersist>(this);
            _iPersistExecutorToken = broker.RegisterInterface<IPersistExecutor>(this);

            return true;
        }

        async Task<bool> IAsyncModule.UnloadAsync(IComponentBroker broker, CancellationToken cancellationToken)
        {
            if (broker.UnregisterInterface(ref _iPersistToken) != 0)
                return false;

            if (broker.UnregisterInterface(ref _iPersistExecutorToken) != 0)
                return false;

            _workQueue.CompleteAdding();
            _workerThread?.Join();

            ArenaActionCallback.Unregister(broker, Callback_ArenaAction);
            _arenaManager.FreeArenaData(ref _adKey);

            if (_serverMetrics is not null)
            {
                broker.ReleaseInterface(ref _serverMetrics);
            }

            await Task.Run(_persistDatastore.Close);

            return true;
        }

        #endregion

        #region IPersist members

        async Task IPersist.RegisterPersistentDataAsync(PersistentData<Player> registration)
        {
            ArgumentNullException.ThrowIfNull(registration);

            await _registrationSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                // TODO: prevent adding a duplicate registration (same key + interval)?
                _playerRegistrations.Add(registration);
            }
            finally
            {
                _registrationSemaphore.Release();
            }
        }

        async Task IPersist.UnregisterPersistentDataAsync(PersistentData<Player> registration)
        {
            ArgumentNullException.ThrowIfNull(registration);

            await _registrationSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                _playerRegistrations.Remove(registration);
            }
            finally
            {
                _registrationSemaphore.Release();
            }
        }

        async Task IPersist.RegisterPersistentDataAsync(PersistentData<Arena> registration)
        {
            ArgumentNullException.ThrowIfNull(registration);

            await _registrationSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                // TODO: prevent adding a duplicate registration (same key + interval)?
                _arenaRegistrations.Add(registration);
            }
            finally
            {
                _registrationSemaphore.Release();
            }
        }

        async Task IPersist.UnregisterPersistentDataAsync(PersistentData<Arena> registration)
        {
            ArgumentNullException.ThrowIfNull(registration);

            await _registrationSemaphore.WaitAsync().ConfigureAwait(false);

            try
            {
                _arenaRegistrations.Remove(registration);
            }
            finally
            {
                _registrationSemaphore.Release();
            }
        }

        string IPersist.GetScoreGroup(Arena arena)
        {
            ArgumentNullException.ThrowIfNull(arena);

            string? group = null;

            if (arena.TryGetExtraData(_adKey, out ArenaData? ad))
                group = ad.ArenaGroup;

            if (string.IsNullOrWhiteSpace(group))
                group = arena.BaseName;

            return group;
        }

        #endregion

        #region IPersistExecutor

        void IPersistExecutor.PutPlayer(Player player, Arena? arena, Action<Player>? callback)
        {
            if (!player.Flags.Authenticated)
            {
                callback?.Invoke(player);
                return;
            }

            QueuePlayerWorkItem(PersistCommand.PutPlayer, player, arena, callback);
        }

        void IPersistExecutor.GetPlayer(Player player, Arena? arena, Action<Player>? callback)
        {
            if (!player.Flags.Authenticated)
            {
                callback?.Invoke(player);
                return;
            }

            QueuePlayerWorkItem(PersistCommand.GetPlayer, player, arena, callback);
        }

        private void QueuePlayerWorkItem(PersistCommand command, Player player, Arena? arena, Action<Player>? callback)
        {
            PlayerWorkItem workItem = _playerWorkItemPool.Get();
            workItem.Command = command;
            workItem.Player = player;
            workItem.Arena = arena;
            workItem.Callback = callback;

            workItem.QueuedTimestamp = Stopwatch.GetTimestamp();
            _workQueue.Add(workItem);
        }

        void IPersistExecutor.PutArena(Arena arena, Action<Arena>? callback)
            => QueueArenaWorkItem(PersistCommand.PutArena, arena, callback);

        void IPersistExecutor.GetArena(Arena arena, Action<Arena>? callback)
            => QueueArenaWorkItem(PersistCommand.GetArena, arena, callback);

        private void QueueArenaWorkItem(PersistCommand command, Arena arena, Action<Arena>? callback)
        {
            ArenaWorkItem workItem = _arenaWorkItemPool.Get();
            workItem.Command = command;
            workItem.Arena = arena;
            workItem.Callback = callback;

            workItem.QueuedTimestamp = Stopwatch.GetTimestamp();
            _workQueue.Add(workItem);
        }

        void IPersistExecutor.EndInterval(PersistInterval interval, string? arenaGroupOrArenaName)
        {
            if (string.IsNullOrWhiteSpace(arenaGroupOrArenaName))
                arenaGroupOrArenaName = Constants.ArenaGroup_Global;

            QueueIntervalWorkItem(PersistCommand.EndInterval, interval, arenaGroupOrArenaName);
        }

        void IPersistExecutor.EndInterval(PersistInterval interval, Arena? arena)
        {
            QueueIntervalWorkItem(PersistCommand.EndInterval, interval, GetArenaGroup(arena, interval));
        }

        private void QueueIntervalWorkItem(PersistCommand command, PersistInterval interval, string arenaGroup)
        {
            IntervalWorkItem workItem = _intervalWorkItemPool.Get();
            workItem.Command = command;
            workItem.Interval = interval;
            workItem.ArenaGroup = arenaGroup;

            workItem.QueuedTimestamp = Stopwatch.GetTimestamp();
            _workQueue.Add(workItem);
        }

        void IPersistExecutor.ResetGameInterval(Arena arena, Action<Arena> callback)
        {
            ArgumentNullException.ThrowIfNull(arena);

            ResetGameIntervalWorkItem workItem = _resetGameIntervalWorkItemPool.Get();
            workItem.Command = PersistCommand.ResetGameInterval;
            workItem.Arena = arena;
            workItem.Callback = callback;

            workItem.QueuedTimestamp = Stopwatch.GetTimestamp();
            _workQueue.Add(workItem);
        }

        void IPersistExecutor.SaveAll(Action? completed)
        {
            PutAllWorkItem workItem = _putAllWorkItemPool.Get();
            workItem.Command = PersistCommand.PutAll;
            workItem.Callback = completed;

            workItem.QueuedTimestamp = Stopwatch.GetTimestamp();
            _workQueue.Add(workItem);
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_memoryStreamPool is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        #endregion

        [ConfigHelp("General", "ScoreGroup", ConfigScope.Arena,
            Description = """
                If this is set, it will be used as the score identifier for
                shared scores for this arena(unshared scores, e.g. per - game
                scores, always use the arena name as the identifier).Setting
                this to the same value in several different arenas will cause
                them to share scores.
                """)]
        private void Callback_ArenaAction(Arena arena, ArenaAction action)
        {
            if (!arena.TryGetExtraData(_adKey, out ArenaData? ad))
                return;

            if (action == ArenaAction.Create)
            {
                string? arenaGroup = _configManager.GetStr(arena.Cfg!, "General", "ScoreGroup");
                if (!string.IsNullOrWhiteSpace(arenaGroup))
                    ad.ArenaGroup = arenaGroup;
                else
                    ad.ArenaGroup = arena.BaseName;
            }
        }

        private void PersistWorkerThread()
        {
            DateTime nextSync = DateTime.UtcNow + _syncTimeSpan;

            // Reused for every batch. Only this thread ever touches them.
            List<PersistWorkItem> batch = new(MaxWorkItemBatch);

            // Work drawn part way through a sync that is not safe to run there, kept until it finishes.
            List<PersistWorkItem> deferred = [];

            // Whether the periodic sync has a transaction open. It opens and closes several, so this
            // cannot be a local of the block that starts it.
            bool syncTransactionOpen = false;

            while (true)
            {
                TimeSpan waitTimeSpan = nextSync - DateTime.UtcNow;
                if (waitTimeSpan < TimeSpan.Zero)
                    waitTimeSpan = TimeSpan.Zero;

                if (!_workQueue.TryTake(out PersistWorkItem? workItem, waitTimeSpan))
                {
                    // Did not get a workitem. This means either:
                    // we've either been signaled to shut down (no more items will be added)
                    // OR
                    // it's time to do a full sync (put all data to the database)
                    if (_workQueue.IsCompleted)
                    {
                        return;
                    }
                    else
                    {
                        // Not signaled to shut down, which means we should do a full sync.
                        // The sync is done periodically so that if there were a server crash or power outage,
                        // we'd at least have the data that was saved since the last sync was committed.
                        _registrationSemaphore.Wait();

                        long syncStart = Stopwatch.GetTimestamp();

                        try
                        {
                            BeginSyncTransaction();

                            try
                            {
                                // Interleaved, so that the sweep lets whatever is waiting through as it
                                // goes rather than making all of it wait for the whole thing.
                                DoPutAll(interleave: true);
                            }
                            finally
                            {
                                CommitSyncTransaction();
                            }
                        }
                        finally
                        {
                            _registrationSemaphore.Release();
                        }

                        // Counted as a work item so that the periodic sync shows up in the mean cost of
                        // one, which is where it belongs: it is by far the most expensive thing this
                        // thread does and hiding it would flatter the average.
                        _serverMetrics?.Add(ServerMetric.PersistWorkItemElapsedTicks, Stopwatch.GetTimestamp() - syncStart);
                        _serverMetrics?.Increment(ServerMetric.PersistWorkItemsProcessed);

                        nextSync = DateTime.UtcNow + _syncTimeSpan;

                        // Whatever the sync drew but would not run part way through.
                        if (deferred.Count > 0)
                        {
                            batch.Clear();
                            batch.AddRange(deferred);
                            deferred.Clear();
                            RunBatch();
                        }
                    }

                    continue;
                }

                batch.Clear();
                batch.Add(workItem);

                // Take whatever else is already waiting and do it in the same transaction. Only what is
                // already there: nothing is ever waited for in the hope that more arrives, so a quiet
                // server still does one item per transaction and nobody waits longer than they did.
                //
                // It is the busy case this is for. Entering and leaving an arena each wait on their work
                // item, so a burst of players arriving is a burst of people standing still, and what they
                // were waiting for was mostly the commits.
                while (batch.Count < MaxWorkItemBatch && _workQueue.TryTake(out PersistWorkItem? queuedWorkItem))
                {
                    batch.Add(queuedWorkItem);
                }

                RunBatch();
            }

            // Runs everything currently in batch through one transaction, then lets the callbacks go.
            void RunBatch()
            {
                // Sampled before the work is done, so it reads as the backlog this batch was taken from
                // rather than as whatever arrived while it ran.
                IServerMetrics? metrics = _serverMetrics;
                bool measuring = metrics is not null && metrics.IsEnabled;
                long batchStart = measuring ? Stopwatch.GetTimestamp() : 0;

                if (measuring)
                {
                    metrics!.SetGauge(ServerMetric.PersistQueueDepth, _workQueue.Count);

                    // How long each item sat in the queue. Depth is only a proxy for this, and a poor one
                    // read as a gauge: a backlog that builds behind a long sync and drains before the
                    // window ends leaves the gauge showing the quiet moment at the end.
                    long now = Stopwatch.GetTimestamp();

                    foreach (PersistWorkItem batchItem in batch)
                    {
                        metrics.Add(ServerMetric.PersistWaitElapsedTicks, now - batchItem.QueuedTimestamp);
                    }
                }

                _registrationSemaphore.Wait();

                try
                {
                    bool startedTransaction = _persistDatastore.BeginTransaction();

                    try
                    {
                        foreach (PersistWorkItem batchItem in batch)
                        {
                            ProcessWorkItem(batchItem);
                        }
                    }
                    finally
                    {
                        if (startedTransaction)
                        {
                            // Timed separately from the rest of the work, because a commit is where the
                            // durability settings show up and those are configuration rather than code.
                            long commitStart = measuring ? Stopwatch.GetTimestamp() : 0;

                            _persistDatastore.CommitTransaction();

                            if (measuring)
                            {
                                metrics!.Add(ServerMetric.PersistCommitElapsedTicks, Stopwatch.GetTimestamp() - commitStart);
                                metrics.Increment(ServerMetric.PersistTransactions);
                            }
                        }
                    }
                }
                finally
                {
                    _registrationSemaphore.Release();
                }

                if (measuring)
                {
                    metrics!.Add(ServerMetric.PersistWorkItemElapsedTicks, Stopwatch.GetTimestamp() - batchStart);
                    metrics.Add(ServerMetric.PersistWorkItemsProcessed, batch.Count);
                }

                DispatchCallbacks();
            }

            // Hands each work item's callback back to the mainloop. Only ever after a commit, so that
            // whatever a callback allows to happen next happens against data that is already durable.
            void DispatchCallbacks()
            {
                foreach (PersistWorkItem batchItem in batch)
                {
                    if (!_mainloop.QueueMainWorkItem(_mainloopWorkItem_ExecuteCallbacksAndDispose, batchItem)
                        && batchItem.Command == PersistCommand.PutAll)
                    {
                        // Couldn't queue a mainloop workitem. This will happen when the server is shutting down.
                        // When the mainloop exits, that thread requests that we save everything by adding a PutAll request, and it waits for us to execute the callback.
                        // Do the callback on worker thread.
                        MainloopWorkItem_ExecuteCallbacksAndDispose(batchItem);
                    }
                }
            }

            // Does one work item. Assumes a transaction is already open.
            void ProcessWorkItem(PersistWorkItem batchItem)
            {
                // Nothing here used to be guarded, so anything that threw took the whole persist thread
                // with it and left every player after that waiting forever on a sync that would never
                // call back. The datastore swallows its own errors, but the modules that serialize their
                // data are called from here and are not obliged to. One record failing costs that record.
                try
                {
                    switch (batchItem.Command)
                    {
                        case PersistCommand.Null:
                            break;

                        case PersistCommand.GetPlayer:
                            if (batchItem is PlayerWorkItem getPlayerWorkItem && getPlayerWorkItem.Player is not null)
                            {
                                GetOnePlayer(getPlayerWorkItem.Player, getPlayerWorkItem.Arena);
                            }
                            break;

                        case PersistCommand.PutPlayer:
                            if (batchItem is PlayerWorkItem putPlayerWorkItem && putPlayerWorkItem.Player is not null)
                            {
                                PutOnePlayer(putPlayerWorkItem.Player, putPlayerWorkItem.Arena);
                            }
                            break;

                        case PersistCommand.GetArena:
                            if (batchItem is ArenaWorkItem getArenaWorkItem)
                            {
                                GetOneArena(getArenaWorkItem.Arena);
                            }
                            break;

                        case PersistCommand.PutArena:
                            if (batchItem is ArenaWorkItem putArenaWorkItem)
                            {
                                PutOneArena(putArenaWorkItem.Arena);
                            }
                            break;

                        case PersistCommand.PutAll:
                            // Not interleaved. This is what a caller asking for everything to be saved
                            // gets, including the mainloop on its way out, and it waits on the callback,
                            // so the whole sweep belongs in one transaction that is done when it says.
                            DoPutAll(interleave: false);
                            nextSync = DateTime.UtcNow + _syncTimeSpan;
                            break;

                        case PersistCommand.EndInterval:
                            if (batchItem is IntervalWorkItem intervalWorkItem && intervalWorkItem.ArenaGroup is not null)
                            {
                                DoEndInterval(intervalWorkItem.Interval, intervalWorkItem.ArenaGroup);
                            }
                            break;

                        case PersistCommand.ResetGameInterval:
                            if (batchItem is ResetGameIntervalWorkItem resetIntervalWorkItem && resetIntervalWorkItem.Arena is not null)
                            {
                                DoResetGameInterval(resetIntervalWorkItem.Arena);
                            }
                            break;

                        //case PersistCommand.GetGeneric:
                        //    break;

                        //case PersistCommand.PutGeneric:
                        //    break;

                        default:
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logManager.LogM(LogLevel.Error, nameof(Persist), $"Caught an exception processing a {batchItem.Command} work item. {ex}");
                }
            }

            void BeginSyncTransaction()
            {
                syncTransactionOpen = _persistDatastore.BeginTransaction();
            }

            void CommitSyncTransaction()
            {
                if (!syncTransactionOpen)
                    return;

                long commitStart = Stopwatch.GetTimestamp();

                _persistDatastore.CommitTransaction();

                _serverMetrics?.Add(ServerMetric.PersistCommitElapsedTicks, Stopwatch.GetTimestamp() - commitStart);
                _serverMetrics?.Increment(ServerMetric.PersistTransactions);

                syncTransactionOpen = false;
            }

            // Whether a command can be run part way through a sync.
            static bool IsInterleavable(PersistCommand command)
            {
                return command is PersistCommand.Null
                    or PersistCommand.GetPlayer
                    or PersistCommand.PutPlayer
                    or PersistCommand.GetArena
                    or PersistCommand.PutArena;
            }

            // Called between chunks of a sync. Closes the chunk, lets whatever is waiting through, and
            // opens the next one. The registration semaphore is already held by the sync, and is not
            // reentrant, so this must not try to take it again.
            void YieldDuringSync()
            {
                if (_workQueue.Count <= 0)
                    return;

                CommitSyncTransaction();

                batch.Clear();

                while (batch.Count < MaxWorkItemBatch && _workQueue.TryTake(out PersistWorkItem? queuedWorkItem))
                {
                    if (IsInterleavable(queuedWorkItem.Command))
                    {
                        batch.Add(queuedWorkItem);
                    }
                    else
                    {
                        // Ending an interval, or saving everything, is not something to run half way
                        // through a sync, and there is no putting it back. It waits until the sync is
                        // finished, which is what would have happened without any of this.
                        deferred.Add(queuedWorkItem);
                        break;
                    }
                }

                if (batch.Count > 0)
                {
                    bool startedTransaction = _persistDatastore.BeginTransaction();

                    try
                    {
                        foreach (PersistWorkItem batchItem in batch)
                        {
                            ProcessWorkItem(batchItem);
                        }
                    }
                    finally
                    {
                        if (startedTransaction)
                        {
                            long commitStart = Stopwatch.GetTimestamp();

                            _persistDatastore.CommitTransaction();

                            _serverMetrics?.Add(ServerMetric.PersistCommitElapsedTicks, Stopwatch.GetTimestamp() - commitStart);
                            _serverMetrics?.Increment(ServerMetric.PersistTransactions);
                        }
                    }

                    _serverMetrics?.Add(ServerMetric.PersistWorkItemsProcessed, batch.Count);

                    DispatchCallbacks();
                }

                BeginSyncTransaction();
            }

            void DoResetGameInterval(Arena arena)
            {
                PlayerState minStatus = PlayerState.ArenaRespAndCBS;
                PlayerState maxStatus = PlayerState.WaitArenaSync2;

                //
                // Players
                //

                _playerData.Lock();

                try
                {
                    foreach (Player p in _playerData.Players)
                    {
                        Arena? playerArena = p.Arena;

                        if (p.Status >= minStatus
                            && p.Status <= maxStatus
                            && p.Arena == arena)
                        {
                            foreach (PersistentData<Player> registration in _playerRegistrations)
                            {
                                if (registration.Interval == PersistInterval.Game
                                    && registration.Scope == PersistScope.PerArena)
                                {
                                    registration.ClearData(p);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                //
                // Arenas
                //

                foreach (PersistentData<Arena> registration in _arenaRegistrations)
                {
                    if (registration.Interval == PersistInterval.Game
                        && registration.Scope == PersistScope.PerArena)
                    {
                        registration.ClearData(arena);
                    }
                }

                //
                // Remove from the database.
                //

                _persistDatastore.ResetGameInterval(arena.Name);
            }

            void DoEndInterval(PersistInterval interval, string arenaGroup)
            {
                if (interval == PersistInterval.Forever || interval == PersistInterval.ForeverNotShared)
                    return; // forever can't be ended

                bool isGlobal = string.Equals(arenaGroup, Constants.ArenaGroup_Global);

                PlayerState minStatus, maxStatus;

                if (isGlobal)
                {
                    // global data is loaded during S_WAIT_GLOBAL_SYNC,
                    // so we want to perform the getting/clearing if the player is after that.
                    minStatus = PlayerState.DoGlobalCallbacks;

                    // after we've saved global data for the last time, status goes to S_TIMEWAIT,
                    // so if we're before that, we still have data to save.
                    maxStatus = PlayerState.WaitGlobalSync2;
                }
                else
                {
                    // similar to above, but for arena data
                    minStatus = PlayerState.ArenaRespAndCBS;
                    maxStatus = PlayerState.WaitArenaSync2;
                }

                //
                // players
                //

                _playerData.Lock();

                try
                {
                    foreach (Player p in _playerData.Players)
                    {
                        Arena? arena = p.Arena;

                        if (p.Status >= minStatus
                            && p.Status <= maxStatus
                            && (isGlobal
                                || (arena is not null && string.Equals(arenaGroup, GetArenaGroup(arena, interval), StringComparison.OrdinalIgnoreCase))))
                        {
                            foreach (PersistentData<Player> registration in _playerRegistrations)
                            {
                                if (registration.Interval == interval)
                                {
                                    if (registration.Scope == PersistScope.Global && isGlobal)
                                    {
                                        PutOnePlayer(registration, p, null);
                                        registration.ClearData(p);
                                    }
                                    else if (registration.Scope == PersistScope.PerArena && !isGlobal)
                                    {
                                        PutOnePlayer(registration, p, arena);
                                        registration.ClearData(p);
                                    }
                                }
                            }
                        }
                    }
                }
                finally
                {
                    _playerData.Unlock();
                }

                //
                // arenas
                //

                if (isGlobal)
                {
                    foreach (PersistentData<Arena> registration in _arenaRegistrations)
                    {
                        if (registration.Interval == interval
                            && registration.Scope == PersistScope.Global)
                        {
                            PutOneArena(registration, null);
                            registration.ClearData(null);
                        }
                    }
                }
                else
                {
                    _arenaManager.Lock();

                    try
                    {
                        foreach (Arena arena in _arenaManager.Arenas)
                        {
                            if (string.Equals(GetArenaGroup(arena, interval), arenaGroup, StringComparison.OrdinalIgnoreCase))
                            {
                                foreach (PersistentData<Arena> registration in _arenaRegistrations)
                                {
                                    if (registration.Interval == interval
                                        && registration.Scope == PersistScope.PerArena)
                                    {
                                        PutOneArena(registration, arena);
                                        registration.ClearData(arena);
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        _arenaManager.Unlock();
                    }
                }

                //
                // Update the ArenaGroupInterval
                //

                _persistDatastore.CreateArenaGroupIntervalAndMakeCurrent(arenaGroup, interval);
            }

            // Writes everything currently in memory to the database.
            //
            // With interleave, the sweep is broken into chunks with a commit between them and whatever is
            // waiting let through in the gap. At ten thousand players one sweep took over five seconds,
            // and entering an arena waits on this thread, so without that every player arriving during a
            // sync waits for the whole of it.
            void DoPutAll(bool interleave)
            {
                // The following logic uses temporary, pooled collections of players and arenas since
                // we don't want to hold onto the player data lock or arena lock while doing the actual processing.
                //
                // Accessing the player's data without holding the global player data lock should be ok.
                // The player's state could change while we're processing, but not in a way that invalidates what we're doing.
                // That is, the player can't request to switch to another arena, but can't until this thread completes additional requests.
                // Similarly, accessing the arena's data without holding the global arena data lock should be ok.
                // The arena can try to switch states, but will not be able to be destroyed until after this thread processes additional requests.

                // Sync all players.
                HashSet<Player> players = _objectPoolManager.PlayerSetPool.Get();

                try
                {
                    // Determine which players to process.
                    _playerData.Lock();

                    try
                    {
                        foreach (Player player in _playerData.Players)
                        {
                            if (player.Status == PlayerState.Playing)
                            {
                                players.Add(player);
                            }
                        }
                    }
                    finally
                    {
                        _playerData.Unlock();
                    }

                    // Process the players.
                    int sinceYield = 0;

                    foreach (Player player in players)
                    {
                        PutOnePlayer(player, null); // global player data
                        if (player.Arena is not null)
                        {
                            PutOnePlayer(player, player.Arena); // per-arena player data
                        }

                        if (interleave && ++sinceYield >= SyncPlayersPerChunk)
                        {
                            sinceYield = 0;
                            YieldDuringSync();
                        }
                    }

                    // Reported once for the whole sweep rather than per player, since the interesting
                    // quantity is how much one sync has to get through.
                    _serverMetrics?.SetGauge(ServerMetric.PersistPlayersSynced, players.Count);
                }
                finally
                {
                    _objectPoolManager.PlayerSetPool.Return(players);
                }

                // Sync all arenas.
                HashSet<Arena> arenas = _objectPoolManager.ArenaSetPool.Get();
                try
                {
                    // Determine which arenas to process.
                    _arenaManager.Lock();

                    try
                    {
                        foreach (Arena arena in _arenaManager.Arenas)
                        {
                            if (arena.Status == ArenaState.Running)
                            {
                                arenas.Add(arena);
                            }
                        }
                    }
                    finally
                    {
                        _arenaManager.Unlock();
                    }

                    // Process the arenas.
                    foreach (Arena arena in arenas)
                    {
                        PutOneArena(arena); // per-arena arena data
                    }
                }
                finally
                {
                    _objectPoolManager.ArenaSetPool.Return(arenas);
                }

                // Sync global data.
                PutOneArena(null);
            }
        }

        private void PutOneArena(Arena? arena)
        {
            foreach (PersistentData<Arena> registration in _arenaRegistrations)
            {
                PutOneArena(registration, arena);
            }
        }

        private void PutOneArena(PersistentData<Arena> registration, Arena? arena)
        {
            // Check correct scope.
            if (registration.Scope == PersistScope.Global && arena is not null
                || registration.Scope == PersistScope.PerArena && arena is null)
            {
                return;
            }

            string arenaGroup = GetArenaGroup(arena, registration.Interval);

            MemoryStream dataStream = _memoryStreamPool.Get();

            try
            {
                registration.GetData(arena, dataStream);

                if (dataStream.Length > 0)
                {
                    dataStream.Position = 0;
                    _persistDatastore.SetArenaData(arenaGroup, registration.Interval, registration.Key, dataStream);
                }
                else
                {
                    _persistDatastore.DeleteArenaData(arenaGroup, registration.Interval, registration.Key);
                }
            }
            finally
            {
                _memoryStreamPool.Return(dataStream);
            }
        }

        /// <summary>
        /// Gets data for one arena or global (zone-wide) data.
        /// </summary>
        /// <param name="arena">The arena to get data for, or <see cref="null"/> for global (zone-wide) data.</param>
        private void GetOneArena(Arena? arena)
        {
            foreach (PersistentData<Arena> registration in _arenaRegistrations)
            {
                GetOneArena(registration, arena);
            }
        }

        private void GetOneArena(PersistentData<Arena> registration, Arena? arena)
        {
            // Check correct scope.
            if (registration.Scope == PersistScope.Global && arena is not null
                || registration.Scope == PersistScope.PerArena && arena is null)
            {
                return;
            }

            registration.ClearData(arena);

            string arenaGroup = GetArenaGroup(arena, registration.Interval);

            MemoryStream dataStream = _memoryStreamPool.Get();

            try
            {
                if (_persistDatastore.GetArenaData(arenaGroup, registration.Interval, registration.Key, dataStream))
                {
                    dataStream.Position = 0;
                    registration.SetData(arena, dataStream);
                }
            }
            finally
            {
                _memoryStreamPool.Return(dataStream);
            }
        }

        private void MainloopWorkItem_ExecuteCallbacksAndDispose(PersistWorkItem workItem)
        {
            if (workItem is null)
                return;

            try
            {
                workItem.ExecuteCallback();

                if (workItem.Command == PersistCommand.EndInterval
                    && workItem is IntervalWorkItem intervalWorkItem)
                {
                    PersistIntervalEndedCallback.Fire(_broker, intervalWorkItem.Interval, intervalWorkItem.ArenaGroup!);
                }
            }
            finally
            {
                // return the workItem to its pool
                workItem.Dispose();
            }
        }

        private void PutOnePlayer(Player player, Arena? arena)
        {
            foreach (PersistentData<Player> registration in _playerRegistrations)
            {
                PutOnePlayer(registration, player, arena);
            }
        }

        private void PutOnePlayer(PersistentData<Player> registration, Player player, Arena? arena)
        {
            // Check correct scope.
            if ((registration.Scope == PersistScope.Global && arena is not null)
                || (registration.Scope == PersistScope.PerArena && arena is null))
            {
                return;
            }

            string arenaGroup = GetArenaGroup(arena, registration.Interval);

            MemoryStream dataStream = _memoryStreamPool.Get();

            try
            {
                registration.GetData(player, dataStream);

                if (dataStream.Length > 0)
                {
                    dataStream.Position = 0;
                    _persistDatastore.SetPlayerData(player, arenaGroup, registration.Interval, registration.Key, dataStream);
                }
                else
                {
                    _persistDatastore.DeletePlayerData(player, arenaGroup, registration.Interval, registration.Key);
                }
            }
            finally
            {
                _memoryStreamPool.Return(dataStream);
            }
        }

        /// <summary>
        /// Gets one player's data. The data can be for a particular arena or global (zone-wide).
        /// </summary>
        /// <param name="player">The player to get data for.</param>
        /// <param name="arena">The arena to get data for, or <see cref="null"/> for global (zone-wide) data.</param>
        private void GetOnePlayer(Player player, Arena? arena)
        {
            foreach (PersistentData<Player> registration in _playerRegistrations)
            {
                GetOnePlayer(registration, player, arena);
            }
        }

        private void GetOnePlayer(PersistentData<Player> registration, Player player, Arena? arena)
        {
            // Check correct scope.
            if (registration.Scope == PersistScope.Global && arena is not null
                || registration.Scope == PersistScope.PerArena && arena is null)
            {
                return;
            }

            registration.ClearData(player);

            string arenaGroup = GetArenaGroup(arena, registration.Interval);

            MemoryStream dataStream = _memoryStreamPool.Get();

            try
            {
                if (_persistDatastore.GetPlayerData(player, arenaGroup, registration.Interval, registration.Key, dataStream))
                {
                    dataStream.Position = 0;
                    registration.SetData(player, dataStream);
                }
            }
            finally
            {
                _memoryStreamPool.Return(dataStream);
            }
        }

        private string GetArenaGroup(Arena? arena, PersistInterval interval)
        {
            if (arena is null)
                return Constants.ArenaGroup_Global;

            if (interval.IsShared()
                && arena.TryGetExtraData(_adKey, out ArenaData? ad))
            {
                return ad.ArenaGroup!;
            }
            else
            {
                return arena.Name;
            }
        }

        private sealed class ArenaData : IResettable
        {
            /// <summary>
            /// For shared intervals.
            /// </summary>
            public string? ArenaGroup { get; set; }

            public bool TryReset()
            {
                ArenaGroup = null;
                return true;
            }
        }

        /// <summary>
        /// Types of commands that the worker thread can be given.
        /// </summary>
        private enum PersistCommand
        {
            Null,
            GetPlayer,
            PutPlayer,
            GetArena,
            PutArena,
            PutAll,
            EndInterval,
            ResetGameInterval,
            //GetGeneric,
            //PutGeneric,
        }

        #region WorkItem helper classes

        private abstract class PersistWorkItem : PooledObject
        {
            protected PersistCommand _command;
            public abstract PersistCommand Command { get; set; }

            /// <summary>
            /// When the item was queued, for measuring how long it then waited.
            /// </summary>
            public long QueuedTimestamp { get; set; }

            public virtual void ExecuteCallback()
            {
            }

            protected override void Dispose(bool isDisposing)
            {
                base.Dispose(isDisposing);

                if (isDisposing)
                {
                    _command = PersistCommand.Null;
                }
            }
        }

        private class ArenaWorkItem : PersistWorkItem
        {
            public override PersistCommand Command
            {
                get { return _command; }
                set
                {
                    if (value != PersistCommand.GetArena && value != PersistCommand.PutArena)
                        throw new ArgumentOutOfRangeException(nameof(value));

                    _command = value;
                }
            }

            public Arena? Arena { get; set; }
            public Action<Arena>? Callback { get; set; }

            public override void ExecuteCallback()
            {
                Callback?.Invoke(Arena!);
            }

            protected override void Dispose(bool isDisposing)
            {
                base.Dispose(isDisposing);

                if (isDisposing)
                {
                    Arena = null;
                    Callback = null;
                }
            }
        }

        private class PlayerWorkItem : PersistWorkItem
        {
            public override PersistCommand Command
            {
                get { return _command; }
                set
                {
                    if (value != PersistCommand.GetPlayer && value != PersistCommand.PutPlayer)
                        throw new ArgumentOutOfRangeException(nameof(value));

                    _command = value;
                }
            }

            public Player? Player { get; set; }
            public Arena? Arena { get; set; }
            public Action<Player>? Callback { get; set; }

            public override void ExecuteCallback()
            {
                Callback?.Invoke(Player!);
            }

            protected override void Dispose(bool isDisposing)
            {
                base.Dispose(isDisposing);

                if (isDisposing)
                {
                    Player = null;
                    Arena = null;
                    Callback = null;
                }
            }
        }

        private class PutAllWorkItem : PersistWorkItem
        {
            public override PersistCommand Command
            {
                get { return _command; }
                set
                {
                    if (value != PersistCommand.PutAll)
                        throw new ArgumentOutOfRangeException(nameof(value));

                    _command = value;
                }
            }

            public Action? Callback { get; set; }

            public override void ExecuteCallback()
            {
                Callback?.Invoke();
            }

            protected override void Dispose(bool isDisposing)
            {
                base.Dispose(isDisposing);

                if (isDisposing)
                {
                    Callback = null;
                }
            }
        }

        private class IntervalWorkItem : PersistWorkItem
        {
            public override PersistCommand Command
            {
                get { return _command; }
                set
                {
                    if (value != PersistCommand.EndInterval)
                        throw new ArgumentOutOfRangeException(nameof(value));

                    _command = value;
                }
            }

            public PersistInterval Interval { get; set; }
            public string? ArenaGroup { get; set; }

            protected override void Dispose(bool isDisposing)
            {
                base.Dispose(isDisposing);

                if (isDisposing)
                {
                    ArenaGroup = null;
                    Interval = default;
                }
            }
        }

        private class ResetGameIntervalWorkItem : PersistWorkItem
        {
            public override PersistCommand Command
            {
                get { return _command; }
                set
                {
                    if (value != PersistCommand.ResetGameInterval)
                        throw new ArgumentOutOfRangeException(nameof(value));

                    _command = value;
                }
            }

            public Arena? Arena { get; set; }

            public Action<Arena>? Callback { get; set; }

            public override void ExecuteCallback()
            {
                Callback?.Invoke(Arena!);
            }

            protected override void Dispose(bool isDisposing)
            {
                base.Dispose(isDisposing);

                if (isDisposing)
                {
                    Arena = null;
                    Callback = null;
                }
            }
        }

        #endregion

        private class MemoryStreamPooledObjectPolicy : IPooledObjectPolicy<MemoryStream>
        {
            public MemoryStream Create()
            {
                return new MemoryStream(_maxRecordLength);
            }

            public bool Return(MemoryStream obj)
            {
                if (obj is null)
                    return false;

                obj.Position = 0;
                obj.SetLength(0);
                return true;
            }
        }
    }
}
