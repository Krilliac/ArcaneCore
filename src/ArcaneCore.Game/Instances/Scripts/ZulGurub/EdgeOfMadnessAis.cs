using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.ZulGurub;

// The Edge of Madness bosses. Gri'lek (15082) and Wushoolay (15085) are not scripted here: classic-db z2815 gives them AIName 'EventAI'
// (creature_ai_scripts 1508201-1508202 and 1508501-1508502) and no ScriptName, so the host's CreatureEventAI runs them.

/// <summary>ClassicDB z2815 creature_spell_list 1508301, and mangos-classic
/// zulgurub/zulgurubScripts.cpp SummonNightmareIllusion::OnEffectExecute.</summary>
public sealed class HazzarahAI : RaidBossAI
{
    public HazzarahAI(Creature creature) : base(creature, null)
    {
        AddAction(4000, 10000, () => Cast(24684, RandomTarget()), () => RandomDelay(8000, 16000));
        AddAction(10000, 18000, () => Cast(24664, RandomTarget()), () => RandomDelay(12000, 20000));
        AddAction(7000, 14000, () => Cast(24685, Victim), () => RandomDelay(9000, 16000));
        AddAction(10000, 18000, () => Cast(24728, Victim), () => RandomDelay(15000, 25000));
    }
}

[SpellScript(24728)]
public sealed class HazzarahIllusionScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 0) return;
        context.System.CastSpell(context.Caster, 24681, SpellCastTargets.ForSelf(), triggered: true);
        context.System.CastSpell(context.Caster, 24729, SpellCastTargets.ForSelf(), triggered: true);
    }
}

/// <summary>
/// mangos-classic zulgurub/boss_renataki.cpp: boss_renatakiAI (Reset, ReceiveAIEvent and the RENATAKI_VANISH_DELAY action), the
/// RenatakiVanish spell script (24699) and the RenatakiVanishTeleport aura script (24700), with the ClassicDB z2815
/// creature_spell_list 1508401 cadence (Thousand Blades, Vanish, Gouge, Thrash).
/// <para>
/// The vanish: the dummy effect of 24699 names an enemy it hit (<see cref="RenatakiVanishScript"/>), and Renataki stops fighting
/// (combat script status: no spell list, no melee, no combat movement). When the delay ends, that enemy casts 24700 on him: he is
/// teleported to the enemy, stunned briefly, and the enemy gains 500 threat. When the 24700 aura comes off, he casts Thrash on that enemy
/// and drops Vanish (<see cref="RenatakiVanishTeleportAuraModule"/>).
/// </para>
/// <para>
/// Two values do not come from mangos-classic. (1) The reference declares RENATAKI_VANISH_DELAY but never arms it. It is armed here
/// for 20 seconds: the duration of 24699 (DurationIndex 18) and the Visible_Timer of vmangos boss_renataki.cpp. (2) The Enrage (8269)
/// below 30 percent health comes from vmangos boss_renataki.cpp UpdateAI (GetHealthPercent() &lt; 30, CF_AURA_NOT_PRESENT).
/// mangos-classic and the spell list have no enrage. Nothing else is taken from vmangos: there is no Ambush and no threat reset.
/// </para>
/// </summary>
public sealed class RenatakiAI : RaidBossAI
{
    public const uint ThousandBlades = 24649, Vanish = 24699, VanishTeleport = 24700, Gouge = 24698, Thrash = 3391, Enrage = 8269;

    /// <summary>The unarmed RENATAKI_VANISH_DELAY of the reference, armed for the 20 s duration of 24699.</summary>
    public const uint VanishDelayMs = 20000;

    private Unit? _vanishTarget;
    private SpellSystem? _spells;
    private uint _vanishMs;

    public RenatakiAI(Creature creature) : base(creature, null)
    {
        AddAction(4000, 8000, () => !Vanished && Cast(ThousandBlades, Victim), () => RandomDelay(7000, 12000));
        AddAction(25000, 30000, () => !Vanished && Cast(Vanish, Me), () => RandomDelay(25000, 40000));
        AddAction(15000, 25000, () => !Vanished && Cast(Gouge, Me), () => RandomDelay(7000, 20000));
        AddAction(10000, 15000, () => !Vanished && Cast(Thrash, Victim), () => RandomDelay(10000, 15000));
    }

    /// <summary>Whether the vanish's combat script is running (SetCombatScriptStatus(true) until the delay ends).</summary>
    public bool Vanished => _vanishMs != 0;

    /// <summary>
    /// boss_renatakiAI::ReceiveAIEvent(AI_EVENT_CUSTOM_A), sent by RenatakiVanish with the enemy hit as the invoker: remember that enemy
    /// and stop fighting. <paramref name="spells"/> is the spell system that cast 24699; the enemy casts 24700 through it when the delay
    /// ends (without one, the vanish ends with no teleport).
    /// </summary>
    public void OnVanishHit(Unit target, SpellSystem? spells)
    {
        ArgumentNullException.ThrowIfNull(target);
        _vanishTarget = target;
        _spells = spells;
        _vanishMs = VanishDelayMs;
        SetMeleeEnabled(false);
        CombatMovement = false;
    }

    /// <summary>RENATAKI_VANISH_DELAY: the vanish target casts 24700 on Renataki, then his combat script ends.</summary>
    private void EndVanish()
    {
        if (_vanishTarget is { IsAlive: true, IsInWorld: true } target && ReferenceEquals(target.Map, Me.Map))
            _spells?.CastSpell(target, VanishTeleport, SpellCastTargets.ForUnit(Me.Guid), triggered: true);
        _vanishTarget = null;
        _spells = null;
        SetMeleeEnabled(true);
        CombatMovement = true;
    }

    /// <summary>cmangos UnitAI::SetMeleeEnabled: a change starts or stops the swing at the current victim.</summary>
    private void SetMeleeEnabled(bool enabled)
    {
        if (enabled == MeleeEnabled) return;
        MeleeEnabled = enabled;
        if (Victim is { } victim) System?.SetMelee(Me, victim, enabled);
    }

    /// <summary>boss_renatakiAI::Reset: combat script off, melee and combat movement on.</summary>
    protected override void ResetActions()
    {
        base.ResetActions();
        _vanishTarget = null;
        _spells = null;
        _vanishMs = 0;
        SetMeleeEnabled(true);
        CombatMovement = true;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_vanishMs != 0)
        {
            _vanishMs = _vanishMs > diffMs ? _vanishMs - diffMs : 0;
            if (_vanishMs == 0) EndVanish();
        }
        base.OnUpdate(diffMs);
    }

    protected override void UpdateCombat(uint diffMs)
    {
        // vmangos boss_renataki.cpp UpdateAI: DoCastSpellIfCan(m_creature, SPELL_ENRAGE, CF_AURA_NOT_PRESENT) below 30 percent.
        if (HealthBelowPct(30) && System?.HasAura(Me, Enrage) != true) Cast(Enrage, Me);
        base.UpdateCombat(diffMs);
    }
}

/// <summary>mangos-classic boss_renataki.cpp RenatakiVanish::OnEffectExecute: effect 0 of Vanish (24699, a dummy on the enemies around
/// him) sends the caster's AI the enemy hit (AI_EVENT_CUSTOM_A). Other casters of 24699 (the Alterac Valley invisibility) are ignored.</summary>
[SpellScript(RenatakiAI.Vanish)]
public sealed class RenatakiVanishScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 0 || context.Caster is not Creature { AI: RenatakiAI ai } || ReferenceEquals(context.Target, context.Caster))
            return;
        ai.OnVanishHit(context.Target, context.System);
    }
}

/// <summary>mangos-classic boss_renataki.cpp RenatakiVanishTeleport::OnApply(apply = false): when the 24700 aura leaves its target
/// (Renataki), the target casts Thrash (3391) on the aura's caster and loses Vanish (24699).</summary>
public sealed class RenatakiVanishTeleportAuraModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.HolderRemoved += holder => OnRemoved(system, holder);
    }

    private static void OnRemoved(SpellSystem system, SpellAuraHolder holder)
    {
        if (holder.Spell.Id != RenatakiAI.VanishTeleport || holder.Target is not { IsAlive: true } target) return;
        if (target.Map?.FindObject(holder.CasterGuid) is Unit caster)
            system.CastSpell(target, RenatakiAI.Thrash, SpellCastTargets.ForUnit(caster.Guid), triggered: true);
        system.RemoveAuras(target, RenatakiAI.Vanish);
    }
}
