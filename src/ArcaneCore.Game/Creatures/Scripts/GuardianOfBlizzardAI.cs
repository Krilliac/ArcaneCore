using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Guardian of Blizzard (entry 5764, <c>npc_guardian</c>): cmangos-classic <c>npc_guardianAI</c>
/// (AI/ScriptDevAI/scripts/world/npcs_special.cpp:748-785). Has melee disabled, says broadcast text 2077
/// on aggro, and casts Deathtouch (spell 5, triggered) on its victim whenever its base attack timer is ready.
/// </summary>
public sealed class GuardianOfBlizzardAI : CreatureAI
{
    public const uint Entry = 5764;
    public const string ScriptName = "npc_guardian";
    public const uint SpellDeathtouch = 5;
    public const int SayAggro = 2077;

    public GuardianOfBlizzardAI(Creature creature)
        : base(creature)
    {
        MeleeEnabled = false;
    }

    public override bool AggroesOnSight => true;

    public override void OnAggro(Unit target)
    {
        System?.SayText(Me, SayAggro, target);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!UpdateVictim() || Victim is not { } victim)
        {
            return;
        }

        if (Me.Combat.IsAttackReady(WeaponAttackType.BaseAttack))
        {
            DoCast(victim, SpellDeathtouch, triggered: true);
            Me.Combat.ResetAttackTimer(WeaponAttackType.BaseAttack);
        }
    }
}
