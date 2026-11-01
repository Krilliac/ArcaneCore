using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.RuinsOfAhnQiraj;

/// <summary>
/// Attack Order 25471 (mangos-classic src/game/Spells/SpellAuras.cpp:1303-1306, Aura::HandleAuraDummy apply case): the marked unit casts Attack Order
/// 25473 on itself, an area taunt on the enemies around it, so Captain Tuubid's pack turns on the mark. vmangos only casts 25471 from boss_tuubid
/// (ruins_of_ahnqiraj.cpp:667-710) and has no aura case; classic-db EventAI casts it for Tuubid. Registered through the DUMMY aura case table;
/// runs once per spell (its first DUMMY effect), on apply only. State: none; world thread of the target's map.
/// </summary>
public sealed class AttackOrderSpellModule : ISpellHandlerModule
{
    /// <summary>The marking aura.</summary>
    public const uint AttackOrder = 25471;

    /// <summary>The area taunt the marked unit casts on itself.</summary>
    public const uint AttackOrderTaunt = 25473;

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterDummyAuraHandler(AttackOrder, (spells, holder, aura, apply) =>
        {
            if (!apply || !IsFirstDummy(holder, aura))
            {
                return;
            }

            spells.CastCustomSpell(holder.Target, AttackOrderTaunt, SpellCastTargets.ForSelf(), null);
        });
    }

    private static bool IsFirstDummy(SpellAuraHolder holder, SpellAura aura)
    {
        for (int i = 0; i < holder.Spell.Effects.Count; i++)
        {
            if (holder.Spell.Effects[i].AuraType == AuraType.Dummy)
            {
                return aura.EffectIndex == i;
            }
        }

        return false;
    }
}
