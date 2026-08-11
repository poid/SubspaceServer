using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentCallbacks;
using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace SS.Core.Modules
{
    /// <summary>
    /// Module that manages <see cref="Player"/> objects.
    /// </summary>
    [CoreModuleInfo]
    public sealed class PlayerData : IModule, IModuleLoaderAware, IPlayerData
    {
        internal readonly IComponentBroker Broker;
        private readonly IConfigManager _configManager;
        private readonly ILogManager _logManager;
        private readonly IMainloop _mainloop;
        private InterfaceRegistrationToken<IPlayerData>? _iPlayerDataToken;

        /// <summary>
        /// Whether the ids clients see are per arena rather than zone-wide.
        /// </summary>
        private bool _useArenaLocalPlayerIds;

        /// <summary>
        /// The most slots an arena may hand out, which is the highest arena-local id plus one.
        /// </summary>
        private int _maxArenaSlots;

        /// <summary>
        /// The hard ceiling on arena slots.
        /// </summary>
        /// <remarks>
        /// Set by the wire formats rather than chosen. The compact position packet carries the id in a
        /// single byte, so an id above 255 costs a packet that is twice the size, which is the entire
        /// reason for having arena-local ids at all. Allowing a cap above this would defeat it.
        /// </remarks>
        public const int MaxArenaSlotCeiling = 256;

        /// <summary>
        /// How long a freed arena slot is left alone before it can be given to someone else.
        /// </summary>
        /// <remarks>
        /// Position packets are unreliable and one can still be in flight when its sender leaves. Handing
        /// their slot straight to the next arrival would have that packet arrive naming someone else, and
        /// a stray ship appear for a frame. Two seconds is far longer than any packet survives.
        /// <para>
        /// Related to <see cref="PlayerReuseDelay"/>, which does the same for zone-wide ids.
        /// </para>
        /// </remarks>
        private static readonly TimeSpan ArenaSlotQuarantine = TimeSpan.FromSeconds(2);

        // Optional dependencies
        private IServerMetrics? _serverMetrics;

#if DEBUG
        // For the debug-only audit of the arena player collections. Retrieved in PostLoad, since
        // ArenaManager loads after this module.
        private IArenaManager? _auditArenaManager;
        private IMainloopTimer? _auditMainloopTimer;
#endif

        /// <summary>
        /// How long after a PlayerID can be reused after it is freed.
        /// </summary>
        private readonly static TimeSpan PlayerReuseDelay = TimeSpan.FromSeconds(10);

        private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.SupportsRecursion);

        /// <summary>
        /// When the current thread took the outermost write lock, for measuring how long it is held.
        /// </summary>
        [ThreadStatic]
        private static long t_writeLockTimestamp;

        /// <summary>
        /// The next available PlayerId for when a <see cref="Player"/> object needs to be allocated.
        /// </summary>
        private int _nextPlayerId = 0;

        /// <summary>
        /// Dictionary of active players.
        /// </summary>
        /// <remarks>Key =  PlayerId</remarks>
        private readonly Dictionary<int, Player> _playerDictionary = new(Constants.TargetPlayerCount);

        /// <summary>
        /// Players queued to be freed that are in a state where they've been removed from the <see cref="_playerDictionary"/>, but are not yet in the <see cref="_freePlayersQueue"/>.
        /// Player objects in this collection still have their extra data.
        /// </summary>
        private readonly HashSet<Player> _playersBeingFreed = new(Constants.TargetPlayerCount);

        /// <summary>
        /// Queue of unused player objects and when each becomes available again.
        /// This is used to keep track when of the PlayerIds, and associated <see cref="Player"/> object, can be reused.
        /// Player objects in this collection have had their extra data removed.
        /// </summary>
        private readonly Queue<FreePlayerInfo> _freePlayersQueue = new(Constants.TargetPlayerCount);

        // For managing per player data
        private readonly SortedList<int, ExtraDataFactory> _extraDataRegistrations = new(Constants.TargetPlayerExtraDataCount);
        private readonly DefaultObjectPoolProvider _poolProvider = new() { MaximumRetained = Constants.TargetPlayerCount };

        // Cached delegates
        private readonly Action<Player> _mainloopWork_FireNewPlayerCallback;
        private readonly Action<Player> _mainloopWork_CompleteFreePlayer;
        private readonly Action<Player> _mainloopWork_KickPlayer;

        public PlayerData(
            IComponentBroker broker,
            IConfigManager configManager,
            ILogManager logManager,
            IMainloop mainloop)
        {
            Broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));

            _mainloop = mainloop ?? throw new ArgumentNullException(nameof(mainloop));
            _mainloopWork_FireNewPlayerCallback = MainloopWork_FireNewPlayerCallback;
            _mainloopWork_CompleteFreePlayer = MainloopWork_CompleteFreePlayer;
            _mainloopWork_KickPlayer = MainloopWork_KickPlayer;
        }

        #region Module Members

        [ConfigHelp<bool>("Net", "UseArenaLocalPlayerIds", ConfigScope.Global, Default = false,
            Description = """
                Whether the player ids sent to clients are per arena rather than zone-wide.
                Zone-wide ids are what this server has always sent and what ASSS sends. They cost
                bandwidth once a zone gets large: the compact position packet carries the id in one byte
                and the batched one in ten bits, so above 1023 players online every position update falls
                back to a packet roughly twice the size. Per arena ids keep every id small no matter how
                many are online, which measured at roughly half the outbound bandwidth.
                This changes what clients are told, so it is off by default. Turn it on only after
                checking a real client against it. The common paths have been checked against Continuum
                0.40 by hand; what has not is anything needing a second real client to see, such as
                banners and crowns. See General:MaxPlayers for the cap it needs.
                """)]
        [ConfigHelp<int>("General", "MaxPlayers", ConfigScope.Global, Default = 240, Min = 1, Max = MaxArenaSlotCeiling,
            Description = """
                The most players allowed in one arena when Net:UseArenaLocalPlayerIds is on.
                It cannot exceed 256, because that is where the compact position packet's one byte id
                runs out and the saving disappears. The default leaves a gap below that so a slot freed
                by someone leaving can be left alone briefly before being reused, which stops a position
                packet still in flight from arriving named after whoever took the slot next.
                """)]
        bool IModule.Load(IComponentBroker broker)
        {
            _serverMetrics = broker.GetInterface<IServerMetrics>();

            _useArenaLocalPlayerIds = _configManager.GetBool(_configManager.Global, "Net", "UseArenaLocalPlayerIds", ConfigHelp.Constants.Global.Net.UseArenaLocalPlayerIds.Default);

            _maxArenaSlots = _configManager.GetInt(_configManager.Global, "General", "MaxPlayers", ConfigHelp.Constants.Global.General.MaxPlayers.Default);
            _maxArenaSlots = Math.Clamp(_maxArenaSlots, 1, MaxArenaSlotCeiling);

            if (_useArenaLocalPlayerIds)
            {
                _logManager.LogM(LogLevel.Info, nameof(PlayerData), $"Player ids sent to clients are per arena, capped at {_maxArenaSlots} per arena. See Net:UseArenaLocalPlayerIds.");
            }

            _iPlayerDataToken = broker.RegisterInterface<IPlayerData>(this);
            return true;
        }

#if DEBUG
        /// <summary>
        /// How often the arena player collections are audited, in debug builds.
        /// </summary>
        private const int AuditIntervalMs = 5000;

        void IModuleLoaderAware.PostLoad(IComponentBroker broker)
        {
            // Retrieved here rather than in Load, since ArenaManager loads after this module.
            _auditArenaManager = broker.GetInterface<IArenaManager>();
            _auditMainloopTimer = broker.GetInterface<IMainloopTimer>();

            _auditMainloopTimer?.SetTimer(MainloopTimer_AuditArenaPlayers, AuditIntervalMs, AuditIntervalMs, null);
        }

        void IModuleLoaderAware.PreUnload(IComponentBroker broker)
        {
            _auditMainloopTimer?.ClearTimer(MainloopTimer_AuditArenaPlayers, null);

            if (_auditArenaManager is not null)
            {
                broker.ReleaseInterface(ref _auditArenaManager);
            }

            if (_auditMainloopTimer is not null)
            {
                broker.ReleaseInterface(ref _auditMainloopTimer);
            }
        }

        private bool MainloopTimer_AuditArenaPlayers()
        {
            if (_auditArenaManager is null)
                return true;

            Lock();

            try
            {
                AuditArenaPlayers(_auditArenaManager);
            }
            finally
            {
                Unlock();
            }

            return true;
        }
#else
        void IModuleLoaderAware.PostLoad(IComponentBroker broker)
        {
        }

        void IModuleLoaderAware.PreUnload(IComponentBroker broker)
        {
        }
#endif

        bool IModule.Unload(IComponentBroker broker)
        {
            if (broker.UnregisterInterface(ref _iPlayerDataToken) != 0)
                return false;

            if (_serverMetrics is not null)
            {
                broker.ReleaseInterface(ref _serverMetrics);
            }

            return true;
        }

        #endregion

        #region IPlayerData Members

        #region Locks

        public void Lock()
        {
            _rwLock.EnterReadLock();

            _serverMetrics?.Increment(ServerMetric.PlayerDataReadLocks);
        }

        public void Unlock()
        {
            _rwLock.ExitReadLock();
        }

        public void WriteLock()
        {
            _rwLock.EnterWriteLock();

            IServerMetrics? serverMetrics = _serverMetrics;
            if (serverMetrics is not null && serverMetrics.IsEnabled)
            {
                serverMetrics.Increment(ServerMetric.PlayerDataWriteLocks);

                // The lock is recursive, so only time the outermost acquisition.
                if (_rwLock.RecursiveWriteCount == 1)
                    t_writeLockTimestamp = Stopwatch.GetTimestamp();
            }
        }

        public void WriteUnlock()
        {
            IServerMetrics? serverMetrics = _serverMetrics;
            if (serverMetrics is not null && serverMetrics.IsEnabled && _rwLock.RecursiveWriteCount == 1)
            {
                serverMetrics.Add(ServerMetric.PlayerDataWriteLockHeldTicks, Stopwatch.GetTimestamp() - t_writeLockTimestamp);
            }

            _rwLock.ExitWriteLock();
        }

        public bool IsReadLockHeld => _rwLock.IsReadLockHeld;

        public bool IsWriteLockHeld => _rwLock.IsWriteLockHeld;

        public bool IsUpgradeableReadLockHeld => _rwLock.IsUpgradeableReadLockHeld;

        /// <summary>
        /// Moves a player into an arena, out of one, or between two, keeping each arena's player
        /// collection in step with <see cref="Player.Arena"/>.
        /// </summary>
        /// <remarks>
        /// The single place <see cref="Player.Arena"/> changes. The two have to move together, under the
        /// write lock, or an arena ends up holding a player it no longer has or missing one it does —
        /// and the arena collection is what the position fan out and every other arena scoped operation
        /// reads, so a discrepancy shows up as players who cannot see each other.
        /// </remarks>
        /// <param name="player">The player to move.</param>
        /// <param name="arena">The arena to move into, or <see langword="null"/> to leave.</param>
        /// <summary>
        /// Checks, in debug builds, that every arena's player collection matches what the players
        /// themselves say, in both directions.
        /// </summary>
        /// <remarks>
        /// The collection is derived state, and derived state drifts. It is maintained in one place,
        /// but a player can leave through several routes — a normal arena change, a disconnect, a kick
        /// from a network thread, an arena being recycled — and any route that misses the update leaves
        /// a player who is in an arena's list but not in the arena, or the reverse. Neither is visible
        /// until players mysteriously cannot see each other. This turns that into an immediate failure.
        /// <para>
        /// The caller must hold the player lock.
        /// </para>
        /// </remarks>
        [Conditional("DEBUG")]
        internal void AuditArenaPlayers(IArenaManager arenaManager)
        {
            ThreadingAssert.PlayerLockHeld(this);

            // Every player claiming an arena must be in that arena's collection.
            foreach (Player player in _playerDictionary.Values)
            {
                Arena? arena = player.Arena;
                if (arena is null)
                    continue;

                bool found = false;
                foreach (Player other in arena.Players)
                {
                    if (ReferenceEquals(other, player))
                    {
                        found = true;
                        break;
                    }
                }

                Debug.Assert(found, $"Player '{player.Name}' says it is in arena '{arena.Name}', but that arena does not list it.");
            }

            // And nothing else may be in a collection: not a player who has moved on, and not one that
            // has been freed.
            arenaManager.Lock();

            try
            {
                foreach (Arena arena in arenaManager.Arenas)
                {
                    foreach (Player player in arena.Players)
                    {
                        Debug.Assert(ReferenceEquals(player.Arena, arena), $"Arena '{arena.Name}' lists player '{player.Name}', which is not in it.");
                        Debug.Assert(_playerDictionary.ContainsKey(player.Id), $"Arena '{arena.Name}' lists player '{player.Name}', which has been freed.");

                        // The slot table is a second structure keyed the same way as the player list, so
                        // it is exactly the kind of thing that drifts. What must hold is that a slot
                        // points back at the player claiming it: if it does not, two players share an id
                        // and clients see one of them as the other.
                        //
                        // Not asserted: that a player has a slot at all. Real players are turned away
                        // when an arena is full, but fake players are put into an arena directly and can
                        // exceed the cap. That is reported as an error where it happens, and leaves the
                        // fake player unseen rather than impersonating anyone.
                        if (_useArenaLocalPlayerIds && player.ArenaSlot >= 0)
                        {
                            Debug.Assert(ReferenceEquals(arena.PlayerFromSlot(player.ArenaSlot), player), $"Arena '{arena.Name}' slot {player.ArenaSlot} does not hold '{player.Name}', which claims it.");
                            Debug.Assert(player.Packet.PlayerId == player.ArenaSlot, $"Player '{player.Name}' has slot {player.ArenaSlot} but its entering packet says {player.Packet.PlayerId}.");
                        }
                    }
                }
            }
            finally
            {
                arenaManager.Unlock();
            }
        }

        public void SetPlayerArena(Player player, Arena? arena)
        {
            ThreadingAssert.PlayerWriteLockHeld(this);

            Arena? current = player.Arena;
            if (current == arena)
                return;

            // The slot is given up and taken here, with the arena membership, so that the two cannot
            // disagree: a player is in an arena's player list exactly when they hold a slot in it. That
            // is one invariant for the auditor to check rather than two that could drift apart.
            if (current is not null)
            {
                current.ReleaseSlot(player);
                current.RemovePlayer(player);
            }

            player.SetArena(arena);

            if (arena is not null)
            {
                arena.AddPlayer(player);

                if (_useArenaLocalPlayerIds)
                {
                    short slot = arena.AllocateSlot(player, _maxArenaSlots, ArenaSlotQuarantine);

                    if (slot < 0)
                    {
                        // Every slot taken. The arena is over its cap, which the entry path is supposed
                        // to have stopped, so say so rather than letting the player be invisible.
                        _logManager.LogP(LogLevel.Error, nameof(PlayerData), player,
                            $"No arena slot free in '{arena.Name}' (cap {_maxArenaSlots}); the player will not be visible to others.");
                    }

                    player.SetArenaSlot(slot);
                }
                else
                {
                    player.SetArenaSlot((short)player.Id);
                }
            }
            else
            {
                player.SetArenaSlot(-1);
            }
        }

        #endregion

        Dictionary<int, Player>.ValueCollection IPlayerData.Players
        {
            get
            {
                // Enumerating this while another thread adds or removes a player is undefined, so the
                // lock is not advisory. It is only ever taken by convention today, which is why this
                // asserts rather than trusting the caller.
                ThreadingAssert.PlayerLockHeld(this);

                return _playerDictionary.Values;
            }
        }

        Player IPlayerData.NewPlayer(ClientType clientType)
        {
            Player player;

            WriteLock();

            try
            {
                if (_freePlayersQueue.TryPeek(out FreePlayerInfo free)
                    && DateTime.UtcNow > free.AvailableTimestamp)
                {
                    // Reuse an existing player object that is now available
                    free = _freePlayersQueue.Dequeue();
                    player = free.Player;
                }
                else
                {
                    // No available player objects, create a new one
                    player = new Player(_nextPlayerId++, this);
                }

                // Set player info
                player.Status = PlayerState.Uninitialized;
                player.Type = clientType;
                SetPlayerArena(player, null);
                player.NewArena = null;
                player.Ship = ShipType.Spec;
                player.Attached = -1;
                player.ConnectTime = DateTime.UtcNow;
                player.ConnectAs = null;

                // Initialize the player's per player data
                foreach ((int keyId, ExtraDataFactory factory) in _extraDataRegistrations)
                {
                    player.SetExtraData(keyId, factory.Get());
                }

                _playerDictionary.Add(player.Id, player);
            }
            finally
            {
                WriteUnlock();
            }

            _mainloop.QueueMainWorkItem(_mainloopWork_FireNewPlayerCallback, player);

            return player;
        }

        private void MainloopWork_FireNewPlayerCallback(Player player)
        {
            NewPlayerCallback.Fire(Broker, player, true);
        }

        void IPlayerData.FreePlayer(Player player)
        {
            if (player is null)
                return;

            WriteLock();

            try
            {
                // Remove the player from the active players.
                if (!_playerDictionary.Remove(player.Id))
                    return;

                // And from its arena, here rather than where Player.Arena is cleared. Arena is not
                // cleared until the queued work below runs, but the player disappears from the
                // dictionary immediately, so tying the two together would leave the player visible in
                // the arena for a mainloop hop after it had gone from everywhere else — a ghost that
                // other players would still be sent updates about.
                SetPlayerArena(player, null);

                // Add the player to the players being freed.
                if (!_playersBeingFreed.Add(player))
                    return;

                // Queue the player to be freed.
                // Note: Queueing it to run on the mainloop thread also has the beneficial side effect
                // of allowing any other already queued mainloop work items on the player to complete first.
                _mainloop.QueueMainWorkItem(_mainloopWork_CompleteFreePlayer, player);
            }
            finally
            {
                WriteUnlock();
            }
        }

        private void MainloopWork_CompleteFreePlayer(Player player)
        {
            // First, execute callbacks.
            // This allows other modules to perform cleanup, including their extra player data.
            NewPlayerCallback.Fire(Broker, player, false);

            // Next, do the "freeing".
            WriteLock();

            try
            {
                if (!_playersBeingFreed.Remove(player))
                    return;

                // Remove the extra player data.
                foreach ((int keyId, ExtraDataFactory factory) in _extraDataRegistrations)
                {
                    if (player.TryRemoveExtraData(keyId, out object? data))
                    {
                        factory.Return(data);
                    }
                }

                // Reset the player's data back to their initial values.
                player.Initialize();

                // Add the player to the queue, "freeing" it to be reused after a configured amount of time.
                _freePlayersQueue.Enqueue(new FreePlayerInfo(player));
            }
            finally
            {
                WriteUnlock();
            }
        }

        void IPlayerData.KickPlayer(Player player)
        {
            if (player is null)
                return;

            // Kicking changes gameplay state: it drives the player out of its arena, which runs the
            // leave-arena callbacks, and sets where the state machine goes next. Most callers are on
            // the mainloop, but the network receive, reliable and send threads all kick on lagout or
            // disconnect, and the chat thread does the same. Doing that work on those threads makes
            // them writers of state the mainloop otherwise owns alone, which is the thing that has to
            // stop before arenas can be given to worker threads.
            //
            // Deferring is safe: the state machine's leave handling is a no-op for a player already on
            // the way out, so a player kicked twice before the first one runs is fine, and connection
            // teardown was already asynchronous.
            if (!_mainloop.IsMainloop)
            {
                _mainloop.QueueMainWorkItem(_mainloopWork_KickPlayer, player);
                return;
            }

            KickPlayerOnMainloop(player);
        }

        private void MainloopWork_KickPlayer(Player player)
        {
            KickPlayerOnMainloop(player);
        }

        private void KickPlayerOnMainloop(Player player)
        {
            ThreadingAssert.OnMainloop(_mainloop);

            WriteLock();

            try
            {
                // This will set state to PlayerState.LeavingArena, if it was anywhere above PlayerState.LoggedIn
                if (player.Arena is not null)
                {
                    IArenaManagerInternal? aman = Broker.GetInterface<IArenaManagerInternal>();
                    if (aman is not null)
                    {
                        try
                        {
                            aman.LeaveArena(player);
                        }
                        finally
                        {
                            Broker.ReleaseInterface(ref aman);
                        }
                    }
                }

                // Set this special flag so that the player will be set to leave
                // the zone when the PlayerState.LeavingArena-initiated actions are completed
                player.WhenLoggedIn = PlayerState.LeavingZone;
            }
            finally
            {
                WriteUnlock();
            }
        }

        int IPlayerData.MaxArenaPlayers => _useArenaLocalPlayerIds ? _maxArenaSlots : 0;

        Player? IPlayerData.PidToPlayer(int pid)
        {
            Lock();

            try
            {
                _playerDictionary.TryGetValue(pid, out Player? player);
                return player;
            }
            finally
            {
                Unlock();
            }
        }

        Player? IPlayerData.PidToPlayer(Arena arena, int pid)
        {
            ArgumentNullException.ThrowIfNull(arena);

            Lock();

            try
            {
                if (_useArenaLocalPlayerIds)
                    return arena.PlayerFromSlot(pid);

                // Zone-wide ids, so the pid names anyone in the zone. Callers want a player in this
                // arena, and every one of them checked that for itself before this overload existed,
                // so the check belongs here now rather than repeated at each of them.
                _playerDictionary.TryGetValue(pid, out Player? player);
                return player is not null && player.Arena == arena ? player : null;
            }
            finally
            {
                Unlock();
            }
        }

        Player? IPlayerData.FindPlayer(ReadOnlySpan<char> name)
        {
            name = name.Trim();

            Lock();

            try
            {
                foreach (Player player in _playerDictionary.Values)
                {
                    if (name.Equals(player.Name, StringComparison.OrdinalIgnoreCase)
                        // this is a sort of hackish way of not returning players who are on their way out.
                        && player.Status < PlayerState.LeavingZone
                        && player.WhenLoggedIn < PlayerState.LeavingZone)
                    {
                        return player;
                    }
                }
            }
            finally
            {
                Unlock();
            }

            return null;
        }

        void IPlayerData.TargetToSet(ITarget target, HashSet<Player> set)
        {
            ((IPlayerData)this).TargetToSet(target, set, null);
        }

        void IPlayerData.TargetToSet(ITarget target, HashSet<Player> set, Predicate<Player>? predicate)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(set);

            switch (target.Type)
            {
                case TargetType.Player:
                    if (target.TryGetPlayerTarget(out Player? targetPlayer) && (predicate is null || predicate(targetPlayer)))
                    {
                        set.Add(targetPlayer);
                    }
                    return;

                case TargetType.Arena:
                case TargetType.Freq:
                case TargetType.Zone:
                    Lock();
                    try
                    {
                        foreach (Player player in _playerDictionary.Values)
                        {
                            if ((player.Status == PlayerState.Playing) && Matches(target, player, predicate))
                                set.Add(player);
                        }
                    }
                    finally
                    {
                        Unlock();
                    }
                    return;

                case TargetType.Set:
                    if (target is ISetTarget setTarget && setTarget.Players is not null)
                    {
                        if (predicate is null)
                        {
                            set.UnionWith(((ISetTarget)target).Players);
                        }
                        else
                        {
                            foreach (Player player in setTarget.Players)
                            {
                                if (predicate(player))
                                    set.Add(player);
                            }
                        }
                    }
                    return;

                case TargetType.None:
                default:
                    return;
            }

            static bool Matches(ITarget target, Player player, Predicate<Player>? predicate)
            {
                if (target is null || player is null)
                    return false;

                return target.Type switch
                {
                    TargetType.Arena => player.Arena == ((IArenaTarget)target).Arena && (predicate is null || predicate(player)),
                    TargetType.Freq => (target is ITeamTarget teamTarget) && (player.Arena == teamTarget.Arena) && (player.Freq == teamTarget.Freq) && (predicate is null || predicate(player)),
                    TargetType.Zone => predicate is null || predicate(player),
                    _ => false,
                };
            }
        }

        PlayerDataKey<T> IPlayerData.AllocatePlayerData<T>()
        {
            // Only use of a pool of T objects if there's a way for the objects to be [re]initialized.
            if (typeof(T).IsAssignableTo(typeof(IResettable)))
                return new PlayerDataKey<T>(AllocatePlayerData(() => new DefaultPooledExtraDataFactory<T>(_poolProvider)));
            else
                return new PlayerDataKey<T>(AllocatePlayerData(() => new NonPooledExtraDataFactory<T>()));
        }

        PlayerDataKey<T> IPlayerData.AllocatePlayerData<T>(IPooledObjectPolicy<T> policy) where T : class
        {
            ArgumentNullException.ThrowIfNull(policy);

            // It's the policy's job to clear/reset an object when it's returned to the pool.
            return new PlayerDataKey<T>(AllocatePlayerData(() => new CustomPooledExtraDataFactory<T>(_poolProvider, policy)));
        }

        PlayerDataKey<T> IPlayerData.AllocatePlayerData<T>(ObjectPool<T> pool) where T : class
        {
            ArgumentNullException.ThrowIfNull(pool);

            // It's the policy's job to clear/reset an object when it's returned to the pool.
            return new PlayerDataKey<T>(AllocatePlayerData(() => new CustomPooledExtraDataFactory<T>(pool)));
        }

        private int AllocatePlayerData(Func<ExtraDataFactory> createExtraDataFactoryFunc)
        {
            ArgumentNullException.ThrowIfNull(createExtraDataFactoryFunc);

            WriteLock();

            try
            {
                //
                // Register
                //

                int keyId;

                // Find the next available extra data key.
                for (keyId = 1; keyId <= _extraDataRegistrations.Keys.Count; keyId++)
                {
                    if (_extraDataRegistrations.Keys[keyId - 1] != keyId)
                        break;
                }

                ExtraDataFactory factory = createExtraDataFactoryFunc();
                _extraDataRegistrations[keyId] = factory;

                //
                // Add the data to each player.
                //

                foreach (Player player in _playerDictionary.Values)
                {
                    player.SetExtraData(keyId, factory.Get());
                }

                return keyId;
            }
            finally
            {
                WriteUnlock();
            }
        }

        bool IPlayerData.FreePlayerData<T>(ref PlayerDataKey<T> key)
        {
            if (key.Id == 0)
            {
                _logManager.LogM(LogLevel.Warn, nameof(ArenaManager), "There was an attempt to FreeArenaData with an uninitialized key (Id = 0).");
                return false;
            }

            WriteLock();

            try
            {
                //
                // Unregister
                //

                if (!_extraDataRegistrations.Remove(key.Id, out ExtraDataFactory? factory))
                    return false;

                //
                // Remove the data from every player
                //

                foreach (Player player in _playerDictionary.Values)
                {
                    if (player.TryRemoveExtraData(key.Id, out object? data))
                    {
                        factory.Return(data);
                    }
                }

                foreach (Player player in _playersBeingFreed)
                {
                    if (player.TryRemoveExtraData(key.Id, out object? data))
                    {
                        factory.Return(data);
                    }
                }

                factory.Dispose();
            }
            finally
            {
                WriteUnlock();
            }

            key = new(0);
            return true;
        }

        void IPlayerData.AddHold(Player player)
        {
            WriteLock();
            try
            {
                switch (player.Status)
                {
                    case PlayerState.WaitConnectHolds:
                    case PlayerState.WaitEnterArenaHolds:
                    case PlayerState.WaitLeaveArenaHolds:
                    case PlayerState.WaitDisconnectHolds:
                        player.Holds++;
                        break;

                    default:
                        _logManager.LogP(LogLevel.Error, nameof(PlayerData), player, $"{nameof(IPlayerData.AddHold)} called from invalid state {player.Status}.");
                        break;
                }
            }
            finally
            {
                WriteUnlock();
            }
        }

        void IPlayerData.RemoveHold(Player player)
        {
            WriteLock();
            try
            {
                switch (player.Status)
                {
                    case PlayerState.WaitConnectHolds:
                    case PlayerState.WaitEnterArenaHolds:
                    case PlayerState.WaitLeaveArenaHolds:
                    case PlayerState.WaitDisconnectHolds:
                        if (player.Holds > 0)
                        {
                            player.Holds--;
                        }
                        else
                        {
                            _logManager.LogP(LogLevel.Error, nameof(PlayerData), player, $"{nameof(IPlayerData.RemoveHold)} called too many times when in status {player.Status}. This is indicates a programming bug and needs to be investigated.");
                        }

                        break;

                    default:
                        _logManager.LogP(LogLevel.Error, nameof(PlayerData), player, $"{nameof(IPlayerData.RemoveHold)} called from invalid state {player.Status}.");
                        break;
                }
            }
            finally
            {
                WriteUnlock();
            }
        }

        #endregion

        #region Helper Types

        private readonly struct FreePlayerInfo(Player player)
        {
            /// <summary>
            /// The time the associated player object (predominantly its Id) will be available.
            /// </summary>
            public readonly DateTime AvailableTimestamp = DateTime.UtcNow + PlayerReuseDelay;

            /// <summary>
            /// The player object
            /// </summary>
            public readonly Player Player = player;
        }

        #endregion
    }
}
