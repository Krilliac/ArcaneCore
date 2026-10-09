using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Naxxramas;

/// <summary>vmangos naxxramas/boss_anubrekhan.cpp mob_cryptguardsAI::Aggro/UpdateAI:
/// web, cleave, acid, half-health enrage. Reimplemented from behaviour.</summary>
public sealed class NaxxramasCryptGuardAI(Creature creature, NaxxramasInstance instance)
    : RaidCreatureAI(creature, instance, null)
{
    private bool _enraged;

    protected override void Reset()
    {
        base.Reset();
        _enraged = false;
        Schedule(12000, 12000, 12000, 12000, () =>
        {
            if (!Cast(28991)) return false;
            foreach (var threat in Me.Combat.Threat.Entries.ToArray())
                Me.Combat.Threat.ModifyThreatPercent(threat.Target, -100);
            if (RandomTarget() is { } target) Me.Combat.Threat.AddThreat(target, 1);
            return true;
        });
        Spell(26350, 6000, 6000, 6000, 6000, () => Victim);
        Spell(28969, 5000, 5000, 5000, 5000, () => Victim);
    }

    public override void OnAggro(Unit target)
    {
        if (System?.Creatures.FirstOrDefault(c => c.Entry == 15956 && c.IsAlive && !c.Combat.IsInCombat)
            is { AI: { } anub }) anub.AttackStart(target);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!Me.Combat.IsInCombat || !UpdateVictim()) return;
        if (!_enraged && Below(50) && Cast(28747)) _enraged = true;
        TickActions(diffMs);
    }
}
