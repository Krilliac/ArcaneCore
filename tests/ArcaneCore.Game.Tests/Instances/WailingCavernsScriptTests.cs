using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class WailingCavernsScriptTests
{
    [Fact]
    public void FortuneAwaitsChestRespawnsForCompletedUnrewardedQuest()
    {
        using var run = new DungeonScriptTestKit(map => new WailingCavernsInstance(map),
            [], [], [(180055, GameObjectType.Chest)],
            objectSpawnTimes: new Dictionary<uint, int> { [180055] = -300 },
            questReady: (_, quest) => quest == 7944);
        Assert.True(run.Object(180055).IsSpawned);
    }

    [Fact]
    public void FinalRitual_AwakensNaralex_AndBothFlyOutTogether()
    {
        var chamber = new CreatureWaypoint(70, -13.4f, -383.07f, 61.78f, 0, 0);
        var after = new CreatureWaypoint(71, -12.4f, -383.07f, 61.78f, 0, 0);
        using var run = new DungeonScriptTestKit(map => new WailingCavernsInstance(map),
            [3678, 3679, 5762, 5763, 3654], [3678, 3679], [],
            entryWaypoints: [(3678u, EscortAI.ScriptWaypointPathBit | 3678u, chamber),
                (3678u, EscortAI.ScriptWaypointPathBit | 3678u, after)]);
        foreach (uint type in new uint[] { 0, 1, 2, 3 }) run.Script.SetData(type, EncounterState.Done);
        var escort = Assert.IsType<DiscipleOfNaralexAi>(run.Creature(3678).AI);
        Assert.True(escort.Start(pathId: 3678));

        bool flying = false;
        for (int i = 0; i < 200 && !flying; i++)
        {
            run.Tick(1_000);
            foreach (Creature enemy in run.Creatures.Creatures.Where(c => c.IsAlive
                && ((c.Entry == 5762 && escort.EventPhase >= 4)
                    || (c.Entry == 5763 && escort.EventPhase >= 6) || c.Entry == 3654)).ToArray())
            {
                run.Map.Combat.Kill(run.Player, enemy);
            }

            Creature? disciple = run.Creatures.Creatures.FirstOrDefault(c => c.Entry == 3678 && c.IsAlive);
            flying = disciple is not null && (disciple.Movement.Flags & MovementFlags.Hover) != 0;
        }

        Assert.True(flying, $"escort point phase {escort.EventPhase}; disciple state {run.Script.GetData(WailingCavernsInstance.TypeDisciple)}");
        Assert.Equal(EncounterState.Done, run.Script.GetData(WailingCavernsInstance.TypeDisciple));
        Assert.Equal(MovementGeneratorType.Follow, run.Creature(3679).Motion.Top.Type);
        Assert.Equal(2, run.Sent(WorldOpcode.SmsgSplineMoveSetHover).Count);
    }

    [Fact]
    public void DiscipleGossip_StartsTheEscortWhenAllFourFanglordsAreDone()
    {
        var point = new CreatureWaypoint(12, -13.4f, -383.07f, 61.78f, 0, 0);
        using var run = new DungeonScriptTestKit(map => new WailingCavernsInstance(map), [3678], [3678], [],
            entryWaypoints: [(3678u, EscortAI.ScriptWaypointPathBit | 3678u, point)]);
        var gossip = new DiscipleOfNaralexGossip();
        Assert.Empty(gossip.Hello(run.Player, run.Npc(3678))!.Items);
        foreach (uint type in new uint[] { 0, 1, 2, 3 }) run.Script.SetData(type, EncounterState.Done);

        ScriptedGossipMenu menu = gossip.Hello(run.Player, run.Npc(3678))!;
        ScriptedGossipItem item = Assert.Single(menu.Items);
        Assert.Equal("Let the event begin!", item.Text);
        Assert.True(gossip.SelectReply(run.Player, run.Npc(3678), item.Sender, item.Action).Close);
        Assert.Equal(EncounterState.InProgress, run.Script.GetData(WailingCavernsInstance.TypeDisciple));
    }

    [Fact]
    public void DiscipleStartsOnlyAfterTheFanglords_ThenWaitsForBothRaptorsAtTheFirstCorner()
    {
        var point = new CreatureWaypoint(12, -13.4f, -383.07f, 61.78f, 0, 0);
        using var run = new DungeonScriptTestKit(map => new WailingCavernsInstance(map),
            [3678, 3679, 3636], [3678, 3679], [],
            entryWaypoints: [(3678u, EscortAI.ScriptWaypointPathBit | 3678u, point),
                (3678u, EscortAI.ScriptWaypointPathBit | 3678u, new CreatureWaypoint(13, -12.4f, -383.07f, 61.78f, 0, 0))]);
        foreach (uint type in new uint[] { 0, 1, 2, 3 })
        {
            run.Script.SetData(type, EncounterState.Done);
        }

        Assert.Equal(EncounterState.Special, run.Script.GetData(WailingCavernsInstance.TypeDisciple));
        EscortAI escort = Assert.IsAssignableFrom<EscortAI>(run.Creature(3678).AI);
        Assert.True(escort.Start(pathId: 3678));
        Assert.Equal(EncounterState.InProgress, run.Script.GetData(WailingCavernsInstance.TypeDisciple));

        for (int i = 0; i < 10 && run.Creatures.Creatures.Count(c => c.Entry == 3636) < 2; i++)
        {
            run.Tick(1_000);
        }

        var raptors = run.Creatures.Creatures.Where(c => c.Entry == 3636).ToArray();
        Assert.Equal(2, raptors.Length);
        Assert.True(escort.HasEscortState(EscortAI.EscortState.Paused));
        foreach (Creature raptor in raptors)
        {
            run.Map.Combat.Kill(run.Player, raptor);
        }

        run.Tick(2_000);
        Assert.False(escort.HasEscortState(EscortAI.EscortState.Paused));
    }
}
