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

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Pvp";
        if (Initiate is null) throw new InvalidOperationException($"{section}:Initiate is missing.");
        Initiate.Validate(section + ":Initiate");
    }
}

/// <summary>
/// <c>World:Playerbots:Pvp:Initiate</c>: while enabled an idle bot (out of combat, not rested or sick, no defend post, in no group that follows a real
/// player) attacks the nearest PvP-flagged enemy-faction player or bot within <see cref="RadiusYards"/> that stands outside the exempt areas, inside the
/// level window and does not have the bot outnumbered. Independent of War Mode; War Mode's grudge hunt takes priority over it.
/// </summary>
public sealed class PlayerbotPvpInitiateOptions
{
    /// <summary>Lowest <see cref="RadiusYards"/>.</summary>
    public const int MinRadiusYards = 5;

    /// <summary>Highest <see cref="RadiusYards"/>.</summary>
    public const int MaxRadiusYards = 100;

    /// <summary>Highest level window.</summary>
    public const int MaxLevelWindow = 60;

    /// <summary>Let idle bots start PvP fights (off by default: not retail behaviour).</summary>
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
