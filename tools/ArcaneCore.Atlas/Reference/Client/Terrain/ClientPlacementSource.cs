using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Reference.Client.Terrain;

/// <summary>
/// The model placements stored in the client's ADT (MODF then MDDF, each in file order) and in a WDT's global WMO
/// (MODF). Positions, rotations and scales are the raw stored fields; this source does not move them into world space.
/// Thread-safe: it reads through <see cref="ClientTerrainService"/>'s caches and builds a fresh list per call.
/// </summary>
/// <remarks>
/// Model paths are reported exactly as stored, with no extension or case rewriting. Doodad names in 1.12.1 ADTs are
/// legacy <c>.mdx</c> names, so the model lane resolves them against the file source itself.
/// </remarks>
/// <param name="service">The shared file and cache service.</param>
public sealed class ClientPlacementSource(ClientTerrainService service) : IClientPlacementSource
{
    private readonly ClientTerrainService _service = service ?? throw new ArgumentNullException(nameof(service));

    /// <inheritdoc />
    /// <remarks>
    /// Null when the WDT or ADT is not in the client files (or no client files are registered). Throws
    /// <see cref="InvalidDataException"/> with the reader's message when the file exists and is refused. A tile outside
    /// the grid that is not the global tile throws <see cref="ArgumentException"/>.
    /// </remarks>
    public IReadOnlyList<ClientPlacement>? Placements(TileKey tile)
    {
        if (!tile.IsValid)
        {
            throw new ArgumentException($"Tile {tile} is outside the 64x64 grid.", nameof(tile));
        }

        if (tile.IsGlobal)
        {
            WdtLoad wdt = _service.LoadWdt(tile.MapId);
            if (wdt.State == TileState.Rejected)
            {
                throw new InvalidDataException(wdt.Detail ?? "The WDT was refused.");
            }

            return wdt.File is { } wdtFile
                ? [.. wdtFile.WmoObjects.Select(o => ToPlacement(o, tile))]
                : null;
        }

        AdtLoad load = _service.LoadAdt(tile);
        if (load.State == TileState.Rejected)
        {
            throw new InvalidDataException(load.Detail ?? "The ADT was refused.");
        }

        if (load.File is not { } adt)
        {
            return null;
        }

        return
        [
            .. adt.WmoObjects.Select(o => ToPlacement(o, tile)),
            .. adt.Doodads.Select(d => new ClientPlacement(PlacementKind.M2, d.ModelPath, d.UniqueId, d.Position, d.Rotation, d.Scale, d.Flags, 0, tile)),
        ];
    }

    private static ClientPlacement ToPlacement(AdtWmoObject o, TileKey tile)
        => new(PlacementKind.Wmo, o.ModelPath, o.UniqueId, o.Position, o.Rotation, 1.0, o.Flags, o.DoodadSet, tile);
}
