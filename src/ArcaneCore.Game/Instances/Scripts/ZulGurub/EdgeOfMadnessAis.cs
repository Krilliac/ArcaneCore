using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.ZulGurub;

/// <summary>ClassicDB z2815 creature_ai_scripts 1508201-1508202, the Gri'lek
/// Avatar and Ground Tremor cadence. The event-day choice is made by game events 29-32.</summary>
public sealed class GrilekAI : RaidBossAI
{
    public GrilekAI(Creature creature) : base(creature, null)
    {
        AddAction(15000, 20000, Avatar, () => RandomDelay(25000, 35000));
        AddAction(8000, 16000, () => Cast(6524, Me), () => RandomDelay(12000, 16000));
    }
    private bool Avatar()
    {
        if (!Cast(24646, Me)) return false;
        if (Victim is { } victim) Me.Combat.Threat.ModifyThreatPercent(victim, -50);
        if (RandomTarget() is { } target) Me.Combat.Threat.AddThreat(target, 50);
        return true;
    }
}

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

/// <summary>mangos-classic zulgurub/boss_renataki.cpp Reset/ExecuteAction and
/// ClassicDB z2815 creature_spell_list 1508401.</summary>
public sealed class RenatakiAI : RaidBossAI
{
    private bool _vanished, _enraged;
    private uint _vanishMs;
    public RenatakiAI(Creature creature) : base(creature, null)
    {
        AddAction(4000, 8000, () => !_vanished && Cast(24649, Victim), () => RandomDelay(7000, 12000));
        AddAction(25000, 30000, Vanish, () => RandomDelay(25000, 40000));
        AddAction(15000, 25000, () => !_vanished && Cast(24698, Me), () => RandomDelay(7000, 20000));
        AddAction(10000, 15000, () => !_vanished && Cast(3391, Me), () => RandomDelay(10000, 15000));
    }
    private bool Vanish()
    {
        if (!Cast(24699, Me)) return false;
        Cast(24700, Me, triggered: true);
        _vanished = true;
        _vanishMs = 20000;
        ResetThreat();
        return true;
    }
    protected override void ResetActions() { base.ResetActions(); _vanished = _enraged = false; _vanishMs = 0; }
    public override void OnUpdate(uint diffMs)
    {
        if (_vanished)
        {
            _vanishMs = _vanishMs > diffMs ? _vanishMs - diffMs : 0;
            if (_vanishMs == 0)
            {
                _vanished = false;
                System?.RemoveAuras(Me, 24699);
                if (RandomTarget() is { } target) Cast(24337, target, triggered: true);
            }
        }
        base.OnUpdate(diffMs);
    }
    protected override void UpdateCombat(uint diffMs)
    {
        if (!_enraged && Below(29) && Cast(8269, Me)) _enraged = true;
        base.UpdateCombat(diffMs);
    }
}

/// <summary>ClassicDB z2815 creature_ai_scripts 1508501-1508502: Lightning
/// Cloud and Lightning Wave with their original cooldown ranges.</summary>
public sealed class WushoolayAI : RaidBossAI
{
    public WushoolayAI(Creature creature) : base(creature, null)
    {
        AddAction(5000, 10000, () => Cast(25033, Me), () => RandomDelay(15000, 20000));
        AddAction(8000, 16000, () => Cast(24819, RandomTarget()), () => RandomDelay(12000, 16000));
    }
}
