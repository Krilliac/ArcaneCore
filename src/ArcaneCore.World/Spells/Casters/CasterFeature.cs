using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells.Casters;
using ArcaneCore.World.Features;

namespace ArcaneCore.World.Spells.Casters;

/// <summary>
/// Installs the mage / priest / warlock spell rules (<see cref="CasterSpellModules"/>) on the world's
/// <see cref="SpellFeature.System"/>. The spell system object exists from the feature's construction, so the
/// registration does not depend on the order in which discovered features attach.
/// </summary>
public sealed class CasterFeature(SpellFeature spells) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        CasterSpellModules.Register(spells.System);
    }
}
