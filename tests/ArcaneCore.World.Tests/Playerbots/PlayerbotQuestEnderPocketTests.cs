using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Npc;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed partial class PlayerbotQuestReturnTests
{
    /// <summary>Bind point placement for the stranded-quest cases.</summary>
    private enum BindAt { None, BesideEnder, FarFromEnder }

    /// <summary>
    /// Runs <see cref="PlayerbotWorldDestinations.AbandonAfterSetAsides"/> rounds of "every route refused for a strong hazard" for the
    /// quest and reports how many bind-point teleports the destinations asked for and whether the last round set the quest aside.
    /// </summary>
    private static async Task<(int Teleports, bool SetAside, QuestStatus Status)> StrandedRoundsAsync(BindAt bindAt, bool complete = true)
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, manualClock: true);
        await fixture.AcceptAndCompleteAsync(complete);
        await AddPocketAsync(fixture, 10);
        var options = new PlayerbotOptions { Enabled = true };
        var brain = new PlayerbotBrain(fixture.Session, options);
        brain.QuestGoals.Suspensions = brain.Suspensions;
        int teleports = 0;
        var destinations = new PlayerbotWorldDestinations(fixture.Session, options)
        {
            Suspensions = brain.Suspensions,
            SkipQuest = brain.QuestGoals.IsRefused,
            TeleportToBind = (bot, why) =>
            {
                bool sent = brain.TeleportToBindPointForQuest(bot, why);
                if (sent) teleports++;
                return sent;
            },
        };
        try
        {
            await fixture.Host.World.InvokeAsync(() =>
            {
                var player = fixture.Session.Player!;
                var ender = (Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
                if (bindAt == BindAt.None) player.Home = default;
                else
                {
                    float x = ender.X + (bindAt == BindAt.BesideEnder ? 40f : 3_000f);
                    player.Home = new HomeBind(player.MapId, 12, x, ender.Y, ender.Z);
                }
                return true;
            });
            bool setAside = false;
            for (int round = 1; round <= PlayerbotWorldDestinations.AbandonAfterSetAsides; round++)
            {
                setAside = false;
                for (int attempt = 0; attempt < 25; attempt++)
                {
                    bool settled = await fixture.Host.World.InvokeAsync(() =>
                    {
                        var player = fixture.Session.Player!;
                        brain.Risk.ScanHazards(player);
                        PlayerbotNavigation.Guard(player, brain.Risk);
                        fixture.Session.ManagedBudget = new ManagedActionBudget(4);
                        destinations.Update(player, 0, 500, QuestId);
                        setAside = brain.Suspensions.IsQuestSuspended(QuestId, fixture.Host.World.NowMs);
                        return setAside || teleports > 0
                            || fixture.Feature.Services.StateOf(player)!.Quests.Get(QuestId)?.Status is not QuestStatus.Complete;
                    });
                    if (settled) break;
                    await fixture.Host.World.AdvanceClockAsync(2_100);
                }
                if (round < PlayerbotWorldDestinations.AbandonAfterSetAsides)
                    await fixture.Host.World.AdvanceClockAsync(PlayerbotSuspensions.SuspendMs + 100);
            }
            return (teleports, setAside, await StatusAsync(fixture));
        }
        finally
        {
            await fixture.Host.World.InvokeAsync(() => { PlayerbotNavigation.Guard(fixture.Session.Player!, null); return true; });
            await StopAsync(fixture, brain);
        }
    }

    [Fact]
    public async Task CompletedQuest_StrongHazardOnEveryRoute_BindPointFarFromEnder_IsKeptAndSetAsideAgain()
    {
        (int teleports, bool setAside, QuestStatus status) = await StrandedRoundsAsync(BindAt.FarFromEnder);
        Assert.Equal(QuestStatus.Complete, status);
        Assert.Equal(0, teleports);
        Assert.True(setAside, "the held quest is set aside for the ordinary retry window");
    }

    [Fact]
    public async Task CompletedQuest_StrongHazardOnEveryRoute_NoBindPoint_IsKeptAndSetAsideAgain()
    {
        (int teleports, bool setAside, QuestStatus status) = await StrandedRoundsAsync(BindAt.None);
        Assert.Equal(QuestStatus.Complete, status);
        Assert.Equal(0, teleports);
        Assert.True(setAside);
    }

    [Fact]
    public async Task CompletedQuest_StrongHazardOnEveryRoute_BindPointBesideEnder_TeleportsAndKeepsTheQuest()
    {
        (int teleports, _, QuestStatus status) = await StrandedRoundsAsync(BindAt.BesideEnder);
        Assert.Equal(QuestStatus.Complete, status);
        Assert.Equal(1, teleports);
    }

    [Fact]
    public async Task IncompleteQuest_StrongHazardOnEveryRoute_NeverTeleportsForTheQuest()
    {
        (int teleports, _, QuestStatus status) = await StrandedRoundsAsync(BindAt.BesideEnder, complete: false);
        Assert.Equal(0, teleports);
        Assert.Equal(QuestStatus.Incomplete, status);
    }

    [Fact]
    public async Task CompletedQuest_AmongSameLevelHostiles_ReachesAndRewardsEnder()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, manualClock: true);
        await fixture.AcceptAndCompleteAsync();
        await AddPocketAsync(fixture, 6);
        var brain = new PlayerbotBrain(fixture.Session, new PlayerbotOptions { Enabled = true });
        bool sawStrong = false;
        try
        {
            for (int tick = 0; tick < 300; tick++)
            {
                bool rewarded = await fixture.Host.World.InvokeAsync(() =>
                {
                    var player = fixture.Session.Player!;
                    PlayerbotMotion.ElapseForTests(player, 500);
                    fixture.Session.ManagedBudget = new ManagedActionBudget(4);
                    brain.Update(500);
                    sawStrong |= brain.Risk.Hazards.Active(player.MapId, fixture.Host.World.NowMs)
                        .Any(hazard => hazard.Kind == PlayerbotHazards.StrongKind && hazard.ReturnPassable);
                    return fixture.Feature.Services.StateOf(player)!.Quests.Get(QuestId)!.Rewarded;
                });
                if (rewarded) break;
                await fixture.Host.World.AdvanceClockAsync(500);
            }
            string trace = await fixture.Host.World.InvokeAsync(() =>
                $"x={fixture.Session.Player!.X:F1} goal={brain.Goal} visible={fixture.Session.Player.VisibleObjects.Count} " +
                $"risk={brain.RiskReport} rewarded={fixture.Feature.Services.StateOf(fixture.Session.Player)!.Quests.Get(QuestId)!.Rewarded}");
            Assert.True(sawStrong, "the return walk must actually encounter the same-level strong pack: " + trace);
            Assert.True(await fixture.Host.World.InvokeAsync(() =>
                fixture.Feature.Services.StateOf(fixture.Session.Player!)!.Quests.Get(QuestId)!.Rewarded), trace);
        }
        finally { await StopAsync(fixture, brain); }
    }

    [Fact]
    public async Task RelaxedReturnRoute_DoesNotOutliveThatQuestsReturnCall()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, manualClock: true);
        await fixture.AcceptAndCompleteAsync();
        await AddPocketAsync(fixture, 6);
        var options = new PlayerbotOptions { Enabled = true };
        var brain = new PlayerbotBrain(fixture.Session, options);
        var destinations = new PlayerbotWorldDestinations(fixture.Session, options);
        try
        {
            await fixture.Host.World.InvokeAsync(() =>
            {
                var player = fixture.Session.Player!;
                brain.Risk.ScanHazards(player);
                PlayerbotNavigation.Guard(player, brain.Risk);
                fixture.Session.ManagedBudget = new ManagedActionBudget(4);
                destinations.Update(player, 0, 500, QuestId);
                Assert.True(destinations.RouteIsRelaxed, "the return trip starts under the relaxed rule");
                // The same NPC, now an ordinary trip (no return quest): RefreshEntries keys its cache on the return quest and drops the route.
                fixture.Session.ManagedBudget = new ManagedActionBudget(4);
                destinations.Update(player, 0, 500);
                Assert.False(destinations.RouteIsRelaxed, "an ordinary trip must not keep the return's relaxed route");
                return true;
            });
        }
        finally
        {
            await fixture.Host.World.InvokeAsync(() => { PlayerbotNavigation.Guard(fixture.Session.Player!, null); return true; });
            await StopAsync(fixture, brain);
        }
    }

    [Theory]
    [InlineData((byte)9, true)]   // 3 above the bot: a manageable pack may be crossed on the return
    [InlineData((byte)10, false)] // 4 above: manageable one at a time, but only the level cap blocks the crossing
    public async Task PackHazard_IsReturnPassableOnlyWithinTheLevelCap(byte mobLevel, bool passable)
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, manualClock: true);
        await fixture.AcceptAndCompleteAsync();
        await AddPocketAsync(fixture, mobLevel);
        var brain = new PlayerbotBrain(fixture.Session, new PlayerbotOptions { Enabled = true });
        try
        {
            await fixture.Host.World.InvokeAsync(() =>
            {
                var player = fixture.Session.Player!;
                brain.Risk.ScanHazards(player);
                var strong = brain.Risk.Hazards.Active(player.MapId, fixture.Host.World.NowMs)
                    .Where(hazard => hazard.Kind == PlayerbotHazards.StrongKind).ToList();
                Assert.NotEmpty(strong);
                Assert.All(strong, hazard => Assert.Equal(passable, hazard.ReturnPassable));
                return true;
            });
        }
        finally { await StopAsync(fixture, brain); }
    }

    [Theory]
    [InlineData((byte)6, true)]   // same-level pack: the quest goal plans the visible ender under the completed-return rule
    [InlineData((byte)10, false)] // over the level cap: still refused
    public async Task QuestGoal_WalkingToAVisibleCompletedEnder_PlansThroughAManageablePackOnly(byte mobLevel, bool walks)
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, manualClock: true);
        await fixture.AcceptAndCompleteAsync();
        await AddPocketAsync(fixture, mobLevel);
        var brain = new PlayerbotBrain(fixture.Session, new PlayerbotOptions { Enabled = true });
        try
        {
            await fixture.Host.World.InvokeAsync(() =>
            {
                var player = fixture.Session.Player!;
                player.SetPosition(player.X + 40, player.Y, player.Z, 0); // from +55 to +95: outside the 25-yard hazards of the pack at +128, ender at +150 in view
                brain.Risk.ScanHazards(player);
                PlayerbotNavigation.Guard(player, brain.Risk);
                fixture.Session.ManagedBudget = new ManagedActionBudget(4);
                bool acted = brain.QuestGoals.Update(player, 500);
                Assert.Equal(walks, acted);
                if (walks) Assert.Equal(PlayerbotGoalKind.Quest, brain.QuestGoals.Goal);
                return true;
            });
        }
        finally
        {
            await fixture.Host.World.InvokeAsync(() => { PlayerbotNavigation.Guard(fixture.Session.Player!, null); return true; });
            await StopAsync(fixture, brain);
        }
    }

    private static async Task AddPocketAsync(ReturnFixture fixture, byte mobLevel)
    {
        await fixture.Host.World.InvokeAsync(() =>
        {
            var player = fixture.Session.Player!;
            player.Level = 6;
            float originX = player.X;
            var ender = (Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            ender.SetPosition(originX + 150, player.Y, player.Z, 0);
            WorldCollision.Of(fixture.Host.World).Install(lineOfSight: new FlatFloor(player.Z),
                pathfinder: new ScriptedRoute(0f) { Blocked = false });
            for (uint i = 0; i < 8; i++)
            {
                var mob = new Creature(910000 + i, new CreatureTemplate
                {
                    Entry = 910000 + i, Name = "ender pocket hostile", Faction = 900012,
                    MinLevel = mobLevel, MaxLevel = mobLevel, MinLevelHealth = 100, MaxLevelHealth = 100,
                    MinMeleeDamage = 3, MaxMeleeDamage = 4, MeleeBaseAttackTime = 2000, DisplayIds = [49],
                }, null, CreatureContent.Empty, new Random((int)i + 1));
                mob.SetPosition(originX + 128 + (i % 2) * 3, player.Y + ((int)i - 4) * 2, player.Z, 0);
                player.Map.AddObject(mob);
            }
            player.SetPosition(originX + 55, player.Y, player.Z, 0);
            return true;
        });
    }
}
