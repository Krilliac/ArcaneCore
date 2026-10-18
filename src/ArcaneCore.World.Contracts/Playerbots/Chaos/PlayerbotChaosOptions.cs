namespace ArcaneCore.World.Playerbots.Chaos;

/// <summary>
/// <c>World:Playerbots:Chaos</c>: adversarial server-breaker mode for managed playerbots (live through <c>.reload config</c> and
/// <c>.playerbot chaos</c>; docs/areas/playbots.md). When enabled, a bounded slice of running autonomous bots (<see cref="ActiveChaosBots"/>,
/// plus any bot explicitly assigned via <c>.playerbot chaos assign</c>) injects <see cref="ProbesPerThink"/> adversarial protocol,
/// state-machine, boundary and malformed-wire probes on each think, verifying life/combat/spell/visibility invariants after every
/// probe and capturing any unhandled handler exception into <see cref="ArcaneCore.Kernel.Diagnostics.FaultLedger"/> and Bot QA
/// without quarantining the bot.
/// </summary>
public sealed class PlayerbotChaosOptions
{
    /// <summary>The highest <see cref="ActiveChaosBots"/> accepted.</summary>
    public const int MaxActiveChaosBots = PlayerbotOptions.MaxBotsCeiling;

    /// <summary>The highest <see cref="ProbesPerThink"/> accepted.</summary>
    public const int MaxProbesPerThink = 16;

    /// <summary>
    /// Run adversarial chaos probes on designated running playerbots (off by default). Live through <c>.reload config</c> and
    /// <c>.playerbot chaos on|off</c>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How many running autonomous bots are automatically selected as chaos bots when <see cref="Enabled"/> is on (0..5000, default 4).
    /// Bots explicitly assigned with <c>.playerbot chaos assign</c> always run probes while <see cref="Enabled"/> is on, even when this
    /// is 0. Live through <c>.reload config</c>.
    /// </summary>
    public int ActiveChaosBots { get; set; } = 4;

    /// <summary>
    /// How many adversarial packet/state probes each active chaos bot executes per think (1..16, default 2). Live through
    /// <c>.reload config</c>.
    /// </summary>
    public int ProbesPerThink { get; set; } = 2;

    /// <summary>
    /// Deterministic PRNG seed for chaos probe selection and payload generation (0 uses a time-seeded sequence; any non-zero value
    /// replays the same sequence per bot; -2147483648..2147483647). Live through <c>.reload config</c>.
    /// </summary>
    public int Seed { get; set; }

    /// <summary>
    /// Include truncated, zero-length, oversized and random-byte malformed wire payloads across registered world opcodes (on by
    /// default). Clean <c>MalformedPacket</c> rejections are counted as expected; any other exception is recorded as a server fault.
    /// Live through <c>.reload config</c>.
    /// </summary>
    public bool MalformedPayloads { get; set; } = true;

    /// <summary>
    /// Include out-of-order state-machine race probes: resurrection/release while alive or dead, double loot/release, self-target and
    /// stale-GUID combat/spell/trade/vendor/quest/guild/group/mail/auction/battleground packets, and inventory slot boundary swaps
    /// (on by default). Live through <c>.reload config</c>.
    /// </summary>
    public bool StateRaces { get; set; } = true;

    /// <summary>
    /// Include movement packets with extreme orientations, boundary flags, rapid start/stop/jump/heartbeat transitions and invalid spline
    /// or transport fields (on by default). Any non-finite coordinate is rolled back if a handler fails to reject it, and a fault is
    /// recorded. Live through <c>.reload config</c>.
    /// </summary>
    public bool BoundaryMovement { get; set; } = true;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Chaos";
        if (ActiveChaosBots is < 0 or > MaxActiveChaosBots)
            throw new InvalidOperationException($"{section}: ActiveChaosBots must be 0..{MaxActiveChaosBots}.");
        if (ProbesPerThink is < 1 or > MaxProbesPerThink)
            throw new InvalidOperationException($"{section}: ProbesPerThink must be 1..{MaxProbesPerThink}.");
    }

    internal static string? CheckActiveBots(int value)
        => value is >= 0 and <= MaxActiveChaosBots ? null : $"must be 0..{MaxActiveChaosBots}";

    internal static string? CheckProbesPerThink(int value)
        => value is >= 1 and <= MaxProbesPerThink ? null : $"must be 1..{MaxProbesPerThink}";
}
