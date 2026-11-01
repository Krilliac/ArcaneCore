using ArcaneCore.Kernel.Configuration;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// <c>World:Playerbots:Pvp</c>: how bots take part in world PvP (docs/areas/playbots-combat.md). <see cref="Initiate"/> lets an idle bot start a
/// fight with a flagged enemy-faction player or bot nearby; without it a bot only fights back. Not retail behaviour. Every key is live:
/// <c>.reload config</c> changes this object in place and a bot reads it at its next think.
/// </summary>
public sealed class PlayerbotPvpOptions
{
    /// <summary>Starting a fight (<c>World:Playerbots:Pvp:Initiate</c>).</summary>
    public PlayerbotPvpInitiateOptions Initiate { get; set; } = new();

    /// <summary>Calling for help when attacked (<c>World:Playerbots:Pvp:Defense</c>).</summary>
    public PlayerbotPvpDefenseOptions Defense { get; set; } = new();

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Pvp";
        if (Initiate is null) throw new InvalidOperationException($"{section}:Initiate is missing.");
        Initiate.Validate(section + ":Initiate");
        if (Defense is null) throw new InvalidOperationException($"{section}:Defense is missing.");
        Defense.Validate(section + ":Defense");
    }
}

/// <summary>
/// <c>World:Playerbots:Pvp:Initiate</c>: while enabled an idle bot (out of combat, not rested or sick, no defend post, in no group that follows a real
/// player) attacks the nearest PvP-flagged enemy-faction player or bot within <see cref="RadiusYards"/> that stands outside the exempt areas, inside the
/// level window and does not have the bot outnumbered. War Mode on means attack on sight with these same limits and needs no <c>Initiate:Enabled</c>; this switch is the opt-in for on-sight engagement while War Mode is off. War Mode's grudge hunt takes priority.
/// </summary>
public sealed class PlayerbotPvpInitiateOptions
{
    /// <summary>Lowest <see cref="RadiusYards"/>.</summary>
    public const int MinRadiusYards = 5;

    /// <summary>Highest <see cref="RadiusYards"/>.</summary>
    public const int MaxRadiusYards = 100;

    /// <summary>Highest level window.</summary>
    public const int MaxLevelWindow = 60;

    /// <summary>Let idle bots start PvP fights while War Mode is off (off by default: not retail behaviour; War Mode on already means attack on sight).</summary>
    public bool Enabled { get; set; }

    /// <summary>A bot looks for a target within this many yards, and counts the friends and enemies around it within the same radius (5 to 100).</summary>
    [ConfigAllowed("5 to 100")]
    public int RadiusYards { get; set; } = 30;

    /// <summary>A bot leaves a target alone that is more than this many levels below it (0 to 60).</summary>
    [ConfigAllowed("0 to 60")]
    public int MaxLevelBelow { get; set; } = 5;

    /// <summary>A bot leaves a target alone that is more than this many levels above it (0 to 60).</summary>
    [ConfigAllowed("0 to 60")]
    public int MaxLevelAbove { get; set; } = 3;

    /// <summary>A bot starts a fight only with at least this percent of its health (0 to 100).</summary>
    [ConfigAllowed("0 to 100")]
    public int MinHealthPct { get; set; } = 60;

    /// <summary>The reason <paramref name="radius"/> is refused, or null.</summary>
    public static string? CheckRadius(int radius) => radius is >= MinRadiusYards and <= MaxRadiusYards ? null : $"must be {MinRadiusYards}..{MaxRadiusYards}";

    /// <summary>The reason a level window <paramref name="levels"/> is refused, or null.</summary>
    public static string? CheckLevels(int levels) => levels is >= 0 and <= MaxLevelWindow ? null : $"must be 0..{MaxLevelWindow}";

    /// <summary>The reason a health percent <paramref name="percent"/> is refused, or null.</summary>
    public static string? CheckHealth(int percent) => percent is >= 0 and <= 100 ? null : "must be 0..100";

    internal void Validate(string section)
    {
        if (CheckRadius(RadiusYards) is { } radius) throw new InvalidOperationException($"{section}: RadiusYards {radius}.");
        if (CheckLevels(MaxLevelBelow) is { } below) throw new InvalidOperationException($"{section}: MaxLevelBelow {below}.");
        if (CheckLevels(MaxLevelAbove) is { } above) throw new InvalidOperationException($"{section}: MaxLevelAbove {above}.");
        if (CheckHealth(MinHealthPct) is { } health) throw new InvalidOperationException($"{section}: MinHealthPct {health}.");
    }
}

/// <summary>
/// <c>World:Playerbots:Pvp:Defense</c>: a bot that is attacked by an enemy player or bot calls for help; free bots of its side nearby walk over and fight
/// the attackers, and a "LocalDefense" channel line names the place (with map coordinates when <see cref="WorldMapAreaDbcPath"/> is set). The switch is
/// independent of War Mode: War Mode on means the calls are active, and <see cref="Enabled"/> turns them on while War Mode is off. Not retail behaviour
/// (retail sends no call, and its "is under attack" line is a client-formatted zone packet, not chat). Every key except <see cref="WorldMapAreaDbcPath"/>
/// is live; the path is read once when the bot feature attaches.
/// </summary>
public sealed class PlayerbotPvpDefenseOptions
{
    /// <summary>Lowest <see cref="RespondRadiusYards"/>.</summary>
    public const int MinRespondRadiusYards = 50;

    /// <summary>Highest <see cref="RespondRadiusYards"/>.</summary>
    public const int MaxRespondRadiusYards = 1000;

    /// <summary>Highest <see cref="MaxRespondersPerSide"/>.</summary>
    public const int MaxRespondersLimit = 20;

    /// <summary>Lowest <see cref="CallerCooldownSeconds"/>.</summary>
    public const int MinCallerCooldownSeconds = 10;

    /// <summary>Highest <see cref="CallerCooldownSeconds"/>.</summary>
    public const int MaxCallerCooldownSeconds = 3600;

    /// <summary>Lowest <see cref="ZoneAnnounceSeconds"/>.</summary>
    public const int MinZoneAnnounceSeconds = 5;

    /// <summary>Highest <see cref="ZoneAnnounceSeconds"/>.</summary>
    public const int MaxZoneAnnounceSeconds = 600;

    /// <summary>Highest <see cref="MaxActiveCallsPerZone"/>.</summary>
    public const int MaxActiveCallsPerZoneLimit = 20;

    /// <summary>Highest <see cref="MaxOpenCallsPerMap"/>.</summary>
    public const int MaxOpenCallsPerMapLimit = 256;

    /// <summary>Lowest <see cref="ProvokeThrottleSeconds"/>.</summary>
    public const int MinProvokeThrottleSeconds = 1;

    /// <summary>Highest <see cref="ProvokeThrottleSeconds"/>.</summary>
    public const int MaxProvokeThrottleSeconds = 3600;

    /// <summary>Let attacked bots call for help while War Mode is off (off by default: not retail behaviour; War Mode on already activates the calls).</summary>
    public bool Enabled { get; set; }

    /// <summary>Post a line in the zone's "LocalDefense" channel when a call opens. Off silences the line and leaves the coordination as it is.</summary>
    public bool Announce { get; set; } = true;

    /// <summary>With War Mode off, bots of the attackers' side also answer a call (with War Mode on they always do).</summary>
    public bool BothSides { get; set; }

    /// <summary>A bot answers a call only within this many yards of the call's centre (50 to 1000).</summary>
    [ConfigAllowed("50 to 1000")]
    public int RespondRadiusYards { get; set; } = 300;

    /// <summary>At most this many bots of one side answer one call (0 to 20; 0 means nobody answers).</summary>
    [ConfigAllowed("0 to 20")]
    public int MaxRespondersPerSide { get; set; } = 4;

    /// <summary>A responder leaves a target alone that is more than this many levels below it (0 to 60).</summary>
    [ConfigAllowed("0 to 60")]
    public int MaxLevelBelow { get; set; } = 5;

    /// <summary>A responder leaves a target alone that is more than this many levels above it (0 to 60).</summary>
    [ConfigAllowed("0 to 60")]
    public int MaxLevelAbove { get; set; } = 5;

    /// <summary>One bot opens at most one call per this many seconds (10 to 3600).</summary>
    [ConfigAllowed("10 to 3600")]
    public int CallerCooldownSeconds { get; set; } = 60;

    /// <summary>One zone and team hear at most one call line per this many seconds (5 to 600).</summary>
    [ConfigAllowed("5 to 600")]
    public int ZoneAnnounceSeconds { get; set; } = 30;

    /// <summary>At most this many calls stay open in one zone of one map (1 to 20).</summary>
    [ConfigAllowed("1 to 20")]
    public int MaxActiveCallsPerZone { get; set; } = 2;

    /// <summary>At most this many calls stay open on one map instance (1 to 256).</summary>
    [ConfigAllowed("1 to 256")]
    public int MaxOpenCallsPerMap { get; set; } = 16;

    /// <summary>One attacking player or bot raises a call at most once per this many seconds (1 to 3600).</summary>
    [ConfigAllowed("1 to 3600")]
    public int ProvokeThrottleSeconds { get; set; } = 30;

    /// <summary>
    /// Path of WorldMapArea.dbc (<c>ClientData:DbcDirectory</c> fills it when unset). Without it a call line names the place and no coordinates. Read once
    /// when the bot feature attaches, so a change takes a restart.
    /// </summary>
    public string WorldMapAreaDbcPath { get; set; } = "";

    /// <summary>The reason <paramref name="radius"/> is refused, or null.</summary>
    public static string? CheckRespondRadius(int radius) => radius is >= MinRespondRadiusYards and <= MaxRespondRadiusYards ? null : $"must be {MinRespondRadiusYards}..{MaxRespondRadiusYards}";

    /// <summary>The reason a responder cap <paramref name="count"/> is refused, or null.</summary>
    public static string? CheckResponders(int count) => count is >= 0 and <= MaxRespondersLimit ? null : $"must be 0..{MaxRespondersLimit}";

    /// <summary>The reason a caller cooldown <paramref name="seconds"/> is refused, or null.</summary>
    public static string? CheckCallerCooldown(int seconds) => seconds is >= MinCallerCooldownSeconds and <= MaxCallerCooldownSeconds ? null : $"must be {MinCallerCooldownSeconds}..{MaxCallerCooldownSeconds}";

    /// <summary>The reason a zone announce interval <paramref name="seconds"/> is refused, or null.</summary>
    public static string? CheckZoneAnnounce(int seconds) => seconds is >= MinZoneAnnounceSeconds and <= MaxZoneAnnounceSeconds ? null : $"must be {MinZoneAnnounceSeconds}..{MaxZoneAnnounceSeconds}";

    /// <summary>The reason a per-zone call cap <paramref name="count"/> is refused, or null.</summary>
    public static string? CheckCallsPerZone(int count) => count is >= 1 and <= MaxActiveCallsPerZoneLimit ? null : $"must be 1..{MaxActiveCallsPerZoneLimit}";

    /// <summary>The reason a per-map call cap <paramref name="count"/> is refused, or null.</summary>
    public static string? CheckCallsPerMap(int count) => count is >= 1 and <= MaxOpenCallsPerMapLimit ? null : $"must be 1..{MaxOpenCallsPerMapLimit}";

    /// <summary>The reason a provoke throttle <paramref name="seconds"/> is refused, or null.</summary>
    public static string? CheckProvokeThrottle(int seconds) => seconds is >= MinProvokeThrottleSeconds and <= MaxProvokeThrottleSeconds ? null : $"must be {MinProvokeThrottleSeconds}..{MaxProvokeThrottleSeconds}";

    internal void Validate(string section)
    {
        if (CheckRespondRadius(RespondRadiusYards) is { } radius) throw new InvalidOperationException($"{section}: RespondRadiusYards {radius}.");
        if (CheckResponders(MaxRespondersPerSide) is { } responders) throw new InvalidOperationException($"{section}: MaxRespondersPerSide {responders}.");
        if (PlayerbotPvpInitiateOptions.CheckLevels(MaxLevelBelow) is { } below) throw new InvalidOperationException($"{section}: MaxLevelBelow {below}.");
        if (PlayerbotPvpInitiateOptions.CheckLevels(MaxLevelAbove) is { } above) throw new InvalidOperationException($"{section}: MaxLevelAbove {above}.");
        if (CheckCallerCooldown(CallerCooldownSeconds) is { } cooldown) throw new InvalidOperationException($"{section}: CallerCooldownSeconds {cooldown}.");
        if (CheckZoneAnnounce(ZoneAnnounceSeconds) is { } announce) throw new InvalidOperationException($"{section}: ZoneAnnounceSeconds {announce}.");
        if (CheckCallsPerZone(MaxActiveCallsPerZone) is { } perZone) throw new InvalidOperationException($"{section}: MaxActiveCallsPerZone {perZone}.");
        if (CheckCallsPerMap(MaxOpenCallsPerMap) is { } perMap) throw new InvalidOperationException($"{section}: MaxOpenCallsPerMap {perMap}.");
        if (CheckProvokeThrottle(ProvokeThrottleSeconds) is { } throttle) throw new InvalidOperationException($"{section}: ProvokeThrottleSeconds {throttle}.");
        if (WorldMapAreaDbcPath is null) throw new InvalidOperationException($"{section}: WorldMapAreaDbcPath is missing.");
    }
}
