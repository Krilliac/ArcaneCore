namespace ArcaneCore.Game.Maps.Collision;

/// <summary>
/// What a path query is for: the moving unit's locomotion capabilities (vmangos
/// <c>PathInfo::createFilter</c> reads <c>CanWalk</c>, <c>CanSwim</c> and the unit's type from the
/// source unit, PathFinder.cpp:657-675). Attached to <see cref="PathOptions.Mover"/>; a query
/// without one uses the options' explicit include flags.
/// </summary>
/// <param name="CanWalk">Ground movement (creature <c>InhabitType</c> ground bit; always true for players).</param>
/// <param name="CanSwim">Swimming (creature <c>InhabitType</c> water bit; players always).</param>
/// <param name="CanFly">Flying (creature <c>InhabitType</c> air bit); fliers path in the air, not on the mesh.</param>
/// <param name="IsPlayer">Players take environmental damage, so they never path through magma or slime.</param>
public readonly record struct PathMover(bool CanWalk, bool CanSwim, bool CanFly, bool IsPlayer)
{
    /// <summary>A ground-bound player (walks and swims).</summary>
    public static PathMover Player { get; } = new(CanWalk: true, CanSwim: true, CanFly: false, IsPlayer: true);

    /// <summary>
    /// The navmesh polygon flags this mover may use (vmangos <c>createFilter</c>): ground when it
    /// walks; water when it swims, plus magma and slime for creatures ("creatures don't take
    /// environmental damage"). Steep slopes are never excluded here: vmangos excludes them only for
    /// fear, flee, confused and random movement (<see cref="PathOptions.ExcludeFlags"/>).
    /// </summary>
    public NavTerrain IncludeFlags
    {
        get
        {
            NavTerrain flags = NavTerrain.Empty;
            if (CanWalk)
            {
                flags |= NavTerrain.Ground;
            }

            if (CanSwim)
            {
                flags |= IsPlayer ? NavTerrain.Water : NavTerrain.Water | NavTerrain.Magma | NavTerrain.Slime;
            }

            return flags;
        }
    }
}
