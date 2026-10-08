using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;

namespace ArcaneCore.Game.Instances.Scripts.RuinsOfAhnQiraj;

/// <summary>
/// mangos-classic .../ruins_of_ahnqiraj/boss_kurinnaxx.cpp constructor/ExecuteAction:
/// Mortal Wound, Sand Trap, Thrash, Wide Slash, one enrage at &lt;=30% (retry on failed casts).
/// vmangos .../silithus/ruins_of_ahnqiraj/boss_kurinnaxx.cpp Aggro: zone combat.
/// Sand Trap uses vmangos' self-cast/random-threat-target spell script and SD2's four-second trap trigger.
/// </summary>
public sealed class KurinnaxxAI : RaidBossAI
{
    private bool _enraged;

    public KurinnaxxAI(Creature creature) : base(creature, 0)
    {
        AddAction(RandomDelay(8000, 10000), () => Cast(25646, Victim), () => RandomDelay(8000, 10000));
        AddAction(RandomDelay(5000, 10000), () => Cast(26524, Me, triggered: true), () => RandomDelay(10000, 15000));
        AddAction(RandomDelay(1000, 5000), () => Cast(3391), () => RandomDelay(12000, 17000));
        AddAction(RandomDelay(10000, 15000), () => Cast(25814, Victim), () => RandomDelay(12000, 15000));
    }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        System?.SetInCombatWithZone(Me);
    }

    protected override void ResetActions()
    {
        base.ResetActions();
        _enraged = false;
    }

    protected override void UpdateCombat(uint diffMs)
    {
        if (!_enraged && (ulong)Me.Health * 100 <= (ulong)Me.MaxHealth * 30 && Cast(26527))
        {
            _enraged = true;
            // vmangos boss_kurinnaxx.cpp EMOTE_FRENZY.
            System?.SayText(Me, 2384);
        }

        base.UpdateCombat(diffMs);
    }
}
