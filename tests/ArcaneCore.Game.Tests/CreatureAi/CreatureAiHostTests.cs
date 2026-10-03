using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>Aggro, attack start, assistance, leash, evade and the AI hooks driven by the creature map system.</summary>
public sealed class CreatureAiHostTests
{
    private const uint OtherFactionEntry = 300;

    [Theory]
    [InlineData(10, 10, 20f)]
    [InlineData(10, 15, 15f)]
    [InlineData(10, 30, 5f)]   // never below 5 yd
    [InlineData(60, 1, 45f)]   // at most 25 levels counted below
    [InlineData(20, 5, 35f)]
    public void AggroRadius_FollowsTheLevelDifference(byte creatureLevel, byte playerLevel, float expected)
    {
        CreatureContent content = Content([Template(configure: t => { t.MinLevel = creatureLevel; t.MaxLevel = creatureLevel; })], [Spawn(1, WolfEntry, 5, 0)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        player.Level = playerLevel;
        Creature wolf = Assert.Single(system.Creatures);

        Assert.Equal(expected, system.GetAttackDistance(wolf, player), 3);
    }

    [Fact]
    public void AggroRate_ScalesTheRadius_AndZeroTurnsAggroOff()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 5, 0)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() },
            new CreatureOptions { AggroRate = 0 });
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);

        Run(world, 500);
        Assert.Equal(0f, system.GetAttackDistance(wolf, player));
        Assert.Null(wolf.Combat.Victim);
    }

    [Fact]
    public void AggressorAggroesInsideItsRadius_AndChasesAtRunSpeed_ButNotOutside()
    {
        // Wolf level 2 vs player level 1: 21 yd plus both bounding radii.
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 15, 0), Spawn(2, WolfEntry, 0, 40)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() });
        using WorldRuntime world = runtime;
        (Player player, FakeSession session) = AddPlayer(world, 1, 0, 0);
        Creature near = system.Creatures.Single(c => c.Spawn!.Guid == 1);
        Creature far = system.Creatures.Single(c => c.Spawn!.Guid == 2);

        world.RunTick(50);

        Assert.Same(player, near.Combat.Victim);
        Assert.True(near.Combat.IsInCombat);
        Assert.True(player.Combat.IsInCombat);
        Assert.Equal(MovementGeneratorType.Chase, near.Motion.CurrentType);
        Assert.Same(player, near.Motion.TargetedUnit);
        Assert.True(near.Spline!.Run);
        PathMove move = Packets(session, WorldOpcode.SmsgMonsterMove).Select(ParsePathMove).Last(m => m.Guid == near.Guid.Value);
        Assert.NotEqual(0u, move.Flags & (uint)SplineFlags.Runmode);
        Assert.Null(far.Combat.Victim);

        Run(world, 3000);
        Assert.True(MapCombat.CanReachWithMeleeAutoAttack(near, player));
        Assert.False(near.IsMoving);
        Assert.Null(far.Combat.Victim);
        Assert.Contains(near, map.Combat.TrackedUnits);
    }

    public enum NoAggroCase
    {
        DefaultHostility,
        GameMaster,
        Civilian,
        NoAggroFlag,
        TooHigh,
        NoLineOfSight,
        DeadPlayer,
        NullAi,
    }

    [Theory]
    [InlineData(NoAggroCase.DefaultHostility)]
    [InlineData(NoAggroCase.GameMaster)]
    [InlineData(NoAggroCase.Civilian)]
    [InlineData(NoAggroCase.NoAggroFlag)]
    [InlineData(NoAggroCase.TooHigh)]
    [InlineData(NoAggroCase.NoLineOfSight)]
    [InlineData(NoAggroCase.DeadPlayer)]
    [InlineData(NoAggroCase.NullAi)]
    public void NoAggroOnSight_WhenARuleForbidsIt(NoAggroCase rule)
    {
        CreatureTemplate template = Template(configure: t =>
        {
            t.Civilian = rule == NoAggroCase.Civilian;
            t.ExtraFlags = rule == NoAggroCase.NoAggroFlag ? Creature.ExtraFlagNoAggro : 0;
            t.AIName = rule == NoAggroCase.NullAi ? "NullAI" : string.Empty;
        });
        float z = rule == NoAggroCase.TooHigh ? 83.5f + 6f : 83.5f;
        CreatureContent content = Content([template], [Spawn(1, WolfEntry, 8, 0, z: z)]);
        var services = new CreatureAiServices
        {
            Hostility = rule == NoAggroCase.DefaultHostility ? FactionCreatureHostility.Empty : new AlwaysHostile(),
        };
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, services);
        using WorldRuntime world = runtime;
        if (rule == NoAggroCase.NoLineOfSight)
        {
            WorldCollision.Of(world).Install(new BlockedSight());
        }

        (Player player, _) = AddPlayer(world, 1, 0, 0);
        if (rule == NoAggroCase.GameMaster)
        {
            player.Flags |= PlayerFlags.Gm;
        }

        if (rule == NoAggroCase.DeadPlayer)
        {
            map.Combat.Kill(null, player);
        }

        Creature wolf = Assert.Single(system.Creatures);
        Run(world, 500);

        Assert.Null(wolf.Combat.Victim);
        Assert.Equal(rule != NoAggroCase.NullAi, !system.CanAggroOnSight(wolf, player)); // NullAI passes the checks but never looks
        Assert.Equal(MovementGeneratorType.Idle, wolf.Motion.CurrentType);
    }

    [Fact]
    public void AttackedReactor_FightsBack_ChasesAndRunsTheAggroHookOnce()
    {
        CreatureContent content = Content([Template(configure: t => t.AIName = RecorderName)], [Spawn(1, WolfEntry, 20, 0)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Factory = RecorderFactory() });
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        var ai = Assert.IsType<RecorderAI>(wolf.AI);
        Assert.Equal(["respawn"], ai.Calls);

        map.Combat.DealDamage(player, wolf, 1, direct: false);
        map.Combat.DealDamage(player, wolf, 1, direct: false);

        Assert.Same(player, wolf.Combat.Victim);
        Assert.Equal(MovementGeneratorType.Chase, wolf.Motion.CurrentType);
        Assert.Equal(["respawn", "aggro"], ai.Calls);
        Run(world, 3000);
        Assert.True(MapCombat.CanReachWithMeleeAutoAttack(wolf, player));
    }

    [Fact]
    public void NullAi_DoesNotFightBack()
    {
        CreatureContent content = Content([Template(configure: t => t.AIName = "NullAI")], [Spawn(1, WolfEntry, 3, 0)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);

        map.Combat.DealDamage(player, wolf, 1, direct: false);
        Run(world, 500);

        Assert.IsType<NullCreatureAI>(wolf.AI);
        Assert.Null(wolf.Combat.Victim);
        Assert.False(wolf.IsMoving);
    }

    [Fact]
    public void Leash_TargetBeyondThreatRadius_EvadesWithFullResetAndRunsHome()
    {
        CreatureContent content = Content([Template(configure: t => t.AIName = RecorderName)], [Spawn(1, WolfEntry, 10, 0)]);
        var spells = new FakeCaster();
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Factory = RecorderFactory(), Spells = spells });
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        var ai = (RecorderAI)wolf.AI!;
        map.Combat.DealDamage(player, wolf, 20, direct: false);
        Run(world, 2000);
        Assert.True(MapCombat.CanReachWithMeleeAutoAttack(wolf, player));
        Assert.True(wolf.Health < wolf.MaxHealth);

        // 61 yd from where the fight began (default ThreatRadius 60, outside the 21 yd aggro radius).
        player.Relocate(wolf.X - 61, wolf.Y, 83.5f, 0, 0);
        world.RunTick(100);

        Assert.True(wolf.IsInEvadeMode);
        Assert.Equal(wolf.MaxHealth, wolf.Health);
        Assert.Empty(wolf.Combat.Threat.Entries);
        Assert.Null(wolf.Combat.Victim);
        Assert.False(wolf.Combat.IsInCombat);
        Assert.Equal(MovementGeneratorType.Home, wolf.Motion.CurrentType);
        Assert.Equal(1, spells.Interrupts);
        Assert.Contains("evade", ai.Calls);
        Assert.False(map.Combat.Hooks.CanAttack(player, wolf));
        Assert.False(map.Combat.Attack(player, wolf));
        wolf.OnAttackedBy(player);
        Assert.Null(wolf.Combat.Victim); // ignores attacks while running home

        Run(world, 3000);
        Assert.False(wolf.IsInEvadeMode);
        Assert.Equal(MovementGeneratorType.Idle, wolf.Motion.CurrentType);
        Assert.Equal(10f, wolf.X, 2);
        Assert.Equal(0f, wolf.Y, 2);
        Assert.Equal(1.5f, wolf.Orientation, 2);
        Assert.Equal(["respawn", "aggro", "evade", "inform:Home:0", "home"], ai.Calls);
        Assert.True(map.Combat.Hooks.CanAttack(player, wolf));
    }

    [Fact]
    public void Leash_IsOffOnInstanceableMaps()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 10, 0, mapId: 0)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        map.Combat.Hooks = new InstanceHooks();
        map.Combat.DealDamage(player, wolf, 1, direct: false);

        player.Relocate(wolf.X - 200, wolf.Y, 83.5f, 0, 0);
        Assert.False(system.IsOutOfThreatArea(wolf, player));
        world.RunTick(100);
        Assert.False(wolf.IsInEvadeMode);
        Assert.Same(player, wolf.Combat.Victim);
    }

    [Fact]
    public void KillingTheVictim_TellsTheAi_ThenTheCreatureEvades()
    {
        CreatureContent content = Content([Template(configure: t => t.AIName = RecorderName)], [Spawn(1, WolfEntry, 3, 0)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Factory = RecorderFactory() });
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        var ai = (RecorderAI)wolf.AI!;
        map.Combat.DealDamage(player, wolf, 1, direct: false);

        map.Combat.Kill(wolf, player);
        Assert.Same(player, ai.KilledUnit);
        world.RunTick(100);

        Assert.True(wolf.IsInEvadeMode);
        Assert.Contains("evade", ai.Calls);
    }

    [Fact]
    public void CreatureDeath_RunsTheDeathHook_AndClearsMovement()
    {
        CreatureContent content = Content([Template(configure: t => t.AIName = RecorderName)], [Spawn(1, WolfEntry, 10, 0)]);
        var spells = new FakeCaster();
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Factory = RecorderFactory(), Spells = spells },
            new CreatureOptions { CorpseDecayNormalSeconds = 1 });
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        var ai = (RecorderAI)wolf.AI!;
        map.Combat.DealDamage(player, wolf, 1, direct: false);
        Assert.True(wolf.IsMoving);

        map.Combat.Kill(player, wolf);

        Assert.Contains("death", ai.Calls);
        Assert.False(wolf.IsMoving);
        Assert.Equal(MovementGeneratorType.Idle, wolf.Motion.CurrentType);
        Assert.Empty(wolf.Motion.ActiveTypes);
        Run(world, 1100);
        Assert.Contains(wolf, spells.Removed); // corpse removal revokes the creature's spell state
    }

    [Fact]
    public void Assistance_SameFactionNeighboursJoinAfterTheDelay_OnlyOnce()
    {
        CreatureTemplate other = Template(OtherFactionEntry, t => t.Faction = 33);
        CreatureContent content = Content([Template(), other],
        [
            Spawn(1, WolfEntry, 10, 0),          // attacked
            Spawn(2, WolfEntry, 17, 0),          // 7 yd: helps
            Spawn(3, WolfEntry, 40, 0),          // 30 yd: too far
            Spawn(4, OtherFactionEntry, 10, 7),  // other faction
            Spawn(5, WolfEntry, 24, 0),          // 7 yd from the helper, 14 from the caller: helpers do not call
        ]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature Get(uint guid) => system.Creatures.Single(c => c.Spawn!.Guid == guid);

        map.Combat.DealDamage(player, Get(1), 1, direct: false);
        Assert.Equal(1, system.PendingAssistCount);

        Run(world, 1400);
        Assert.Null(Get(2).Combat.Victim);
        Run(world, 200);
        Assert.Same(player, Get(2).Combat.Victim);
        Assert.Equal(MovementGeneratorType.Chase, Get(2).Motion.CurrentType);
        Assert.Contains(Get(2), player.Combat.ThreatenedBy);
        Assert.Null(Get(3).Combat.Victim);
        Assert.Null(Get(4).Combat.Victim);
        Assert.Null(Get(5).Combat.Victim);
        Assert.Equal(0, system.PendingAssistCount);

        // A second hit in the same fight does not call again.
        map.Combat.DealDamage(player, Get(1), 1, direct: false);
        Assert.Equal(0, system.PendingAssistCount);
    }

    [Fact]
    public void Assistance_HelperThatDiesOrLeavesBeforeTheDelay_IsDropped()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 10, 0), Spawn(2, WolfEntry, 15, 0)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature caller = system.Creatures.Single(c => c.Spawn!.Guid == 1);
        Creature helper = system.Creatures.Single(c => c.Spawn!.Guid == 2);
        map.Combat.DealDamage(player, caller, 1, direct: false);
        Assert.Equal(1, system.PendingAssistCount);

        system.KillCreature(helper);
        Assert.Equal(0, system.PendingAssistCount);
        Run(world, 2000);
        Assert.Null(helper.Combat.Victim);
    }

    [Fact]
    public void CallForHelp_JoinsAtOnce_AndFleeForAssistance_RunsToTheHelperFirst()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 5, 0), Spawn(2, WolfEntry, 25, 0)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature caller = system.Creatures.Single(c => c.Spawn!.Guid == 1);
        Creature helper = system.Creatures.Single(c => c.Spawn!.Guid == 2);
        map.Combat.DealDamage(player, caller, 1, direct: false);
        Assert.Equal(0, system.PendingAssistCount); // 20 yd apart: outside the 10 yd assistance radius

        system.FleeForAssistance(caller);
        Assert.Equal(MovementGeneratorType.Point, caller.Motion.CurrentType);
        Run(world, 3000);

        Assert.Same(player, helper.Combat.Victim);
        Assert.Equal(MovementGeneratorType.Chase, caller.Motion.CurrentType);
    }

    [Fact]
    public void FleeForAssistance_WithoutHelpers_FleesForTheConfiguredTime()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 5, 0)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, options: new CreatureOptions { FleeDelayMs = 2000 });
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        map.Combat.DealDamage(player, wolf, 1, direct: false);

        system.FleeForAssistance(wolf);
        Assert.Equal(MovementGeneratorType.Fleeing, wolf.Motion.CurrentType);
        Assert.NotEqual(UnitFlags.None, wolf.UnitFlags & UnitFlags.Fleeing);
        Run(world, 1000);
        Assert.True(Distance2D(wolf, player) > 6f);

        Run(world, 1100);
        Assert.Equal(UnitFlags.None, wolf.UnitFlags & UnitFlags.Fleeing);
        Assert.Equal(MovementGeneratorType.Chase, wolf.Motion.CurrentType);
    }

    [Fact]
    public void FactionHostility_UsesTemplateRelations_AndFailsClosedForUnknownTemplates()
    {
        var catalog = new FactionTemplateCatalog(
        [
            new FactionTemplateRecord(1, 1, 0, OwnMask: 1, FriendlyMask: 0, HostileMask: 0),      // player
            new FactionTemplateRecord(14, 14, 0, OwnMask: 0, FriendlyMask: 0, HostileMask: 1),    // monster
            new FactionTemplateRecord(35, 35, 0, OwnMask: 0, FriendlyMask: 0, HostileMask: 0),    // friendly
            new FactionTemplateRecord(500, 72, 0x1000, OwnMask: 0, FriendlyMask: 0, HostileMask: 0), // contested guard
        ]);
        var hostility = new FactionCreatureHostility(catalog);
        CreatureContent content = Content(
            [Template(1, t => t.Faction = 14), Template(2, t => t.Faction = 35), Template(3, t => t.Faction = 999), Template(4, t => t.Faction = 500)],
            [Spawn(1, 1, 5, 0), Spawn(2, 2, 6, 0), Spawn(3, 3, 7, 0), Spawn(4, 4, 8, 0)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature Get(uint entry) => system.Creatures.Single(c => c.Entry == entry);

        Assert.True(hostility.IsHostile(Get(1), player));
        Assert.False(hostility.IsHostile(Get(2), player));
        Assert.False(hostility.IsHostile(Get(3), player));
        Assert.False(hostility.IsHostile(Get(4), player));
        player.UnitFlags |= UnitFlags.Pvp;
        Assert.True(hostility.IsHostile(Get(4), player));
        Assert.True(hostility.CanAssist(Get(1), Get(1)));
        Assert.False(hostility.CanAssist(Get(1), Get(2)));
    }

    [Fact]
    public void Factory_UnknownNameUsesTheDefault_AndDuplicateRegistrationFails()
    {
        CreatureContent content = Content(
            [Template(1, t => t.AIName = "NoSuchAI"), Template(2, t => t.Civilian = true), Template(3, t => t.AIName = "passiveai"), Template(4)],
            [Spawn(1, 1, 5, 0), Spawn(2, 2, 6, 0), Spawn(3, 3, 7, 0), Spawn(4, 4, 8, 0)]);
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        AddPlayer(world, 1, 0, 0);
        Creature Get(uint entry) => system.Creatures.Single(c => c.Entry == entry);
        var factory = new CreatureAiFactory();

        Assert.IsType<AggressorAI>(factory.Create(Get(1), content, out bool unknown));
        Assert.True(unknown);
        Assert.IsType<ReactorAI>(factory.Create(Get(2), content, out unknown));
        Assert.False(unknown);
        Assert.IsType<ReactorAI>(factory.Create(Get(3), content, out _));
        Assert.IsType<AggressorAI>(Get(4).AI);
        Assert.Throws<InvalidOperationException>(() => factory.Register("aggressorai", c => new ReactorAI(c)));
        Assert.Throws<ArgumentException>(() => factory.Register(" ", c => new ReactorAI(c)));
    }

    [Fact]
    public void SpellHit_AndDespawn_GoThroughTheSpellSeam()
    {
        CreatureContent content = Content([Template(configure: t => t.AIName = RecorderName)], [Spawn(1, WolfEntry, 5, 0)]);
        var spells = new FakeCaster();
        (WorldRuntime runtime, _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Factory = RecorderFactory(), Spells = spells });
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        var ai = (RecorderAI)wolf.AI!;

        spells.RaiseHit(player, wolf, Spells.SpellTestKit.Spell(4242));
        spells.RaiseHit(player, player, Spells.SpellTestKit.Spell(4243)); // not a creature: ignored
        Assert.Contains("spellhit:4242", ai.Calls);
        Assert.DoesNotContain("spellhit:4243", ai.Calls);

        system.Despawn(wolf);
        Assert.Contains(wolf, spells.Removed);
        Assert.Null(wolf.AI);
    }

    [Fact]
    public void Respawn_ResetsTheAi_AndRunsTheRespawnHook()
    {
        CreatureContent content = Content([Template(configure: t => t.AIName = RecorderName)], [Spawn(1, WolfEntry, 5, 0, respawnSeconds: 1)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Factory = RecorderFactory() },
            new CreatureOptions { CorpseDecayNormalSeconds = 1 });
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        var ai = (RecorderAI)wolf.AI!;
        map.Combat.DealDamage(player, wolf, 1, direct: false);
        map.Combat.Kill(player, wolf);

        Run(world, 2100);
        Assert.True(wolf.IsAlive);
        Assert.Equal(["respawn", "aggro", "death", "respawn"], ai.Calls);
        Assert.False(wolf.IsInEvadeMode);
        Assert.Null(wolf.Combat.Victim);
    }

    private sealed class InstanceHooks : CombatHooks
    {
        public override bool IsInstanceable(uint mapId) => true;
    }
}
