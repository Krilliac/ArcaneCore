using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Battlegrounds;
using ArcaneCore.World.Tests.Playerbots.Combat;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Step = ArcaneCore.World.Playerbots.PlayerbotRecoveryStep;
using Verdict = ArcaneCore.World.Playerbots.PlayerbotPvpCampVerdict;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A dead bot standing in reclaim range of its body weighs the living enemy players near the body (PlayerbotRecovery.JudgePvpCamp,
/// PlayersNearBody): outnumbered by the margin it stays a ghost (invisible to the living) and waits, it reclaims as soon as the count
/// turns favourable, and the wait is bounded (then the spirit healer, or the battleground's spirit guide). The pure judgement and
/// the step decision are tested as tables; the rest runs real players of both teams in a world with the manual clock, so the
/// server's own CanAttack rule (opposite team, the bot PvP-flagged) decides who is an enemy.
/// </summary>
public sealed class PlayerbotRecoveryPvpCampTests
{
    // --- JudgePvpCamp (pure) --------------------------------------------------------------------------------------------------

    // enemies, allies, margin, contested, waitedMs, maxWaitMs, verdict. "needed" = margin (+1 at a contested objective); the bot counts itself.
    [Theory]
    [InlineData(0, 0, 1, false, 0L, 90_000L, Verdict.Clear)]            // nobody there
    [InlineData(0, 5, 0, false, 0L, 90_000L, Verdict.Clear)]            // nobody there, even at margin 0 and with allies
    [InlineData(0, 0, 0, false, 999_999L, 90_000L, Verdict.Clear)]      // no enemies is Clear whatever has been waited
    [InlineData(1, 0, 1, false, 0L, 90_000L, Verdict.Clear)]            // 1 v 1
    [InlineData(2, 0, 1, false, 0L, 90_000L, Verdict.Wait)]             // 2 v 1
    [InlineData(2, 1, 1, false, 0L, 90_000L, Verdict.Clear)]            // 2 v bot + 1 ally
    [InlineData(3, 1, 1, false, 0L, 90_000L, Verdict.Wait)]             // 3 v bot + 1 ally
    [InlineData(1, 0, 0, false, 0L, 90_000L, Verdict.Wait)]             // margin 0: even numbers wait
    [InlineData(1, 1, 0, false, 0L, 90_000L, Verdict.Clear)]            // margin 0, 1 v 2
    [InlineData(2, 0, 1, true, 0L, 90_000L, Verdict.Clear)]             // contested objective: 2 v 1 reclaims
    [InlineData(3, 0, 1, true, 0L, 90_000L, Verdict.Wait)]              // contested objective: 3 v 1 waits
    [InlineData(3, 1, 1, true, 0L, 90_000L, Verdict.Clear)]             // contested: 3 v 2 needs 2, has 1
    [InlineData(4, 1, 1, true, 0L, 90_000L, Verdict.Wait)]              // contested: 4 v 2 is 2
    [InlineData(2, 0, 1, false, 89_999L, 90_000L, Verdict.Wait)]        // one ms short of the bound
    [InlineData(2, 0, 1, false, 90_000L, 90_000L, Verdict.GiveUp)]      // the bound reached
    [InlineData(2, 0, 1, false, 500_000L, 90_000L, Verdict.GiveUp)]
    [InlineData(3, 0, 1, true, 30_000L, 30_000L, Verdict.GiveUp)]       // contested, bound reached
    [InlineData(1, 0, 1, false, 500_000L, 90_000L, Verdict.Clear)]      // Clear wins over the max wait when not outnumbered
    [InlineData(2, 0, 1, true, 500_000L, 90_000L, Verdict.Clear)]
    internal void JudgePvpCamp_Table(int enemies, int allies, int margin, bool contested, long waitedMs, long maxWaitMs, Verdict expected)
        => Assert.Equal(expected, PlayerbotRecovery.JudgePvpCamp(enemies, allies, margin, contested, waitedMs, maxWaitMs));

    // --- Decide (pure) --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Decide_EnemyPlayersCampTheCorpse_WaitsForThem_AndWithoutThemReclaims()
    {
        Assert.Equal(Step.WaitForEnemyPlayers, Decide(enemyPlayersCampCorpse: true));
        Assert.Equal(Step.Reclaim, Decide(enemyPlayersCampCorpse: false));
        Assert.Equal(Step.Reclaim, PlayerbotRecovery.Decide(true, PlayerbotCorpsePlace.ThisMap, false, true, 0, false, false, false)); // the old call shape
    }

    [Fact]
    public void Decide_TheReclaimDelayComesBeforeTheEnemyPlayers()
        => Assert.Equal(Step.WaitForReclaimDelay, Decide(wait: 30, enemyPlayersCampCorpse: true));

    [Fact]
    public void Decide_EnemyPlayersComeBeforeTheCreatureCamp_AndBeforeAWalkToAReviveSpot()
    {
        Assert.Equal(Step.WaitForEnemyPlayers, Decide(hostile: true, enemyPlayersCampCorpse: true));
        Assert.Equal(Step.WaitForEnemyPlayers, Decide(hostile: true, reviveSpotKnown: true, enemyPlayersCampCorpse: true));
        Assert.Equal(Step.WaitForHostiles, Decide(hostile: true, enemyPlayersCampCorpse: false));
        Assert.Equal(Step.WalkToReviveSpot, Decide(hostile: true, reviveSpotKnown: true, enemyPlayersCampCorpse: false));
    }

    [Fact]
    public void Decide_TheFlagChangesNothingBeforeThePvpWait()
    {
        Assert.Equal(Step.WalkToCorpse, Decide(atCorpse: false, enemyPlayersCampCorpse: true));          // not at the body yet
        Assert.Equal(Step.SpiritHealer, Decide(stalled: true, enemyPlayersCampCorpse: true));            // a stall still gives the body up
        Assert.Equal(Step.SpiritHealer, Decide(fallback: true, enemyPlayersCampCorpse: true));           // so does the fallback
        Assert.Equal(Step.SpiritHealer, Decide(corpse: PlayerbotCorpsePlace.None, enemyPlayersCampCorpse: true));
        Assert.Equal(Step.Release, Decide(ghost: false, enemyPlayersCampCorpse: true));                  // alive: nothing to wait for
    }

    private static Step Decide(bool ghost = true, PlayerbotCorpsePlace corpse = PlayerbotCorpsePlace.ThisMap, bool atCorpse = true, long wait = 0,
        bool hostile = false, bool stalled = false, bool fallback = false, bool reviveSpotKnown = false, bool enemyPlayersCampCorpse = false)
        => PlayerbotRecovery.Decide(ghost, corpse, entranceKnown: false, atCorpse, wait, hostile, stalled, fallback, reviveSpotKnown, enemyPlayersCampCorpse);

    // --- ContestedNodeNear (pure) ---------------------------------------------------------------------------------------------

    /// <summary>The real banner positions (vmangos BattleGroundAB.h BG_AB_NodePositions, as the AB tactics tests use them).</summary>
    private static readonly Vector3[] s_nodes =
    [
        new(1166.79f, 1200.13f, -56.70f),   // stables
        new(977.08f, 1046.54f, -44.80f),    // blacksmith
        new(806.18f, 874.27f, -55.99f),     // farm
        new(856.14f, 1148.90f, 11.18f),     // lumber mill
        new(1146.92f, 848.17f, -110.92f),   // gold mine
    ];

    private static IReadOnlyList<PlayerbotAbNode> Nodes(params byte[] statuses)
        => [.. statuses.Select((s, i) => new PlayerbotAbNode(i, s_nodes[i], s))];

    [Theory]
    [InlineData(ArathiBasin.StatusAllianceContested)]
    [InlineData(ArathiBasin.StatusHordeContested)]
    public void ContestedNodeNear_AContestedNodeWithinTheBodyRadius_IsTrue(byte status)
    {
        byte[] statuses = [ArathiBasin.StatusNeutral, status, ArathiBasin.StatusNeutral, ArathiBasin.StatusNeutral, ArathiBasin.StatusNeutral];
        Vector3 body = s_nodes[1] + new Vector3(25f, 0f, 0f);
        Assert.True(PlayerbotArathiBasinTactics.ContestedNodeNear(Nodes(statuses), body, PlayerbotArathiBasinTactics.BodyNodeYards));
    }

    [Theory]
    [InlineData(ArathiBasin.StatusNeutral)]
    [InlineData(ArathiBasin.StatusAllianceOccupied)]
    [InlineData(ArathiBasin.StatusHordeOccupied)]
    public void ContestedNodeNear_ANodeThatIsNotContested_IsFalse(byte status)
    {
        byte[] statuses = [ArathiBasin.StatusNeutral, status, ArathiBasin.StatusNeutral, ArathiBasin.StatusNeutral, ArathiBasin.StatusNeutral];
        Vector3 body = s_nodes[1] + new Vector3(5f, 0f, 0f);
        Assert.False(PlayerbotArathiBasinTactics.ContestedNodeNear(Nodes(statuses), body, PlayerbotArathiBasinTactics.BodyNodeYards));
    }

    [Fact]
    public void ContestedNodeNear_AContestedNodeTooFarAway_IsFalse()
    {
        byte[] statuses = [ArathiBasin.StatusNeutral, ArathiBasin.StatusAllianceContested, ArathiBasin.StatusNeutral, ArathiBasin.StatusNeutral, ArathiBasin.StatusNeutral];
        Vector3 body = s_nodes[1] + new Vector3(60f, 0f, 0f);
        Assert.False(PlayerbotArathiBasinTactics.ContestedNodeNear(Nodes(statuses), body, PlayerbotArathiBasinTactics.BodyNodeYards));
        Assert.True(PlayerbotArathiBasinTactics.ContestedNodeNear(Nodes(statuses), body, 61f)); // the radius is the argument
        Assert.True(PlayerbotArathiBasinTactics.ContestedNodeNear(Nodes(statuses), s_nodes[1] + new Vector3(39f, 0f, 0f), PlayerbotArathiBasinTactics.BodyNodeYards));
        Assert.False(PlayerbotArathiBasinTactics.ContestedNodeNear(Nodes(statuses), s_nodes[1] + new Vector3(41f, 0f, 0f), PlayerbotArathiBasinTactics.BodyNodeYards));
    }

    [Fact]
    public void ContestedNodeNear_MeasuresInThreeDimensions()
    {
        byte[] statuses = [ArathiBasin.StatusNeutral, ArathiBasin.StatusHordeContested, ArathiBasin.StatusNeutral, ArathiBasin.StatusNeutral, ArathiBasin.StatusNeutral];
        // 30 yards across and 30 up: 42.4 yards away in 3D although only 30 on the ground.
        Vector3 body = s_nodes[1] + new Vector3(30f, 0f, 30f);
        Assert.False(PlayerbotArathiBasinTactics.ContestedNodeNear(Nodes(statuses), body, PlayerbotArathiBasinTactics.BodyNodeYards));
        // 30 across and 20 up: 36.1 yards in 3D, inside the radius (the near counterpart, so the case above is not vacuous).
        Assert.True(PlayerbotArathiBasinTactics.ContestedNodeNear(Nodes(statuses), s_nodes[1] + new Vector3(30f, 0f, 20f), PlayerbotArathiBasinTactics.BodyNodeYards));
    }

    [Fact]
    public void ContestedNodeNear_OnlyTheNodeNearTheBodyCounts_AndAnEmptyListIsFalse()
    {
        // The contested node is the gold mine, far from the stables where the body lies; the stables are occupied.
        byte[] statuses = [ArathiBasin.StatusHordeOccupied, ArathiBasin.StatusNeutral, ArathiBasin.StatusNeutral, ArathiBasin.StatusNeutral, ArathiBasin.StatusAllianceContested];
        Assert.False(PlayerbotArathiBasinTactics.ContestedNodeNear(Nodes(statuses), s_nodes[0], PlayerbotArathiBasinTactics.BodyNodeYards));
        Assert.True(PlayerbotArathiBasinTactics.ContestedNodeNear(Nodes(statuses), s_nodes[4], PlayerbotArathiBasinTactics.BodyNodeYards));
        Assert.False(PlayerbotArathiBasinTactics.ContestedNodeNear([], s_nodes[4], PlayerbotArathiBasinTactics.BodyNodeYards));
        Assert.Equal(PlayerbotArathiBasinTactics.DefendYards, PlayerbotArathiBasinTactics.BodyNodeYards);
    }

    // --- PlayersNearBody (world) ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlayersNearBody_CountsEnemiesAndAlliesWithinTheRadius_ByTheServersOwnRules()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddEnemiesAsync(2);                                   // within 40 yards
        await scene.AddAlliesAsync(1);
        Player far = await scene.AddOneAsync(PlayerbotPvpTestKit.Orc, new Vector3(60f, 0f, 0f));   // 60 yards: outside 40, inside 100
        Player high = await scene.AddOneAsync(PlayerbotPvpTestKit.Orc, new Vector3(5f, 0f, 50f));  // 5 across but 50 up: 3D, outside 40
        await scene.SeeAllAsync();
        await scene.OnWorldAsync(() =>
        {
            Assert.True(scene.Map.Combat.Hooks.CanAttack(far, scene.Bot), "the far orc is not attackable to the flagged bot: the fixture is wrong");
            Assert.Equal((2, 1), PlayerbotRecovery.PlayersNearBody(scene.Bot, scene.Body, 40f));
            Assert.Equal((3, 1), PlayerbotRecovery.PlayersNearBody(scene.Bot, scene.Body, 70f));   // the far one, not the high one
            Assert.Equal((4, 1), PlayerbotRecovery.PlayersNearBody(scene.Bot, scene.Body, 100f));
            Assert.True(high.IsAlive);
        });
    }

    [Fact]
    public async Task PlayersNearBody_ADeadEnemyAndAnUnflaggedBot_AreNoCamp()
    {
        await using Scene scene = await Scene.StartAsync();
        List<Player> enemies = await scene.AddEnemiesAsync(2);
        await scene.AddAlliesAsync(1);
        await scene.SeeAllAsync();
        await scene.OnWorldAsync(() =>
        {
            Assert.Equal((2, 1), PlayerbotRecovery.PlayersNearBody(scene.Bot, scene.Body, 40f));
            enemies[0].Health = 0;
            scene.Map.Combat.KillPlayer(enemies[0]);
            Assert.False(enemies[0].IsAlive);
            Assert.Equal((1, 1), PlayerbotRecovery.PlayersNearBody(scene.Bot, scene.Body, 40f));     // a corpse attacks nobody
            scene.Bot.UnitFlags &= ~UnitFlags.Pvp;
            // Not PvP-flagged in the open world: nobody may attack it (CanAttack), so the living enemy is no camp; the ally still counts.
            Assert.Equal((0, 1), PlayerbotRecovery.PlayersNearBody(scene.Bot, scene.Body, 40f));
        });
    }

    // --- the recovery with players (world) ------------------------------------------------------------------------------------

    [Fact]
    public async Task Outnumbered_TheGhostStaysAtItsBody_WaitingForTheEnemyPlayers()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddEnemiesAsync(2);
        await scene.SeeAllAsync();
        using AnomalyCapture anomalies = await scene.OnWorldAsync(() => BotAnomalyCapture.For(scene.Bot));

        for (int i = 0; i < 5; i++)
        {
            Assert.False(await scene.ThinkAsync());
            Assert.Equal(Step.WaitForEnemyPlayers, scene.Recovery.LastStep);
            await scene.OnWorldAsync(() =>
            {
                Assert.False(scene.Bot.IsAlive);
                Assert.True(scene.Bot.Flags.HasFlag(PlayerFlags.Ghost));
                Assert.NotNull(scene.Bot.Combat.Corpse);
            });
        }

        Assert.Equal(2, scene.Recovery.PvpCampEnemies);
        Assert.Equal(0, scene.Recovery.PvpCampAllies);
        Assert.False(scene.Recovery.PvpCampGaveUp);
        Assert.Equal(1, anomalies.Count("recovery/waiting-out-campers")); // one report per death, not one per think
    }

    [Fact]
    public async Task Outnumbered_WhenTheEnemiesLeave_TheGhostReclaimsItsBody()
    {
        await using Scene scene = await Scene.StartAsync();
        List<Player> enemies = await scene.AddEnemiesAsync(2);
        await scene.SeeAllAsync();
        for (int i = 0; i < 3; i++) { await scene.ThinkAsync(); Assert.Equal(Step.WaitForEnemyPlayers, scene.Recovery.LastStep); }
        await scene.OnWorldAsync(() => Assert.False(scene.Bot.IsAlive));

        await scene.OnWorldAsync(() => { foreach (Player e in enemies) e.Relocate(scene.Body.X + 300f, scene.Body.Y, scene.Body.Z, 0, scene.Host.World.NowMs); });
        await scene.ThinkAsync();

        await scene.OnWorldAsync(() =>
        {
            Assert.True(scene.Bot.IsAlive, $"still a ghost, step {scene.Recovery.LastStep}");
            Assert.Null(scene.Bot.Combat.Corpse);
        });
        Assert.Equal(Step.Reclaim, scene.Recovery.LastStep);
    }

    [Fact]
    public async Task Outnumbered_WhenAnEnemyDies_TheCountTurnsFavourable_AndTheGhostReclaims()
    {
        await using Scene scene = await Scene.StartAsync();
        List<Player> enemies = await scene.AddEnemiesAsync(2);
        await scene.SeeAllAsync();
        await scene.ThinkAsync();
        Assert.Equal(Step.WaitForEnemyPlayers, scene.Recovery.LastStep);

        await scene.OnWorldAsync(() => { enemies[0].Health = 0; scene.Map.Combat.KillPlayer(enemies[0]); });
        await scene.ThinkAsync();

        Assert.Equal(Step.Reclaim, scene.Recovery.LastStep);
        Assert.Equal(1, scene.Recovery.PvpCampEnemies);
        await scene.OnWorldAsync(() => Assert.True(scene.Bot.IsAlive));
    }

    [Fact]
    public async Task Outnumbered_WhenAnAllyArrives_TheGhostReclaims()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddEnemiesAsync(2);
        await scene.SeeAllAsync();
        await scene.ThinkAsync();
        Assert.Equal(Step.WaitForEnemyPlayers, scene.Recovery.LastStep);
        await scene.OnWorldAsync(() => Assert.False(scene.Bot.IsAlive));

        await scene.AddAlliesAsync(1);
        await scene.SeeAllAsync();
        await scene.ThinkAsync();

        Assert.Equal(Step.Reclaim, scene.Recovery.LastStep);
        Assert.Equal(2, scene.Recovery.PvpCampEnemies);
        Assert.Equal(1, scene.Recovery.PvpCampAllies);
        await scene.OnWorldAsync(() => Assert.True(scene.Bot.IsAlive));
    }

    [Fact]
    public async Task OneEnemyAgainstTheBot_IsEven_AndTheGhostReclaims()
    {
        await using Scene scene = await Scene.StartAsync();
        await scene.AddEnemiesAsync(1);
        await scene.SeeAllAsync();
        await scene.ThinkAsync();
        Assert.Equal(Step.Reclaim, scene.Recovery.LastStep);
        await scene.OnWorldAsync(() => Assert.True(scene.Bot.IsAlive));
    }

    [Fact]
    public async Task WithMarginZero_OneEnemyAgainstTheBot_MakesTheGhostWait()
    {
        await using Scene scene = await Scene.StartAsync(new PlayerbotRecoveryOptions { PvpCampMargin = 0 });
        await scene.AddEnemiesAsync(1);
        await scene.SeeAllAsync();
        Assert.False(await scene.ThinkAsync());
        Assert.Equal(Step.WaitForEnemyPlayers, scene.Recovery.LastStep);
        await scene.OnWorldAsync(() => Assert.False(scene.Bot.IsAlive));
    }

    [Fact]
    public async Task WithNoPlayersAtTheBody_TheGhostReclaimsAsBefore()
    {
        await using Scene scene = await Scene.StartAsync();
        Assert.True(await scene.ThinkAsync());
        Assert.Equal(Step.Reclaim, scene.Recovery.LastStep);
        Assert.Equal(0, scene.Recovery.PvpCampEnemies);
        Assert.False(scene.Recovery.PvpCampGaveUp);
        await scene.OnWorldAsync(() => Assert.True(scene.Bot.IsAlive));
    }

    [Fact]
    public async Task ABotThatIsNotPvpFlagged_CannotBeAttacked_SoItReclaimsPastTwoEnemyPlayers()
    {
        await using Scene scene = await Scene.StartAsync(flagged: false);
        await scene.AddEnemiesAsync(2);
        await scene.SeeAllAsync();
        await scene.OnWorldAsync(() => Assert.False(scene.Map.Combat.Hooks.CanAttack(scene.Others[0].Player!, scene.Bot)));
        await scene.ThinkAsync();
        Assert.Equal(Step.Reclaim, scene.Recovery.LastStep);
        Assert.Equal(0, scene.Recovery.PvpCampEnemies);
        await scene.OnWorldAsync(() => Assert.True(scene.Bot.IsAlive));
    }

    [Fact]
    public async Task WithTheOptionOff_TheGhostReclaimsPastTwoEnemyPlayers()
    {
        await using Scene scene = await Scene.StartAsync(new PlayerbotRecoveryOptions { PvpCampWait = false });
        await scene.AddEnemiesAsync(2);
        await scene.SeeAllAsync();
        await scene.ThinkAsync();
        Assert.Equal(Step.Reclaim, scene.Recovery.LastStep);
        await scene.OnWorldAsync(() => Assert.True(scene.Bot.IsAlive));
    }

    [Fact]
    public async Task TheReclaimDelayIsStillWaitedOutFirst_BeforeTheEnemyPlayersAreWeighed()
    {
        await using Scene scene = await Scene.StartAsync(delayPassed: false);
        await scene.AddEnemiesAsync(2);
        await scene.SeeAllAsync();
        await scene.ThinkAsync();
        Assert.Equal(Step.WaitForReclaimDelay, scene.Recovery.LastStep);
        scene.Clock.Seconds += 31;
        await scene.ThinkAsync();
        Assert.Equal(Step.WaitForEnemyPlayers, scene.Recovery.LastStep);
    }

    [Fact]
    public async Task TheWaitIsBounded_AfterTheMaxWait_TheGhostTakesTheSpiritHealer_AndNeverReclaims()
    {
        await using Scene scene = await Scene.StartAsync(new PlayerbotRecoveryOptions { PvpCampMaxWaitSeconds = 5 });
        await scene.AddEnemiesAsync(2);
        await scene.SeeAllAsync();
        using AnomalyCapture anomalies = await scene.OnWorldAsync(() => BotAnomalyCapture.For(scene.Bot));

        long start = scene.UptimeMs;
        long gaveUpAfter = -1;
        for (int i = 0; i < 12 && gaveUpAfter < 0; i++)
        {
            await scene.Host.World.AdvanceClockAsync(1000);
            await scene.ThinkAsync(advance: false, elapsedMs: 1000);
            if (scene.Recovery.PvpCampGaveUp) gaveUpAfter = scene.UptimeMs - start;
            else Assert.Equal(Step.WaitForEnemyPlayers, scene.Recovery.LastStep);
        }

        Assert.True(gaveUpAfter >= 5000, $"gave up after {gaveUpAfter} ms, before the 5 s bound");
        Assert.True(gaveUpAfter <= 8000, $"gave up only after {gaveUpAfter} ms");
        Assert.True(scene.Recovery.SpiritHealerChosen || scene.Recovery.UsingSpiritHealer, "the spirit healer was not chosen");
        await scene.OnWorldAsync(() => Assert.False(scene.Bot.IsAlive));
        Assert.Equal(1, anomalies.Count("recovery/waiting-out-campers"));
    }

    // --- battlegrounds (world) ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task InABattleground_AtAContestedObjective_TheBotRunsBackToItsBody_AndReclaimsPastTwoEnemies()
    {
        await using Scene scene = await Scene.StartAsync(battleground: true, contested: true);
        await scene.AddEnemiesAsync(2);
        await scene.SeeAllAsync();
        await scene.ThinkAsync();
        Assert.True(scene.Recovery.BattlegroundBodyRun, "a contested objective at the body did not make the body run");
        Assert.Equal(Step.Reclaim, scene.Recovery.LastStep);
        await scene.OnWorldAsync(() => Assert.True(scene.Bot.IsAlive));
    }

    [Fact]
    public async Task InABattleground_AtAContestedObjective_ThreeEnemiesAgainstTheBot_MakeItWait()
    {
        await using Scene scene = await Scene.StartAsync(battleground: true, contested: true);
        await scene.AddEnemiesAsync(3);
        await scene.SeeAllAsync();
        for (int i = 0; i < 3; i++)
        {
            Assert.False(await scene.ThinkAsync());
            Assert.Equal(Step.WaitForEnemyPlayers, scene.Recovery.LastStep);
        }
        Assert.True(scene.Recovery.BattlegroundBodyRun);
        await scene.OnWorldAsync(() => Assert.False(scene.Bot.IsAlive));
    }

    [Fact]
    public async Task InABattleground_ANodeListWithAContestedNodeAtTheBody_DecidesTheBodyRunThroughContestedNodeNear()
    {
        byte[] statuses = [ArathiBasin.StatusNeutral, ArathiBasin.StatusNeutral, ArathiBasin.StatusHordeContested, ArathiBasin.StatusNeutral, ArathiBasin.StatusNeutral];
        await using Scene scene = await Scene.StartAsync(battleground: true, contested: false);
        // The body lies 12 yards from the farm, which is contested.
        scene.Recovery.ContestedObjectiveNear = (_, at) => PlayerbotArathiBasinTactics.ContestedNodeNear(
            [.. statuses.Select((s, i) => new PlayerbotAbNode(i, scene.Body + new Vector3(i == 2 ? 12f : 500f, 0f, 0f), s))], at, PlayerbotArathiBasinTactics.BodyNodeYards);
        await scene.AddEnemiesAsync(2);
        await scene.SeeAllAsync();
        await scene.ThinkAsync();
        Assert.True(scene.Recovery.BattlegroundBodyRun);
        Assert.Equal(Step.Reclaim, scene.Recovery.LastStep); // contested preference: 2 v 1 reclaims

        await using Scene occupied = await Scene.StartAsync(battleground: true, contested: false);
        byte[] held = [ArathiBasin.StatusNeutral, ArathiBasin.StatusNeutral, ArathiBasin.StatusHordeOccupied, ArathiBasin.StatusNeutral, ArathiBasin.StatusNeutral];
        occupied.Recovery.ContestedObjectiveNear = (_, at) => PlayerbotArathiBasinTactics.ContestedNodeNear(
            [.. held.Select((s, i) => new PlayerbotAbNode(i, occupied.Body + new Vector3(i == 2 ? 12f : 500f, 0f, 0f), s))], at, PlayerbotArathiBasinTactics.BodyNodeYards);
        await occupied.AddEnemiesAsync(2);
        await occupied.SeeAllAsync();
        await occupied.ThinkAsync();
        Assert.False(occupied.Recovery.BattlegroundBodyRun, "an occupied node is no contested objective");
        Assert.False(occupied.Recovery.PvpCampGaveUp);
    }

    [Fact]
    public async Task InABattleground_TheConfiguredNinetySecondWait_IsCappedAtOneSpiritGuideWave_ThenTheGuideTakesOver()
    {
        // Config 90 s (the default): the battleground caps it at AreaResurrectionIntervalMs (30 s).
        await using Scene scene = await Scene.StartAsync(new PlayerbotRecoveryOptions { PvpCampMaxWaitSeconds = 90 }, battleground: true, contested: true);
        await scene.AddEnemiesAsync(3);
        await scene.SeeAllAsync();

        long start = scene.UptimeMs;
        long gaveUpAfter = -1;
        for (int i = 0; i < 60 && gaveUpAfter < 0; i++)
        {
            await scene.Host.World.AdvanceClockAsync(1000);
            await scene.ThinkAsync(advance: false, elapsedMs: 1000);
            if (scene.Recovery.PvpCampGaveUp) gaveUpAfter = scene.UptimeMs - start;
        }

        Assert.True(gaveUpAfter >= PlayerbotRecovery.AreaResurrectionIntervalMs, $"gave up after {gaveUpAfter} ms, before the 30 s spirit-guide wave");
        Assert.True(gaveUpAfter <= PlayerbotRecovery.AreaResurrectionIntervalMs + 3000, $"gave up only after {gaveUpAfter} ms: the 90 s config was not capped");
        Assert.False(scene.Recovery.BattlegroundBodyRun, "the body run was not handed back to the spirit guide");
        Assert.Equal(Step.SpiritHealer, scene.Recovery.LastStep);
        await scene.OnWorldAsync(() => Assert.False(scene.Bot.IsAlive));
    }

    [Fact]
    public async Task InABattleground_WithNoContestedObjective_TheGhostKeepsToTheSpiritGuide_NotTheBody()
    {
        await using Scene scene = await Scene.StartAsync(battleground: true, contested: false);
        await scene.AddEnemiesAsync(2);
        await scene.SeeAllAsync();
        await scene.ThinkAsync();
        Assert.False(scene.Recovery.BattlegroundBodyRun);
    }

    // --- harness --------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A world on the manual clock with a human bot that died and released where it stands (its ghost at its body), the death clock past
    /// the reclaim delay, and helpers adding orc enemies (Horde) and human allies (Alliance) a few yards from the body.
    /// </summary>
    private sealed class Scene : IAsyncDisposable
    {
        private int _next;
        private readonly WorldSession _botSession;

        public WorldTestHost Host { get; }
        public Player Bot => _botSession.Player!;
        public Map Map => Bot.Map!;
        public List<WorldSession> Others { get; } = [];
        public TestDeathClock Clock { get; }
        public PlayerbotRecovery Recovery { get; }
        public Vector3 Body { get; private set; }
        public long UptimeMs => (long)Host.World.Uptime.TotalMilliseconds;

        private Scene(WorldTestHost host, WorldSession bot, TestDeathClock clock, PlayerbotRecovery recovery)
        {
            Host = host;
            _botSession = bot;
            Clock = clock;
            Recovery = recovery;
        }

        public static async Task<Scene> StartAsync(PlayerbotRecoveryOptions? recovery = null, bool flagged = true, bool delayPassed = true,
            bool battleground = false, bool contested = false)
        {
            WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IWorldFeature, ManualClockFeature>());
            WorldSession bot = await PlayerbotPvpTestKit.EnterAsync(host, "CAMPBOT", "Campbot", PlayerbotPvpTestKit.Human);
            var clock = new TestDeathClock(1_000);
            var options = new PlayerbotOptions { Enabled = true, AllowedMaps = [0, 1], Recovery = recovery ?? new PlayerbotRecoveryOptions() };
            var recoveryRun = new PlayerbotRecovery(bot, options);
            var scene = new Scene(host, bot, clock, recoveryRun);
            await host.OnWorldAsync(() =>
            {
                DeathHooks.Register(host.World, new DeathHooks(new DeathOptions(), clock));
                Player player = bot.Player!;
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                Assert.True(player.Map!.Combat.RepopPlayer(player));
                if (flagged) MapCombat.UpdatePvp(player, true); // flagged with the 5-minute linger, so the world tick keeps the flag (a bare flag bit is cleared at once)
                Corpse corpse = player.Combat.Corpse!;
                scene.Body = new Vector3(corpse.X, corpse.Y, corpse.Z);
                if (battleground)
                {
                    recoveryRun.AreaSpiritGuides = _ => true;
                    recoveryRun.ContestedObjectiveNear = (_, _) => contested;
                }
                return true;
            });
            if (delayPassed) clock.Seconds += 31;
            // A few ticks so the release settles (the ghost stays where it died).
            await host.World.AdvanceClockAsync(500);
            return scene;
        }

        public Task OnWorldAsync(Action action) => Host.OnWorldAsync(action);

        public Task<T> OnWorldAsync<T>(Func<T> action) => Host.OnWorldAsync(action);

        public Task<List<Player>> AddEnemiesAsync(int count) => AddAsync(count, PlayerbotPvpTestKit.Orc);

        public async Task AddAlliesAsync(int count) => await AddAsync(count, PlayerbotPvpTestKit.Human);

        public async Task<Player> AddOneAsync(byte race, Vector3 offset) => (await AddAsync(1, race, offset))[0];

        private async Task<List<Player>> AddAsync(int count, byte race, Vector3? offset = null)
        {
            var added = new List<Player>();
            for (int i = 0; i < count; i++)
            {
                int n = ++_next;
                string name = "Camp" + (race == PlayerbotPvpTestKit.Orc ? "foe" : "pal") + (char)('a' + n);
                WorldSession session = await PlayerbotPvpTestKit.EnterAsync(Host, "CAMPOTHER" + n, name, race);
                Others.Add(session);
                Vector3 at = Body + (offset ?? new Vector3(4f + 2f * n, 0f, 0f));
                await Host.OnWorldAsync(() =>
                {
                    Player other = session.Player!;
                    if (!ReferenceEquals(other.Map, Map)) throw new InvalidOperationException("the players start on different maps");
                    other.Relocate(at.X, at.Y, at.Z, 0, Host.World.NowMs);
                    added.Add(other);
                    return true;
                });
            }
            return added;
        }

        /// <summary>Tick the world until the ghost has every other player in its visible objects.</summary>
        public async Task SeeAllAsync()
        {
            for (int i = 0; i < 100; i++)
            {
                bool all = await Host.OnWorldAsync(() => Others.All(o => Bot.VisibleObjects.Contains(o.Player!.Guid)));
                if (all) return;
                await Host.World.AdvanceClockAsync(50);
            }
            throw new TimeoutException("the ghost never saw the other players");
        }

        /// <summary>One recovery think (a fresh action budget); returns what Update returned.</summary>
        public async Task<bool> ThinkAsync(bool advance = true, uint elapsedMs = 100)
        {
            if (advance) await Host.World.AdvanceClockAsync(elapsedMs);
            return await Host.OnWorldAsync(() =>
            {
                _botSession.ManagedBudget = new ManagedActionBudget(4);
                return Recovery.Update(Bot, elapsedMs);
            });
        }

        public async ValueTask DisposeAsync()
        {
            foreach (WorldSession other in Others) { other.Kick(); await other.ManagedClosed; }
            _botSession.Kick();
            await _botSession.ManagedClosed;
            await Host.DisposeAsync();
        }
    }

    private sealed class ManualClockFeature : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    private sealed class TestDeathClock(long seconds) : DeathClock
    {
        public long Seconds { get; set; } = seconds;
        public override long UnixSeconds => Seconds;
    }
}
