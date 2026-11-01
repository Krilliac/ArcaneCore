using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.BlackrockSpire;

/// <summary>
/// Dominion of Soul 16053, the spell of the Orb of Draconic Energy. mangos-classic scripts/world/item_scripts.cpp
/// ItemUse_item_orb_of_draconic_energy: with Emberstrife (10321) within 20 yd, using the orb fails with SPELL_FAILED_TARGET_AURASTATE while he is
/// already dominated or above 10% health; without him the item use goes on as normal (the spell's script target then decides). The
/// possession itself is the spell's own aura effects. Item casts only (the reference hooks the item use). Reimplemented, no GPL source copied.
/// </summary>
[SpellScript(DominionOfSoul)]
public sealed class DominionOfSoulScript : ISpellScript
{
    public const uint DominionOfSoul = 16053, Emberstrife = 10321;
    public const float SearchRange = 20f;

    public SpellCastResult OnCheckCast(in SpellCastCheckContext context)
    {
        if (context.CastItem is null || context.Caster is not Player caster)
        {
            return SpellCastResult.CastOk;
        }

        Creature? emberstrife = ClosestEmberstrife(caster);
        if (emberstrife is null)
        {
            return SpellCastResult.CastOk;
        }

        return context.System.HasAura(emberstrife, DominionOfSoul) || emberstrife.HealthPercent > 10f
            ? SpellCastResult.TargetAurastate : SpellCastResult.CastOk;
    }

    /// <summary>GetClosestCreatureWithEntry(player, 10321, 20): the nearest living creature of the entry within range (3D).</summary>
    private static Creature? ClosestEmberstrife(Player caster)
        => caster.Map?.FindUpdater<CreatureMapSystem>()?.CreaturesOfEntryInRange(caster, Emberstrife, SearchRange).FirstOrDefault();
}
