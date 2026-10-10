namespace ArcaneCore.Kernel.WorldData.WorldState;

/// <summary>
/// vmangos OutdoorPvPSI::UpdateWorldState saved variables WS_OPVP_SI_GATHERED_A (2313), WS_OPVP_SI_GATHERED_H (2314) and
/// WS_OPVP_SI_SILITHYST_MAX (2317), written at every change. SetupZoneScript reads back only the maximum (the counters restart at 0).
/// </summary>
public readonly record struct SilithystState(uint GatheredAlliance, uint GatheredHorde, uint MaxResources);

/// <summary>The saved Silithus Silithyst variables (characters database).</summary>
public interface ISilithystStore
{
    Task<SilithystState?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(SilithystState state, CancellationToken cancellationToken = default);
}
