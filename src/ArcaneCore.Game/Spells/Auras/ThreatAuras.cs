using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_AURA_MOD_TAUNT (11) and SPELL_AURA_MOD_TOTAL_THREAT (103), after vmangos <c>Aura::HandleModTaunt</c> and
/// <c>Aura::HandleAuraModTotalThreat</c> (SpellAuras.cpp:3920-3967).
/// <list type="bullet">
/// <item>Mod taunt: the caster joins the target's taunt list while the aura lasts (the latest taunter is preferred, and the list
/// outranks the threat list when the creature picks its victim). A living target that can hold a threat list reacts
/// (<see cref="Taunt.Apply"/> on apply, <see cref="Taunt.FadeOut"/> on removal) when the caster is alive.</item>
/// <item>Mod total threat (Fade): on a living player with a living caster, the aura value is folded once into the player's threat
/// on every list that holds it and taken out again on removal (<see cref="HostileRefs.AddTempThreat"/>).</item>
/// </list>
/// MOD_THREAT and MOD_CRITICAL_THREAT stay queried by the threat calculation, not applied here.
/// </summary>
public sealed class ThreatAuras : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterAura(AuraType.ModTaunt, new AuraHandler(HandleModTaunt, null));
        system.RegisterAura(AuraType.ModTotalThreat, new AuraHandler(HandleModTotalThreat, null));
    }

    private static void HandleModTaunt(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        Unit target = holder.Target;
        if (apply)
        {
            target.Combat.Threat.AddTauntCaster(holder.CasterGuid);
        }
        else
        {
            target.Combat.Threat.RemoveTauntCaster(holder.CasterGuid);
        }

        if (!target.IsAlive || !ThreatRules.CanHaveThreatList(target) || holder.CasterOwner.Caster is not { IsAlive: true } caster)
        {
            return;
        }

        if (apply)
        {
            Taunt.Apply(target, caster);
        }
        else
        {
            Taunt.FadeOut(target, caster);
        }
    }

    private static void HandleModTotalThreat(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        Unit target = holder.Target;
        if (!target.IsAlive || target is not Player || holder.CasterOwner.Caster is not { IsAlive: true })
        {
            return;
        }

        HostileRefs.AddTempThreat(target, aura.Amount, apply);
    }
}
