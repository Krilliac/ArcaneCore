namespace ArcaneCore.World.Playerbots;

/// <summary>Placeholder: the trip driver and <see cref="IPlayerbotTravel"/> implementation (docs/areas/playbots.md, Travel).</summary>
internal static class PlayerbotTravel
{
    /// <summary>Off-thread reachability from static data only (for the home progression's sweep).</summary>
    internal static bool CanReachStatic(bool alliance, uint fromMap, System.Numerics.Vector3 from, uint toMap, System.Numerics.Vector3 to) => fromMap == toMap;
}
