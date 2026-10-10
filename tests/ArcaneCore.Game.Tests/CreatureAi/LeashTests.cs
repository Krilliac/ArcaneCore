using ArcaneCore.Protocol;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// The vmangos leash: the soft leash (Creature::IsOutOfThreatArea, Objects/Creature.cpp:2796-2815, selected through
/// ThreatContainer::selectNextVictim, Threat/ThreatManager.cpp:305-312) and the template's hard leash (Creature::Update,
/// Objects/Creature.cpp:976-993). Positions and the clock are set directly; every expectation is derived from those sources.
/// </summary>
public sealed class LeashTests
{
    private sealed record Fight(WorldRuntime World, Map Map, CreatureMapSystem System, Player Player, Creature Wolf) : IDisposable
    {
        public void Dispose() => World.Dispose();
    }

    private static Fight Start(CreatureTemplate? template = null, CreatureOptions? options = null)
    {
        CreatureContent content = Content([template ?? Template()], [Spawn(1, WolfEntry, 5, 0)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() }, options);
        (Player player, _) = AddPlayer(runtime, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        wolf.AI!.CombatMovement = false; // stay where the test puts it
        map.Combat.DealDamage(player, wolf, 1, direct: false);
        Assert.Same(player, wolf.Combat.Victim);
        return new Fight(runtime, map, system, player, wolf);
    }

    private static void MoveBoth(Fight f, float wolfX, float playerX)
    {
        f.Wolf.Relocate(wolfX, 0, 83.5f, 0, 0);
        f.Player.Relocate(playerX, 0, 83.5f, 0, 0);
    }

    [Fact]
    public void TheThreatRadiusDefaultsTo50()
        => Assert.Equal(50f, new CreatureOptions().ThreatRadius);

    [Fact]
    public void ACreatureInsideTheArea_DoesNotLeash_EvenWithItsVictimFarAway()
    {
        using Fight f = Start();

        MoveBoth(f, wolfX: 5, playerX: 90); // the victim ran 85 yd away, the creature stayed
        Run(f.World, 30000);

        Assert.False(f.Wolf.IsInEvadeMode);
        Assert.Same(f.Player, f.Wolf.Combat.Victim);
        Assert.False(f.System.IsOutOfThreatArea(f.Wolf, f.Player));
    }

    [Fact]
    public void AVictimInsideTheArea_DoesNotLeash_EvenWithTheCreatureFarAway()
    {
        using Fight f = Start();

        MoveBoth(f, wolfX: 90, playerX: 6);
        Run(f.World, 30000);

        Assert.False(f.Wolf.IsInEvadeMode);
        Assert.False(f.System.IsOutOfThreatArea(f.Wolf, f.Player));
    }

    [Fact]
    public void BothOutsideTheArea_LeashAfterTheTwelveSecondExtension_NotBefore()
    {
        using Fight f = Start();

        MoveBoth(f, wolfX: 100, playerX: 101);
        Run(f.World, 11000);
        Assert.False(f.Wolf.IsInEvadeMode);        // inside the 12 s since the first look outside the area

        Run(f.World, 3000);
        Assert.True(f.Wolf.IsInEvadeMode);         // the leash extension ran out: out of area, evade
        Assert.Null(f.Wolf.Combat.Victim);
    }

    [Fact]
    public void TheThreatAreaRadius_IsTheLargerOfThreatRadiusAndOneAndAHalfTimesTheAggroRadius()
    {
        // Detection 60: radius max(1.5 x ~61, 50) = 91.5, so both at 70 yd are still inside it and the creature never leashes.
        using Fight wide = Start(Template() with { Detection = 60f });
        MoveBoth(wide, wolfX: 70, playerX: 71);
        Run(wide.World, 20000);
        Assert.False(wide.Wolf.IsInEvadeMode);

        // Detection 18: radius 50, so both at 70 yd are outside.
        using Fight narrow = Start();
        MoveBoth(narrow, wolfX: 70, playerX: 71);
        Run(narrow.World, 16000);
        Assert.True(narrow.Wolf.IsInEvadeMode);
    }

    [Fact]
    public void NoLeashEvade_AndInstanceableMaps_NeverLeash()
    {
        using Fight flagged = Start(Template() with { ExtraFlags = 0x01, ExtraFlagsDialect = CreatureExtraFlagsDialect.VMangos });
        MoveBoth(flagged, wolfX: 100, playerX: 101);
        Run(flagged.World, 20000);
        Assert.False(flagged.Wolf.IsInEvadeMode);

        using Fight dungeon = Start();
        dungeon.Map.Combat.Hooks = new InstanceHooks();
        MoveBoth(dungeon, wolfX: 100, playerX: 101);
        Run(dungeon.World, 20000);
        Assert.False(dungeon.Wolf.IsInEvadeMode);
    }

    [Fact]
    public void ACrowdControlledCreature_KeepsRefreshingItsExtension_SoItCannotLeash()
    {
        using Fight f = Start();
        MoveBoth(f, wolfX: 100, playerX: 101);
        f.Wolf.UnitFlags |= UnitFlags.Stunned; // the 3 s check refreshes the extension clock (Creature.cpp:988-989)

        Run(f.World, 20000);
        Assert.False(f.Wolf.IsInEvadeMode);

        f.Wolf.UnitFlags &= ~UnitFlags.Stunned;
        Run(f.World, 9000);
        Assert.False(f.Wolf.IsInEvadeMode);
        Run(f.World, 7000);
        Assert.True(f.Wolf.IsInEvadeMode);
    }

    [Fact]
    public void TheTemplateLeash_EvadesAtTheThreeSecondCheck_WhenTheCreatureIsFarFromWhereTheFightBegan()
    {
        // creature_template.Leash is a hard leash polled every 3000 ms (tickTime % 3000 <= diff): 30 yd from the combat start.
        using Fight f = Start(Template() with { Leash = 30f });

        f.Wolf.Relocate(5 + 20, 0, 83.5f, 0, 0); // 20 yd from the fight's start: inside the leash
        Run(f.World, 7000);
        Assert.False(f.Wolf.IsInEvadeMode);

        f.Wolf.Relocate(5 + 40, 0, 83.5f, 0, 0);
        Run(f.World, 3100); // the next 3 s boundary falls inside this window
        Assert.True(f.Wolf.IsInEvadeMode);
    }

    [Fact]
    public void TheHardLeash_AddsTheCreaturesBoundingRadius()
    {
        // vmangos IsWithinDist3d (Object.cpp:1712-1721) defaults to SizeFactor::BoundingRadius: a 3 yd creature leashes past 33 yd.
        using Fight f = Start(Template() with { Leash = 30f });
        f.Wolf.SetFloat(UpdateFields.UnitFieldBoundingradius, 3f);

        f.Wolf.Relocate(5 + 32, 0, 83.5f, 0, 0);
        Run(f.World, 3100);
        Assert.False(f.Wolf.IsInEvadeMode);

        f.Wolf.Relocate(5 + 34, 0, 83.5f, 0, 0);
        Run(f.World, 3100);
        Assert.True(f.Wolf.IsInEvadeMode);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(7u)]
    [InlineData(333u)]
    [InlineData(1499u)]
    [InlineData(2999u)]
    [InlineData(3001u)]
    [InlineData(4500u)]
    [InlineData(9100u)]
    public void TheHardLeashCheck_IsNeverSkipped_WhateverTheTickLength(uint step)
    {
        // The clock advances by exactly the tick's diff before the check, so clock % 3000 <= diff holds on every tick that
        // reaches a multiple of 3000 (vmangos Creature.cpp:979 polls tickTime() % 3000 <= update_diff the same way): one window of
        // 3000 ms plus one tick always contains a check.
        using Fight f = Start(Template() with { Leash = 30f });
        f.Wolf.Relocate(5 + 40, 0, 83.5f, 0, 0);

        Run(f.World, 3000 + step, step);

        Assert.True(f.Wolf.IsInEvadeMode);
    }

    [Fact]
    public void TheLeashCheckInterval_IsConfigurable_AndZeroTurnsTheHardLeashOff()
    {
        using Fight off = Start(Template() with { Leash = 30f }, new CreatureOptions { LeashCheckIntervalMs = 0 });
        off.Wolf.Relocate(5 + 40, 0, 83.5f, 0, 0);
        Run(off.World, 10000);
        Assert.False(off.Wolf.IsInEvadeMode);
    }

    [Fact]
    public void CreaturesThatJoinThroughAnAssistanceCall_ShareTheCallersLeashClock()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 5, 0), Spawn(2, WolfEntry, 6, 1)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() },
            new CreatureOptions { AggroScanMode = AggroScanMode.Poll, AggroRate = 0 });
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature caller = system.Creatures.Single(c => c.Spawn!.Guid == 1);
        Creature helper = system.Creatures.Single(c => c.Spawn!.Guid == 2);

        map.Combat.DealDamage(player, caller, 1, direct: false);
        Run(world, 2500); // the assistance call executes after 1500 ms

        Assert.Same(player, helper.Combat.Victim);
        Assert.NotNull(caller.LeashClock);
        Assert.Same(caller.LeashClock, helper.LeashClock);
    }

    [Fact]
    public void TheClockIsClearedWhenCombatStops_SoTheNextFightGetsAFreshTwelveSeconds()
    {
        using Fight f = Start();
        MoveBoth(f, wolfX: 100, playerX: 101);
        Run(f.World, 14000);
        Assert.True(f.Wolf.IsInEvadeMode);

        Run(f.World, 14000); // 95 yd home at run speed
        Assert.False(f.Wolf.IsInEvadeMode);
        f.Map.Combat.DealDamage(f.Player, f.Wolf, 1, direct: false);
        f.Wolf.AI!.CombatMovement = false;
        MoveBoth(f, wolfX: 100, playerX: 101);

        Run(f.World, 11000);

        Assert.False(f.Wolf.IsInEvadeMode);
    }

    private sealed class InstanceHooks : CombatHooks
    {
        public override bool IsInstanceable(uint mapId) => true;
    }
}
