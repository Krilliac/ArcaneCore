using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// H1 outcome checks of the creature system, one fires/silent pair per key: <c>creature/under-map</c>, <c>creature/stuck</c>,
/// <c>creature/stuck-in-combat</c>, <c>creature/path-failed</c>, <c>creature/home-unreachable</c>, <c>respawn/duplicate</c>, and (at the
/// helper level, see the notes on those tests) <c>creature/evade-loop</c>, <c>creature/leash-reset-loop</c> and
/// <c>creature/waypoint-path-missing</c>. A creature's subject is <c>creature entry E spawn S</c>, so each scenario owns an entry and the
/// capture filters on it (the ledger is process-wide).
/// </summary>
public sealed class CreatureOutcomeAnomalyTests
{
    private const uint UnderHardEntry = 949001;
    private const uint UnderTerrainEntry = 949002;
    private const uint UnderMarginEntry = 949003;
    private const uint UnderFlyerEntry = 949004;
    private const uint UnderWaterEntry = 949005;
    private const uint UnderRearmEntry = 949006;
    private const uint StuckEntry = 949010;
    private const uint MovingEntry = 949011;
    private const uint StunnedEntry = 949012;
    private const uint IdleEntry = 949013;
    private const uint UnreachableChaserEntry = 949014;
    private const uint CombatEmptyEntry = 949020;
    private const uint CombatHealthyEntry = 949021;
    private const uint CombatEndedEntry = 949022;
    private const uint NoPathEntry = 949030;
    private const uint HomeEntry = 949031;
    private const uint WanderEntry = 949032;
    private const uint MeshOkEntry = 949033;
    private const uint TeleportHomeEntry = 949034;
    private const uint RearmPathEntry = 949035;
    private const uint AtHomeEntry = 949036;
    private const uint DuplicateEntry = 949040;
    private const uint DuplicateOtherEntry = 949041;
    private const uint DistinctEntry = 949042;
    private const uint EvadeEntry = 949050;
    private const uint LeashEntry = 949051;
    private const uint WaypointEntry = 949052;

    private const float WolfX = 30f;

    private sealed class FixedHeight(float? height) : ICreatureHeightProvider
    {
        public float? GetHeight(uint mapId, float x, float y, float z) => height;
    }

    /// <summary>The model-aware under-map query with one fixed answer; records the margin it was asked with.</summary>
    private sealed class FixedUnderMap(UnderMapVerdict verdict) : IUnderMapClassifier
    {
        public List<float> Margins { get; } = [];

        public UnderMapVerdict ClassifyUnderMap(float x, float y, float z, float minDepthYards = 0f)
        {
            Margins.Add(minDepthYards);
            return verdict;
        }
    }

    /// <summary>A pathfinder with one fixed answer: no route, or the straight two-point route.</summary>
    private sealed class FixedPathfinder(bool route) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => route ? new PathResult(PathType.Normal, [start, end]) : new PathResult(PathType.NoPath, []);
    }

    private sealed record Scene(WorldRuntime World, Map Map, CreatureMapSystem System, Creature Wolf, Player Player) : IDisposable
    {
        public void Dispose() => World.Dispose();

        public void Run(uint ms, uint step = 100) => ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport.Run(World, ms, step);
    }

    private static Scene Start(uint entry, Func<CreatureTemplate, CreatureTemplate>? shape = null, float z = 83.5f, byte movementType = 0,
        float wander = 0, ICreatureHeightProvider? height = null, IPathfinder? pathfinder = null, CreatureOptions? options = null,
        IUnderMapClassifier? underMap = null)
    {
        CreatureTemplate template = Template(entry);
        if (shape is not null)
        {
            template = shape(template);
        }

        CreatureContent content = Content([template], [Spawn(1, entry, WolfX, 0, z, movementType: movementType, wander: wander)]);
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var system = new CreatureMapSystem(map, content, options, height, random: new Random(1), underMap: underMap ?? new FixedUnderMap(UnderMapVerdict.Under(100f)));
        map.AddUpdater(system);
        if (pathfinder is not null)
        {
            WorldCollision.Of(world).Install(pathfinder: pathfinder);
        }

        (Player player, _) = AddPlayer(world, 1, 20, 0);
        world.RunTick(50);
        return new Scene(world, map, system, Assert.Single(system.Creatures), player);
    }

    private static AnomalyCapture CaptureFor(string key, uint entry)
        => AnomalyLedger.Shared.Capture(n => n.Key == key && n.Subject.StartsWith($"creature entry {entry} ", StringComparison.Ordinal));

    private static AnomalyCapture CaptureAny(uint entry)
        => AnomalyLedger.Shared.Capture(n => n.Subject.StartsWith($"creature entry {entry} ", StringComparison.Ordinal));

    // --- creature/under-map -------------------------------------------------------------------------------

    [Fact]
    public void ACreatureBelowTheHardWorldFloor_ReportsUnderMap_OncePerEpisode()
    {
        using Scene s = Start(UnderHardEntry);
        using AnomalyCapture capture = CaptureFor("creature/under-map", UnderHardEntry);

        s.Wolf.Relocate(WolfX, 0, CreatureAnomalies.HardUnderMapZ - 1f, 0, 0);
        s.Run(5_000);

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains("hard world floor", notice.Detail);
        Assert.Equal(AnomalySeverity.Error, notice.Severity);
    }

    [Fact]
    public void ACreatureMoreThanTheMarginBelowTheTerrain_ReportsUnderMap()
    {
        using AnomalyCapture capture = CaptureFor("creature/under-map", UnderTerrainEntry); // armed first: the creature is sampled on its first tick
        using Scene s = Start(UnderTerrainEntry, z: 100f - CreatureAnomalies.UnderMapMarginYards - 1f, height: new FixedHeight(100f));

        s.Run(3_000);

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains("below terrain/mesh floor", notice.Detail);
    }

    [Theory]
    [InlineData(949007u, UnderMapKind.Ok)]      // a cave or a city: a model explains the position
    [InlineData(949008u, UnderMapKind.Unknown)] // the model tile is not loaded: no data, no report
    public void ACreatureBelowTheTerrain_IsNotReported_WhenTheModelsExplainItOrAreNotLoaded(uint entry, UnderMapKind kind)
    {
        var classifier = new FixedUnderMap(new UnderMapVerdict(kind, 0f));
        using Scene s = Start(entry, z: 100f - CreatureAnomalies.UnderMapMarginYards - 30f, height: new FixedHeight(100f), underMap: classifier);
        using AnomalyCapture capture = CaptureAny(entry);

        s.Run(5_000);

        Assert.NotEmpty(classifier.Margins);
        Assert.All(classifier.Margins, margin => Assert.Equal(CreatureAnomalies.UnderMapMarginYards, margin));
        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void ACreatureBelowTheTerrain_IsReported_WhenTheModelAwareQueryConvicts()
    {
        const uint entry = 949009;
        using AnomalyCapture capture = CaptureFor("creature/under-map", entry);
        using Scene s = Start(entry, z: 100f - CreatureAnomalies.UnderMapMarginYards - 30f, height: new FixedHeight(100f),
            underMap: new FixedUnderMap(UnderMapVerdict.Under(45f)));

        s.Run(3_000);

        Assert.Single(capture.Notices);
    }

    [Fact]
    public void ACreatureExactlyAtTheMargin_IsNotUnderTheMap()
    {
        using Scene s = Start(UnderMarginEntry, z: 100f - CreatureAnomalies.UnderMapMarginYards, height: new FixedHeight(100f));
        using AnomalyCapture capture = CaptureAny(UnderMarginEntry);

        s.Run(5_000);

        Assert.Equal(100f - CreatureAnomalies.UnderMapMarginYards, s.Wolf.Z, 0.001f);
        Assert.Empty(capture.Notices);
    }

    [Theory]
    [InlineData(UnderFlyerEntry, (byte)4)]  // flies
    [InlineData(UnderWaterEntry, (byte)2)]  // water only
    public void AFlyerOrSwimmerBelowTheTerrain_IsNotUnderTheMap(uint entry, byte inhabit)
    {
        using Scene s = Start(entry, t => t with { InhabitType = inhabit }, z: 40f, height: new FixedHeight(100f));
        using AnomalyCapture capture = CaptureAny(entry);

        s.Run(5_000);

        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void AnUnderMapEpisodeEnds_WhenTheCreatureIsBackOnTheGround_AndReportsAgainWhenItFallsAgain()
    {
        using Scene s = Start(UnderRearmEntry, z: 83.5f, height: new FixedHeight(83.5f));
        using AnomalyCapture capture = CaptureFor("creature/under-map", UnderRearmEntry);

        s.Wolf.Relocate(WolfX, 0, 83.5f - CreatureAnomalies.UnderMapMarginYards - 5f, 0, 0);
        s.Run(3_000);
        Assert.Single(capture.Notices);

        s.Wolf.Relocate(WolfX, 0, 83.5f, 0, 0);
        s.Run(3_000);
        s.Wolf.Relocate(WolfX, 0, 83.5f - CreatureAnomalies.UnderMapMarginYards - 5f, 0, 0);
        s.Run(3_000);

        Assert.Equal(2, capture.Notices.Count);
    }

    // --- creature/stuck -----------------------------------------------------------------------------------

    private static void StartSlowWalk(Scene s)
    {
        s.Wolf.WalkSpeed = 0.01f; // a leg that makes well under StuckMinProgressYards (0.5) in StuckThresholdMs (6 s)
        s.Wolf.StartSpline(WolfX + 40f, 0, 83.5f, run: false, finalOrientation: null, splineId: 1, clockMs: s.System.ClockMs);
        Assert.True(s.Wolf.IsMoving);
    }

    [Fact]
    public void ACreatureOnASplineThatMakesNoProgress_ReportsStuck_AfterTheThreshold_OncePerEpisode()
    {
        using Scene s = Start(StuckEntry);
        using AnomalyCapture capture = CaptureFor("creature/stuck", StuckEntry);

        StartSlowWalk(s);
        s.Run(CreatureAnomalies.StuckThresholdMs - 1_000);
        Assert.Empty(capture.Notices);

        s.Run(4_000);
        s.Run(5_000);

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains("moving on spline", notice.Detail);
    }

    [Fact]
    public void ACreatureThatKeepsMoving_ReportsNoStuck()
    {
        using Scene s = Start(MovingEntry);
        using AnomalyCapture capture = CaptureAny(MovingEntry);

        s.Wolf.StartSpline(WolfX + 60f, 0, 83.5f, run: true, finalOrientation: null, splineId: 1, clockMs: s.System.ClockMs);
        float before = s.Wolf.X;
        s.Run(10_000);

        Assert.True(s.Wolf.X > before + 5f, $"the wolf went from {before} to {s.Wolf.X}");
        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void AChaserStandingBecauseItsTargetIsUnreachable_IsNotStuck()
    {
        // vmangos TargetedMovementGenerator.cpp:188-189 / cmangos ChaseMovementGenerator::DispatchSplineToPosition: with no path (or at the
        // end of a partial one) the chaser stands on purpose while the unreachable-target timer runs (soft evade at 3 s, evade at 24 s;
        // an evade that never comes is creature/stuck-in-combat). Live 2026-10-16: 351 of 851 "stuck while chasing" reports followed a
        // path-failed report for the same creature within a minute.
        using Scene s = Start(UnreachableChaserEntry, pathfinder: new FixedPathfinder(route: false));
        using AnomalyCapture capture = CaptureFor("creature/stuck", UnreachableChaserEntry);

        s.Map.Combat.DealDamage(s.Player, s.Wolf, 1, direct: false);
        s.Run(CreatureAnomalies.StuckThresholdMs + 4_000);

        // The state the stuck check reads: chasing a living victim out of melee reach, in combat, standing still.
        Assert.Equal(MovementGeneratorType.Chase, s.Wolf.Motion.CurrentType);
        Assert.True(s.Wolf.Combat.IsInCombat);
        Assert.False(MapCombat.CanReachWithMeleeAutoAttack(s.Wolf, s.Player));
        Assert.False(s.Wolf.IsMoving);
        Assert.Equal(WolfX, s.Wolf.X, 3);
        Assert.False(s.Wolf.Motion.IsReachable);
        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void AStunnedCreature_IsNotStuck_BecauseItIsNotTryingToMove()
    {
        using Scene s = Start(StunnedEntry);
        using AnomalyCapture capture = CaptureAny(StunnedEntry);

        StartSlowWalk(s);
        s.Wolf.UnitFlags |= UnitFlags.Stunned;
        s.Run(15_000);

        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void AnIdleCreature_IsNotStuck()
    {
        using Scene s = Start(IdleEntry);
        using AnomalyCapture capture = CaptureAny(IdleEntry);

        s.Run(15_000);

        Assert.False(s.Wolf.IsMoving);
        Assert.Empty(capture.Notices);
    }

    // --- creature/stuck-in-combat -------------------------------------------------------------------------

    [Fact]
    public void ACreatureInCombatWithNoVictimThreatOrAttacker_ReportsStuckInCombat_AfterTenSeconds()
    {
        using Scene s = Start(CombatEmptyEntry);
        using AnomalyCapture capture = CaptureFor("creature/stuck-in-combat", CombatEmptyEntry);
        s.Wolf.AI = null; // AggressorAI evades a victimless fight on the first tick; the check is the backstop for a creature whose AI does not

        s.Wolf.UnitFlags |= UnitFlags.InCombat;
        s.Run(CreatureAnomalies.StuckInCombatEmptyMs - 1_000);
        Assert.Empty(capture.Notices);

        s.Run(3_000);
        s.Run(5_000);

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains("no valid victim", notice.Detail);
    }

    [Fact]
    public void ACreatureInARealFight_ReportsNoStuckInCombat()
    {
        using Scene s = Start(CombatHealthyEntry, pathfinder: new FixedPathfinder(route: true));
        using AnomalyCapture capture = CaptureAny(CombatHealthyEntry);

        s.Map.Combat.DealDamage(s.Player, s.Wolf, 1, direct: false);
        Assert.True(s.Wolf.Combat.IsInCombat);
        s.Run(20_000);

        Assert.True(s.Wolf.Combat.IsInCombat, "the fight ended, so the silence proves nothing");
        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void AStuckInCombatEpisode_NeedsAFreshTenSeconds_AfterTheCombatFlagWasCleared()
    {
        using Scene s = Start(CombatEndedEntry);
        using AnomalyCapture capture = CaptureAny(CombatEndedEntry);
        s.Wolf.AI = null;

        s.Wolf.UnitFlags |= UnitFlags.InCombat;
        s.Run(8_000);
        s.Wolf.UnitFlags &= ~UnitFlags.InCombat;
        s.Run(1_000);
        s.Wolf.UnitFlags |= UnitFlags.InCombat;
        s.Run(8_000); // 8 s + 8 s overall, but never 10 s in one episode

        Assert.Empty(capture.Notices);
    }

    // --- creature/path-failed, creature/home-unreachable --------------------------------------------------

    [Fact]
    public void ANoPathAnswer_ReportsPathFailed_OncePerEpisode()
    {
        using Scene s = Start(NoPathEntry, pathfinder: new FixedPathfinder(route: false));
        using AnomalyCapture capture = CaptureFor("creature/path-failed", NoPathEntry);
        var destination = new Vector3(WolfX - 10f, 0, 83.5f);

        Assert.Equal(PathOutcome.NoPath, s.System.PlanPath(s.Wolf, destination).Outcome);
        Assert.Equal(PathOutcome.NoPath, s.System.PlanPath(s.Wolf, destination).Outcome);

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains("No path for", notice.Detail);
    }

    [Fact]
    public void ANoPathHome_ReportsHomeUnreachable_AndPathFailed_OncePerEpisode()
    {
        using Scene s = Start(HomeEntry, pathfinder: new FixedPathfinder(route: false));
        using AnomalyCapture home = CaptureFor("creature/home-unreachable", HomeEntry);
        using AnomalyCapture failed = CaptureFor("creature/path-failed", HomeEntry);
        var spawnPoint = new Vector3(WolfX, 0, 83.5f);
        s.Wolf.Relocate(WolfX - 10f, 0, 83.5f, 0, 0);

        s.System.PlanPath(s.Wolf, spawnPoint, toSpawn: true);
        s.System.PlanPath(s.Wolf, spawnPoint, toSpawn: true);

        Assert.Contains("no mesh path to home", Assert.Single(home.Notices).Detail);
        Assert.Contains("for home", Assert.Single(failed.Notices).Detail);
    }

    /// <summary>
    /// Live: entry 1224 evaded while standing on its own off-mesh spawn point and reported "no mesh path to home" from 0.0 yd. A creature
    /// already home gets the zero-length straight move, and nothing is reported.
    /// </summary>
    [Fact]
    public void ACreatureAlreadyOnItsSpawnPoint_IsNotHomeUnreachable_WhenTheSpawnIsOffTheMesh()
    {
        using Scene s = Start(AtHomeEntry, pathfinder: new FixedPathfinder(route: false));
        using AnomalyCapture capture = CaptureAny(AtHomeEntry);
        var spawnPoint = new Vector3(WolfX, 0, 83.5f);

        PlannedPath plan = s.System.PlanPath(s.Wolf, spawnPoint, toSpawn: true);

        Assert.Equal(PathOutcome.Direct, plan.Outcome);
        Assert.Equal(spawnPoint, Assert.Single(plan.Corners));
        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void AHomeReturnThatFellBackToATeleport_ReportsHomeUnreachable()
    {
        using Scene s = Start(TeleportHomeEntry);
        using AnomalyCapture capture = CaptureFor("creature/home-unreachable", TeleportHomeEntry);

        ((ICreatureMover)s.System).TeleportHome(s.Wolf, WolfX, 0, 83.5f, 0);

        Assert.Contains("fell back to teleport", Assert.Single(capture.Notices).Detail);
    }

    [Fact]
    public void ARouteThatExists_ReportsNeitherPathFailedNorHomeUnreachable()
    {
        using Scene s = Start(MeshOkEntry, pathfinder: new FixedPathfinder(route: true));
        using AnomalyCapture capture = CaptureAny(MeshOkEntry);

        PlannedPath toPoint = s.System.PlanPath(s.Wolf, new Vector3(WolfX - 10f, 0, 83.5f));
        PlannedPath home = s.System.PlanPath(s.Wolf, new Vector3(WolfX + 10f, 0, 83.5f), toSpawn: true);

        Assert.NotEqual(PathOutcome.NoPath, toPoint.Outcome);
        Assert.NotEqual(PathOutcome.NoPath, home.Outcome);
        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void ARandomWandererWithNoPath_IsNotReported_BecauseASkippedWanderLegIsNormal()
    {
        using Scene s = Start(WanderEntry, movementType: 1, wander: 5f, pathfinder: new FixedPathfinder(route: false));
        using AnomalyCapture capture = CaptureAny(WanderEntry);

        Assert.Equal(MovementGeneratorType.Random, s.Wolf.Motion.CurrentType);
        Assert.Equal(PathOutcome.NoPath, s.System.PlanPath(s.Wolf, new Vector3(WolfX + 3f, 0, 83.5f)).Outcome);

        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void APathFailedEpisode_EndsWithASuccessfulPath_AndReportsAgainOnTheNextFailure()
    {
        var answer = new SwitchablePathfinder();
        using Scene s = Start(RearmPathEntry, pathfinder: answer);
        using AnomalyCapture capture = CaptureFor("creature/path-failed", RearmPathEntry);
        var destination = new Vector3(WolfX - 10f, 0, 83.5f);

        answer.Route = false;
        s.System.PlanPath(s.Wolf, destination);
        answer.Route = true;
        s.System.PlanPath(s.Wolf, destination);
        answer.Route = false;
        s.System.PlanPath(s.Wolf, destination);

        Assert.Equal(2, capture.Notices.Count);
    }

    private sealed class SwitchablePathfinder : IPathfinder
    {
        public bool Route { get; set; }

        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => Route ? new PathResult(PathType.Normal, [start, end]) : new PathResult(PathType.NoPath, []);
    }

    // --- respawn/duplicate --------------------------------------------------------------------------------

    private static Creature Twin(Creature original, uint entry, CreatureContent content)
        => new(original.Spawn!.Guid, Template(entry), original.Spawn, content, new Random(1));

    [Fact]
    public void ASecondCreatureOfTheSameSpawn_ReportsADuplicateRespawn_OnceNotOnReindex()
    {
        CreatureContent content = Content([Template(DuplicateEntry), Template(DuplicateOtherEntry)], [Spawn(1, DuplicateEntry, WolfX, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        using (AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "respawn/duplicate" && n.Subject == $"creature entry {DuplicateOtherEntry} spawn 1"))
        {
            AddPlayer(world, 1, 20, 0);
            world.RunTick(50);
            Creature first = Assert.Single(system.Creatures);
            Creature second = Twin(first, DuplicateOtherEntry, content);

            system.IndexCreature(second);
            system.IndexCreature(second);

            AnomalyNotice notice = Assert.Single(capture.Notices);
            Assert.Contains($"incoming={second.Guid}", notice.Detail);
            Assert.Contains($"existing={first.Guid}", notice.Detail);
            Assert.Equal(AnomalySeverity.Warning, notice.Severity);
        }
    }

    [Fact]
    public void IndexingTheSameCreatureAgain_OrADistinctSpawn_ReportsNoDuplicate()
    {
        CreatureContent content = Content([Template(DistinctEntry)], [Spawn(1, DistinctEntry, WolfX, 0), Spawn(2, DistinctEntry, WolfX + 3f, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        using (AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "respawn/duplicate" && n.Subject.StartsWith($"creature entry {DistinctEntry} ", StringComparison.Ordinal)))
        {
            AddPlayer(world, 1, 20, 0);
            world.RunTick(50);
            Assert.Equal(2, system.Creatures.Count);

            foreach (Creature creature in system.Creatures.ToArray())
            {
                system.IndexCreature(creature); // already indexed: the same object is not a duplicate of itself
            }

            Assert.Equal(0, system.SpawnDuplicateCount);
            Assert.Empty(capture.Notices);
        }
    }

    [Fact]
    public void ALaterCreatureOfASpawn_ThatTookTheIndexAfterTheFirstLeft_ReportsNoDuplicate()
    {
        CreatureContent content = Content([Template(DuplicateEntry), Template(DuplicateOtherEntry)], [Spawn(1, DuplicateEntry, WolfX, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        using (AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "respawn/duplicate" && n.Subject == $"creature entry {DuplicateOtherEntry} spawn 1"))
        {
            AddPlayer(world, 1, 20, 0);
            world.RunTick(50);
            Creature first = Assert.Single(system.Creatures);
            Creature second = Twin(first, DuplicateOtherEntry, content);

            system.UnindexCreature(first); // the first one is gone (a normal despawn) before the successor is indexed
            system.IndexCreature(second);

            Assert.Empty(capture.Notices);
            Assert.True(system.HasSpawnIndex(1));
        }
    }

    [Theory]
    [InlineData(false)] // a stun flag: the Home generator stays on the stack
    [InlineData(true)]  // a script root: EnterEvadeMode leaves it out of evade mode on the spot (cMaNGOS UnitAI.cpp:122-125)
    public void AnEvadingCreatureThatIsStunnedOrRooted_ReportsNoEvadeLoopAndNoScriptedStateInconsistency(bool scriptRoot)
    {
        uint entry = EvadeEntry + (scriptRoot ? 300u : 200u);
        using Scene s = Start(entry);
        using AnomalyCapture capture = CaptureAny(entry);

        s.Wolf.Relocate(WolfX - 20f, 0, 83.5f, 0, 0); // away from home, so the evade has somewhere to go
        if (scriptRoot)
        {
            s.Wolf.AiImmobilized = true;
        }
        else
        {
            s.Wolf.UnitFlags |= UnitFlags.Stunned;
        }

        s.System.EnterEvadeMode(s.Wolf);
        Assert.Equal(!scriptRoot, s.Wolf.IsEvading);
        Assert.Equal(scriptRoot ? MovementGeneratorType.Idle : MovementGeneratorType.Home, s.Wolf.Motion.CurrentType);
        s.Run(3_000);

        Assert.Empty(capture.Notices);
    }

    private const uint RootLeakEntry = 949060;
    private const uint RootHeldEntry = 949061;

    [Fact]
    public void ACombatOnlyScriptRoot_ThatSurvivesOutOfCombat_ReportsBossStateInconsistent_AndEndsTheEpisodeWhenLifted()
    {
        using Scene s = Start(RootLeakEntry);
        using AnomalyCapture capture = CaptureFor("creature-script/boss-state-inconsistent", RootLeakEntry);

        s.System.SetAiImmobilized(s.Wolf, true, combatOnly: true);
        s.Run(2_000);
        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains("combat-only AI root out of combat", notice.Detail);

        s.System.SetAiImmobilized(s.Wolf, false, combatOnly: false);
        s.Run(1_000);
        s.System.SetAiImmobilized(s.Wolf, true, combatOnly: true); // leaks again: a new episode
        s.Run(1_000);

        Assert.Equal(2, capture.Notices.Count);
    }

    [Fact]
    public void ARootHeldInCombat_OrAScriptRootThatIsNotCombatOnly_ReportsNothing()
    {
        using Scene s = Start(RootHeldEntry);
        using AnomalyCapture capture = CaptureAny(RootHeldEntry);
        s.Wolf.AI = null; // no AI to pull it out of the (flag-only) combat

        s.Wolf.UnitFlags |= UnitFlags.InCombat;
        s.System.SetAiImmobilized(s.Wolf, true, combatOnly: true);
        s.Run(3_000);
        Assert.True(s.Wolf.Combat.IsInCombat);

        s.Wolf.UnitFlags &= ~UnitFlags.InCombat;
        s.System.SetAiImmobilized(s.Wolf, false, combatOnly: false); // lift the combat-only root first (it also clears the combat-only mark)
        s.System.SetAiImmobilized(s.Wolf, true, combatOnly: false); // a permanent script root is allowed out of combat
        s.Run(3_000);

        Assert.False(s.Wolf.AiImmobilizedCombatOnly);
        Assert.True(s.Wolf.AiImmobilized);
        Assert.Empty(capture.Notices);
    }

    // --- creature/evade-loop, leash-reset-loop, waypoint-path-missing (helper level) ----------------------
    // The production tree has no caller of RecordEvadeAnomaly, RecordLeashResetAnomaly or ReportMissingWaypointPath (see the report), so
    // these tests prove the detection logic when it is fed, not that a real evade or leash reset feeds it.

    [Fact]
    public void ThreeEvadesInTheWindow_ReportEvadeLoop_OnceAndTwoDoNot()
    {
        using Scene s = Start(EvadeEntry);
        using AnomalyCapture capture = CaptureFor("creature/evade-loop", EvadeEntry);

        s.System.RecordEvadeAnomaly(s.Wolf);
        s.System.RecordEvadeAnomaly(s.Wolf);
        Assert.Empty(capture.Notices);

        s.System.RecordEvadeAnomaly(s.Wolf);
        s.System.RecordEvadeAnomaly(s.Wolf);
        s.System.RecordEvadeAnomaly(s.Wolf);

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains($"entered evade mode {CreatureAnomalies.LoopThreshold} times", notice.Detail);
    }

    [Fact]
    public void EvadesSpreadBeyondTheWindow_NeverReachTheThreshold()
    {
        using Scene s = Start(EvadeEntry + 100);
        using AnomalyCapture capture = CaptureAny(EvadeEntry + 100);

        for (int i = 0; i < 2 * CreatureAnomalies.LoopThreshold; i++)
        {
            s.System.RecordEvadeAnomaly(s.Wolf);
            s.Run((uint)CreatureAnomalies.LoopWindowMs + 1_000, step: 1_000);
        }

        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void ThreeLeashResetsInTheWindow_ReportLeashResetLoop_OnceAndTwoDoNot()
    {
        using Scene s = Start(LeashEntry);
        using AnomalyCapture capture = CaptureFor("creature/leash-reset-loop", LeashEntry);

        s.System.RecordLeashResetAnomaly(s.Wolf, "leash");
        s.System.RecordLeashResetAnomaly(s.Wolf, "leash");
        Assert.Empty(capture.Notices);

        s.System.RecordLeashResetAnomaly(s.Wolf, "leash");
        s.System.RecordLeashResetAnomaly(s.Wolf, "leash");

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Contains("leash reset (leash)", notice.Detail);
    }

    [Fact]
    public void AMissingWaypointPath_IsReportedOncePerPath()
    {
        using Scene s = Start(WaypointEntry);
        using AnomalyCapture capture = CaptureFor("creature/waypoint-path-missing", WaypointEntry);

        s.System.ReportMissingWaypointPath(s.Wolf, 7, "spawn");
        s.System.ReportMissingWaypointPath(s.Wolf, 7, "spawn");
        s.System.ReportMissingWaypointPath(s.Wolf, 8, "spawn");

        Assert.Equal(2, capture.Notices.Count);
        Assert.All(capture.Notices, n => Assert.Contains("has 0 points", n.Detail));
    }

    [Fact]
    public void AnOpenWorldSpawnClippedBelowTerrainWithMeshPointsAndNoLowerFloor_RecoversToSurfaceWithoutReportingUnderMap()
    {
        const uint entry = 949099;
        var mesh = new SurfaceOnlyMesh(100f);
        CreatureContent content = Content([Template(entry)], [Spawn(1, entry, WolfX, 0, 75f)]);
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        WorldCollision.Of(world).Install(lineOfSight: mesh, pathfinder: mesh);
        var system = new CreatureMapSystem(map, content, height: new SurfaceOnlyHeight(100f), random: new Random(1), underMap: new FixedUnderMap(UnderMapVerdict.Under(25f)));
        map.AddUpdater(system);
        using AnomalyCapture capture = CaptureFor("creature/under-map", entry);
        AddPlayer(world, 1, 20, 0);

        for (int i = 0; i < 30; i++)
            world.RunTick(100);

        Creature wolf = Assert.Single(system.Creatures);
        Assert.Empty(capture.Notices);
        Assert.True(wolf.Z >= 100f, $"expected clipped open-world spawn to recover to surface >= 100, got {wolf.Z}");
    }

    private sealed class SurfaceOnlyHeight(float surfaceZ) : ICreatureHeightProvider
    {
        public float? GetHeight(uint mapId, float x, float y, float z) => z >= surfaceZ - 1f ? surfaceZ : null;
        public float? GetTerrainHeight(uint mapId, float x, float y, float z) => z >= surfaceZ - 1f ? surfaceZ : null;
    }

    private sealed class SurfaceOnlyMesh(float surfaceZ) : ILineOfSight, IPathfinder, IMeshPointQuery
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float zSearch, float maxSearchDistance) => null;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float zSearch, out ModelAreaInfo info) { info = default; return false; }
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => new(PathType.NoPath, []);
        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point) { point = center; return false; }
        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => MathF.Abs(start.Z - surfaceZ) <= 5f ? new MeshRaycastResult(true, false, 1f, end with { Z = surfaceZ }) : MeshRaycastResult.Unavailable(end);
    }
}
