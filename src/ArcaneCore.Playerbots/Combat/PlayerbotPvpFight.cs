using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>
/// One fight of a managed bot against a unit that is not a creature of its own grind: the class rotation of <see cref="PlayerbotCombatSpells"/>,
/// the positioning of <see cref="PlayerbotBrain.DecidePosition"/> and the melee swing. The battleground AI fights enemy players with it and the
/// brain defends itself against a player attacker and holds a defend post with it; they decide whom to fight (<see cref="PlayerbotHostility"/>),
/// this decides how, think by think. World thread only; one per bot, owned by the AI that drives the bot. The caller owns the route: it passes
/// the walk and the route reset in.
/// </summary>
internal sealed class PlayerbotPvpFight(WorldSession session, PlayerbotCombatSpells spells)
{
    /// <summary>A mana user below this share of its mana fights in melee (vmangos CombatBotBaseAI::BeginChasing's 10%).</summary>
    internal const int LowManaPct = 10;

    /// <summary>The unit the bot fights (inspection and tests); null between fights.</summary>
    internal Unit? Target { get; set; }

    /// <summary>The attack swing was submitted for <see cref="Target"/>.</summary>
    internal bool Attacking { get; set; }

    /// <summary>How long a ranged bot has had nothing to cast at its range in this fight (it closes in after <see cref="PlayerbotBrain.RangedIdleLimitMs"/>).</summary>
    internal uint RangedIdleMs { get; set; }

    /// <summary>
    /// One think of the fight against <paramref name="target"/>: cast, else keep the fighting distance, else face and swing. A new target resets the
    /// fight state and the route (<paramref name="dropRoute"/>). <paramref name="walkTo"/> walks to a point (the goal, the think interval, whether
    /// it is a chase) and answers false when the walk is refused, which drops the target.
    /// </summary>
    internal void Fight(Player player, Unit target, uint interval, Func<Vector3, uint, bool, bool> walkTo, Action dropRoute)
    {
        if (!ReferenceEquals(Target, target))
        {
            Target = target;
            Attacking = false;
            dropRoute();
            RangedIdleMs = 0;
        }

        if (spells.Update(player, target, interval))
        {
            RangedIdleMs = 0;
            return;
        }

        float distance = Vector3.Distance(Position(player), Position(target));
        // A ranged bot with nothing to cast at its range (out of mana, or no spell for this target) closes in and swings instead of standing
        // there for the whole fight (live 2026-10-16 ~20:10-20:22: Bokae, a mage, and Poforound, a shaman, held in the Horde flag room below
        // 10% mana for 45 s of one fight each while an enemy fought there). vmangos CombatBotBaseAI::BeginChasing keeps the 25-yard caster
        // distance only above 10% mana or with a ranged weapon; the brain closes in after RangedIdleLimitMs (PlayerbotBrain.Update).
        PlayerbotFightPosition position = PlayerbotBrain.DecidePosition(spells.PreferredRange(player), distance,
            player.Class == Class.Hunter, ReferenceEquals(target.Combat.Victim, player),
            idleTooLong: RangedIdleMs >= PlayerbotBrain.RangedIdleLimitMs || LowMana(player),
            inMeleeReach: MapCombat.CanReachWithMeleeAutoAttack(player, target));
        switch (position)
        {
            case PlayerbotFightPosition.Hold:
                RangedIdleMs = unchecked(RangedIdleMs + Math.Max(1u, interval));
                dropRoute();
                if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0) PlayerbotMovementControl.Stop(session, player);
                return;
            case PlayerbotFightPosition.ChaseToRange or PlayerbotFightPosition.ChaseToMelee:
                if (!walkTo(Position(target), interval, true)) Target = null;
                return;
        }

        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0)
        {
            PlayerbotMovementControl.Stop(session, player);
            dropRoute();
            return;
        }

        // The server refuses a swing outside the auto-attack arc; a managed player turns to its victim as the client does.
        if (MathF.Sqrt(MathF.Pow(target.X - player.X, 2) + MathF.Pow(target.Y - player.Y, 2)) > CombatConstants.NoFacingChecksDistance
            && !MapCombat.HasInArc(player, target, CombatConstants.AutoAttackArc))
        {
            PlayerbotMotion.Face(session, player, MathF.Atan2(target.Y - player.Y, target.X - player.X));
            return;
        }

        if (!Attacking || !ReferenceEquals(player.Combat.Victim, target))
            Attacking = Groups.PlayerbotGroupCoordinator.Act(session, WorldOpcode.CmsgAttackswing, PlayerbotNavigation.GuidPayload(target.Guid.Value), budgeted: true);
    }

    /// <summary>Stop swinging (CMSG_ATTACKSTOP when the bot has a victim) and forget the target.</summary>
    internal void Stop(Player player)
    {
        if (player.Combat.Victim is not null)
            Groups.PlayerbotGroupCoordinator.Act(session, WorldOpcode.CmsgAttackstop, [], budgeted: false);
        Attacking = false;
        Target = null;
    }

    /// <summary>A mana user below <see cref="LowManaPct"/> of its mana (vmangos CombatBotBaseAI::BeginChasing's 10%).</summary>
    internal static bool LowMana(Player player)
    {
        if (player.PowerType != PowerType.Mana) return false;
        uint max = player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana);
        return max > 0 && Game.Spells.SpellSystem.GetPower(player, PowerType.Mana) * 100UL < max * (ulong)LowManaPct;
    }

    private static Vector3 Position(WorldObject o) => o is Player p ? PlayerbotMotion.CurrentPosition(p) : new(o.X, o.Y, o.Z);
}
