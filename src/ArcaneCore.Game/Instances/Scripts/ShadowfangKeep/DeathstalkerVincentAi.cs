using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>Vincent's staged death, from mangos-classic shadowfang_keep/shadowfang_keep.cpp:570-611
/// (npc_deathstalker_vincentAI::Reset/DamageTaken/UpdateAI). The combat engine's invincibility
/// threshold clamps a lethal hit at one health; the following AI tick changes faction and evades.</summary>
public sealed class DeathstalkerVincentAi(Creature creature, ShadowfangKeepInstance instance) : AggressorAI(creature)
{
    public override void OnRespawn()
    {
        Me.InvincibilityHpThreshold = 1;
        Me.StandState = instance.GetData(ShadowfangKeepInstance.TypeIntro) == EncounterState.Done
            ? StandState.Dead : StandState.Stand;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (Me.Health <= 1 && Me.FactionTemplate != 35)
        {
            Me.StandState = StandState.Stand;
            Me.FactionTemplate = 35;
            EnterEvadeMode();
            System?.SayText(Me, -1033016);
            return;
        }

        if (Me.Combat.IsInCombat && Me.FactionTemplate == 35)
        {
            EnterEvadeMode();
            return;
        }

        UpdateVictim();
    }
}
