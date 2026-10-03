using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.World.Features;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// Registers the PvP-area rules (<see cref="PvpAreaTracker"/>, docs/areas/world-state.md): entering a hostile
/// capital (or any enforced zone on a PvP realm) flags the player and freezes the PvP flag timer, FFA realms and
/// arenas toggle the FFA flag. The realm kind is <c>World:Zones:PvpRealmMode</c> (default Normal).
/// </summary>
public sealed class PvpAreaFeature : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        WorldStateHooks hooks = WorldStateHooks.For(world);
        hooks.AddLocationListener(new PvpAreaTracker(hooks));
    }
}
