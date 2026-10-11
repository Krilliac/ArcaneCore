namespace ArcaneCore.World.Playerbots;

/// <summary>
/// How a following bot catches up with a leader it cannot simply walk to (<see cref="PlayerbotFollowOptions.CatchUpMode"/>).
/// </summary>
public enum PlayerbotCatchUpMode
{
    /// <summary>
    /// The reference behaviour (default): vmangos PartyBotAI teleports a follower out of combat to a leader more than 100 yards
    /// away or on another map (<c>HandleGonameCommand</c>), and mangoszero's playerbot teleports one whose leader is within 50 yards
    /// across but 30 yards or more above or below. Besides those, a leader the follow planner cannot reach, reaches only by a way
    /// far longer than the straight line, or reaches only past creatures far above the follower's level is teleported to, as is
    /// one the follower has made no progress towards; a survivable drop straight down to a leader below is taken instead.
    /// </summary>
    Teleport,

    /// <summary>
    /// Never teleport: the follower always walks (and drops), engages the hostiles whose aggro reaches the way ahead, and walks
    /// into the entrance that leads to its leader's map; stalls are re-planned, never teleported. A follower that keeps dying
    /// stays a corpse-run follower.
    /// </summary>
    FightThrough,

    /// <summary>Teleport only when the navigation mesh offers no way to the leader (no route, or no progress after a re-plan); otherwise as <see cref="FightThrough"/>.</summary>
    Auto,
}

/// <summary>
/// <c>World:Playerbots:Follow</c>: how a following bot keeps up with its leader (docs/areas/playbots.md, Following and catch-up).
/// Every key is live: <c>.reload config</c> changes this object in place and a follower reads it at its next decision. Used by the
/// party AI (a real player's group) and by any other follow of a bot (<see cref="Party.PlayerbotFollower"/>).
/// </summary>
public sealed class PlayerbotFollowOptions
{
    /// <summary>Teleport to catch up, fight through, or teleport only when unreachable (<see cref="PlayerbotCatchUpMode.Teleport"/> by default, the reference behaviour).</summary>
    public PlayerbotCatchUpMode CatchUpMode { get; set; } = PlayerbotCatchUpMode.Teleport;

    /// <summary>Teleport mode: a leader on the bot's map farther than this many yards is teleported to (vmangos PartyBotAI <c>IsWithinDistInMap(pLeader, 100.0f)</c>; 10..1000).</summary>
    public float TeleportDistanceYards { get; set; } = 100f;

    /// <summary>
    /// Teleport mode: a way to the leader longer than this many times the straight distance counts as no way (1.5..20), when it is
    /// also at least <see cref="MinDetourYards"/> longer than the straight line.
    /// </summary>
    public float MaxPathRatio { get; set; } = 3f;

    /// <summary>Teleport mode: a way to the leader at most this many yards longer than the straight line is always walked (0..500).</summary>
    public float MinDetourYards { get; set; } = 40f;

    /// <summary>
    /// Teleport mode: a way that passes inside the aggro radius of a hostile creature this many levels or more above the bot, or of an
    /// elite at or above the bot's level, counts as no way (0..60).
    /// </summary>
    public int DangerLevelMargin { get; set; } = 3;

    /// <summary>
    /// A bot in combat makes a catch-up teleport only when its leader is out of combat and farther than this many yards (the leader
    /// left the fight; vmangos counts a member's attacker within 50 yards as the group's fight, <c>SelectPartyAttackTarget</c>; 0..1000).
    /// </summary>
    public float LeaderCombatRangeYards { get; set; } = 50f;

    /// <summary>
    /// Seconds without getting closer to a leader who stands still before the follower plans again; the same again after that
    /// re-plan and it teleports (Teleport and Auto modes) or plans once more (FightThrough). 2..120.
    /// </summary>
    public int NoProgressSeconds { get; set; } = 8;

    /// <summary>
    /// A drop to a leader below is taken when the fall (vmangos <c>Player::HandleFall</c> damage) would cost less than this percentage
    /// of the bot's current health (0: only falls that do no damage, under 14.57 yards; 0..99).
    /// </summary>
    public int MaxFallHealthPercent { get; set; } = 50;

    /// <summary>Throws when a key is out of range (the reload refuses such a value with the same ranges).</summary>
    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Follow";
        if (CheckMode(CatchUpMode) is { } mode) throw new InvalidOperationException($"{section}: CatchUpMode {mode}.");
        if (CheckTeleportDistance(TeleportDistanceYards) is { } distance) throw new InvalidOperationException($"{section}: TeleportDistanceYards {distance}.");
        if (CheckPathRatio(MaxPathRatio) is { } ratio) throw new InvalidOperationException($"{section}: MaxPathRatio {ratio}.");
        if (CheckDetour(MinDetourYards) is { } detour) throw new InvalidOperationException($"{section}: MinDetourYards {detour}.");
        if (CheckLevelMargin(DangerLevelMargin) is { } margin) throw new InvalidOperationException($"{section}: DangerLevelMargin {margin}.");
        if (CheckCombatRange(LeaderCombatRangeYards) is { } range) throw new InvalidOperationException($"{section}: LeaderCombatRangeYards {range}.");
        if (CheckNoProgress(NoProgressSeconds) is { } seconds) throw new InvalidOperationException($"{section}: NoProgressSeconds {seconds}.");
        if (CheckFallPercent(MaxFallHealthPercent) is { } fall) throw new InvalidOperationException($"{section}: MaxFallHealthPercent {fall}.");
    }

    internal static string? CheckMode(PlayerbotCatchUpMode value) => Enum.IsDefined(value) ? null : "must be Teleport, FightThrough or Auto";

    internal static string? CheckTeleportDistance(float value) => float.IsFinite(value) && value is >= 10f and <= 1000f ? null : "must be 10..1000";

    internal static string? CheckPathRatio(float value) => float.IsFinite(value) && value is >= 1.5f and <= 20f ? null : "must be 1.5..20";

    internal static string? CheckDetour(float value) => float.IsFinite(value) && value is >= 0f and <= 500f ? null : "must be 0..500";

    internal static string? CheckLevelMargin(int value) => value is >= 0 and <= 60 ? null : "must be 0..60";

    internal static string? CheckCombatRange(float value) => float.IsFinite(value) && value is >= 0f and <= 1000f ? null : "must be 0..1000";

    internal static string? CheckNoProgress(int value) => value is >= 2 and <= 120 ? null : "must be 2..120";

    internal static string? CheckFallPercent(int value) => value is >= 0 and <= 99 ? null : "must be 0..99";
}
