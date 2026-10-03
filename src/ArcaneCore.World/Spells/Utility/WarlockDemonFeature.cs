using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using ArcaneCore.World.Pets;

namespace ArcaneCore.World.Spells.Utility;

/// <summary>
/// The warlock demons in the world daemon (discovered <see cref="IWorldFeature"/>): registers SPELL_EFFECT_SUMMON_PET on the world spell system through the
/// summon service of <see cref="PetsFeature"/> (<c>SummonService.InstallDemons</c>). Startup fails if another feature already claimed the effect.
/// </summary>
public sealed class WarlockDemonFeature(SpellFeature spells, PetsFeature pets) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        pets.Service.InstallDemons(spells.System);
    }
}
