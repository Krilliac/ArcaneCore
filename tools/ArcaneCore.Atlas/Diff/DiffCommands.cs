using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Diff;

/// <summary>
/// Registers the three Reference-vs-Server comparison commands (<c>diff.terrain</c>, <c>diff.models</c>, <c>diff.nav</c>).
/// Each command compares a single tile (<c>radius = 0</c>) or a square neighbourhood (<c>radius = 1..4</c>) and returns
/// counts of <c>expected</c> extractor/assembler/Detour differences and <c>unexplained</c> discrepancies.
/// </summary>
public static class DiffCommands
{
    /// <summary>Command name for the terrain diff (<c>diff.terrain</c>).</summary>
    public const string TerrainCommand = "diff.terrain";

    /// <summary>Command name for the model diff (<c>diff.models</c>).</summary>
    public const string ModelsCommand = "diff.models";

    /// <summary>Command name for the navmesh diff (<c>diff.nav</c>).</summary>
    public const string NavCommand = "diff.nav";

    private static readonly IReadOnlyList<CommandArg> Args =
    [
        new("map", ArgType.Integer, "Map id (for example 0 for Eastern Kingdoms).", Required: true),
        new("x", ArgType.Integer, "Tile X index (0..63).", Required: true),
        new("y", ArgType.Integer, "Tile Y index (0..63).", Required: true),
        new("radius", ArgType.Integer, "Tile neighbourhood radius around (x, y), 0..4 (0 = single tile, 1 = 3x3).", Required: false, Default: 0),
    ];

    /// <summary>Registers <c>diff.terrain</c>, <c>diff.models</c>, and <c>diff.nav</c> on <paramref name="commands"/>.</summary>
    public static void Register(CommandRegistry commands, AtlasServices services)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(services);

        commands.Register(new CommandDefinition(
            TerrainCommand,
            "Compare server .map terrain (heights, holes, liquid) against 1.12.1 client ADT over a tile or neighbourhood.",
            Args,
            (inv, ct) => RunAsync(inv, services, TerrainDiff.RunCommand, ct)));

        commands.Register(new CommandDefinition(
            ModelsCommand,
            "Compare server .vmtree/.vmtile/.vmo spawns and geometry against 1.12.1 client ADT/WDT/WMO/M2 placements.",
            Args,
            (inv, ct) => RunAsync(inv, services, ModelDiff.RunCommand, ct)));

        commands.Register(new CommandDefinition(
            NavCommand,
            "Compare server .mmtile navmesh polygons, detail mesh, BV-tree, and portals against vendored DotRecast.",
            Args,
            (inv, ct) => RunAsync(inv, services, NavDiff.RunCommand, ct)));
    }

    private static Task<JsonNode?> RunAsync(
        CommandInvocation inv,
        AtlasServices services,
        Func<AtlasServices, uint, int, int, int, CancellationToken, JsonObject> runner,
        CancellationToken cancellationToken)
    {
        uint mapId = inv.GetMapId();
        long x = inv.GetInt("x");
        long y = inv.GetInt("y");
        if (x is < 0 or >= WorldGrid.TilesPerSide || y is < 0 or >= WorldGrid.TilesPerSide)
        {
            throw new CommandException(
                CommandException.BadArguments,
                $"{inv.Definition.Name}: tile ({x}, {y}) is outside the {WorldGrid.TilesPerSide}x{WorldGrid.TilesPerSide} grid.");
        }

        long radius = inv.OptionalInt("radius") ?? 0;
        if (radius is < 0 or > 4)
        {
            throw new CommandException(
                CommandException.BadArguments,
                $"{inv.Definition.Name}: radius {radius} must be between 0 and 4.");
        }

        return Task.Run<JsonNode?>(
            () => runner(services, mapId, (int)x, (int)y, (int)radius, cancellationToken),
            cancellationToken);
    }
}
