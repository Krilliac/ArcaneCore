using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.BlackwingLair;

/// <summary>
/// mangos-classic .../blackwing_lair/boss_broodlord_lashlayer.cpp boss_broodlordAI:
/// constructor/ExecuteAction timers, Aggro/JustDied/JustReachedHome, z &lt; 448.60 leash.
/// vmangos .../burning_steppes/blackwing_lair/boss_broodlord_lashlayer.cpp SpellHitTarget:
/// Knock Away halves the threat of each hit unit; broadcast texts 9967/9968.
/// </summary>
public sealed class BroodlordAI : RaidBossAI
{
    public BroodlordAI(Creature creature) : base(creature, 2)
    {
        AddAction(8000, () => Cast(15284, Victim), () => 7000);
        AddAction(12000, () => Cast(18670, Victim), () => RandomDelay(15000, 30000));
        AddAction(20000, () => Cast(23331), () => RandomDelay(8000, 16000));
        AddAction(30000, () => Cast(24573, Victim), () => RandomDelay(25000, 35000));
    }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        System?.SayText(Me, 9967);
    }

    public override void OnSpellHitTarget(Unit target, SpellInfo spell)
    {
        if (spell.Id == 18670)
        {
            Me.Combat.Threat.ModifyThreatPercent(target, -50);
        }
    }

    protected override void UpdateCombat(uint diffMs)
    {
        if (Me.Z < 448.60f)
        {
            System?.SayText(Me, 9968);
            EnterEvadeMode();
            return;
        }

        base.UpdateCombat(diffMs);
    }
}
