using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Gm.DebugDraw;

/// <summary>
/// The <c>World:GmCommands:DebugDraw</c> section: limits of the <c>.debug vis</c> markers (docs/areas/debug-draw.md). The markers exist
/// only in the requesting GM's client, so these bound what one GM can make that client hold, not server state.
/// </summary>
public sealed class DebugDrawOptions
{
    public const string SectionName = "World:GmCommands:DebugDraw";

    /// <summary>Most markers one GM's client holds at once (glow companions count); a new drawing removes the oldest ones to fit. 1..2000.</summary>
    public int MaxMarkersPerGm { get; set; } = 300;

    /// <summary>Seconds a drawing stays before it is removed on its own (it also goes with .debug vis clear, a logout or a map change). 5..3600.</summary>
    public int LifetimeSeconds { get; set; } = 120;

    /// <summary>Yards between the dots of a line or path (at least 0.5; a long line spreads its dots further to stay within its share of markers).</summary>
    public float Spacing { get; set; } = 2.0f;

    /// <summary>Add a coloured glow model next to the key markers (end points, hits, path corners, waypoints, heights, spawns).</summary>
    public bool Glow { get; set; } = true;

    /// <summary>Bind <c>World:GmCommands:DebugDraw</c> and clamp every value into its range.</summary>
    public static DebugDrawOptions Bind(IConfiguration? configuration)
    {
        var options = new DebugDrawOptions();
        configuration?.GetSection(SectionName).Bind(options);
        options.MaxMarkersPerGm = Math.Clamp(options.MaxMarkersPerGm, 1, 2000);
        options.LifetimeSeconds = Math.Clamp(options.LifetimeSeconds, 5, 3600);
        options.Spacing = float.IsFinite(options.Spacing) ? Math.Clamp(options.Spacing, 0.5f, 50f) : 2.0f;
        return options;
    }
}
