using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.Gnomeregan;

/// <summary>
/// Activate Bomb A (11511) and B (11795), Mekgineer Thermaplugg's phase one and two casts: the DUMMY picks one of the six gnome faces at random
/// and activates it (an active face drops a Walking Bomb three seconds later; an already active face stays as it is). vmangos
/// gnomeregan/boss_thermaplugg.cpp EffectDummyCreature_spell_boss_thermaplugg and mangos-classic ActivateBombThermaplugg
/// (<c>DoActivateBombFace(urand(0, MAX_GNOME_FACES - 1))</c>), on effect 0 only. Reimplemented, no GPL source copied.
/// </summary>
[SpellScript(ThermapluggAi.ActivateBombA, ThermapluggAi.ActivateBombB)]
public sealed class ThermapluggActivateBombScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex == 0 && context.Caster.Map?.FindUpdater<GnomereganInstance>() is { } instance)
            instance.ActivateBombFace(Random.Shared.Next(GnomereganInstance.FaceEntries.Length));
    }
}
