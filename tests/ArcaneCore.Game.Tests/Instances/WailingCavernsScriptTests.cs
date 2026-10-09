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
    public void NaralexEscortWaypoint_RunsItsMovementScriptEmote()
    {
        const uint scriptId = 367802; // ClassicDB z2815: EMOTE_POINT (25), then text template 1257.
        var ai = new CreatureAiContent([], [])
        {
            DbScripts = new DbScriptCatalog([(DbScriptKind.CreatureMovement,
                new RelayScriptStep(scriptId, 0, 0, 1, 25, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0))]),
        };
        using var run = new DungeonScriptTestKit(map => new WailingCavernsInstance(map), [3678], [3678], [],
            ai: ai, entryWaypoints: [(0u, CreatureContent.WaypointPathBit | DiscipleOfNaralexAi.PathId,
                new CreatureWaypoint(1, -13.4f, -383.07f, 61.78f, 100, 5000) { ScriptId = scriptId })]);
        var escort = Assert.IsType<DiscipleOfNaralexAi>(run.Creature(3678).AI);
        Assert.True(escort.Start(waypointPath: DiscipleOfNaralexAi.PathId));
        for (int i = 0; i < 100 && run.Sent(WorldOpcode.SmsgEmote).Count == 0; i++) run.Tick(100);

        Assert.True(run.Sent(WorldOpcode.SmsgEmote).Any(packet => BitConverter.ToUInt32(packet, 0) == 25),
            $"escort index {escort.CurrentWaypointIndex}, point count {escort.WaypointCount}, location {run.Creature(3678).X}, moving {run.Creature(3678).IsMoving}");
    }
    /// <summary>The disciple's path: cmangos <c>waypoint_path</c> 3678, stored under entry 0 (CreatureContent.WaypointPathBit).</summary>
    private const uint NaralexPathKey = CreatureContent.WaypointPathBit | DiscipleOfNaralexAi.PathId;

    private static (uint Entry, uint PathId, CreatureWaypoint Point) PathPoint(uint point, float x, uint waitMs = 0)
        => (CreatureContent.WaypointPathEntry, NaralexPathKey, new CreatureWaypoint(point, x, -383.07f, 61.78f, 100, waitMs));

    private static void FinishTheFanglords(DungeonScriptTestKit run)
    {
        foreach (uint type in new uint[] { 0, 1, 2, 3 })
        {
            run.Script.SetData(type, EncounterState.Done);
        }
    }

    [Fact]
    public void FortuneAwaitsChestRespawnsForCompletedUnrewardedQuest()
    {
        using var run = new DungeonScriptTestKit(map => new WailingCavernsInstance(map),
            [], [], [(180055, GameObjectType.Chest)],
            objectSpawnTimes: new Dictionary<uint, int> { [180055] = -300 },
            questReady: (_, quest) => quest == 7944);
        Assert.True(run.Object(180055).IsSpawned);
    }

    /// <summary>
    /// The chamber ritual to the flight out (mangos-classic wailing_cavernsScripts.cpp:300-417). Step 12 never advances in the source, so it
    /// repeats every 30 s while the disciple flies on, and the event ends when the disciple reaches the end of the path and despawns
    /// (npc_escortAI::MovementInform, escort_ai.cpp:182-202): Naralex is left behind and no other spawn of the instance goes (the source's
    /// step 13 and DespawnAll are unreachable).
    /// </summary>
    [Fact]
    public void FinalRitual_AwakensNaralex_BothFlyOut_AndTheEventEndsWhenTheDiscipleReachesTheEndOfThePath()
    {
        // The last point is 300 yd out: a run of more than 30 s, so the flight step fires a second time before the path ends.
        using var run = new DungeonScriptTestKit(map => new WailingCavernsInstance(map),
            [3678, 3679, 5762, 5763, 3654, 10000], [3678, 3679, 10000], [],
            entryWaypoints: [PathPoint(70, -13.4f, waitMs: 1000), PathPoint(71, 286.6f)]);
        FinishTheFanglords(run);
        Creature disciple = run.Creature(3678);
        Creature naralex = run.Creature(3679);
        Creature bystander = run.Creature(10000);
        var escort = Assert.IsType<DiscipleOfNaralexAi>(disciple.AI);
        Assert.True(escort.Start(waypointPath: DiscipleOfNaralexAi.PathId));

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

            flying = disciple.IsAlive && (disciple.Movement.Flags & MovementFlags.Hover) != 0;
        }

        Assert.True(flying, $"escort point phase {escort.EventPhase}; disciple state {run.Script.GetData(WailingCavernsInstance.TypeDisciple)}");
        Assert.Equal(EncounterState.Done, run.Script.GetData(WailingCavernsInstance.TypeDisciple));
        Assert.Equal(MovementGeneratorType.Follow, naralex.Motion.Top.Type);
        Assert.Equal(2, run.Sent(WorldOpcode.SmsgSplineMoveSetHover).Count);
        Assert.Equal(12, escort.EventPhase);

        // 30 s later the flight step runs again: still step 12, Naralex still following, no extra hover packets (already hovering).
        for (int i = 0; i < 31; i++)
        {
            run.Tick(1_000);
        }

        Assert.True(disciple.IsAlive);
        Assert.Equal(12, escort.EventPhase);
        Assert.Equal(MovementGeneratorType.Follow, naralex.Motion.Top.Type);
        Assert.Equal(2, run.Sent(WorldOpcode.SmsgSplineMoveSetHover).Count);

        // The end of the path: the disciple despawns (no instant respawn); Naralex and the rest of the instance stay.
        for (int i = 0; i < 60 && disciple.IsAlive; i++)
        {
            run.Tick(1_000);
        }

        Assert.False(disciple.IsAlive);
        Assert.True(naralex.IsAlive);
        Assert.True(bystander.IsAlive);
        Assert.Equal(EncounterState.Done, run.Script.GetData(WailingCavernsInstance.TypeDisciple));
    }

    [Fact]
    public void DiscipleGossip_StartsTheEscortWhenAllFourFanglordsAreDone()
    {
        using var run = new DungeonScriptTestKit(map => new WailingCavernsInstance(map), [3678], [3678], [],
            entryWaypoints: [PathPoint(12, -13.4f)]);
        var gossip = new DiscipleOfNaralexGossip();
        Assert.Empty(gossip.Hello(run.Player, run.Npc(3678))!.Items);
        FinishTheFanglords(run);

        ScriptedGossipMenu menu = gossip.Hello(run.Player, run.Npc(3678))!;
        ScriptedGossipItem item = Assert.Single(menu.Items);
        Assert.Equal("Let the event begin!", item.Text);
        Assert.True(gossip.SelectReply(run.Player, run.Npc(3678), item.Sender, item.Action).Close);
        Assert.Equal(EncounterState.InProgress, run.Script.GetData(WailingCavernsInstance.TypeDisciple));
        Assert.Equal(1, Assert.IsType<DiscipleOfNaralexAi>(run.Creature(3678).AI).WaypointCount);
    }

    /// <summary>
    /// mangos-classic npc_escortAI::Start with a path id reads <c>waypoint_path</c> only (origin PATH_FROM_WAYPOINT_PATH, escort_ai.cpp:253-261):
    /// a <c>script_waypoint</c> path or an entry path of the disciple's entry does not start the escort.
    /// </summary>
    [Fact]
    public void DiscipleGossip_DoesNotWalkAnEntryPath_OnlyWaypointPath3678()
    {
        var point = new CreatureWaypoint(12, -13.4f, -383.07f, 61.78f, 0, 0);
        using var run = new DungeonScriptTestKit(map => new WailingCavernsInstance(map), [3678], [3678], [],
            entryWaypoints: [(3678u, EscortAI.ScriptWaypointPathBit | 3678u, point), (3678u, EscortAI.ScriptWaypointPathBit, point),
                (3678u, 3678u, point), (3678u, 0u, point)]);
        var gossip = new DiscipleOfNaralexGossip();
        FinishTheFanglords(run);
        ScriptedGossipItem item = Assert.Single(gossip.Hello(run.Player, run.Npc(3678))!.Items);

        Assert.True(gossip.SelectReply(run.Player, run.Npc(3678), item.Sender, item.Action).Close);
        var escort = Assert.IsType<DiscipleOfNaralexAi>(run.Creature(3678).AI);
        Assert.False(escort.HasEscortState(EscortAI.EscortState.Escorting));
        Assert.Equal(EncounterState.Special, run.Script.GetData(WailingCavernsInstance.TypeDisciple));
    }

    [Fact]
    public void DiscipleStartsOnlyAfterTheFanglords_ThenWaitsForBothRaptorsAtTheFirstCorner()
    {
        using var run = new DungeonScriptTestKit(map => new WailingCavernsInstance(map),
            [3678, 3679, 3636], [3678, 3679], [],
            entryWaypoints: [PathPoint(12, -13.4f, waitMs: 1000), PathPoint(13, -12.4f)]);
        FinishTheFanglords(run);

        Assert.Equal(EncounterState.Special, run.Script.GetData(WailingCavernsInstance.TypeDisciple));
        EscortAI escort = Assert.IsAssignableFrom<EscortAI>(run.Creature(3678).AI);
        Assert.True(escort.Start(waypointPath: DiscipleOfNaralexAi.PathId));
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
