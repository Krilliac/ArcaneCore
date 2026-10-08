using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class ShadowfangKeepScriptTests
{
    [Fact]
    public void ArugalDoesNotCurseTheOnlyAttacker_OrThunderShockOutOfMeleeRange()
    {
        var spells = new RecordingCreatureSpells();
        using var run = new DungeonScriptTestKit(map => new ShadowfangKeepInstance(map),
            [4275], [], [], aiServices: new CreatureAiServices { Spells = spells }, extraSpawns:
            [Spawn(10, 4275, -14f, -383.07f, 151f, mapId: InstanceFixture.Dungeon)]);
        Creature arugal = run.Creature(4275);
        Assert.True(run.Creatures.AttackStart(arugal, run.Player));
        arugal.AI!.OnUpdate(20_000);
        Assert.DoesNotContain(7621u, spells.Casts); // random non-tank target does not exist
        Assert.DoesNotContain(7803u, spells.Casts); // target is far below the ledge
    }

    [Fact]
    public void IntroArugalAppearsForHisDialogue_ThenVanishes()
    {
        using var run = new DungeonScriptTestKit(map => new ShadowfangKeepInstance(map),
            [4444, 10000], [4444, 10000], []);
        Creature arugal = run.Creature(10000);
        Assert.DoesNotContain(arugal.Guid, run.Player.VisibleObjects);

        for (int i = 0; i < 20 && !run.Player.VisibleObjects.Contains(arugal.Guid); i++) run.Tick(1_000);
        Assert.Contains(arugal.Guid, run.Player.VisibleObjects);

        for (int i = 0; i < 50 && run.Script.GetData(ShadowfangKeepInstance.TypeIntro) != EncounterState.Done; i++) run.Tick(1_000);
        Assert.Equal(EncounterState.Done, run.Script.GetData(ShadowfangKeepInstance.TypeIntro));
        Assert.DoesNotContain(arugal.Guid, run.Player.VisibleObjects);
    }

    [Fact]
    public void NandosMovesWhenTheLastTopLevelPackWolfDies()
    {
        using var run = new DungeonScriptTestKit(map => new ShadowfangKeepInstance(map),
            [3927, 3863, 5058], [3927], [], extraSpawns:
            [
                Spawn(100, 3863, -12f, -383.07f, 152f, mapId: InstanceFixture.Dungeon),
                Spawn(101, 5058, -11f, -383.07f, 152f, mapId: InstanceFixture.Dungeon),
            ]);
        run.Kill(3863);
        Assert.NotEqual(MovementGeneratorType.Point, run.Creature(3927).Motion.Top.Type);
        run.Kill(5058);
        Assert.Equal(MovementGeneratorType.Point, run.Creature(3927).Motion.Top.Type);
    }

    [Fact]
    public void VoidwalkersChooseOnePathLeader_AndThreeFollowers()
    {
        var path = new CreatureWaypoint(1, -10.4f, -383.07f, 61.78f, 0, 0);
        using var run = new DungeonScriptTestKit(map => new ShadowfangKeepInstance(map), [4627],
            [4627, 4627, 4627, 4627], [], entryWaypoints: [(4627u, 0u, path)]);
        run.Tick();
        Creature[] walkers = [.. run.Creatures.Creatures.Where(c => c.Entry == 4627)];
        Assert.Equal(4, walkers.Length);
        Assert.Single(walkers, c => c.Motion.Top.Type == MovementGeneratorType.Waypoint);
        Assert.Equal(3, walkers.Count(c => c.Motion.Top.Type == MovementGeneratorType.Follow));
    }

    [Fact]
    public void VincentSurvivesALethalHit_ThenBecomesFriendly()
    {
        using var run = new DungeonScriptTestKit(map => new ShadowfangKeepInstance(map), [4444], [4444], []);
        Creature vincent = run.Creature(4444);
        run.Map.Combat.DealDamage(run.Player, vincent, vincent.Health + 10, direct: false);
        Assert.True(vincent.IsAlive);
        run.Tick();
        Assert.Equal(35u, vincent.FactionTemplate);
    }

    [Fact]
    public void VincentIntroLeavesHimInTheDeathPose_AndMarksItDone()
    {
        using var run = new DungeonScriptTestKit(map => new ShadowfangKeepInstance(map),
            [4444, 10000], [4444, 10000], []);
        for (int i = 0; i < 65 && run.Script.GetData(ShadowfangKeepInstance.TypeIntro) != EncounterState.Done; i++)
        {
            run.Tick(1_000);
        }

        Assert.Equal(EncounterState.Done, run.Script.GetData(ShadowfangKeepInstance.TypeIntro));
        Assert.Equal(StandState.Dead, run.Creature(4444).StandState);
    }

    [Fact]
    public void PrisonerGossip_OffersTheDoorUntilItIsOpened_AndStartsTheEscort()
    {
        var point = new CreatureWaypoint(12, -12.4f, -383.07f, 61.78f, 0, 0);
        using var run = new DungeonScriptTestKit(map => new ShadowfangKeepInstance(map), [3850], [3850], [],
            entryWaypoints: [(3850u, EscortAI.ScriptWaypointPathBit, point)]);
        var gossip = new ShadowfangPrisonerGossip();
        ScriptedGossipMenu menu = gossip.Hello(run.Player, run.Npc(3850))!;
        ScriptedGossipItem item = Assert.Single(menu.Items);
        Assert.Equal("Please unlock the courtyard door.", item.Text);
        Assert.True(gossip.SelectReply(run.Player, run.Npc(3850), item.Sender, item.Action).Close);
        Assert.True(((EscortAI)run.Creature(3850).AI!).HasEscortState(EscortAI.EscortState.Escorting));
        run.Script.SetData(ShadowfangKeepInstance.TypeFreeNpc, EncounterState.Done);
        Assert.Empty(gossip.Hello(run.Player, run.Npc(3850))!.Items);
    }

    [Fact]
    public void RethilgoreDeathSpeaksThroughTheTwoPrisoners_AndFenrusDeathSummonsArugal()
    {
        var ai = new CreatureAiContent([], [
            new CreatureAiText(-1033007, "Synthetic Ada speech", 0, 0, 0),
            new CreatureAiText(-1033008, "Synthetic Ash speech", 0, 0, 0),
        ]);
        using var run = new DungeonScriptTestKit(map => new ShadowfangKeepInstance(map),
            [3849, 3850, 4274, 4275], [3849, 3850, 4274], [], ai);
        run.Script.SetData(ShadowfangKeepInstance.TypeRethilgore, EncounterState.Done);
        Assert.Equal(2, run.Sent(WorldOpcode.SmsgMessagechat).Count);

        run.Script.SetData(ShadowfangKeepInstance.TypeFenrus, EncounterState.Done);
        Assert.Single(run.Creatures.Creatures, c => c.Entry == 4275);
    }

    [Fact]
    public void PrisonerEscortOpensCourtyardDoorAtWaypointTwelve()
    {
        const float x = -14.4f, y = -383.07f, z = 61.78f;
        CreatureWaypoint[] path = [new(11, x + 1, y, z, 0, 0), new(12, x + 2, y, z, 0, 0), new(13, x + 3, y, z, 0, 0)];
        using var run = new DungeonScriptTestKit(map => new ShadowfangKeepInstance(map),
            [3850], [3850], [(18895, GameObjectType.Door)],
            entryWaypoints: path.Select(p => (3850u, EscortAI.ScriptWaypointPathBit, p)));
        EscortAI escort = Assert.IsAssignableFrom<EscortAI>(run.Creature(3850).AI);
        Assert.True(escort.Start());
        for (int i = 0; i < 30 && run.Script.GetData(ShadowfangKeepInstance.TypeFreeNpc) != EncounterState.Done; i++)
        {
            run.Tick(1_000);
        }

        Assert.Equal(EncounterState.Done, run.Script.GetData(ShadowfangKeepInstance.TypeFreeNpc));
        Assert.Equal(GameObjectState.Active, run.Object(18895).State);
    }
}
