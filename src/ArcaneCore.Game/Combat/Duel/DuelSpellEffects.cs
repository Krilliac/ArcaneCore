using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// SPELL_EFFECT_DUEL (83), the effect of spell 7266 "Duel" (classic-db spell_template: Effect1 83, TargetA 25 TARGET_UNIT, EffectMiscValue 21680, the
/// Duel Flag gameobject, type 16). vmangos Spell::EffectDuel, SpellEffects.cpp:4650-4761; the rules live in <see cref="DuelService.Challenge"/>. Discovered
/// like every <see cref="ISpellHandlerModule"/>. A spell system without a duel service (<see cref="DuelService.Install"/>) refuses the spell with
/// SPELL_FAILED_NO_DUELING instead of reporting an unsupported effect.
/// </summary>
public sealed class DuelSpellEffects : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffect(SpellEffectName.Duel, EffectDuel);
    }

    private static void EffectDuel(SpellEffectContext context)
    {
        if (context.Caster is not Player caster || context.Target is not Player target)
        {
            return; // vmangos: "!m_casterUnit->IsPlayer() || !unitTarget->IsPlayer()" returns
        }

        SpellCastResult? failure = DuelService.ForSpells(context.System) is { } service
            ? service.Challenge(caster, target, (uint)context.Effect.MiscValue, context.Spell.GetDuration())
            : SpellCastResult.NoDueling;
        if (failure is { } result)
        {
            // SendCastResult at effect time: the failure packet for the spell, to the caster only.
            caster.Session.Send(WorldOpcode.SmsgCastResult, SpellPackets.BuildCastResult(context.Spell.Id, result));
        }
    }
}
