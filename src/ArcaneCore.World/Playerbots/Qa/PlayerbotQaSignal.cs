namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>What a bot QA signal is about.</summary>
internal enum QaSignalKind
{
    /// <summary>A living bot made no progress for the stall bound (<see cref="PlayerbotStallWatch"/>).</summary>
    Stall,

    /// <summary>Enough bots stalled on one quest or entry that every bot sets it aside.</summary>
    SharedSetAside,

    /// <summary>A bot keeps dying within a few yards of one place.</summary>
    DeathLoop,

    /// <summary>A bot's update threw.</summary>
    ActionFault,

    /// <summary>A bot was quarantined after a fault.</summary>
    Quarantine,

    /// <summary>A bot was disabled after too many faults.</summary>
    Disabled,

    /// <summary>A relay script step needs data (a waypoint path) the content does not have.</summary>
    RelayDataMissing,

    /// <summary>A creature asked for a relay script that does not exist.</summary>
    RelayScriptMissing,

    /// <summary>
    /// A bot's spell reached something the spell system does not implement (an effect or aura type with no handler, a DUMMY or SCRIPT_EFFECT
    /// that no script claims, an implicit target it cannot select), or the start-up coverage found such a gap in a spell bots can cast
    /// (<see cref="QaSignal.FromCoverage"/>). Bot-independent: one occurrence is enough.
    /// </summary>
    SpellUnsupported,

    /// <summary>The server refused a bot's cast: a suspicious result at once, an expected one (no mana, cooldown, range) only when one bot keeps repeating it.</summary>
    SpellRefused,

    /// <summary>A bot's completed casts of a spell that should deal damage, heal or apply an aura repeatedly did none of it on any target hit.</summary>
    SpellNoEffect,

    /// <summary>
    /// An exception the server caught and survived at one of its catch-and-log sites (<see cref="ArcaneCore.Kernel.Diagnostics.FaultLedger"/>), one finding per ledger signature.
    /// Bot-independent: counted in occurrences only, so a fault no bot caused is still reported.
    /// </summary>
    ServerFault,

    /// <summary>
    /// A dungeon or raid run ended without finishing: the group gave up (no path, too few members, the leader left or stopped, out of wipes) or a gather,
    /// travel, enter or run step timed out (<see cref="ArcaneCore.World.Playerbots.Groups.PlayerbotGroupCoordinator"/>). Bot-independent: identified by the
    /// instance map, wing, stage, boss and reason, never by the group.
    /// </summary>
    RunFailed,

    /// <summary>
    /// The world tick hung (<see cref="ArcaneCore.Kernel.Ops.Watchdog.TickMonitor"/>, log event 7102): identified by the tick phase and the kind of work
    /// running in it, never by a duration. Bot-independent; one occurrence is enough.
    /// </summary>
    TickHang,

    /// <summary>
    /// A bot skipped a goal whose place no walk reaches from where it stands (the map's walking region graph puts the two in different
    /// components, <c>PlayerbotNavigation.IsUnreachableOnFoot</c>) instead of walking toward it. One line per entry and bot per window,
    /// so a spawn on ground the navigation mesh does not join to the rest (an island platform, a mesh gap) surfaces as content or mesh work.
    /// </summary>
    UnreachableGoal,
}

/// <summary>
/// One observation, parsed from a warning log line. Carries no bot name: <see cref="BotKey"/> is the bot's id (a random guid), used only to count
/// distinct bots, and the message has the id and the name removed.
/// </summary>
internal sealed record QaSignal
{
    public required QaSignalKind Kind { get; init; }

    public DateTime AtUtc { get; init; }

    public Guid? BotKey { get; init; }

    /// <summary>For a shared set-aside: how many bots stalled (no bot ids are logged then).</summary>
    public int BotCount { get; init; }

    public string Goal { get; init; } = string.Empty;

    public uint Target { get; init; }

    public uint Quest { get; init; }

    public bool HasPosition { get; init; }

    /// <summary>
    /// For a stall: the bot stood outside its home area (the line ends <c>it gives up that goal outside its home</c>). Such a stall set
    /// nothing aside for other bots, and is an expected one for a homed bot (<see cref="QaCause.OutsideHome"/>).
    /// </summary>
    public bool OutsideHome { get; init; }

    public uint Map { get; init; }

    /// <summary>A stall of the walk home to a completed quest's ender (the line carries <see cref="PlayerbotQaSignalParser.ReturnTripToken"/>).</summary>
    public bool ReturnTrip { get; init; }

    public float X { get; init; }

    public float Y { get; init; }

    public float Z { get; init; }

    public IReadOnlyList<uint> Attackers { get; init; } = [];

    /// <summary>Facts captured on the world thread when a bot stalled or died; absent in historical warning lines.</summary>
    public QaBotObservation? Bot { get; init; }

    public string ExceptionType { get; init; } = string.Empty;

    /// <summary>The <c>playerbot-...</c> error code found in a fault message, if any.</summary>
    public string FaultCode { get; init; } = string.Empty;

    /// <summary>For faults: the fault text; for relay signals: what the step wanted (<c>MOVEMENT</c>, <c>TEMP_SPAWN_CREATURE</c> or the unsupported step text).</summary>
    public string Detail { get; init; } = string.Empty;

    public uint Relay { get; init; }

    public uint PathId { get; init; }

    public uint Entry { get; init; }

    /// <summary>A shared set-aside's length in minutes; zero when not applicable.</summary>
    public int Minutes { get; init; }

    /// <summary>A stall's idle seconds, or a death loop's span in seconds.</summary>
    public int Seconds { get; init; }

    /// <summary>A death loop's death count, a fault count of a quarantine.</summary>
    public int Count { get; init; }

    /// <summary>The log line with the bot id and name removed (a direct signal: a short description).</summary>
    public string Message { get; init; } = string.Empty;

    // The optional ids of a direct signal (IPlayerbotQaSink); zero or empty when the kind has none. They are part of the finding (and of the
    // fingerprint where the kind says so), never a bot's identity.

    /// <summary>The spell id (spell kinds).</summary>
    public uint Spell { get; init; }

    /// <summary>The spell's name, as the spell table has it.</summary>
    public string SpellName { get; init; } = string.Empty;

    /// <summary>The spell's rank text (<c>Rank 3</c>), empty when it has none.</summary>
    public string SpellRank { get; init; } = string.Empty;

    /// <summary>The effect index (0..2) a spell signal is about; -1 when it is about the whole spell.</summary>
    public int EffectIndex { get; init; } = -1;

    /// <summary>The effect's name (<c>ApplyAura</c>, <c>SchoolDamage</c>).</summary>
    public string EffectName { get; init; } = string.Empty;

    /// <summary>The effect's base value (BasePoints + 1).</summary>
    public int EffectValue { get; init; }

    /// <summary>The aura type number of an aura effect; zero when none.</summary>
    public uint Aura { get; init; }

    /// <summary>A machine-readable reason: the gap code of an unsupported spell (<c>effect</c>, <c>aura</c>, <c>dummy</c>, ...), the cast result of a refusal.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>An item entry (item kinds).</summary>
    public uint Item { get; init; }

    /// <summary>A creature entry an interaction is about (NPC kinds; relay signals keep using <see cref="Entry"/>).</summary>
    public uint Npc { get; init; }

    /// <summary>A game object entry an interaction is about.</summary>
    public uint GameObject { get; init; }

    /// <summary>The bot's class (1..11, <c>ArcaneCore.Game.Class</c>); zero when unknown. Shown as the classes that met a finding.</summary>
    public byte BotClass { get; init; }

    /// <summary>
    /// A static fact rather than an occurrence (the start-up spell coverage): it creates or marks the finding but adds no occurrence, no
    /// sample and no bot, so a gap no bot has met yet is listed and never counted as seen.
    /// </summary>
    public bool FromCoverage { get; init; }

    /// <summary>For a server fault: the fault ledger's signature id (16 hex), the identity of the finding.</summary>
    public string FaultId { get; init; } = string.Empty;

    /// <summary>How many occurrences this signal stands for (a polled server fault carries its delta; every other signal is one).</summary>
    public long Occurrences { get; init; } = 1;

    /// <summary>A run failure's stage (<c>travel</c>, <c>boss</c> ...) or a tick hang's phase (<c>Features</c>, <c>Maps</c> ...): a content or code identity, never a bot.</summary>
    public string Stage { get; init; } = string.Empty;

    /// <summary>The wing (segment) of the dungeon or raid a run failure is about.</summary>
    public int Segment { get; init; }
}
