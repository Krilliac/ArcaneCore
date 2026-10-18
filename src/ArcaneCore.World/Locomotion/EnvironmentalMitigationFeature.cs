using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;

namespace ArcaneCore.World.Locomotion;

/// <summary>
/// Gives environmental and fall damage the spell combat rules (vmangos Player::EnvironmentalDamage, Player.cpp:713-758, and
/// Player::HandleFall, Player.cpp:20854-20855): one <see cref="SpellEnvironmentalMitigation"/> over the daemon's
/// <see cref="SpellFeature.System"/> answers both the immunity, resist and absorb of lava, fire, slime, drowning, fatigue and falls
/// and the damage-taken modifier of falls. The rules it uses are read when the damage happens, so the attach order of the spell
/// rule features does not matter.
/// </summary>
public sealed class EnvironmentalMitigationFeature(SpellFeature spells) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var mitigation = new SpellEnvironmentalMitigation(spells.System);
        LocomotionEnvironment.RegisterMitigation(world, mitigation);
        LocomotionEnvironment.RegisterFallModifiers(world, mitigation);
    }
}
