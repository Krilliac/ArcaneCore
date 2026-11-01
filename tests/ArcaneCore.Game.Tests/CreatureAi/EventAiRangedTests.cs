using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.CombatCore;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// EventAI ranged modes against cmangos UnitAI: the main spell (CAST_MAIN_SPELL, 0x100) that fails for power sends the creature to melee
/// (HandleSpellCastResult, UnitAI.cpp:838-858), the return threshold is added to the melee reach before the minimum clamp
/// (CanReachWithMeleeAttack(victim, 3.f)), and a charmed player's swing pulses its charmer's PvP flag (vmangos Unit.cpp:431-433, 6038-6056).
/// </summary>
public sealed class EventAiRangedTests
{
    private const uint MainSpellId = 5000;
    private const uint OtherSpellId = 5001;

    private static CreatureAiEvent Row(uint id, EventAiEventType type, int p1 = 0, int p2 = 0, int p3 = 0, int p4 = 0,
        byte flags = 0, CreatureAiAction a1 = default)
        => new()
        {
            Id = id,
            CreatureId = WolfEntry,
            EventType = (byte)type,
            Param1 = p1,
            Param2 = p2,
            Param3 = p3,
            Param4 = p4,
            Flags = flags,
            Chance = 100,
            Action1 = a1,
        };

    private static CreatureAiAction Act(EventAiActionType type, int p1 = 0, int p2 = 0, int p3 = 0) => new((byte)type, p1, p2, p3);

    private static CreatureContent EventContent(IEnumerable<CreatureAiEvent> events)
        => new(
            [Template(configure: t => t.AIName = CreatureAiFactory.EventAIName)],
            [Spawn(1, WolfEntry, 5, 0)], [], [], [],
            new CreatureAiContent(events, []));

    /// <summary>A caster whose casts all end with <see cref="Result"/>.</summary>
    private sealed class ResultCaster(CreatureCastResult result) : ICreatureSpellCaster
    {
        public CreatureCastResult Result { get; set; } = result;

        public List<uint> Casts { get; } = [];

        public event Action<Unit, Unit, SpellInfo>? SpellHit
        {
            add { }
            remove { }
        }

        public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
        {
            Casts.Add(spellId);
            return Result;
        }

        public bool IsCasting(Creature caster) => false;

        public bool HasAura(Unit unit, uint spellId) => false;

        public void Interrupt(Creature caster)
        {
        }

        public void OnCreatureRemoved(Creature creature)
        {
        }
    }

    private static CreatureContent CasterContent(int rangedType, int castFlags, uint spellId)
        => EventContent(
        [
            Row(1, EventAiEventType.Spawned, a1: Act(EventAiActionType.SetRangedMode, rangedType, 20)),
            Row(2, EventAiEventType.TimerInCombat, 0, 0, 500, 500, flags: CreatureEventAI.FlagRepeatable,
                a1: Act(EventAiActionType.Cast, (int)spellId, (int)EventAiTarget.Victim, castFlags)),
        ]);

    private static (WorldRuntime World, CreatureEventAI Ai, ResultCaster Caster) StartCaster(
        int rangedType, int castFlags, CreatureCastResult result, uint spellId = MainSpellId)
    {
        var caster = new ResultCaster(result);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(
            CasterContent(rangedType, castFlags, spellId), new CreatureAiServices { Spells = caster });
        (Player player, _) = AddPlayer(runtime, 1, 50, 0);
        Creature wolf = Assert.Single(system.Creatures);
        var ai = Assert.IsType<CreatureEventAI>(wolf.AI);
        ai.OnRespawn();
        map.Combat.DealDamage(player, wolf, 1, direct: false);
        Assert.Same(player, wolf.Combat.Victim);
        Assert.True(ai.CurrentRangedMode);
        return (runtime, ai, caster);
    }

    [Theory]
    [InlineData(1, CreatureCastResult.NoPower)]
    [InlineData(2, CreatureCastResult.NoPower)]
    [InlineData(4, CreatureCastResult.NoPower)]
    [InlineData(2, CreatureCastResult.TooClose)]
    [InlineData(2, CreatureCastResult.Prevented)]
    public void MainSpellThatFails_LeavesRangedMode_SoTheCreatureClosesToMelee(int type, CreatureCastResult result)
    {
        // cmangos UnitAI::HandleSpellCastResult: CAST_FAIL_POWER / TOO_CLOSE / CAST_PREVENTED on a main spell -> SetCurrentRangedMode(false).
        (WorldRuntime runtime, CreatureEventAI ai, ResultCaster caster) = StartCaster(type, CreatureEventAI.CastMainSpell, result);
        using WorldRuntime world = runtime;
        Assert.True(ai.IsMainSpell(MainSpellId));

        ai.OnUpdate(600);

        Assert.Contains(MainSpellId, caster.Casts);
        Assert.False(ai.CurrentRangedMode);
        Assert.Equal(0f, ai.AttackDistance); // melee reach: it chases to melee range
        Assert.True(ai.RangedMode);
    }

    [Theory]
    [InlineData(CreatureCastResult.Ok)]
    [InlineData(CreatureCastResult.Failed)]
    [InlineData(CreatureCastResult.AlreadyCasting)]
    public void MainSpell_WithAnotherOutcome_KeepsRangedMode(CreatureCastResult result)
    {
        (WorldRuntime runtime, CreatureEventAI ai, ResultCaster caster) = StartCaster(2, CreatureEventAI.CastMainSpell, result);
        using WorldRuntime world = runtime;

        ai.OnUpdate(600);

        Assert.Contains(MainSpellId, caster.Casts);
        Assert.True(ai.CurrentRangedMode);
    }

    [Fact]
    public void NonMainSpell_ThatFailsForPower_KeepsRangedMode()
    {
        (WorldRuntime runtime, CreatureEventAI ai, ResultCaster caster) = StartCaster(2, 0, CreatureCastResult.NoPower, OtherSpellId);
        using WorldRuntime world = runtime;
        Assert.False(ai.IsMainSpell(OtherSpellId));

        ai.OnUpdate(600);

        Assert.Contains(OtherSpellId, caster.Casts);
        Assert.True(ai.CurrentRangedMode);
    }

    [Fact]
    public void FullCasterMode_IsAccepted()
    {
        CreatureContent content = EventContent([Row(1, EventAiEventType.Spawned, a1: Act(EventAiActionType.SetRangedMode, 1, 20))]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        AddPlayer(world, 1, 50, 0);
        Creature wolf = Assert.Single(system.Creatures);
        var ai = Assert.IsType<CreatureEventAI>(wolf.AI);

        ai.OnRespawn();

        Assert.True(ai.RangedMode);
        Assert.Equal(1, ai.RangedModeType);
        Assert.True(ai.CurrentRangedMode);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void ReturnThreshold_IsAddedToTheMeleeReach_BeforeTheMinimumReachClamp(int type)
    {
        // cmangos CanReachWithMeleeAttack(victim, 3.f): reach = combined combat reach + 3, clamped to ATTACK_DISTANCE afterwards. The wolf and
        // the player have a combined reach under 5 yd, so a victim between (reach + 3) and (5 + 3) yd is already out of reach.
        CreatureContent content = EventContent([Row(1, EventAiEventType.Spawned, a1: Act(EventAiActionType.SetRangedMode, type, 20))]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 50, 0);
        Creature wolf = Assert.Single(system.Creatures);
        var ai = Assert.IsType<CreatureEventAI>(wolf.AI);
        ai.OnRespawn();
        map.Combat.DealDamage(player, wolf, 1, direct: false);

        player.Relocate(wolf.X + 1f, wolf.Y, wolf.Z, 0, 0);
        ai.OnUpdate(100);
        Assert.False(ai.CurrentRangedMode);

        float baseReach = MathF.Max(wolf.GetFloat(UpdateFields.UnitFieldCombatreach), CombatConstants.MinMeleeCombatReach)
            + MathF.Max(player.GetFloat(UpdateFields.UnitFieldCombatreach), CombatConstants.MinMeleeCombatReach) + CombatConstants.BaseMeleeRangeOffset;
        Assert.True(baseReach + 3f + 0.2f < CombatConstants.AttackDistance + 3f, "the scenario needs a combined reach under the 5 yd minimum");

        player.Relocate(wolf.X + baseReach + 3f - 0.2f, wolf.Y, wolf.Z, 0, 0); // just inside reach + 3: stays in melee mode
        ai.OnUpdate(100);
        Assert.False(ai.CurrentRangedMode);

        player.Relocate(wolf.X + baseReach + 3f + 0.2f, wolf.Y, wolf.Z, 0, 0); // just past it, though inside 5 + 3: back to ranged
        ai.OnUpdate(100);
        Assert.True(ai.CurrentRangedMode);
    }

    [Fact]
    public void RangedModeByReach_DoesNotDependOnCombatMovement()
    {
        // cmangos UnitAI::UpdateAI has no IsCombatMovement() condition on the in-reach branch.
        CreatureContent content = EventContent([Row(1, EventAiEventType.Spawned, a1: Act(EventAiActionType.SetRangedMode, 2, 20))]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 50, 0);
        Creature wolf = Assert.Single(system.Creatures);
        var ai = Assert.IsType<CreatureEventAI>(wolf.AI);
        ai.OnRespawn();
        map.Combat.DealDamage(player, wolf, 1, direct: false);
        ai.CombatMovement = false;
        player.Relocate(wolf.X + 1f, wolf.Y, wolf.Z, 0, 0);

        ai.OnUpdate(100);

        Assert.False(ai.CurrentRangedMode);
    }

    [Theory]
    [InlineData(UnitFlags.Fleeing)]
    [InlineData(UnitFlags.Confused)]
    public void AFleeingOrConfusedIdleCreature_IsPutInCombatByAnAttacker_ButDoesNotRetaliate(UnitFlags cc)
    {
        // vmangos Creature::OnEnterCombat skips AttackedBy while confused or fleeing (Creature.cpp:3652); a calm creature retaliates.
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 0, 0), Spawn(2, WolfEntry, 3, 0), Spawn(3, WolfEntry, 6, 0)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() });
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 45, 0);
        Creature[] wolves = [.. system.Creatures];
        Assert.Equal(3, wolves.Length);
        foreach (Creature w in wolves)
        {
            w.AI!.CombatMovement = false;
        }

        Creature attacker = wolves[0];
        Creature impaired = wolves[1];
        Creature calm = wolves[2];
        attacker.AI!.AttackStart(player);
        Assert.Same(player, attacker.Combat.Victim);
        impaired.UnitFlags |= cc;

        system.EnterCombatWithTarget(attacker, impaired);
        system.EnterCombatWithTarget(attacker, calm);

        Assert.True(impaired.Combat.IsInCombat);
        Assert.Null(impaired.Combat.Victim);
        Assert.Same(attacker, calm.Combat.Victim);
    }

    [Fact]
    public void AMindControlledPlayersSwing_PulsesItsCharmersPvpFlag_NotItsOwn()
    {
        // vmangos Unit::UpdateMeleeAttackingState calls TogglePlayerPvPFlagOnAttackVictim for a player's swing, which acts for GetAffectingPlayer
        // (the charmer, Unit.cpp:4824-4833, 6038-6056).
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime w = world;
        Player charmed = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        Player victim = CombatTestKit.AddPlayer(world, 2, 2, 0, new FakeSession(2), Race.Orc);
        Player charmer = CombatTestKit.AddPlayer(world, 3, -20, 0, new FakeSession(3));
        map.Combat.TogglePvp(victim, true);
        charmed.SetUInt64(UpdateFields.UnitFieldCharmedby, charmer.Guid.Value);
        map.Combat.Attack(charmed, victim);

        // Only the swing's own pulse remains under test: clear what starting the attack flagged.
        foreach (Player p in new[] { charmed, charmer })
        {
            p.UnitFlags &= ~UnitFlags.Pvp;
            p.Combat.PvpFlagTimer = 0;
            p.Combat.InPvpCombat = false;
        }

        for (int i = 0; i < 60; i++)
        {
            world.RunTick(100);
        }

        Assert.NotEqual(0u, (uint)(charmer.UnitFlags & UnitFlags.Pvp));
        Assert.Equal(0u, (uint)(charmed.UnitFlags & UnitFlags.Pvp));
        Assert.False(charmed.Combat.InPvpCombat);
    }
}
