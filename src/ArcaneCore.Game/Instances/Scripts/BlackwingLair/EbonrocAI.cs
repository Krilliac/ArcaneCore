using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.BlackwingLair;

/// <summary>
/// mangos-classic blackwing_lair/boss_ebonroc.cpp boss_ebonrocAI constructor/ExecuteAction:
/// Shadow of Ebonroc on the victim, then the common drake breath, buffet and thrash.
/// </summary>
public sealed class EbonrocAI : BlackwingDrakeAI
{
    public EbonrocAI(Creature creature) : base(creature, 4)
    {
        AddAction(45000, () => Cast(23340, Victim), () => RandomDelay(25000, 35000));
        AddDrakeActions();
        AddThrash();
    }
}
