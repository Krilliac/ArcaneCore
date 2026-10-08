using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Paladin;

/// <summary>
/// Reckoning (20178, the ADD_EXTRA_ATTACKS spell the talent's PROC_TRIGGER_SPELL aura 20177 casts on the paladin when a critical hit is taken; vmangos
/// scripts/spells/spell_paladin.cpp:178-206, <c>PaladinReckoningScript::OnEffectExecute</c>). Unlike the plain effect, which only queues attacks on a
/// unit that has none pending, Reckoning adds one extra attack per proc to those already pending, up to 4 (builds after 1.4.2 cap the stack; the
/// 1.2 attack timer reset is not part of a 1.12.1 server). The pending attacks are swung on the paladin's next melee update with a victim, and every
/// reset that clears extra attacks (death, mounting, a new auto-attack target) clears them.
/// <para>
/// Deviation, documented: vmangos only counts the extra attacks up and lets the paladin's next own swing release them (<c>AddExtraAttackOnUpdate</c> in
/// <c>AttackerStateUpdate</c>); this server's extra attack queue (UnitCombat, owned by the combat area) makes every queued batch ready on the next unit
/// update, so stacks build while the paladin is not swinging and are released on the next melee update rather than after the next swing.
/// </para>
/// </summary>
[SpellScript(Reckoning, ExecuteEffects = [SpellEffectName.AddExtraAttacks])]
public sealed class ReckoningScript : ISpellScript
{
    public const uint Reckoning = 20178;

    /// <summary>The talent's proc aura (rank 1; 20177, 20179, 20180, 20181, 20182 all trigger 20178).</summary>
    public const uint ReckoningTalent = 20177;

    /// <summary>"It was possible to stack infinite extra attacks in early vanilla": after 1.4.2 a proc adds one only below this.</summary>
    public const uint MaxStack = 4;

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 0 || context.Effect.Effect != SpellEffectName.AddExtraAttacks)
        {
            return;
        }

        Unit target = context.Target;
        uint pending = target.Combat.ExtraAttacks;
        if (pending >= MaxStack || !target.IsAlive || target.Combat.ExtraAttacksLocked)
        {
            return; // the plain effect that runs next refuses a unit with attacks pending, so nothing is added
        }

        // AddExtraAttack(): one more than pending. The queue only takes a whole batch, so the pending ones are re-queued with the new one.
        target.Combat.ResetExtraAttacks();
        target.Combat.QueueExtraAttacks((int)pending + 1);
    }
}
