using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Tests.Spells.ContractSweep;

/// <summary>
/// One action an aura's AuraInterruptFlags can name, performed through the production entry point the game uses for it
/// (the movement handler for a move, a real melee swing for damage, ...). <see cref="Flags"/> is every Spell.dbc
/// AuraInterruptFlags bit the action legitimately fires; an aura carrying none of them must survive the action.
/// </summary>
/// <param name="Name">Stable name (part of the baseline key).</param>
/// <param name="Flags">The AuraInterruptFlags bits this action fires.</param>
/// <param name="PlayerOnly">Only a player can perform it (client movement, stand state).</param>
/// <param name="Perform">Performs the action on the unit; false when it could not be performed (then the check is recorded as not run).</param>
/// <param name="StateFlags">Bits the action also fires given the unit's state before it (a hit stands a seated player up: STANDING).</param>
internal sealed record SweepAction(string Name, uint Flags, bool PlayerOnly, Func<SweepScene, Unit, bool> Perform, Func<Unit, uint>? StateFlags = null)
{
    /// <summary>Every bit the action fires on <paramref name="unit"/> in its current state.</summary>
    public uint FlagsFor(Unit unit) => Flags | (StateFlags?.Invoke(unit) ?? 0);
}

/// <summary>The actions the interrupt check (c) performs. A test double replaces this to prove the check can fail.</summary>
internal interface ISweepActions
{
    IReadOnlyList<SweepAction> Actions { get; }

    /// <summary>Move the player forward through the production movement path (also the cast/channel movement check, d).</summary>
    bool Move(SweepScene scene, Player player);
}

/// <summary>The production action drivers.</summary>
internal sealed class ProductionSweepActions : ISweepActions
{
    /// <summary>Flags no driver here fires: the report counts the spells whose check for these bits could not run.</summary>
    public const uint UndrivenFlags = AuraInterruptMask.Anim | AuraInterruptMask.Dismount | AuraInterruptMask.UnderWater | AuraInterruptMask.AboveWater
        | AuraInterruptMask.Sheathing | AuraInterruptMask.Interacting | AuraInterruptMask.Looting | AuraInterruptMask.ItemUse
        | AuraInterruptMask.DamageChannelDuration | AuraInterruptMask.Shapeshifting | AuraInterruptMask.LeaveWorld
        | AuraInterruptMask.StealthInvisibility | AuraInterruptMask.InvulnerabilityBuff | AuraInterruptMask.EnterWorld;

    public ProductionSweepActions()
    {
        Actions =
        [
            new("move", AuraInterruptMask.Moving, PlayerOnly: true, (scene, unit) => Move(scene, (Player)unit)),
            new("turn", AuraInterruptMask.Turning, PlayerOnly: true, (scene, unit) => Turn(scene, (Player)unit)),
            // MapCombat.DealDamage stands a seated player up (vmangos Unit::DealDamage), which fires STANDING_CANCELS; a spell with that
            // bit seats its caster (food, drink), so the hit legitimately ends it.
            new(MeleeHitTakenName, AuraInterruptMask.Damage, PlayerOnly: false, MeleeHitTaken,
                unit => unit is Player && unit.StandState is not (StandState.Stand or StandState.Dead) ? AuraInterruptMask.Standing : 0),
            new("melee-swing", AuraInterruptMask.Attacking, PlayerOnly: false, MeleeSwing),
            new("cast", AuraInterruptMask.Action | AuraInterruptMask.ActionLate, PlayerOnly: true, (scene, unit) => CastOther(scene, (Player)unit)),
            new("mount", AuraInterruptMask.Mount, PlayerOnly: false, (scene, unit) => MountService.Mount(scene.Rig.System, unit, MountDisplay)),
            new("stand-up", AuraInterruptMask.Standing, PlayerOnly: true, (scene, unit) => StandUp((Player)unit)),
        ];
    }

    /// <summary>The name of the melee-hit-taken action (the interrupt check exempts procFlags auras from its DAMAGE bit, as vmangos does).</summary>
    public const string MeleeHitTakenName = "melee-hit-taken";

    /// <summary>A horse model (CreatureDisplayInfo 2404, the Brown Horse); any display id mounts.</summary>
    public const uint MountDisplay = 2404;

    public IReadOnlyList<SweepAction> Actions { get; }

    bool ISweepActions.Move(SweepScene scene, Player player) => Move(scene, player);

    /// <summary>MSG_MOVE_START_FORWARD with a 3-yard step, through <see cref="MovementHandlers.ApplyObserved"/> (the client movement path).</summary>
    public static bool Move(SweepScene scene, Player player)
    {
        MovementInfo movement = player.Movement;
        movement.Flags = (movement.Flags & ~(MovementFlags.MaskMoving | MovementFlags.TurnLeft | MovementFlags.TurnRight)) | MovementFlags.Forward;
        movement.X += 3f * MathF.Cos(movement.Orientation);
        movement.Y += 3f * MathF.Sin(movement.Orientation);
        movement.Time = scene.Rig.World.NowMs;
        MovementHandlers.ApplyObserved(scene.Session, player, WorldOpcode.MsgMoveStartForward, movement);
        movement = player.Movement;
        movement.Flags &= ~MovementFlags.MaskMoving;
        movement.Time = scene.Rig.World.NowMs;
        MovementHandlers.ApplyObserved(scene.Session, player, WorldOpcode.MsgMoveStop, movement);
        return true;
    }

    /// <summary>MSG_MOVE_START_TURN_LEFT and a changed facing, then MSG_MOVE_STOP_TURN, through the client movement path.</summary>
    public static bool Turn(SweepScene scene, Player player)
    {
        MovementInfo movement = player.Movement;
        movement.Flags = (movement.Flags & ~(MovementFlags.MaskMoving | MovementFlags.TurnRight)) | MovementFlags.TurnLeft;
        movement.Orientation = (movement.Orientation + 1f) % (2f * MathF.PI);
        movement.Time = scene.Rig.World.NowMs;
        MovementHandlers.ApplyObserved(scene.Session, player, WorldOpcode.MsgMoveStartTurnLeft, movement);
        movement = player.Movement;
        movement.Flags &= ~MovementFlags.TurnLeft;
        movement.Time = scene.Rig.World.NowMs;
        MovementHandlers.ApplyObserved(scene.Session, player, WorldOpcode.MsgMoveStopTurn, movement);
        return true;
    }

    /// <summary>A hostile unit's white swings at <paramref name="victim"/> (MapCombat.AttackerStateUpdate) until one does damage.</summary>
    public static bool MeleeHitTaken(SweepScene scene, Unit victim)
    {
        Unit attacker = victim is Player ? scene.Striker() : scene.EnemyPlayer();
        return SwingUntilHit(scene, attacker, victim);
    }

    /// <summary>
    /// <paramref name="attacker"/>'s own white swings at an enemy. The struck creature does not answer (passive): its answering hit would be
    /// damage taken, which is the melee-hit-taken action's to check, not this one's.
    /// </summary>
    public static bool MeleeSwing(SweepScene scene, Unit attacker)
    {
        Unit victim = attacker is Player ? scene.Striker() : scene.EnemyPlayer();
        if (victim is Creature struck)
        {
            struck.ReactState = CreatureReactState.Passive;
        }

        return SwingUntilHit(scene, attacker, victim);
    }

    private static bool SwingUntilHit(SweepScene scene, Unit attacker, Unit victim)
    {
        MapCombat combat = scene.Rig.Map.Combat;
        for (int i = 0; i < 20; i++)
        {
            MeleeDamageInfo? info = combat.AttackerStateUpdate(attacker, victim, WeaponAttackType.BaseAttack);
            if (info is { TotalDamage: > 0 })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The player starts and completes another, harmless spell (Spell.cpp prepare: ACTION, cast: ACTION_LATE).</summary>
    public static bool CastOther(SweepScene scene, Player player)
    {
        PlayerCheats saved = player.Cheats;
        player.Cheats = saved | PlayerCheats.NoCastTime | PlayerCheats.NoPowerCost | PlayerCheats.NoCooldown;
        try
        {
            return scene.Rig.System.CastSpell(player, SweepHelperSpells.SelfDummy, SpellCastTargets.ForSelf(), triggered: false) == SpellCastResult.CastOk;
        }
        finally
        {
            player.Cheats = saved;
        }
    }

    /// <summary>Sit, then stand (CMSG_STANDSTATECHANGE's effect: Player.SetStandState raises StandStateChanged).</summary>
    public static bool StandUp(Player player)
    {
        player.SetStandState(StandState.Sit);
        player.SetStandState(StandState.Stand);
        return true;
    }
}

/// <summary>The helper spells the drivers cast (chosen from the client data, not invented: see <see cref="SpellSweepRunner"/>).</summary>
internal static class SweepHelperSpells
{
    /// <summary>Synthetic: an instant self Dummy with no aura and no flags, added to the sweep store (id outside Spell.dbc's range).</summary>
    public const uint SelfDummy = 990_900;

    public static SpellInfo SelfDummySpell() => new()
    {
        Id = SelfDummy,
        Name = "Sweep Self Dummy",
        RangeIndex = SpellConstants.RangeIndexSelfOnly,
        Range = new SpellRange(0, 0),
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.Dummy, BaseDice = 1, DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
    };
}
