using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>cmangos-style EventAI rows driving casts, phases, texts and the other supported actions.</summary>
public sealed class CreatureEventAiTests
{
    private static CreatureAiEvent Row(uint id, EventAiEventType type, int p1 = 0, int p2 = 0, int p3 = 0, int p4 = 0,
        uint phaseMask = 0, byte flags = 0, byte chance = 100, CreatureAiAction a1 = default, CreatureAiAction a2 = default, CreatureAiAction a3 = default)
        => new()
        {
            Id = id,
            CreatureId = WolfEntry,
            EventType = (byte)type,
            Param1 = p1,
            Param2 = p2,
            Param3 = p3,
            Param4 = p4,
            InversePhaseMask = phaseMask,
            Flags = flags,
            Chance = chance,
            Action1 = a1,
            Action2 = a2,
            Action3 = a3,
        };

    private static CreatureAiAction Act(EventAiActionType type, int p1 = 0, int p2 = 0, int p3 = 0) => new((byte)type, p1, p2, p3);

    [Fact]
    public void SpawnedSetRangedMode_TypeThreeKeepsRangedStateAndLeavesAutoAttackIndependent()
    {
        CreatureContent content = EventContent(
            [Row(1, EventAiEventType.Spawned, a1: Act(EventAiActionType.SetRangedMode, 3, 20))]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        AddPlayer(world, 1, 50, 0); // Activate the spawn's grid through an actual player.
        Creature wolf = Assert.Single(system.Creatures);
        var ai = Assert.IsType<CreatureEventAI>(wolf.AI);

        Assert.Empty(ai.Unsupported);
        ai.OnRespawn();

        Assert.True(ai.RangedMode);
        Assert.True(ai.CurrentRangedMode);
        Assert.Equal(3, ai.RangedModeType);
        Assert.Equal(20f, ai.ChaseDistance);
        Assert.True(ai.MeleeEnabled);

        ai.Reset();
        Assert.True(ai.CurrentRangedMode);
        Assert.True(ai.MeleeEnabled);
        _ = world;
    }

    [Fact]
    public void NoMeleeRangedMode_ApproachesConfiguredMaximumDistanceWithoutRetreating()
    {
        CreatureContent content = EventContent(
            [Row(1, EventAiEventType.Spawned, a1: Act(EventAiActionType.SetRangedMode, 3, 20),
                a2: Act(EventAiActionType.AutoAttack, 0))]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 50, 0);
        Creature wolf = Assert.Single(system.Creatures);
        var ai = Assert.IsType<CreatureEventAI>(wolf.AI);
        ai.OnRespawn();

        map.Combat.DealDamage(player, wolf, 1, direct: false);
        Run(world, 5000);

        float distance = MathF.Sqrt(MathF.Pow(wolf.X - player.X, 2) + MathF.Pow(wolf.Y - player.Y, 2));
        Assert.InRange(distance, 18.5f, 21.5f);
        Assert.False(ai.MeleeEnabled); // action 20, not ranged mode, owns this state.
    }

    [Fact]
    public void RangedModeAction_RejectsUnimplementedModeTypes()
    {
        CreatureContent content = EventContent(
            [Row(1, EventAiEventType.Spawned, a1: Act(EventAiActionType.SetRangedMode, 2, 20))]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        AddPlayer(world, 1, 50, 0);
        Creature wolf = Assert.Single(system.Creatures);
        var ai = Assert.IsType<CreatureEventAI>(wolf.AI);

        ai.OnRespawn();

        Assert.False(ai.RangedMode);
        Assert.True(ai.MeleeEnabled);
    }

    [Fact]
    public void MainSpellFlag_IsMetadataOnly_AndRealSpellSystemCastsAtConfiguredRange()
    {
        using var kit = new SpellTestKit();
        CreatureContent content = EventContent(
        [
            Row(1, EventAiEventType.Spawned, a1: Act(EventAiActionType.SetRangedMode, 3, 20)),
            Row(2, EventAiEventType.TimerInCombat, 5000, 5000, 5000, 5000,
                flags: CreatureEventAI.FlagRepeatable,
                a1: Act(EventAiActionType.Cast, (int)SpellTestKit.DotSpell, (int)EventAiTarget.Victim, 0x100)),
        ]);
        var caster = new SpellSystemCreatureCaster(kit.System);
        (_, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = caster }, world: kit.World);
        (Player player, _) = kit.AddPlayer(1, 50, 0);
        Creature wolf = Assert.Single(system.Creatures);
        var ai = Assert.IsType<CreatureEventAI>(wolf.AI);
        ai.OnRespawn();

        map.Combat.DealDamage(player, wolf, 1, direct: false);
        Run(kit.World, 6000); // Include the next EventAI timer batch after the 5-second timer.

        float distance = MathF.Sqrt(MathF.Pow(wolf.X - player.X, 2) + MathF.Pow(wolf.Y - player.Y, 2));
        Assert.InRange(distance, 18.5f, 21.5f);
        SpellAuraHolder aura = Assert.Single(kit.System.GetAuras(player));
        Assert.Equal(SpellTestKit.DotSpell, aura.Spell.Id);
        Assert.Equal(wolf.Guid, aura.CasterGuid);
        Assert.True(ai.MeleeEnabled); // 0x100 marks metadata; it is neither triggered nor melee suppression.
    }

    [Fact]
    public void NoMeleeRangeMode_DoesNotRetreatFromCloseVictim_AndAction20StillControlsMelee()
    {
        CreatureContent content = EventContent(
            [Row(1, EventAiEventType.Spawned, a1: Act(EventAiActionType.SetRangedMode, 3, 20)),
             Row(2, EventAiEventType.TimerInCombat, 2000, 2000,
                 a1: Act(EventAiActionType.AutoAttack, 0), a2: Act(EventAiActionType.SetRangedMode, 0, 20))]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 6, 0);
        Creature wolf = Assert.Single(system.Creatures);
        var ai = Assert.IsType<CreatureEventAI>(wolf.AI);
        ai.OnRespawn();

        map.Combat.DealDamage(player, wolf, 1, direct: false);
        Run(world, 500);

        Assert.InRange(wolf.X, 4f, 7f);
        Assert.True(ai.RangedMode);
        Assert.True(ai.MeleeEnabled);
        Run(world, 2000);
        Assert.False(ai.RangedMode);
        Assert.False(ai.MeleeEnabled);
        Assert.False(wolf.Combat.IsMeleeAttacking);
    }

    private static CreatureContent EventContent(IEnumerable<CreatureAiEvent> events, IEnumerable<CreatureAiText>? texts = null, params CreatureTemplate[] extra)
        => new(
            [Template(configure: t => t.AIName = CreatureAiFactory.EventAIName), .. extra],
            [Spawn(1, WolfEntry, 5, 0)], [], [], [],
            new CreatureAiContent(events, texts ?? []));

    [Fact]
    public void AggroTextAndCast_TimerSetsPhase_AndPhaseGatedEventsWaitForIt()
    {
        CreatureContent content = EventContent(
        [
            Row(1, EventAiEventType.Aggro, a1: Act(EventAiActionType.Text, -1), a2: Act(EventAiActionType.Cast, 500, (int)EventAiTarget.Victim)),
            Row(2, EventAiEventType.TimerInCombat, 1000, 1000, 1000, 1000, a1: Act(EventAiActionType.SetPhase, 1)),
            Row(3, EventAiEventType.TimerInCombat, 0, 0, 500, 500, phaseMask: 0x1, flags: CreatureEventAI.FlagRepeatable,
                a1: Act(EventAiActionType.Cast, 501, (int)EventAiTarget.Self, CreatureEventAI.CastTriggered)),
        ],
        [new CreatureAiText(-1, "Grr, $N!", 0, 7, 0)]);
        var spells = new FakeCaster();
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = spells });
        using WorldRuntime world = runtime;
        (Player player, FakeSession session) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        var ai = Assert.IsType<CreatureEventAI>(wolf.AI);
        Assert.Equal(3, ai.EventCount);
        Assert.Empty(ai.Unsupported);

        map.Combat.DealDamage(player, wolf, 1, direct: false);

        MonsterChat say = ParseMonsterChat(Assert.Single(Packets(session, WorldOpcode.SmsgMessagechat)));
        Assert.Equal(ChatType.MonsterSay, say.Type);
        Assert.Equal(7u, say.Language);
        Assert.Equal("Young Wolf", say.Name);
        Assert.Equal(player.Guid.Value, say.Target);
        Assert.Equal("Grr, P1!", say.Message);
        Assert.Equal([(500u, (Unit?)player, false)], spells.Casts);

        // EventAI evaluates its timers in 500 ms batches (cmangos EVENT_UPDATE_TIME, CreatureEventAI.h:32;
        // UpdateEventTimers, CreatureEventAI.cpp:1929). With 100 ms ticks a batch lands every 600 ms (the
        // remaining update time must be strictly less than the tick, :1931), so the 1000 ms timer fires at the
        // second batch, 1200 ms after the aggro, not at 1000 ms.
        Run(world, 900);
        Assert.Equal(0, ai.Phase);
        Assert.DoesNotContain(spells.Casts, c => c.Spell == 501);

        Run(world, 300);
        Assert.Equal(1, ai.Phase);

        // The phase-gated repeating cast was hidden at the batch that set the phase, so it casts at the next two
        // batches (1800 and 2400 ms).
        Run(world, 1500);
        Assert.Equal(2, spells.Casts.Count(c => c.Spell == 501));
        Assert.All(spells.Casts.Where(c => c.Spell == 501), c => Assert.True(c.Triggered && ReferenceEquals(c.Target, wolf)));
    }

    [Fact]
    public void HealthPercent_Yells_OnceWithoutTheRepeatFlag_AndDeathEmotes()
    {
        CreatureContent content = EventContent(
        [
            Row(1, EventAiEventType.HealthPercent, 50, 0, 1000, 1000, a1: Act(EventAiActionType.Text, -2)),
            Row(2, EventAiEventType.Death, a1: Act(EventAiActionType.Text, -3)),
        ],
        [new CreatureAiText(-2, "Help!", 1, 0, 0), new CreatureAiText(-3, "%s dies.", 2, 0, 0)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, FakeSession session) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);

        map.Combat.DealDamage(player, wolf, 10, direct: false);
        Run(world, 300);
        Assert.Empty(Packets(session, WorldOpcode.SmsgMessagechat));

        map.Combat.DealDamage(player, wolf, 30, direct: false);
        Run(world, 3000);
        MonsterChat yell = ParseMonsterChat(Assert.Single(Packets(session, WorldOpcode.SmsgMessagechat)));
        Assert.Equal(ChatType.MonsterYell, yell.Type);
        Assert.Equal("Help!", yell.Message);

        session.Clear();
        map.Combat.Kill(player, wolf);
        MonsterChat emote = ParseMonsterChat(Assert.Single(Packets(session, WorldOpcode.SmsgMessagechat)));
        Assert.Equal(ChatType.MonsterEmote, emote.Type);
        Assert.Equal("%s dies.", emote.Message);
    }

    [Fact]
    public void OutOfCombatTimer_SpellHitWithCooldown_AndRandomAction()
    {
        CreatureContent content = EventContent(
        [
            Row(1, EventAiEventType.TimerOutOfCombat, 500, 500, 500, 500, flags: CreatureEventAI.FlagRepeatable, a1: Act(EventAiActionType.Cast, 600)),
            // A spell id AND a school mask must both match (cmangos SpellHit, CreatureEventAI.cpp:1667-1678); -1 is the full mask.
            Row(2, EventAiEventType.SpellHit, 4242, -1, 1000, 1000, flags: CreatureEventAI.FlagRepeatable,
                a1: Act(EventAiActionType.Cast, 601, (int)EventAiTarget.Invoker)),
            Row(3, EventAiEventType.SpellHit, 0, 1 << (int)SpellSchool.Fire, 0, 0, flags: CreatureEventAI.FlagRandomAction,
                a1: Act(EventAiActionType.Cast, 700), a2: Act(EventAiActionType.Cast, 701)),
        ]);
        var spells = new FakeCaster();
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = spells });
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);

        // 500 ms out-of-combat timer, evaluated in 600 ms batches with 100 ms ticks (cmangos UpdateEventTimers,
        // CreatureEventAI.cpp:1929): casts at 600 and 1200 ms.
        Run(world, 1300);
        Assert.Equal(2, spells.Casts.Count(c => c.Spell == 600));

        SpellInfo frost = SpellTestKit.Spell(4242) with { School = SpellSchool.Frost };
        spells.RaiseHit(player, wolf, frost);
        spells.RaiseHit(player, wolf, frost); // inside the 1 s cooldown
        Assert.Single(spells.Casts, c => c.Spell == 601 && ReferenceEquals(c.Target, player));

        // The cooldown is the event's repeat timer (parameters 3 and 4 for a spell hit) and counts down by whole
        // batches: two batches (1200 ms) are needed to get from 1000 ms to 0.
        Run(world, 1300);
        spells.RaiseHit(player, wolf, frost);
        Assert.Equal(2, spells.Casts.Count(c => c.Spell == 601));

        spells.RaiseHit(player, wolf, SpellTestKit.Spell(9) with { School = SpellSchool.Fire });
        spells.RaiseHit(player, wolf, SpellTestKit.Spell(9) with { School = SpellSchool.Fire }); // not repeatable
        Assert.Single(spells.Casts, c => c.Spell is 700 or 701);
    }

    [Fact]
    public void CombatMovementOff_StopsChasing_AutoAttackOff_StopsMelee_AndEvadeAndDieActionsWork()
    {
        CreatureContent content = EventContent(
        [
            Row(1, EventAiEventType.Aggro, a1: Act(EventAiActionType.CombatMovement, 0), a2: Act(EventAiActionType.AutoAttack, 0)),
            Row(2, EventAiEventType.TimerInCombat, 500, 500, 0, 0, a1: Act(EventAiActionType.Evade)),
        ]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, -10, 0);
        Creature wolf = Assert.Single(system.Creatures);
        var ai = (CreatureEventAI)wolf.AI!;

        map.Combat.DealDamage(player, wolf, 1, direct: false);
        Assert.False(ai.CombatMovement);
        Assert.False(ai.MeleeEnabled);
        Assert.False(wolf.Combat.IsMeleeAttacking);
        Assert.Same(player, wolf.Combat.Victim);
        Run(world, 300);
        Assert.False(wolf.IsMoving);
        Assert.Equal(5f, wolf.X, 2);

        Run(world, 300); // the evade action at 500 ms; the wolf never left home, so it is back at once
        Assert.Null(wolf.Combat.Victim);
        Assert.False(wolf.Combat.IsInCombat);
        Assert.Empty(wolf.Combat.Threat.Entries);
        Run(world, 1000);
        Assert.False(wolf.IsInEvadeMode);
        Assert.True(ai.CombatMovement); // reset on reaching home
        Assert.True(ai.MeleeEnabled);
    }

    [Fact]
    public void DieAction_KillsTheCreature_AndSummon_SpawnsAnAttackerThatDespawns()
    {
        CreatureTemplate summoned = Template(301, t => t.Name = "Summoned Wolf");
        CreatureContent content = EventContent(
        [
            Row(1, EventAiEventType.Aggro, a1: Act(EventAiActionType.Summon, 301, (int)EventAiTarget.Victim, 1000),
                a2: Act(EventAiActionType.Summon, 9999, 0, 0)),
            Row(2, EventAiEventType.TimerInCombat, 300, 300, 0, 0, a1: Act(EventAiActionType.Die)),
        ], null, summoned);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);

        map.Combat.DealDamage(player, wolf, 1, direct: false);
        Creature add = Assert.Single(system.Creatures, c => c.Entry == 301);
        Assert.Same(player, add.Combat.Victim);
        Assert.Equal(2, system.Creatures.Count); // the missing template summoned nothing

        // The 300 ms in-combat timer fires at the first 600 ms batch (cmangos UpdateEventTimers).
        Run(world, 700);
        Assert.False(wolf.IsAlive);

        // cmangos ACTION_T_SPAWN with a duration is TEMPSPAWN_TIMED_OOC_OR_DEAD_DESPAWN (CreatureEventAI.cpp:819-820;
        // TemporarySpawn.cpp:129-149): the 1000 ms run only while the add is alive and out of combat, so it stays while it fights.
        Run(world, 1500);
        Assert.Same(add, system.FindCreature(add.Guid));
        Assert.Same(player, add.Combat.Victim);

        world.RemovePlayer(player); // the fight ends
        Run(world, 700);
        Assert.Same(add, system.FindCreature(add.Guid));
        Run(world, 500);
        Assert.Null(system.FindCreature(add.Guid));
    }

    [Fact]
    public void UnsupportedRows_AreListed_AndTheirSupportedActionsStillRun()
    {
        CreatureContent content = EventContent(
        [
            Row(1, EventAiEventType.Aggro, a1: new CreatureAiAction(99, 0, 0, 0), a2: Act(EventAiActionType.Cast, 42)),
            new CreatureAiEvent { Id = 2, CreatureId = WolfEntry, EventType = 77 },
        ]);
        var spells = new FakeCaster();
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = spells });
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        var ai = (CreatureEventAI)wolf.AI!;

        Assert.Equal(["action type 99 (row 1)", "event type 77 (row 2)"], ai.Unsupported);
        map.Combat.DealDamage(player, wolf, 1, direct: false);
        Assert.Single(spells.Casts, c => c.Spell == 42);
    }

    [Fact]
    public void ZeroChance_NeverFires_AndAuraNotPresentFlagSkipsTheCast()
    {
        CreatureContent content = EventContent(
        [
            Row(1, EventAiEventType.Aggro, chance: 0, a1: Act(EventAiActionType.Cast, 10)),
            Row(2, EventAiEventType.Aggro, a1: Act(EventAiActionType.Cast, 11, (int)EventAiTarget.Self, CreatureEventAI.CastAuraNotPresent)),
            Row(3, EventAiEventType.Aggro, a1: Act(EventAiActionType.Cast, 12, (int)EventAiTarget.SecondOnThreat)),
        ]);
        var spells = new FakeCaster();
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = spells });
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        spells.Auras.Add((wolf, 11));

        map.Combat.DealDamage(player, wolf, 1, direct: false);

        Assert.Empty(spells.Casts); // no chance, aura present, no second target on the threat list
    }

    [Fact]
    public void CastThroughTheRealSpellSystem_AppliesTheCreaturesAura_AndDespawnDropsItsSpellState()
    {
        using var kit = new SpellTestKit();
        CreatureContent content = EventContent(
            [Row(1, EventAiEventType.Aggro, a1: Act(EventAiActionType.Cast, (int)SpellTestKit.DotSpell, (int)EventAiTarget.Victim))]);
        var caster = new SpellSystemCreatureCaster(kit.System);
        (_, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = caster }, world: kit.World);
        (Player player, _) = kit.AddPlayer(1);
        Creature wolf = Assert.Single(system.Creatures);
        var hits = new List<(Unit Caster, Unit Target, uint Spell)>();
        caster.SpellHit += (c, t, s) => hits.Add((c, t, s.Id));

        map.Combat.DealDamage(player, wolf, 1, direct: false);

        SpellAuraHolder holder = Assert.Single(kit.System.GetAuras(player));
        Assert.Equal(SpellTestKit.DotSpell, holder.Spell.Id);
        Assert.Equal(wolf.Guid, holder.CasterGuid);
        Assert.True(caster.HasAura(player, SpellTestKit.DotSpell));
        Assert.Contains(hits, h => ReferenceEquals(h.Caster, wolf) && ReferenceEquals(h.Target, player) && h.Spell == SpellTestKit.DotSpell);
        Assert.Equal(CreatureCastResult.UnknownSpell, system.CastSpell(wolf, 999_999, player, false));
        Assert.NotNull(kit.System.GetState(wolf.Guid));

        system.Despawn(wolf);
        Assert.Null(kit.System.GetState(wolf.Guid));
    }
}
