using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Spells.Effects;

/// <summary>vmangos Spell::EffectAddExtraAttacks: queue a bounded batch for the target's next melee update.</summary>
public sealed class ExtraAttackSpellEffects : ISpellHandlerModule
{
    public void Register(SpellSystem system)
        => system.RegisterEffect(SpellEffectName.AddExtraAttacks, static context =>
            context.Target.Combat.QueueExtraAttacks(context.Value));
}
