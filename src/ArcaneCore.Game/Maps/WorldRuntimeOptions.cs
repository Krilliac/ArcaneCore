using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.Game.Maps;

/// <summary>Tuning for the world simulation (bound from the "World" configuration section).</summary>
public sealed class WorldRuntimeOptions
{
    /// <summary>World tick length in milliseconds (vmangos WORLD_SLEEP_CONST = 50).</summary>
    public int TickIntervalMs { get; set; } = 50;

    /// <summary>
    /// How the world thread waits for its next tick (<see cref="WorldTickTimer.Precise"/> by default). Precise wakes within a
    /// fraction of a millisecond of the due time; Legacy is the plain event wait, which on Windows wakes on the ~15.6 ms system
    /// timer (frame p50 46 / p90 61 ms on a 50 ms tick; docs/integration/perf-limits-20261008.md). Read once at world start.
    /// </summary>
    public WorldTickTimer TickTimer { get; set; } = WorldTickTimer.Precise;

    /// <summary>
    /// Milliseconds a tick may start after its due time, and a frame may exceed the tick interval, before it counts as late
    /// (<see cref="WorldTickScheduler.LateTicks"/>) or as a frame overrun (0..1000, default 2). Read once at world start.
    /// </summary>
    public int TickLateToleranceMs { get; set; } = 2;

    /// <summary>
    /// ArcaneCore hardening: the shortest time (milliseconds) between two account data or tutorial writes of one account. A change inside
    /// it is kept in memory at once and written when the interval ends, coalesced to the latest value per type (never dropped). 0 = write
    /// every change at once. vmangos writes every CMSG_UPDATE_ACCOUNT_DATA (MiscHandler.cpp HandleUpdateAccountData).
    /// </summary>
    public int AccountDataWriteIntervalMs { get; set; } = 1000;

    /// <summary>Maximum queued world commands admitted before the map pass of one tick.</summary>
    public int MaxCommandsPerTick { get; set; } = 1024;

    /// <summary>Maximum command-drain time per tick in milliseconds; 0 disables the time bound.</summary>
    public int CommandTimeBudgetMs { get; set; } = 5;

    /// <summary>
    /// Update packets larger than this many bytes are zlib-compressed into
    /// SMSG_COMPRESSED_UPDATE_OBJECT (vmangos Compression.Update.Size default 128). 0 disables.
    /// </summary>
    public int UpdateCompressionThreshold { get; set; } = 128;

    /// <summary>Periodic save of online characters, in milliseconds (vmangos PlayerSave.Interval default 900000). 0 disables.</summary>
    public int AutosaveIntervalMs { get; set; } = 15 * 60 * 1000;

    /// <summary>
    /// The character save queue warns (at most every 30 s) while this many characters or more have a snapshot waiting
    /// or being written. 0 disables the depth check. Default 1000 (docs/areas/persistence.md).
    /// </summary>
    public int SaveQueueWarnDepth { get; set; } = 1000;

    /// <summary>
    /// The character save queue warns while its oldest waiting snapshot has waited this many milliseconds. 0 disables the age
    /// check. Default 5000, the characters database's write bound (Database:SqliteWriteTimeoutMs): a snapshot that old has waited out at
    /// least one write-gate bound (5 s without a hand-off; a moving queue may keep a waiter up to 20 s) (docs/areas/persistence.md).
    /// </summary>
    public int SaveQueueWarnAgeMs { get; set; } = 5000;

    /// <summary>Characters an account may have on this realm (vmangos CharactersPerRealm: default 10, at most 10).</summary>
    public int CharactersPerRealm { get; set; } = 10;

    /// <summary>Message of the day sent as system lines at login; '@' separates lines (vmangos Motd).</summary>
    public string Motd { get; set; } = "Welcome to ArcaneCore.";

    /// <summary>
    /// Distance /say reaches. vmangos and cmangos-classic World.cpp default to 25 (cmangos'
    /// shipped mangosd.conf keeps 25; vmangos' raises it to 40). 0 = the whole map.
    /// </summary>
    public float ListenRangeSay { get; set; } = 25.0f;

    /// <summary>Distance /yell reaches (vmangos/cmangos ListenRange.Yell default 300). 0 = the whole map.</summary>
    public float ListenRangeYell { get; set; } = 300.0f;

    /// <summary>Distance /emote and text emotes reach (vmangos/cmangos ListenRange.TextEmote default 25). 0 = the whole map.</summary>
    public float ListenRangeTextEmote { get; set; } = 25.0f;

    /// <summary>
    /// Allow whispers and custom emotes across factions, and send Common/Orcish as Universal
    /// (vmangos AllowTwoSide.Interaction.Chat, default off).
    /// </summary>
    public bool AllowTwoSideChat { get; set; }

    /// <summary>Show the other faction in /who (vmangos AllowTwoSide.WhoList, default off).</summary>
    public bool AllowTwoSideWhoList { get; set; }

    /// <summary>
    /// How long a requested logout counts down before the player leaves the world (vmangos
    /// WorldSession::ShouldLogOut hard-codes 20 seconds; configurable here for tests and tuning).
    /// </summary>
    public uint LogoutDelayMs { get; set; } = Entities.Player.DefaultLogoutDelayMs;

    /// <summary>Lowest security level that logs out instantly (vmangos/cmangos InstantLogout default SEC_MODERATOR).</summary>
    public AccountSecurity InstantLogoutSecurity { get; set; } = AccountSecurity.Moderator;

    /// <summary>
    /// Highest staff level ordinary players see in /who (vmangos/cmangos GM.InWhoList.Level
    /// default SEC_ADMINISTRATOR, i.e. every account).
    /// </summary>
    public AccountSecurity GmLevelInWhoList { get; set; } = AccountSecurity.Administrator;

    /// <summary>
    /// Whether accounts without a GM level may use player commands such as .help and .save
    /// (vmangos PlayerCommands, default on).
    /// </summary>
    public bool PlayerCommands { get; set; } = true;

    /// <summary>
    /// A map updater that throws in this many consecutive ticks is skipped from then on, until
    /// <see cref="Map.ClearUpdaterFaults"/> (a code hot reload calls it after each applied edit).
    /// 0 (the default) never skips: a throwing updater is logged every tick and the others
    /// still run, as before. Only <c>World:HotCode:Enabled</c> sets it (docs/areas/code-hot-reload.md).
    /// </summary>
    public int MaxConsecutiveUpdaterFaults { get; set; }

    /// <summary>
    /// Slow-update thresholds (the top-level "PerformanceLog" section, copied in by
    /// AddWorldDaemon; vmangos mangosd.conf.dist.in:898-906). 0 disables each.
    /// </summary>
    public PerformanceLogOptions Perf { get; set; } = new();

    /// <summary>Parallel map updates (the <c>World:Parallel</c> section; docs/research/multithreading-plan-20261017.md). Read at world start.</summary>
    public ParallelMapOptions Parallel { get; set; } = new();

    /// <summary>Grid lifecycle and terrain data (the <c>World:Maps</c> section; docs/areas/grid-terrain.md).</summary>
    public MapOptions Maps { get; set; } = new();

    /// <summary>Respawn tuning (the <c>World:Respawn</c> section): population-scaled respawn delays (<c>World:Respawn:Dynamic</c>).</summary>
    public WorldRespawnOptions Respawn { get; set; } = new();
}
