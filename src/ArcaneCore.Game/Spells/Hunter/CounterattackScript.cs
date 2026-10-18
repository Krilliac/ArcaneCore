using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Hunter;

/// <summary>
/// Counterattack (19306, 20909, 20910; vmangos scripts/spells/spell_hunter.cpp:121-136, <c>HunterCounterAttackScript::OnCheckCast</c>). The spell data
/// already needs the caster aura state HUNTER_PARRY (CasterAuraState 7), which a hunter's parry opens for four seconds (Unit::ProcSkillsAndReactives).
/// The script adds the target rule: the cast must be at the unit whose attack was parried, vmangos <c>GetReactiveTarget(REACTIVE_HUNTER_PARRY)</c>;
/// any other target is BAD_TARGETS.
/// <para>
/// The parried attacker is read from <see cref="UnitCombat.GetReactiveTarget"/> (or the hunter's combo target when set directly: the same parry that
/// opens the window gives the hunter one combo point on that attacker in <c>Unit::ProcSkillsAndReactives</c>).
/// </para>
/// </summary>
[SpellScript(19306, 20909, 20910)]
public sealed class CounterattackScript : ISpellScript
{
    public static readonly uint[] Ranks = [19306, 20909, 20910];

    public SpellCastResult OnCheckCast(in SpellCastCheckContext context)
    {
        if (context.Caster is not Player hunter)
        {
            return SpellCastResult.CastOk; // vmangos asks m_casterUnit; only a player keeps the marker here
        }

        ObjectGuid parried = hunter.Combat.GetReactiveTarget(ReactiveType.HunterParry);
        if (parried.IsEmpty)
        {
            parried = new ObjectGuid(hunter.GetUInt64(UpdateFields.PlayerFieldComboTarget));
        }

        return context.Target is { } target && !parried.IsEmpty && target.Guid == parried
            ? SpellCastResult.CastOk
            : SpellCastResult.BadTargets;
    }
}

/// <summary>
/// Mongoose Bite (1495, 14269, 14270, 14271; vmangos scripts/spells/spell_hunter.cpp:105-119, <c>HunterMongooseBiteScript::OnCheckCast</c>).
/// The spell data requires <see cref="AuraState.Defense"/> on the caster; the script requires the explicit target to be the unit that opened the
/// <see cref="ReactiveType.Defense"/> window (<see cref="UnitCombat.GetReactiveTarget"/>), returning <see cref="SpellCastResult.BadTargets"/> otherwise.
/// </summary>
[SpellScript(1495, 14269, 14270, 14271)]
public sealed class MongooseBiteScript : ISpellScript
{
    public static readonly uint[] Ranks = [1495, 14269, 14270, 14271];

    public SpellCastResult OnCheckCast(in SpellCastCheckContext context)
    {
        ObjectGuid defenseTarget = context.Caster.Combat.GetReactiveTarget(ReactiveType.Defense);
        return context.Target is { } target && !defenseTarget.IsEmpty && target.Guid == defenseTarget
            ? SpellCastResult.CastOk
            : SpellCastResult.BadTargets;
    }
}

/// <summary>
/// Registers the hunter aura scripts (discovered <see cref="ISpellHandlerModule"/>):
/// <list type="bullet">
/// <item>Wyvern Sting (19386, 24132, 24133; vmangos spell_hunter.cpp:19-56, <c>HunterWyvernStingScript::OnAfterApply</c>): when the sleep aura
/// (effect 0) is removed from its target and its caster is a player in the world, the caster casts the rank's wake-up poison DoT (24131, 24134,
/// 24135) on the target, triggered by the aura.</item>
/// <item>Frost Trap Aura (13810; vmangos spell_hunter.cpp:137-154, <c>HunterFrostTrapAuraScript::OnPeriodicTrigger</c>): each periodic trigger tick
/// fires <see cref="ProcFlags.OnTrapActivation"/> on the aura's caster against the affected unit so Entrapment (19184, 19387, 19388) can proc.</item>
/// </list>
/// </summary>
public sealed class HunterScriptsModule : ISpellHandlerModule
{
    public const uint FrostTrapAura = 13810;

    /// <summary>Wyvern Sting sleep rank → wake-up DoT rank (spell_hunter.cpp:32-46).</summary>
    public static readonly IReadOnlyDictionary<uint, uint> WyvernStingDots = new Dictionary<uint, uint>
    {
        [19386] = 24131,
        [24132] = 24134,
        [24133] = 24135,
    };

    /// <summary>
    /// Hunter "Taming the Beast" quest chain channel dummy auras → completion spells (vmangos Aura::HandleAuraDummy, SpellAuras.cpp:1816-1870).
    /// When the channel finishes (<see cref="AuraRemoveMode.Channel"/>) and both caster and target are alive, the caster casts the completion spell
    /// on the target.
    /// </summary>
    public static readonly IReadOnlyDictionary<uint, uint> TamingRodSpells = new Dictionary<uint, uint>
    {
        [19548] = 19597, // Tame Adult Plainstrider
        [19674] = 19677, // Tame Armored Scorpid
        [19687] = 19676, // Tame Nightsaber Stalker
        [19688] = 19678, // Tame Prairie Stalker
        [19689] = 19679, // Tame Surf Crawler
        [19692] = 19680, // Tame Swoop
        [19693] = 19684, // Tame Webwood Lurker
        [19694] = 19681, // Tame Dire Mottled Boar
        [19696] = 19682, // Tame Strigid Screecher
        [19697] = 19683, // Tame Barbed Crawler
        [19699] = 19685, // Tame Greater Timberstrider
        [19700] = 19686, // Tame Nightstalker
    };

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.HolderRemoved += holder => OnHolderRemoved(system, holder);
        system.RegisterPeriodicTriggerScript(FrostTrapAura, OnFrostTrapTick);
    }

    private static void OnHolderRemoved(SpellSystem system, SpellAuraHolder holder)
    {
        if (TamingRodSpells.TryGetValue(holder.Spell.Id, out uint tamedSpell))
        {
            if (holder.RemoveMode == AuraRemoveMode.Channel
                && system.AuraCaster(holder) is { IsAlive: true } tamingCaster
                && holder.Target.IsAlive
                && system.Store.Get(tamedSpell) is not null)
            {
                system.CastSpellTriggeredByAura(tamingCaster, tamedSpell, SpellCastTargets.ForUnit(holder.Target.Guid), holder.Spell);
            }

            return;
        }

        if (holder.Auras[0] is null || !WyvernStingDots.TryGetValue(holder.Spell.Id, out uint dotSpell)
            || system.AuraCaster(holder) is not Player caster || system.Store.Get(dotSpell) is null)
        {
            return;
        }

        system.CastSpellTriggeredByAura(caster, dotSpell, SpellCastTargets.ForUnit(holder.Target.Guid), holder.Spell);
    }

    private static void OnFrostTrapTick(SpellSystem system, SpellAuraHolder holder, SpellAura aura)
    {
        Unit? caster = system.AuraCaster(holder);
        if (caster is not null)
        {
            system.ProcDamageAndSpell(caster, new ProcEvent
            {
                Victim = holder.Target,
                AttackerFlags = ProcFlags.OnTrapActivation,
                Extra = ProcFlagsEx.NormalHit,
                Amount = 1,
                OriginalAmount = 1,
                AttackType = WeaponAttackType.BaseAttack,
                ProcSpell = holder.Spell,
            });
        }

        uint triggerSpell = holder.Spell.Effects[aura.EffectIndex].TriggerSpell;
        if (triggerSpell != 0 && system.Store.Get(triggerSpell) is not null)
        {
            system.CastSpellTriggeredByAura(caster ?? holder.Target, triggerSpell, SpellCastTargets.ForUnit(holder.Target.Guid), holder.Spell);
        }
    }
}

