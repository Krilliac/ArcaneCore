using System.Buffers;
using System.Numerics;
using System.Text.Json;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Net;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Policy;
using ArcaneCore.World.Tests.Playerbots.Opportunities;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Opportunities.OpportunityWorld;

namespace ArcaneCore.World.Tests.Playerbots.Policy;

/// <summary>
/// The opportunity goal in the goal policy (docs/design/playerbot-policy-phases.md, phase 2): its place in the goal vocabulary, the weights
/// file that predates it being refused, the open-goal probe and the promotion in a real brain, and the <c>"k":"opportunity"</c> records.
/// </summary>
public sealed class PlayerbotOpportunityPolicyTests
{
    private static readonly ChestSpawn Free = new(FreeChest, 1, 3f, 0f);

    // --- the goal vocabulary --------------------------------------------------------------------------------------------

    [Fact]
    public void TheOpportunityGoal_SitsBetweenTownAndTrain_AndEveryGoalFitsTheMask()
    {
        Assert.Equal(PlayerbotPolicyGoal.Town + 1, PlayerbotPolicyGoal.Opportunity);
        Assert.Equal(PlayerbotPolicyGoal.Opportunity + 1, PlayerbotPolicyGoal.Train);
        Assert.Equal(PlayerbotPolicySchema.GoalCount, Enum.GetValues<PlayerbotPolicyGoal>().Length);
        Assert.Equal(PlayerbotPolicySchema.GoalCount, PlayerbotPolicySchema.GoalNames.Count);
        Assert.Contains("Opportunity", PlayerbotPolicySchema.GoalNames);

        var all = default(PlayerbotPolicyGoalMask);
        foreach (PlayerbotPolicyGoal goal in Enum.GetValues<PlayerbotPolicyGoal>()) all = all.With(goal);
        Assert.Equal(PlayerbotPolicySchema.GoalCount, all.Count);
        Assert.True(all.Contains(PlayerbotPolicyGoal.Explore));

        // The ladder takes the opportunity before the trainer trip, the quest travel and the grind, and after a town errand.
        PlayerbotPolicyGoalMask behind = default(PlayerbotPolicyGoalMask).With(PlayerbotPolicyGoal.Train).With(PlayerbotPolicyGoal.Grind)
            .With(PlayerbotPolicyGoal.Opportunity);
        Assert.Equal(PlayerbotPolicyGoal.Opportunity, behind.LadderPick);
        Assert.Equal(PlayerbotPolicyGoal.Town, behind.With(PlayerbotPolicyGoal.Town).LadderPick);
    }

    [Fact]
    public void TheLootGoal_IsLabelledOpportunity_OnlyWhenTheOpportunityControllerDroveTheThink()
    {
        // The brain's Loot goal covers a chest and a corpse: by itself it has no policy goal, as before.
        Assert.Null(PlayerbotPolicySchema.GoalOf(PlayerbotGoalKind.Loot));
        Assert.Null(PlayerbotPolicySchema.GoalOf(PlayerbotGoalKind.Loot, opportunityDriven: false));
        Assert.Equal(PlayerbotPolicyGoal.Opportunity, PlayerbotPolicySchema.GoalOf(PlayerbotGoalKind.Loot, opportunityDriven: true));
        // The others keep their rungs.
        Assert.Equal(PlayerbotPolicyGoal.Grind, PlayerbotPolicySchema.GoalOf(PlayerbotGoalKind.Grind, opportunityDriven: false));
        Assert.Equal(PlayerbotPolicyGoal.Town, PlayerbotPolicySchema.GoalOf(PlayerbotGoalKind.Vendor));
        Assert.Null(PlayerbotPolicySchema.GoalOf(PlayerbotGoalKind.Combat));
    }

    [Fact]
    public void TheRecordVersion_IsThree()
    {
        Assert.Equal(3, PlayerbotPolicyRecord.SchemaVersion);
    }

    [Fact]
    public void AModelTrainedBeforeTheOpportunityGoal_IsRefused_ByItsGoalNames()
    {
        string directory = Directory.CreateTempSubdirectory("policy-model-").FullName;
        try
        {
            string[] oldGoals = [.. PlayerbotPolicySchema.GoalNames.Where(name => name != "Opportunity")];
            Assert.Equal(PlayerbotPolicySchema.GoalCount - 1, oldGoals.Length);
            File.WriteAllText(Path.Combine(directory, "old.json"), LinearModelJson(oldGoals));
            File.WriteAllText(Path.Combine(directory, "new.json"), LinearModelJson(PlayerbotPolicySchema.GoalNames));

            var old = new PlayerbotPolicyEngine(new PlayerbotPolicyOptions { Enabled = true, ModelPath = "old.json" }, contentRoot: directory);
            Assert.False(old.ModelLoaded);
            Assert.Same(RuleBasedPolicy.Instance, old.Policy);
            Assert.NotNull(old.ModelError);
            Assert.Contains("goals", old.ModelError, StringComparison.OrdinalIgnoreCase);

            // The same file with this layout's names loads: the refusal is the goal list, not something else about the file.
            var current = new PlayerbotPolicyEngine(new PlayerbotPolicyOptions { Enabled = true, ModelPath = "new.json" }, contentRoot: directory);
            Assert.True(current.ModelLoaded, current.ModelError);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // --- the probe and the promotion ------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSnapshot_ListsTheOpportunityOpen_OnlyWhileAFindIsInReach()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        PlayerbotPolicyGoalMask open = await rig.World(() =>
        {
            rig.Bot.Session.ManagedBudget = new ManagedActionBudget(5); // as a think has
            return rig.Bot.Brain.PolicySnapshot(rig.Player, new float[PlayerbotPolicySchema.FeatureCount]);
        });
        Assert.True(open.Contains(PlayerbotPolicyGoal.Opportunity), $"open bits {open.Bits}");
        Assert.True(open.Contains(PlayerbotPolicyGoal.Explore));

        // A find the controller set aside is no longer open (the probe reads the ledger the controller writes).
        await rig.World(() => { rig.Bot.Brain.Opportunities.Ledger.Defer(ChestGuid(Free), rig.Host.World.NowMs); return true; });
        Assert.False(await rig.World(() => rig.Bot.Brain.Opportunities.HasCandidate(rig.Player)));
        // With the goal off, and with no find at all, it is not open either.
        await rig.World(() => { rig.Bot.Brain.Opportunities.Ledger.Forget(ChestGuid(Free)); rig.Options.Opportunities.Enabled = false; return true; });
        Assert.False((await rig.World(() => rig.Bot.Brain.PolicySnapshot(rig.Player, new float[PlayerbotPolicySchema.FeatureCount]))).Contains(PlayerbotPolicyGoal.Opportunity));
    }

    [Fact]
    public async Task APolicyThatPrefersTheOpportunity_PromotesItAheadOfAnEarlierOpenGoal()
    {
        await using Rig rig = await Rig.StartAsync([Free], tweak: options => options.Opportunities.Enabled = false);
        var engine = new PlayerbotPolicyEngine(new PlayerbotPolicyOptions { Enabled = true },
            policy: new PlayerbotPolicyEngineTests.StubPolicy(PlayerbotPolicyGoal.Opportunity));
        await rig.World(() => { rig.Bot.Brain.PolicyEngine = engine; return true; });

        // One think so the bot has its policy agent. The goal is off, so neither the snapshot nor the ladder touches the chest.
        await rig.ThinkBrainAsync();
        PlayerbotPolicyAgent agent = (await rig.World(() => rig.Bot.Brain.PolicyAgent))!;
        Assert.Null(rig.Bot.Brain.Opportunities.ActiveKey);

        // A snapshot in which a town errand comes before the opportunity: the ladder's choice is Town, the model's is Opportunity.
        PlayerbotPolicyGoalMask open = default(PlayerbotPolicyGoalMask).With(PlayerbotPolicyGoal.Town).With(PlayerbotPolicyGoal.Opportunity)
            .With(PlayerbotPolicyGoal.Explore);
        await rig.World(() =>
        {
            rig.Options.Opportunities.Enabled = true;
            agent.Request(rig.Player.MapId, new float[PlayerbotPolicySchema.FeatureCount], open, rig.Host.World.NowMs);
            engine.Dispatch();
            return true;
        });
        await engine.WhenIdleAsync();

        await rig.ThinkBrainAsync();
        PlayerbotPolicyVerdict? verdict = await rig.World(() => agent.LastVerdict);
        Assert.Equal(PlayerbotPolicyGoal.Town, verdict?.Window?.LadderPick);
        Assert.True(verdict is { Source: PlayerbotPolicySource.Applied, Promote: PlayerbotPolicyGoal.Opportunity },
            $"source {verdict?.Source}, promote {verdict?.Promote}, refused {engine.Stats.Refused}");
        Assert.Equal(0, engine.Stats.Refused);
        Assert.Equal(ChestGuid(Free), rig.Bot.Brain.Opportunities.ActiveKey);
        Assert.Equal(PlayerbotGoalKind.Loot, await rig.World(() => rig.Bot.Brain.Goal));
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task APromotedOpportunityWithAGuard_HandsTheThinkToTheCombatPath_SoTheGuardIsTheTarget()
    {
        await using Rig rig = await Rig.StartAsync([Free], tweak: options => options.Opportunities.Enabled = false);
        await rig.World(() => { InstallOpenGround(rig.Host); return true; });
        (PlayerbotPolicyEngine engine, PlayerbotPolicyAgent agent) = await rig.StartPolicyAsync();
        // The guard appears after the bot's first think, so the bot stands idle with no target when the promotion comes.
        Creature guard = await rig.AddGuardAsync(96901, 5f, 2f);
        Assert.Null(await rig.World(() => rig.Bot.Brain.InspectionTarget));
        PlayerbotPolicyGoalMask open = default(PlayerbotPolicyGoalMask).With(PlayerbotPolicyGoal.Town).With(PlayerbotPolicyGoal.Opportunity)
            .With(PlayerbotPolicyGoal.Explore);
        await rig.InjectAsync(engine, agent, open);
        var sent = new List<WorldOpcode>();
        await rig.World(() => { rig.Bot.Session.ManagedDispatchObserver = sent.Add; return true; });

        await rig.ThinkBrainAsync();

        PlayerbotPolicyVerdict? verdict = await rig.World(() => agent.LastVerdict);
        Assert.True(verdict is { Source: PlayerbotPolicySource.Applied, Promote: PlayerbotPolicyGoal.Opportunity }, $"source {verdict?.Source}");
        Assert.Equal(ChestGuid(Free), rig.Bot.Brain.Opportunities.ActiveKey);
        // One think: the promoted goal chose the guard and the same think went on to the combat path, which made it the target.
        Assert.Same(guard, await rig.World(() => rig.Bot.Brain.InspectionTarget));
        // And the combat path ran in that same think: it began to close on the guard (a promotion that returned at once would send nothing).
        Assert.NotEmpty(sent);
        Assert.True(await rig.World(() => rig.Bot.Brain.OpportunityDriven));
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task UnderResurrectionSickness_APromotedOpportunityIsRefusedOnce_AndTheChestIsLeftAlone()
    {
        await using Rig rig = await Rig.StartAsync([Free], tweak: options => options.Opportunities.Enabled = false);
        (PlayerbotPolicyEngine engine, PlayerbotPolicyAgent agent) = await rig.StartPolicyAsync();
        await rig.World(() => { PlayerbotSicknessTestKit.Apply(rig.Host.WorldServices, rig.Player); return true; });
        PlayerbotPolicyGoalMask open = default(PlayerbotPolicyGoalMask).With(PlayerbotPolicyGoal.Town).With(PlayerbotPolicyGoal.Opportunity)
            .With(PlayerbotPolicyGoal.Explore);
        await rig.InjectAsync(engine, agent, open);

        await rig.ThinkBrainAsync();

        Assert.Equal(1, engine.Stats.Refused);
        Assert.Equal(PlayerbotPolicySource.Refused, (await rig.World(() => agent.LastVerdict))?.Source);
        Assert.Null(rig.Bot.Brain.Opportunities.ActiveKey);
        Assert.False(await rig.World(() => rig.Bot.Brain.OpportunityDriven));
        await engine.DisposeAsync();
    }

    /// <summary>
    /// A promoted opportunity whose far chest still waits for its region route (a search outstanding, here a lost one marked asked) has no
    /// verdict yet: it is not a refusal. The policy's refusal count and the snapshot's window stay as they were, the scan is asked again on
    /// the next think instead of after the scan interval, and the chest is taken once the pair is answered.
    /// </summary>
    [Fact]
    public async Task APromotedOpportunityWhoseRouteIsPending_IsNotRefused_AndIsTakenOnceTheRouteIsSearched()
    {
        const float cell = 1f;
        const int width = 150;
        const int lastRow = 46;
        ChestSpawn chest = new(FreeChest, 9, 0f, lastRow * cell + 2f);
        using var fixture = new NavMeshFixture();
        (int detourX, int detourY) = ((int)MathF.Floor(StartY / 533.3333f), (int)MathF.Floor(StartX / 533.3333f));
        (int fileX, int fileY) = TerrainTile.TileOf(StartX, StartY)!.Value;
        fixture.WriteParams(0);
        fixture.WriteTile(0, fileX, fileY, new CellTile(detourX, detourY, StartX - (cell / 2f), StartY - (cell / 2f), width, lastRow + 1, cell)
        {
            Height = (_, _) => StartZ,
            Walkable = (i, j) => (j & 1) == 0 || i == ((((j - 1) / 2) & 1) == 0 ? width - 1 : 0),
            LinkedSides = [0],
        });
        fixture.WriteTile(0, fileX + 1, fileY, new CellTile(detourX + 1, detourY, StartX - (cell / 2f), StartY - (cell / 2f) + ((lastRow + 1) * cell), width, 10, cell)
        {
            Height = (_, _) => StartZ,
            LinkedSides = [4],
        });
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(0, fileX, fileY));
        Assert.True(nav.LoadTile(0, fileX + 1, fileY));
        Task<NavRegionGraph?>? building = nav.RegionGraphTask(0, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes);
        Assert.NotNull(building);
        NavRegionGraph graph = (await building)!;

        await using Rig rig = await Rig.StartAsync([chest], tweak: options =>
        {
            options.Opportunities.Enabled = false;
            options.Opportunities.ScanRadiusYards = 80f;
            options.Opportunities.PersonalityVariance = 0f;
            options.Opportunities.MinValue = 0f;
        });
        (PlayerbotPolicyEngine engine, PlayerbotPolicyAgent agent) = await rig.StartPolicyAsync();
        (int from, int to, Vector3 here, Vector3 goal) = await rig.World(() =>
        {
            WorldCollision.Of(rig.Host.World).Install(pathfinder: nav);
            GameObject box = (GameObject)rig.Player.Map!.FindObject(new ObjectGuid(ChestGuid(chest)))!;
            Vector3 position = PlayerbotMotion.CurrentPosition(rig.Player);
            Vector3 target = new(box.X, box.Y, box.Z);
            var locate = new PathOptions { Mover = PathMover.Player, ExcludeFlags = NavTerrain.SteepSlopes, LoadTiles = true, NonBlockingTiles = true };
            return (nav.RegionAt(graph, position, locate), nav.RegionAt(graph, target, locate), position, target);
        });
        Assert.True(from >= 0 && to >= 0 && from != to, $"regions {from}, {to}");
        PlayerbotRegionRoutes.RouteCache cache = PlayerbotRegionRoutes.Of(graph);
        cache.MarkAsked(from, to, long.MaxValue / 2); // a search that never comes back, never old enough to be searched on the think

        PlayerbotPolicyGoalMask open = default(PlayerbotPolicyGoalMask).With(PlayerbotPolicyGoal.Town).With(PlayerbotPolicyGoal.Opportunity)
            .With(PlayerbotPolicyGoal.Explore);
        await rig.InjectAsync(engine, agent, open);

        for (int think = 0; think < 3; think++) await rig.ThinkBrainAsync();

        PlayerbotPolicyVerdict? verdict = await rig.World(() => agent.LastVerdict);
        Assert.Equal(0, engine.Stats.Refused);
        Assert.True(verdict is { Source: PlayerbotPolicySource.Applied, Promote: PlayerbotPolicyGoal.Opportunity }, $"source {verdict?.Source}, promote {verdict?.Promote}");
        Assert.Null(rig.Bot.Brain.Opportunities.ActiveKey);
        Assert.Equal(0, cache.Searches);

        // Answered: the next think takes the chest (a refused window would have left it to the ladder).
        cache.Search(from, here, to, goal);
        Assert.Equal(1, cache.Searches);
        Assert.True(await rig.RunBrainAsync(() => rig.Bot.Brain.Opportunities.ActiveKey == ChestGuid(chest)), "the chest was never taken up after the route was searched");
        Assert.Equal(0, engine.Stats.Refused);
        await engine.DisposeAsync();
    }

    /// <summary>
    /// The outcome record of a rare the bot went for and killed. <paramref name="beforeKill"/> sets the scene on the world thread (a second
    /// player in the map, passed in, may tap it first); the bot then lands the killing blow.
    /// </summary>
    private static async Task<JsonElement> RareOutcomeAsync(Action<Rig, Bot, Creature> beforeKill, bool credit = true, uint blow = 100_000)
    {
        await using Rig rig = await Rig.StartAsync([]);
        Bot other = await rig.EnterOtherAsync();
        var records = new List<IPlayerbotTelemetryRecord>();
        rig.Bot.Controller.Sink = new PlayerbotCombatTelemetry(records.Add, () => 5L);
        Creature rare = await rig.World(() => AddCreature(rig.Host, rig.Player, RareEntry, 5f, 2f, 1, 4, 20));
        await rig.Host.WaitForWorldAsync(() => rig.Player.VisibleObjects.Contains(rare.Guid), "rare visible");
        Assert.Same(rare, (await rig.World(() => rig.Bot.Step())).Pull);
        await rig.World(() =>
        {
            beforeKill(rig, other, rare);
            rig.Player.Map!.Combat.DealDamage(rig.Player, rare, blow);
            Assert.False(rare.IsAlive);
            Assert.Equal(credit, rare.KillCreditAllowed);
            rig.Bot.Step();
            return true;
        });
        return Parse(Assert.IsType<PlayerbotOpportunityRecord>(Assert.Single(records))).GetProperty("o");
    }

    [Fact]
    public async Task ARareAnotherPlayerTappedAndKilled_IsLost_NotASuccess()
    {
        JsonElement outcome = await RareOutcomeAsync((rig, other, rare) =>
        {
            rig.Player.Map!.Combat.DealDamage(other.Player, rare, 1); // the first hit taps it for the other player
            Assert.Equal(other.Player.Guid, rare.LootTapper);
        });

        Assert.Equal(1, outcome.GetProperty("lost").GetInt32());
        Assert.Equal(0, outcome.GetProperty("loot").GetInt32());
        Assert.Equal(0, outcome.GetProperty("died").GetInt32());
    }

    [Fact]
    public async Task ARareAGroupMateTapped_IsNotLost_EvenWithTheMateOutOfTheMap()
    {
        JsonElement outcome = await RareOutcomeAsync((rig, other, rare) =>
        {
            GroupManager groups = rig.Host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups;
            groups.Invite(rig.Player, other.Player.Name);
            groups.Accept(other.Player);
            Assert.True(groups.AreInSameGroup(rig.Player.Guid, other.Player.Guid));
            rig.Player.Map!.Combat.DealDamage(other.Player, rare, 1); // the mate taps it, for the group
            // The mate's guid no longer resolves to a player on the map (offline): the group's tap is still the bot's.
            rare.LootTapPlayerGuid = new ObjectGuid(0x7777);
        });

        Assert.Equal(0, outcome.GetProperty("lost").GetInt32());
        Assert.Equal(JsonValueKind.Null, outcome.GetProperty("loot").ValueKind); // a success whose loot the brain takes
    }

    [Fact]
    public async Task ARareTheBotsOwnPetTapped_IsNotLost_EvenWhenThePetIsGone()
    {
        JsonElement outcome = await RareOutcomeAsync((rig, other, rare) =>
        {
            rig.Player.Map!.Combat.DealDamage(other.Player, rare, 1); // sets the tap and the viewer filter
            var pet = new ObjectGuid(0x6666);
            rig.Player.SetPetGuid(pet);
            rare.LootTapPlayerGuid = pet; // the pet struck first, then despawned: no unit has this guid
        });

        Assert.Equal(0, outcome.GetProperty("lost").GetInt32());
        Assert.Equal(JsonValueKind.Null, outcome.GetProperty("loot").ValueKind);
    }

    [Fact]
    public async Task ARareKilledWithNoCredit_IsNotASuccess_AndNotLost()
    {
        JsonElement outcome = await RareOutcomeAsync((rig, other, rare) =>
        {
            // A creature did most of the damage: the kill earns nobody the corpse and SettleKillCredit clears the tap.
            Creature attacker = AddCreature(rig.Host, rig.Player, 96950, 4f, 2f, 1, 0, 100);
            rig.Player.Map!.Combat.DealDamage(attacker, rare, 15);
            Assert.True(rare.IsAlive);
        }, credit: false, blow: 5);

        Assert.Equal(0, outcome.GetProperty("lost").GetInt32());
        Assert.Equal(0, outcome.GetProperty("loot").GetInt32()); // 0, not null: not a success
        Assert.Equal(0, outcome.GetProperty("died").GetInt32());
    }

    [Fact]
    public async Task OpportunityDriven_IsClearedOnTheNextThink_WhenTheControllerDoesNothing()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        for (int i = 0; i < 20 && !await rig.World(() => rig.Bot.Brain.OpportunityDriven); i++)
        {
            await rig.ThinkBrainAsync();
            await Task.Delay(25);
        }

        Assert.True(await rig.World(() => rig.Bot.Brain.OpportunityDriven), "the controller never drove a think");
        await rig.World(() => { rig.Options.Opportunities.Enabled = false; return true; });
        await rig.ThinkBrainAsync();
        Assert.False(await rig.World(() => rig.Bot.Brain.OpportunityDriven));
    }

    [Fact]
    public async Task AGoalRecord_OfAControllerDrivenThink_IsLabelledOpportunity()
    {
        string directory = Directory.CreateTempSubdirectory("policy-telemetry-").FullName;
        try
        {
            var options = new PlayerbotPolicyOptions { Enabled = true, Telemetry = { Enabled = true, OutcomeMinutes = 1 } };
            var engine = new PlayerbotPolicyEngine(options);
            engine.Telemetry = new PlayerbotPolicyTelemetry(options.Telemetry, directory);
            await using (Rig rig = await Rig.StartAsync([Free]))
            {
                await rig.World(() => { rig.Bot.Brain.PolicyEngine = engine; return true; });
                uint t = 1_000;
                int driven = 0;
                for (int i = 0; i < 8; i++)
                {
                    await rig.ThinkBrainAsync();
                    await rig.World(() =>
                    {
                        if (rig.Bot.Brain.OpportunityDriven) driven++;
                        engine.Telemetry!.AfterThink(rig.Bot.Brain, rig.Player, t);
                        engine.Dispatch();
                        return true;
                    });
                    await engine.WhenIdleAsync();
                    t += 100;
                }

                Assert.True(driven > 0, "the controller never drove a think");
                // A minute of world time later the decision window is over and it is written.
                await rig.World(() => { engine.Telemetry!.AfterThink(rig.Bot.Brain, rig.Player, t + 61_000); return true; });
                await engine.Telemetry!.FlushAsync();
                Assert.True(engine.Telemetry.Counts.Written > 0, $"queued {engine.Telemetry.Counts.Queued}");
            }

            await engine.DisposeAsync();
            JsonElement root = JsonDocument.Parse(Directory.GetFiles(directory).SelectMany(File.ReadAllLines).First()).RootElement;
            Assert.Equal("goal", root.GetProperty("k").GetString());
            Assert.Equal("Loot", root.GetProperty("kind").GetString()); // the brain's goal for a chest
            Assert.Equal("Opportunity", root.GetProperty("goal").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ARareTheBotKilled_IsASuccess_WithNoLootSeen_AndNotLost()
    {
        await using Rig rig = await Rig.StartAsync([]);
        var records = new List<IPlayerbotTelemetryRecord>();
        rig.Bot.Controller.Sink = new PlayerbotCombatTelemetry(records.Add, () => 5L);
        Creature rare = await rig.World(() => AddCreature(rig.Host, rig.Player, RareEntry, 5f, 2f, 1, 4, 20));
        await rig.Host.WaitForWorldAsync(() => rig.Player.VisibleObjects.Contains(rare.Guid), "rare visible");
        Assert.Same(rare, (await rig.World(() => rig.Bot.Step())).Pull);
        await rig.World(() =>
        {
            rig.Player.Map!.Combat.DealDamage(rig.Player, rare, 100_000);
            rig.Bot.Step();
            return true;
        });

        JsonElement outcome = Parse(Assert.IsType<PlayerbotOpportunityRecord>(Assert.Single(records))).GetProperty("o");
        Assert.Equal(0, outcome.GetProperty("lost").GetInt32());
        Assert.Equal(JsonValueKind.Null, outcome.GetProperty("loot").ValueKind); // the brain loots the corpse
    }

    [Fact]
    public async Task AFindWhoseFirstGuardTheBrainWillNotTake_IsWrittenWithItsOwnReason()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        var records = new List<IPlayerbotTelemetryRecord>();
        rig.Bot.Controller.Sink = new PlayerbotCombatTelemetry(records.Add, () => 5L);
        rig.Bot.Controller.Untakeable = (_, _) => true;
        await rig.AddGuardAsync(96902, 5f, 2f);

        Assert.True(await rig.RunClockAsync(() => records.Count > 0, steps: 20));
        JsonElement json = Parse(Assert.IsType<PlayerbotOpportunityRecord>(records[0]));
        Assert.Equal(0, json.GetProperty("engaged").GetInt32());
        Assert.Equal("untakeable", json.GetProperty("reason").GetString());
    }

    // --- the records ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ALootedChest_IsWrittenOnceItsEpisodeEnds_WithTheFeaturesTheEstimatesAndTheLoot()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        var records = new List<IPlayerbotTelemetryRecord>();
        var telemetry = new PlayerbotCombatTelemetry(records.Add, () => 1_700_000_000_000L);
        rig.Bot.Controller.Sink = telemetry;

        Assert.True(await rig.RunAsync(() => rig.Bot.Sink.Any(a => a.Opcode == WorldOpcode.CmsgLootRelease)), "the controller never reached the loot");
        await rig.PlayLootAsync();
        Assert.Null(rig.Bot.Controller.ActiveKey);

        PlayerbotOpportunityRecord record = Assert.IsType<PlayerbotOpportunityRecord>(Assert.Single(records));
        OpportunityVerdict verdict = rig.Bot.Controller.LastVerdict!.Value;
        JsonElement json = Parse(record);
        Assert.Equal(["v", "k", "t", "bot", "map", "zone", "f", "kind", "entry", "reward", "risk", "guards", "reason", "engaged", "o"],
            json.EnumerateObject().Select(p => p.Name));
        Assert.Equal("opportunity", json.GetProperty("k").GetString());
        Assert.Equal("chest", json.GetProperty("kind").GetString());
        Assert.Equal(FreeChest, json.GetProperty("entry").GetUInt32());
        Assert.Equal(1_700_000_000_000L, json.GetProperty("t").GetInt64());
        Assert.Equal(1, json.GetProperty("engaged").GetInt32());
        Assert.Equal(0, json.GetProperty("guards").GetInt32());
        Assert.Equal(verdict.Net, json.GetProperty("reward").GetSingle(), 3);
        Assert.Equal(0f, json.GetProperty("risk").GetSingle(), 3);

        float[] features = [.. json.GetProperty("f").EnumerateArray().Select(e => e.GetSingle())];
        Assert.Equal(PlayerbotCombatTelemetry.OpportunityFeatureNames.Length, features.Length);
        Assert.Equal(rig.Player.Level / 60f, features[0], 3);
        Assert.Equal(1f, features[1], 3); // full health
        Assert.Equal(0f, features[4], 3); // an ordinary chest
        Assert.InRange(features[3], 0.01f, 0.2f); // the chest is 3 yards away

        // The loot is what the window held that the bot stored: the green item, and the blue one half the time.
        uint blue = await rig.World(() => rig.Player.Inventory.GetItemCount(BlueItem));
        JsonElement outcome = json.GetProperty("o");
        Assert.Equal(["loot", "died", "seconds", "lost"], outcome.EnumerateObject().Select(p => p.Name));
        Assert.Equal(60 + (200 * blue), outcome.GetProperty("loot").GetUInt64());
        Assert.Equal(0, outcome.GetProperty("died").GetInt32());
        Assert.True(outcome.GetProperty("seconds").GetDouble() >= 0);
        Assert.Equal(1, telemetry.Opportunities);
    }

    [Fact]
    public async Task ARefusedFind_IsWrittenAsADecisionNotTaken_OncePerMinute_WithNoOutcome()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        var records = new List<IPlayerbotTelemetryRecord>();
        rig.Bot.Controller.Sink = new PlayerbotCombatTelemetry(records.Add, () => 5L);
        await rig.AddGuardAsync(96801, 5f, 2f, level: 6);

        Assert.True(await rig.RunClockAsync(() => rig.Bot.Controller.LastVerdict is not null));
        PlayerbotOpportunityRecord record = Assert.IsType<PlayerbotOpportunityRecord>(Assert.Single(records));
        JsonElement json = Parse(record);
        Assert.Equal(0, json.GetProperty("engaged").GetInt32());
        Assert.Equal(1, json.GetProperty("guards").GetInt32());
        Assert.Equal("over-level", json.GetProperty("reason").GetString());
        Assert.All(json.GetProperty("o").EnumerateObject(), p => Assert.Equal(JsonValueKind.Null, p.Value.ValueKind));

        // The controller weighs it again after its own recheck (20 s); the telemetry writes it again only after a minute.
        rig.Clock += PlayerbotOpportunities.RefusedRecheckMs + 1_000;
        Assert.True(await rig.RunClockAsync(() => rig.Bot.Controller.Ledger.IsDeferred(ChestGuid(Free), rig.Clock), steps: 20));
        Assert.Single(records);
        rig.Clock += PlayerbotCombatTelemetry.SkipRepeatMs;
        Assert.True(await rig.RunClockAsync(() => records.Count == 2, steps: 60), $"records {records.Count}");
    }

    [Fact]
    public async Task ADeathDuringTheEpisode_IsWrittenWithDiedAndNoLoot()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        var records = new List<IPlayerbotTelemetryRecord>();
        rig.Bot.Controller.Sink = new PlayerbotCombatTelemetry(records.Add, () => 5L);
        Creature guard = await rig.AddGuardAsync(96802, 5f, 2f);
        Assert.Same(guard, (await rig.World(() => rig.Bot.Step())).Pull);
        Assert.Empty(records); // the episode is under way: its record waits for the outcome

        await rig.World(() =>
        {
            rig.Player.Map!.Combat.DealDamage(guard, rig.Player, 100_000);
            Assert.False(rig.Player.IsAlive);
            rig.Bot.Step();
            return true;
        });

        JsonElement json = Parse(Assert.IsType<PlayerbotOpportunityRecord>(Assert.Single(records)));
        Assert.Equal(1, json.GetProperty("engaged").GetInt32());
        Assert.Equal(1, json.GetProperty("guards").GetInt32());
        JsonElement outcome = json.GetProperty("o");
        Assert.Equal(1, outcome.GetProperty("died").GetInt32());
        Assert.Equal(0, outcome.GetProperty("loot").GetInt32());
    }

    [Fact]
    public async Task ASinkThatThrows_NeverChangesTheDecision()
    {
        await using Rig rig = await Rig.StartAsync([Free]);
        rig.Bot.Controller.Sink = new ThrowingSink();
        Assert.True(await rig.RunAsync(() => rig.Bot.Sink.Any(a => a.Opcode == WorldOpcode.CmsgLootRelease)), "a faulting sink stopped the chest being taken");
    }

    private static JsonElement Parse(IPlayerbotTelemetryRecord record)
    {
        var buffer = new ArrayBufferWriter<byte>();
        record.WriteTo(buffer);
        string line = System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
        Assert.EndsWith("\n", line, StringComparison.Ordinal);
        Assert.Single(line.TrimEnd('\n').Split('\n'));
        return JsonDocument.Parse(line).RootElement.Clone();
    }

    /// <summary>A model file with the given goal names and one output per name (the weights are zero: only the layout matters).</summary>
    private static string LinearModelJson(IReadOnlyList<string> goals)
    {
        int f = PlayerbotPolicySchema.FeatureCount, g = goals.Count;
        return JsonSerializer.Serialize(new
        {
            format = PlayerbotModelPolicy.Format,
            version = PlayerbotModelPolicy.Version,
            features = PlayerbotPolicySchema.FeatureNames,
            goals,
            layers = new object[] { new { inputs = f, outputs = g, activation = "linear", weights = new float[g * f], bias = new float[g] } },
        });
    }

    private sealed class ThrowingSink : IPlayerbotOpportunitySink
    {
        public void Decided(Player player, in OpportunityDecisionInfo decision, bool engaged, uint nowMs) => throw new InvalidOperationException("sink fault");

        public void Ended(ulong botGuid, ulong key, in OpportunityOutcome outcome, uint nowMs) => throw new InvalidOperationException("sink fault");
    }

    private sealed class Rig : IAsyncDisposable
    {
        public WorldTestHost Host = null!;
        public Bot Bot = null!;
        public uint Clock = 1_000_000;
        public PlayerbotClaims Claims = new(64);

        public Player Player => Bot.Player;

        public PlayerbotOptions Options => Bot.Options;

        public static async Task<Rig> StartAsync(IEnumerable<ChestSpawn> chests, Action<PlayerbotOptions>? tweak = null)
        {
            ChestSpawn[] spawns = [.. chests];
            var rig = new Rig { Host = OpportunityWorld.Start(spawns, out _) };
            var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 };
            tweak?.Invoke(options);
            rig.Bot = await EnterAsync(rig.Host, "OPPPOL", "Opppol", 1, options, rig.Claims, () => rig.Clock);
            foreach (ChestSpawn spawn in spawns)
                await rig.Host.WaitForWorldAsync(() => rig.Player.VisibleObjects.Contains(new ObjectGuid(ChestGuid(spawn))), "chest " + spawn.Entry + " visible");
            return rig;
        }

        public Task<T> World<T>(Func<T> action) => Host.OnWorldAsync(action);

        /// <summary>Gives the bot its policy agent: an engine whose model prefers the opportunity, and one think with the goal off.</summary>
        public async Task<(PlayerbotPolicyEngine Engine, PlayerbotPolicyAgent Agent)> StartPolicyAsync()
        {
            var engine = new PlayerbotPolicyEngine(new PlayerbotPolicyOptions { Enabled = true },
                policy: new PlayerbotPolicyEngineTests.StubPolicy(PlayerbotPolicyGoal.Opportunity));
            await World(() => { Bot.Brain.PolicyEngine = engine; return true; });
            await ThinkBrainAsync();
            return (engine, (await World(() => Bot.Brain.PolicyAgent))!);
        }

        /// <summary>Turns the goal on and puts a snapshot with the given open goals in front of the bot, scored and ready for its next think.</summary>
        public async Task InjectAsync(PlayerbotPolicyEngine engine, PlayerbotPolicyAgent agent, PlayerbotPolicyGoalMask open)
        {
            await World(() =>
            {
                Options.Opportunities.Enabled = true;
                agent.Request(Player.MapId, new float[PlayerbotPolicySchema.FeatureCount], open, Host.World.NowMs);
                engine.Dispatch();
                return true;
            });
            await engine.WhenIdleAsync();
        }

        /// <summary>One think of the whole brain (its ladder, with the policy hook) with a fresh action budget.</summary>
        public Task ThinkBrainAsync() => World(() =>
        {
            Bot.Session.ManagedBudget = new ManagedActionBudget(5);
            Bot.Brain.Update(500);
            return true;
        });

        /// <summary>Thinks the brain until <paramref name="done"/> or the step budget ends.</summary>
        public async Task<bool> RunBrainAsync(Func<bool> done, int steps = 40)
        {
            for (int i = 0; i < steps; i++)
            {
                await ThinkBrainAsync();
                if (await World(done)) return true;
                await Task.Delay(5);
            }

            return false;
        }

        /// <summary>Steps the controller (real time passing for the server's replies) until <paramref name="done"/> or the step budget ends.</summary>
        public async Task<bool> RunAsync(Func<bool> done, int steps = 60)
        {
            for (int i = 0; i < steps; i++)
            {
                if (await World(() => { Bot.Step(); return done(); })) return true;
                await Task.Delay(25);
            }

            return false;
        }

        /// <summary>Steps the controller while the fake clock advances until <paramref name="done"/>.</summary>
        public async Task<bool> RunClockAsync(Func<bool> done, uint stepMs = 500, int steps = 400)
        {
            for (int i = 0; i < steps; i++)
            {
                if (await World(() => { Bot.Step(); return done(); })) return true;
                Clock += stepMs;
                await Task.Delay(2);
            }

            return false;
        }

        /// <summary>The loot actions the controller queued, played to the server the way the brain's loot queue does.</summary>
        public Task PlayLootAsync() => World(() =>
        {
            foreach ((WorldOpcode opcode, byte[] payload) in Bot.Sink)
            {
                Bot.Session.ManagedBudget = new ManagedActionBudget(5);
                Assert.True(Bot.Session.TryManagedAction(opcode, payload));
            }

            Bot.Sink.Clear();
            return true;
        });

        public async Task<Creature> AddGuardAsync(uint low, float dx, float dy, byte level = 1)
        {
            Creature guard = await World(() => AddCreature(Host, Player, low, dx, dy, level));
            await Host.WaitForWorldAsync(() => Player.VisibleObjects.Contains(guard.Guid), "guard " + low + " visible");
            return guard;
        }

        private readonly List<Bot> _others = [];

        /// <summary>A second player (same map and place) that can tap a creature first or join the bot's group.</summary>
        public async Task<Bot> EnterOtherAsync()
        {
            Bot other = await EnterAsync(Host, "OPPOTH", "Oppother", 1, Options, Claims, () => Clock);
            _others.Add(other);
            return other;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (Bot other in _others)
            {
                other.Brain.Stop();
                other.Session.Kick();
                await other.Session.ManagedClosed;
            }

            Bot.Brain.Stop();
            Bot.Session.Kick();
            await Bot.Session.ManagedClosed;
            await Host.DisposeAsync();
        }
    }
}
