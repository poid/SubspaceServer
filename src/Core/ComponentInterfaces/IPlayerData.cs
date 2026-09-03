using Microsoft.Extensions.ObjectPool;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SS.Core.ComponentInterfaces
{
    public interface IPlayerData : IComponentInterface
    {
        /// <summary>
        /// Locks the global player lock for reading.
        /// </summary>
        /// <remarks><see cref="Unlock"/> must be called when done.</remarks>
        void Lock();

        /// <summary>
        /// Unlocks the global player lock for reading.
        /// </summary>
        void Unlock();

        /// <summary>
        /// Locks the global player lock for writing.
        /// </summary>
        /// <remarks><see cref="WriteUnlock"/> must be called when done.</remarks>
        /// <param name="caller">
        /// Filled in by the compiler. Used only to name whoever waited a long time for the lock, which
        /// is otherwise impossible to tell apart from any other caller.
        /// </param>
        void WriteLock([CallerMemberName] string? caller = null);

        /// <summary>
        /// Unlocks the global player lock for writing.
        /// </summary>
        void WriteUnlock();

        /// <summary>
        /// Whether the calling thread holds no player lock at all, in any of its forms.
        /// </summary>
        /// <remarks>
        /// For a worker to check at the top of its loop that whatever it ran last did not leave a lock
        /// behind. A leaked hold would stop every writer forever, and now that a nested acquisition is
        /// skipped when the thread already holds one, it would also let later work read player state
        /// holding nothing.
        /// <para>
        /// Unlike <see cref="IsReadLockHeld"/> this is a thread-local field read, so it is cheap enough
        /// to check on a hot loop.
        /// </para>
        /// </remarks>
        bool HoldsNoPlayerLock { get; }

        /// <summary>
        /// Whether the calling thread holds the global player lock for reading.
        /// </summary>
        /// <remarks>
        /// For asserting that a caller has taken the lock its contract requires. Not for deciding
        /// whether to take it: code that has to ask does not know who owns the data it is touching.
        /// <para>
        /// Not usable on a hot path either. It reads the lock's recursion bookkeeping, which takes that
        /// lock's internal spin lock, so testing with it and skipping costs what the skip would save.
        /// Use <see cref="HoldsNoPlayerLock"/> where the answer is needed rather than asserted.
        /// </para>
        /// </remarks>
        bool IsReadLockHeld { get; }

        /// <summary>
        /// Whether the calling thread holds the global player lock for writing.
        /// </summary>
        /// <inheritdoc cref="IsReadLockHeld" path="/remarks"/>
        bool IsWriteLockHeld { get; }

        /// <summary>
        /// Whether the calling thread holds the global player lock as an upgradeable reader.
        /// </summary>
        /// <inheritdoc cref="IsReadLockHeld" path="/remarks"/>
        bool IsUpgradeableReadLockHeld { get; }

        /// <summary>
        /// Moves a player into an arena, out of one, or between two.
        /// </summary>
        /// <remarks>
        /// The only way to change <see cref="Player.Arena"/>. It also maintains
        /// <see cref="Arena.Players"/>, and the two must move together or an arena ends up holding a
        /// player it no longer has, or missing one it does. Since the arena's collection is what the
        /// position fan out and every other arena scoped operation reads, a discrepancy shows up as
        /// players who cannot see each other.
        /// <para>
        /// The caller must hold the write lock.
        /// </para>
        /// </remarks>
        /// <param name="player">The player to move.</param>
        /// <param name="arena">The arena to move into, or <see langword="null"/> to leave.</param>
        void SetPlayerArena(Player player, Arena? arena);

        /// <summary>
        /// Use to enumerate over all of the players.
        /// </summary>
        /// <remarks>
        /// Remember to use <see cref="Lock"/> and <see cref="Unlock"/> or <see cref="WriteLock"/> and <see cref="WriteUnlock"/>.
        /// </remarks>
        Dictionary<int, Player>.ValueCollection Players { get; } // ideally this would be IEnumerable<Player>, but exposing the underlying type allows the compiler to use the enumerable struct rather than box it

        /// <summary>
        /// Creates a new player.
        /// This is called by the network modules when they get a new connection.
        /// </summary>
        /// <param name="clientType">the type of player to create</param>
        /// <returns></returns>
        Player NewPlayer(ClientType clientType);

        /// <summary>
        /// Frees memory associated with a player.
        /// This is called by the network modules when a connection has terminated.
        /// </summary>
        /// <param name="player">the player to free</param>
        void FreePlayer(Player player);

        /// <summary>
        /// Disconnects a player from the server.
        /// This does most of the work of disconnecting a player. The
        /// player's state will be transitioned to TimeWait, at which point
        /// one of the network modules must take responsibility for final cleanup.
        /// </summary>
        /// <param name="player">the player to kick</param>
        void KickPlayer(Player player);

        /// <summary>
        /// The most players allowed in one arena, or 0 when there is no limit.
        /// </summary>
        /// <remarks>
        /// Only limited when Net:UseArenaLocalPlayerIds is on, where the arena has a fixed number of
        /// slots to hand out and a player without one cannot be seen by anyone. See General:MaxPlayers.
        /// </remarks>
        int MaxArenaPlayers { get; }

        /// <summary>
        /// Finds the player with the given pid.
        /// </summary>
        /// <remarks>
        /// This is the zone-wide id, which is what the billing server, persistence and logging use. For
        /// a pid that came from a client, use <see cref="PidToPlayer(Arena, int)"/> instead: the two are
        /// the same today but differ when Net:UseArenaLocalPlayerIds is on.
        /// </remarks>
        /// <param name="pid">the pid to find</param>
        /// <returns>the player with the given pid, or null if not found</returns>
        Player? PidToPlayer(int pid);

        /// <summary>
        /// Finds the player that a client in a given arena means by a pid.
        /// </summary>
        /// <remarks>
        /// For pids that arrived from a client, which name a player as that client knows them. That is
        /// the zone-wide id normally, and a slot within the arena when Net:UseArenaLocalPlayerIds is on.
        /// <para>
        /// The result is always a player in <paramref name="arena"/>, or <see langword="null"/>. Callers
        /// that used to check the result was in the same arena no longer need to, though doing so is
        /// harmless.
        /// </para>
        /// </remarks>
        /// <param name="arena">The arena the client that sent the pid is in.</param>
        /// <param name="pid">The pid, as it appeared on the wire.</param>
        /// <returns>The player, or <see langword="null"/> if there is no such player in that arena.</returns>
        Player? PidToPlayer(Arena arena, int pid);

        /// <summary>
        /// Finds the player with the given name.
        /// The name is matched case-insensitively.
        /// </summary>
        /// <param name="name">The name to match.</param>
        /// <returns>The player with the given name, or <see langword="null"/> if not found.</returns>
        Player? FindPlayer(ReadOnlySpan<char> name);

        /// <summary>
        /// Converts an <see cref="ITarget"/> to a specific <paramref name="set"/> of players.
        /// The players represented by the <paramref name="target"/> will be added to the <paramref name="set"/>.
        /// </summary>
        /// <param name="target">A target that represents which players to add.</param>
        /// <param name="set">The set to add players to.</param>
        void TargetToSet(ITarget target, HashSet<Player> set);

        /// <summary>
        /// Converts an <see cref="ITarget"/> to a specific <paramref name="set"/> of players.
        /// The players represented by the <paramref name="target"/>, and match an optional <paramref name="predicate"/>, will be added to the <paramref name="set"/>.
        /// </summary>
        /// <param name="target">A target that represents which players to add.</param>
        /// <param name="set">The set to add players to.</param>
        /// <param name="predicate">Additional criteria that a player must match to be added.</param>
        void TargetToSet(ITarget target, HashSet<Player> set, Predicate<Player>? predicate);

        /// <summary>
        /// Allocates a slot for per-player data.
        /// </summary>
        /// <remarks>
        /// This adds an instance of <typeparamref name="T"/> in each <see cref="Player"/> object that can be accessed using <see cref="Player.TryGetExtraData{T}(PlayerDataKey{T}, out T)"/>.
        /// <para>
        /// If <typeparamref name="T"/> implements <see cref="IResettable"/>, an object pool is used.
        /// </para>
        /// <para>
        /// If <typeparamref name="T"/> implements <see cref="IDisposable"/>, objects will get disposed when they are discarded.
        /// </para>
        /// </remarks>
        /// <typeparam name="T">The type of data to store in the slot.</typeparam>
        /// <returns>A key that can be used to access the data using <see cref="Player.TryGetExtraData{T}(PlayerDataKey{T}, out T)"/>.</returns>
        PlayerDataKey<T> AllocatePlayerData<T>() where T : class, new();

        /// <summary>
        /// Allocates a slot for per-player data.
        /// </summary>
        /// <remarks>
        /// This adds an instance of <typeparamref name="T"/> in each <see cref="Player"/> object that can be accessed using <see cref="Player.TryGetExtraData{T}(PlayerDataKey{T}, out T)"/>.
        /// <para>
        /// If <typeparamref name="T"/> implements <see cref="IDisposable"/>, objects will get disposed when they are discarded.
        /// </para>
        /// </remarks>
        /// <typeparam name="T">The type of data to store in the slot.</typeparam>
        /// <param name="policy">The policy to use for object pooling.</param>
        /// <returns>A key that can be used to access the data using <see cref="Player.TryGetExtraData{T}(PlayerDataKey{T}, out T)"/>.</returns>
        /// <exception cref="ArgumentNullException">The <paramref name="policy"/> was <see langword="null"/>.</exception>
        PlayerDataKey<T> AllocatePlayerData<T>(IPooledObjectPolicy<T> policy) where T : class;

        /// <summary>
        /// Allocates a slot for per-player data using an existing object pool.
        /// </summary>
        /// <remarks>
        /// This adds an instance of <typeparamref name="T"/> in each <see cref="Player"/> object that can be accessed using <see cref="Player.TryGetExtraData{T}(PlayerDataKey{T}, out T)"/>.
        /// </remarks>
        /// <typeparam name="T">The type of data to store in the slot.</typeparam>
        /// <param name="pool">The object pool to use.</param>
        /// <returns>A key that can be used to access the data using <see cref="Player.TryGetExtraData{T}(PlayerDataKey{T}, out T)"/>.</returns>
        /// <exception cref="ArgumentNullException">The <paramref name="pool"/> was <see langword="null"/>.</exception>
        PlayerDataKey<T> AllocatePlayerData<T>(ObjectPool<T> pool) where T : class;

        /// <summary>
        /// Frees a per-player data slot.
        /// </summary>
        /// <typeparam name="T">The type of data to store in the slot.</typeparam>
        /// <param name="key">The key from <see cref="AllocatePlayerData{T}"/>.</param>
        /// <returns><see langword="true"/> if the slot for given <paramref name="key"/> was freed. <see langword="false"/> if the <paramref name="key"/> was invalid.</returns>
        bool FreePlayerData<T>(ref PlayerDataKey<T> key) where T : class;

        /// <summary>
        /// Adds a "hold" on a player, preventing it from proceeding to the next stage in the player life-cycle, until the hold is removed.
        /// </summary>
        /// <remarks>
        /// This can be used to do time-consuming work asynchronously during certain steps in the player life-cycle.
        /// It may only be used in <see cref="ComponentCallbacks.PlayerActionCallback"/> handlers,
        /// only for <see cref="PlayerAction.Connect"/> and <see cref="PlayerAction.Disconnect"/>.
        /// </remarks>
        /// <param name="player">The player to add a hold on.</param>
        void AddHold(Player player);

        /// <summary>
        /// Removes a "hold" on a player.
        /// </summary>
        /// <remarks>
        /// This must be called exactly once for each time <see cref="AddHold(Player)"/> is called.
        /// It may be called from any thread.
        /// </remarks>
        /// <param name="player">The player to remove a hold from.</param>
        void RemoveHold(Player player);
    }
}
