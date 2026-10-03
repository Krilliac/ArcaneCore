using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>Real map ticks and combat regressions for server facing and delayed AI eligibility.</summary>
public sealed class CreatureAiCorrectnessTests
{
    [Fact]
    public void Chase_AlreadyInMeleeReach_FacesTheVictimAndDealsActualDamage()
    {
        CreatureContent content = Content([DamagingTemplate()], [Spawn(1, WolfEntry, 3, 0) with { Orientation = 0 }]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { Hostility = new AlwaysHostile() });
        using WorldRuntime world = runtime;
        map.Combat.Random = new ScriptedRandom();
        (Player player, FakeSession session) = AddPlayer(world, 1, 0, 0);
        player.MaxHealth = player.Health = 1000;
        Creature creature = Assert.Single(system.Creatures);

        Run(world, 500);

        Assert.Same(player, creature.Combat.Victim);
        Assert.True(player.Health < 1000);
        Assert.NotEmpty(Packets(session, WorldOpcode.SmsgAttackerstateupdate));
        Assert.Equal(MathF.PI, creature.Orientation, 3);
        Assert.Equal(3f, creature.X);
        Assert.Equal(0f, creature.Y);
        Assert.False(creature.IsMoving);
    }

    [Fact]
    public void Chase_VictimCirclesInsideMeleeReach_TurnsAgainAndKeepsDealingDamage()
    {
        CreatureContent content = Content([DamagingTemplate()], [Spawn(1, WolfEntry, 3, 0) with { Orientation = 0 }]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { Hostility = new AlwaysHostile() });
        using WorldRuntime world = runtime;
        map.Combat.Random = new ScriptedRandom();
        (Player player, FakeSession session) = AddPlayer(world, 1, 6, 0);
        player.MaxHealth = player.Health = 1000;
        Creature creature = Assert.Single(system.Creatures);
        Run(world, 500);
        Assert.True(player.Health < 1000); // Already faces east: the immutable baseline can hit here.
        uint healthBeforeTurn = player.Health;
        session.Clear();

        player.Relocate(0, 0, player.Z, 0, 0);
        Run(world, 2500);

        Assert.True(player.Health < healthBeforeTurn);
        Assert.NotEmpty(Packets(session, WorldOpcode.SmsgAttackerstateupdate));
        Assert.Equal(MathF.PI, creature.Orientation, 3);
        Assert.Equal(3f, creature.X);
        Assert.Equal(0f, creature.Y);
        Assert.False(creature.IsMoving);
    }

    [Fact]
    public void Chase_FacingUpdatesDuringSettlement_WithoutDamageUntilSettlementEnds()
    {
        CreatureContent content = Content([DamagingTemplate()], [Spawn(1, WolfEntry, 3, 0) with { Orientation = 0 }]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { Hostility = new AlwaysHostile() });
        using WorldRuntime world = runtime;
        map.Combat.Random = new ScriptedRandom();
        (Player player, FakeSession session) = AddPlayer(world, 1, 6, 0);
        player.MaxHealth = player.Health = 1000;
        Creature creature = Assert.Single(system.Creatures);
        Run(world, 500);
        Assert.True(player.Health < 1000);
        uint healthBeforeHold = player.Health;
        Guid operation = Guid.Parse("b2585a90-3f77-4dac-ab69-2038b244fa02");
        Assert.True(player.BeginQuestSettlement(operation));
        session.Clear();

        player.Relocate(0, 0, player.Z, 0, 0);
        Run(world, 2500);

        Assert.Equal(healthBeforeHold, player.Health);
        Assert.Empty(Packets(session, WorldOpcode.SmsgAttackerstateupdate));
        Assert.Equal(MathF.PI, creature.Orientation, 3);
        Assert.False(creature.IsMoving);
        Assert.True(player.EndQuestSettlement(operation));
        Run(world, 500);
        Assert.True(player.Health < healthBeforeHold);
        Assert.NotEmpty(Packets(session, WorldOpcode.SmsgAttackerstateupdate));
    }

    public enum AssistanceChange
    {
        None,
        HelperFaction,
        CallerFaction,
        HelperUnselectable,
        HelperImmuneToPlayer,
        CallerDeath,
        CallerDespawn,
        CallerEvade,
    }

    [Theory]
    [InlineData(AssistanceChange.None)]
    [InlineData(AssistanceChange.HelperFaction)]
    [InlineData(AssistanceChange.CallerFaction)]
    [InlineData(AssistanceChange.HelperUnselectable)]
    [InlineData(AssistanceChange.HelperImmuneToPlayer)]
    [InlineData(AssistanceChange.CallerDeath)]
    [InlineData(AssistanceChange.CallerDespawn)]
    [InlineData(AssistanceChange.CallerEvade)]
    public void DelayedAssistance_RechecksEligibilityAndTheExactCallersFight(AssistanceChange change)
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 10, 0), Spawn(2, WolfEntry, 17, 0)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        player.MaxHealth = player.Health = 1000;
        Creature caller = Assert.Single(system.Creatures, c => c.Spawn!.Guid == 1);
        Creature helper = Assert.Single(system.Creatures, c => c.Spawn!.Guid == 2);
        map.Combat.DealDamage(player, caller, 1, direct: false);
        Assert.Equal(1, system.PendingAssistCount);
        Assert.Null(helper.Combat.Victim);

        switch (change)
        {
            case AssistanceChange.HelperFaction:
                helper.FactionTemplate = 33;
                break;
            case AssistanceChange.CallerFaction:
                caller.FactionTemplate = 33;
                break;
            case AssistanceChange.HelperUnselectable:
                helper.UnitFlags |= UnitFlags.NotSelectable;
                break;
            case AssistanceChange.HelperImmuneToPlayer:
                helper.UnitFlags |= UnitFlags.ImmuneToPlayer;
                break;
            case AssistanceChange.CallerDeath:
                system.KillCreature(caller);
                break;
            case AssistanceChange.CallerDespawn:
                system.Despawn(caller);
                break;
            case AssistanceChange.CallerEvade:
                caller.AI!.EnterEvadeMode();
                break;
        }

        Run(world, 1400);
        Assert.Null(helper.Combat.Victim);
        Run(world, 200);

        Assert.Equal(0, system.PendingAssistCount);
        if (change == AssistanceChange.None)
        {
            Assert.Same(player, helper.Combat.Victim);
            Assert.Contains(helper, player.Combat.ThreatenedBy);
            Assert.True(helper.CalledAssistance);
            // Delay-time range is intentionally not a new restriction on a healthy call.
            Assert.True(Distance2D(helper, caller) > 10f + helper.BoundingRadius + caller.BoundingRadius);
        }
        else
        {
            Assert.Null(helper.Combat.Victim);
            Assert.False(helper.Combat.IsInCombat);
            Assert.DoesNotContain(helper, player.Combat.ThreatenedBy);
            Assert.False(helper.CalledAssistance);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ContestedGuard_RealAggroAndDamageUseTheContestedPlayerFlag(bool ordinaryPvp, bool contestedPvp)
    {
        var catalog = new FactionTemplateCatalog(
        [
            new FactionTemplateRecord(1, 1, 0, OwnMask: 1, FriendlyMask: 0, HostileMask: 0),
            new FactionTemplateRecord(500, 72, 0x1000, OwnMask: 0, FriendlyMask: 0, HostileMask: 0),
        ]);
        CreatureContent content = Content([DamagingTemplate() with { Faction = 500 }],
            [Spawn(1, WolfEntry, 3, 0) with { Orientation = MathF.PI }]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { Hostility = new FactionCreatureHostility(catalog) });
        using WorldRuntime world = runtime;
        map.Combat.Random = new ScriptedRandom();
        (Player player, FakeSession session) = AddPlayer(world, 1, 0, 0);
        player.MaxHealth = player.Health = 1000;
        if (ordinaryPvp)
        {
            // Use the real toggle path so the combat tick cannot immediately expire a
            // directly assigned ordinary PvP flag whose five-minute timer was never set.
            map.Combat.TogglePvp(player, desired: true);
        }

        if (contestedPvp)
        {
            player.Flags |= PlayerFlags.ContestedPvp;
        }

        Creature guard = Assert.Single(system.Creatures);
        Run(world, 500);

        Assert.Equal(ordinaryPvp, (player.UnitFlags & UnitFlags.Pvp) != 0);
        Assert.Equal(contestedPvp, (player.Flags & PlayerFlags.ContestedPvp) != 0);

        if (contestedPvp)
        {
            Assert.Same(player, guard.Combat.Victim);
            Assert.True(player.Health < 1000);
            Assert.NotEmpty(Packets(session, WorldOpcode.SmsgAttackerstateupdate));
            Assert.True(guard.Combat.IsInCombat);
        }
        else
        {
            Assert.Null(guard.Combat.Victim);
            Assert.Equal(1000u, player.Health);
            Assert.Empty(Packets(session, WorldOpcode.SmsgAttackerstateupdate));
            Assert.False(guard.Combat.IsInCombat);
        }
    }

    private static CreatureTemplate DamagingTemplate()
        => Template() with { MinMeleeDamage = 10, MaxMeleeDamage = 10 };
}
