namespace ArcaneCore.Kernel.WorldData;

/// <summary>
/// One safe location (vmangos <c>WorldSafeLocs.dbc</c> row plus <c>world_safe_locs_facing</c>; cmangos
/// <c>world_safe_locs</c>): a graveyard, or a battleground entry or exit point. <see cref="Orientation"/> is
/// the facing a released spirit gets (vmangos <c>GetWorldSafeLocFacing</c>, 0 when no facing is stored).
/// </summary>
public sealed record WorldSafeLoc(uint Id, uint MapId, float X, float Y, float Z, float Orientation, string Name);

/// <summary>
/// A graveyard link (<c>game_graveyard_zone</c>): safe location <see cref="SafeLocId"/> serves the ghosts of zone or
/// area <see cref="ZoneId"/> of team <see cref="Team"/> (0 = both, 67 = Horde, 469 = Alliance; vmangos
/// <c>ObjectMgr::LoadGraveyardZones</c>).
/// </summary>
public sealed record GraveyardLink(uint SafeLocId, uint ZoneId, uint Team);

/// <summary>Everything the graveyard feature reads from the world database at startup.</summary>
public sealed record GraveyardContent(IReadOnlyList<WorldSafeLoc> SafeLocs, IReadOnlyList<GraveyardLink> Links)
{
    public static GraveyardContent Empty { get; } = new([], []);
}

/// <summary>Read access to the world database's graveyard tables (docs/areas/graveyards-resurrection.md).</summary>
public interface IGraveyardDataStore
{
    /// <summary>Load every safe location and graveyard link row.</summary>
    Task<GraveyardContent> LoadAsync(CancellationToken cancellationToken = default);
}
