using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.BlackwingLair;

/// <summary>
/// mangos-classic .../blackwing_lair/boss_firemaw.cpp and boss_flamegor.cpp:
/// constructor, ExecuteAction, SpellHitTarget and encounter lifecycle. Shadow Flame, Wing Buffet and Thrash
/// are self-targeted area/proc spells; Wing Buffet halves threat on hits, not on cast requests.
/// </summary>
public abstract class BlackwingDrakeAI(Creature creature, uint encounter) : RaidBossAI(creature, encounter)
{
    protected void AddDrakeActions()
    {
        AddAction(18000, () => Cast(22539), () => RandomDelay(15000, 18000));
        AddAction(30000, () => Cast(23339), () => RandomDelay(30000, 35000));
    }

    protected void AddThrash() => AddAction(6000, () => Cast(3391), () => RandomDelay(2000, 6000));

    public override void OnSpellHitTarget(Unit target, SpellInfo spell)
    {
        if (spell.Id == 23339)
        {
            Me.Combat.Threat.ModifyThreatPercent(target, -50);
        }
    }
}

public sealed class FiremawAI : BlackwingDrakeAI
{
    public FiremawAI(Creature creature) : base(creature, 3)
    {
        AddDrakeActions();
        AddAction(5000, () => Cast(23341), () => 5000);
        AddThrash();
    }
}

public sealed class FlamegorAI : BlackwingDrakeAI
{
    public FlamegorAI(Creature creature) : base(creature, 5)
    {
        AddAction(10000, Frenzy, () => RandomDelay(10000, 15000));
        AddDrakeActions();
        AddThrash();
    }

    private bool Frenzy()
    {
        if (!Cast(23342))
        {
            return false;
        }

        // vmangos boss_flamegor.cpp EMOTE_FRENZY (broadcast_text rather than SD2 negative text ids).
        System?.SayText(Me, 1191);
        return true;
    }
}
