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

    public uint Map { get; init; }

    public float X { get; init; }

    public float Y { get; init; }

    public float Z { get; init; }

    public IReadOnlyList<uint> Attackers { get; init; } = [];

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

    /// <summary>The log line with the bot id and name removed.</summary>
    public string Message { get; init; } = string.Empty;
}
