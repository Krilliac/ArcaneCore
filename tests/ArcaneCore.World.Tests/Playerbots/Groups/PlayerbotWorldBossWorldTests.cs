using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Groups;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.WorldBossTestKit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// World-boss raids on the real world (<see cref="PlayerbotGroupCoordinator"/>, docs/areas/playbots-groups.md, World-boss raids) with the group test
/// world, its manual clock and managed bots (p7b design section 9). The boss is a weakened rank-3 template listed as <c>entry@0</c> and placed with
/// <c>SpawnTemporary</c> (<see cref="WorldBossTestKit"/>). Covers: the full kill, the level-63 boss against level-60 bots, the holds that put players
/// first (another's tap, real players near, a player's kill, the stall watch), the wipe counter, the catch-up teleport guard, the caps, and that a
/// battleground never takes a raid member.
/// </summary>
[Collection(MeterCollection)]
public sealed class PlayerbotWorldBossWorldTests
{
    /// <summary>Two warriors, three priests and four mages: roles for a raid of five or six (one tank, two healers) with room for a refusal.</summary>
    private static Task<List<(Guid Id, string Name)>> RaidersAsync(GroupTestWorld world, string prefix = "Wb", byte level = 60) => AddBotsAsync(world, prefix, 2, 3, 4, level);

    private static Task<bool> RaidFormedAsync(GroupTestWorld world, uint maxMs = 300_000)
        => world.RunUntilAsync(maxMs, () => Raid(world) is { State: not PlayerbotGroupState.Forming });

    // --- the kill --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABotRaid_FormsOnTheBoss_TravelsPullsKills_SettlesTheLoot_AndDisbands()
    {
        using var counts = new OutcomeCounts();
        await using GroupTestWorld world = await StartAsync();
        List<(Guid Id, string Name)> bots = await RaidersAsync(world);
        Creature boss = await SpawnAsync(world, bots[0].Id, Boss(), BossAt);
        var warnings = new RaidWarningLog();

        PlayerbotGroupCoordinator.BotGroup? group = null;
        var states = new HashSet<PlayerbotGroupState>();
        Assert.True(await world.RunUntilAsync(900_000, () =>
        {
            if (group is null && Raid(world) is { } formed)
            {
                group = formed;
                foreach (PlayerbotGroupCoordinator.Member m in formed.Members) warnings.Listen(world, m.BotId);
            }

            if (group is not null) states.Add(group.State);
            return group is not null && world.Coordinator.Groups.Count == 0;
        }), await world.TraceAsync());

        Assert.NotNull(group);
        Assert.Equal(PlayerbotGroupGoalKind.WorldBoss, group.Goal.Kind);
        Assert.Equal(BossEntry, group.Goal.ObjectiveEntry);
        Assert.InRange(group.Members.Count, 5, 6);
        Assert.Equal(group.Members.Count > PlayerbotGroupContent.PartySize, group.Raid);
        Assert.Contains(PlayerbotGroupState.Travelling, states);
        Assert.Contains(PlayerbotGroupState.Engaging, states);

        Assert.False(await world.OnWorldAsync(() => boss.IsAlive), "the boss lived" + Environment.NewLine + await world.TraceAsync());
        Assert.Contains(await world.OnWorldAsync(() => boss.LootTapper), group.Members.Select(m => world.Player(m.BotId).Guid));
        Assert.True(Noted(world, "disbanded (killed)"), await world.TraceAsync());
        Assert.True(group.SuccessNoted, "GroupSucceeded not noted for a kill the raid held the tap on");
        Assert.Equal(1, counts.Of(PlayerbotWorldBossOutcomes.Formed));
        Assert.Equal(1, counts.Of(PlayerbotWorldBossOutcomes.Killed));
        Assert.Equal(2, counts.Total);

        PlayerbotWorldBossWarning[] kinds = WarningsOf(group);
        Assert.Contains(PlayerbotWorldBossWarning.Form, kinds);
        Assert.Contains(PlayerbotWorldBossWarning.Pull, kinds);
        Assert.Contains(PlayerbotWorldBossWarning.Kill, kinds);
        Assert.DoesNotContain(PlayerbotWorldBossWarning.GiveUp, kinds);
        Assert.True(warnings.Count >= 3, "the clients heard " + string.Join(" | ", warnings.Lines));
        Assert.All(warnings.Lines, line => Assert.Contains("Test Wyrm", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALevel63Boss_IsMusteredByLevel60Bots_AndKilled()
    {
        // Item 1: the floor is min(63, 60) - 2 = 58, so the player cap does not lock a level-63 boss out.
        using var counts = new OutcomeCounts();
        CreatureTemplate boss63 = Boss(level: 63);
        await using GroupTestWorld world = await StartAsync(templates: [boss63]);
        List<(Guid Id, string Name)> bots = await RaidersAsync(world, "Wc", level: 60);
        Creature boss = await SpawnAsync(world, bots[0].Id, boss63, BossAt);

        Assert.True(await RaidFormedAsync(world), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = Raid(world)!;
        Assert.Equal((byte)58, group.Goal.MinLevel);
        Assert.All(group.Members, m => Assert.Equal(60, world.Player(m.BotId).Level));
        Assert.True(await world.RunUntilAsync(900_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
        Assert.False(await world.OnWorldAsync(() => boss.IsAlive), await world.TraceAsync());
        Assert.Equal(1, counts.Of(PlayerbotWorldBossOutcomes.Killed));
    }

    [Fact]
    public async Task BotsBelowTheFloor_AreNeverMustered()
    {
        CreatureTemplate boss63 = Boss(level: 63);
        await using GroupTestWorld world = await StartAsync(templates: [boss63]);
        List<(Guid Id, string Name)> low = await AddBotsAsync(world, "Wl", 2, 3, 4, level: 57);
        await SpawnAsync(world, low[0].Id, boss63, BossAt);

        Assert.False(await world.RunUntilAsync(120_000, () => Raid(world) is not null), "level-57 bots were mustered for a level-63 boss");
        Assert.DoesNotContain(world.Coordinator.Groups, g => g.WorldBoss is not null);
    }

    [Fact]
    public async Task TheKillTalk_ReadsTheCoordinatorsCatalogueSnapshot()
    {
        // S4 wiring: the guild chat and the conversations ask the coordinator's frozen snapshot, so the entry listed as entry@0 talks and a rank-3 leader does not.
        await using GroupTestWorld world = await StartAsync();
        await AddBotsAsync(world, "Wk", 1, 1, 1);
        await world.RunAsync(10_000);
        var social = world.World.Services.GetRequiredService<PlayerbotSocialFeature>();

        Assert.True(world.Coordinator.IsWorldBossEntry(BossEntry));
        Assert.False(world.Coordinator.IsWorldBossEntry(1748));
        Assert.True(social.GuildChat.IsWorldBossEntry?.Invoke(BossEntry));
        Assert.True(social.Conversations.IsWorldBossEntry?.Invoke(BossEntry));
        Assert.False(social.GuildChat.IsWorldBossEntry?.Invoke(1748));
        Assert.False(social.Conversations.IsWorldBossEntry?.Invoke(1748));
    }

    [Fact]
    public async Task TheReport_HasAWorldBossLine_NamingTheBossAndItsState()
    {
        await using GroupTestWorld world = await StartAsync();
        List<(Guid Id, string Name)> bots = await RaidersAsync(world);
        await SpawnAsync(world, bots[0].Id, Boss(), BossAt);
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.Report().Any(l => l.StartsWith("worldboss:", StringComparison.Ordinal) && l.Contains("Test Wyrm", StringComparison.Ordinal))),
            await world.TraceAsync());
    }

    // --- players first -------------------------------------------------------------------------------------------------------

    /// <summary>A real player standing at the passive boss, which the player tapped: the boss fights it for as long as the test wants and hits for nothing.</summary>
    private static async Task<(WorldTestClient Client, Player Player, Creature Boss)> TakenByAPlayerAsync(GroupTestWorld world, Guid onMapOf, string account, string name)
    {
        Creature boss = await SpawnAsync(world, onMapOf, Passive(), BossAt);
        WorldTestClient client = await world.World.EnterWorldAsync(account, name, AccountSecurity.Player);
        Player player = await world.OnWorldAsync(() => world.World.Host.World.FindOnlinePlayer(name)!);
        await world.OnWorldAsync(() =>
        {
            player.Relocate(BossAt.X - 3f, BossAt.Y, BossAt.Z, 0f, world.World.Host.World.NowMs);
            player.Map!.Combat.DealDamage(player, boss, 1); // the first hit taps it
            return true;
        });
        return (client, player, boss);
    }

    [Fact]
    public async Task ABossAPlayerTapped_IsHeldAtTheRally_NeverPulled_ThenGivenUpAsBossTaken_WithNoStallAndNoKillCredit()
    {
        using var counts = new OutcomeCounts();
        await using GroupTestWorld world = await StartAsync(boss => boss.HoldSeconds = 60, options => options.StallSeconds = 10);
        List<(Guid Id, string Name)> bots = await RaidersAsync(world);
        (WorldTestClient client, Player player, Creature boss) = await TakenByAPlayerAsync(world, bots[0].Id, "WBTAKER1", "Wbtakerone");
        await using WorldTestClient taker = client;

        PlayerbotGroupCoordinator.BotGroup? group = null;
        bool held = false, memberOnThreat = false;
        string threatTrace = string.Empty;
        Assert.True(await world.RunUntilAsync(900_000, () =>
        {
            group ??= Raid(world);
            if (boss.IsAlive && world.World.Host.World.NowMs % 1000 < 50) player.Map!.Combat.DealDamage(player, boss, 1); // keep the tap fresh from the start, before the raid forms
            if (group is not null && boss.IsAlive)
            {
                held |= LeaderStep(world, group) == "lead:hold-worldboss" && group.WorldBoss!.PullHeld;
                if (!memberOnThreat && ThreatenedByAnyOf(boss, MembersOf(world, group))) { memberOnThreat = true; threatTrace = world.Trace(); }
            }

            return group is not null && world.Coordinator.Groups.Count == 0;
        }), await world.TraceAsync());

        Assert.True(held, "the leader never held at the rally: " + await world.TraceAsync());
        Assert.False(memberOnThreat, "a raid member got on the boss's threat list: " + threatTrace);
        Assert.True(Noted(world, "disbanded (boss-taken)"), await world.TraceAsync());
        Assert.False(Noted(world, "stalled"), "the stall watch ended a holding raid: " + await world.TraceAsync());
        Assert.False(group!.SuccessNoted);
        Assert.Equal(1, counts.Of(PlayerbotWorldBossOutcomes.BossTaken));
        Assert.Equal(0, counts.Of(PlayerbotWorldBossOutcomes.Killed));
        Assert.Equal(0, counts.Of(PlayerbotWorldBossOutcomes.Stalled));
    }

    [Fact]
    public async Task AHoldLongerThanTheStallBound_IsNotStalled_AndTheBossIsNotShunned()
    {
        // Item 3: StallSeconds 10 against a 60-second hold. Without the Waiting exemption the watch shuns the target at 10 s and ends the group at 20 s.
        await using GroupTestWorld world = await StartAsync(boss => boss.HoldSeconds = 60, options => options.StallSeconds = 10);
        List<(Guid Id, string Name)> bots = await RaidersAsync(world);
        (WorldTestClient client, Player player, Creature boss) = await TakenByAPlayerAsync(world, bots[0].Id, "WBTAKER2", "Wbtakertwo");
        await using WorldTestClient taker = client;

        PlayerbotGroupCoordinator.BotGroup? group = null;
        uint holdStartedMs = 0;
        bool heldFortySeconds = false;
        Assert.True(await world.RunUntilAsync(900_000, () =>
        {
            group ??= Raid(world);
            if (boss.IsAlive && world.World.Host.World.NowMs % 1000 < 50) player.Map!.Combat.DealDamage(player, boss, 1); // keep the tap fresh from the start, before the raid forms
            if (group is not null && boss.IsAlive)
            {
                if (group.WorldBoss!.Waiting && holdStartedMs == 0) holdStartedMs = (uint)world.World.Host.World.NowMs;
                if (holdStartedMs != 0 && world.World.Host.World.NowMs - holdStartedMs >= 40_000) heldFortySeconds = true;
            }

            return heldFortySeconds || group is not null && world.Coordinator.Groups.Count == 0;
        }), await world.TraceAsync());

        Assert.True(heldFortySeconds, "the raid did not hold for 40 s: " + await world.TraceAsync());
        Assert.NotNull(Raid(world));
        Assert.False(Noted(world, "stalled"), await world.TraceAsync());
        Assert.False(Noted(world, "shun"), await world.TraceAsync());
    }

    [Fact]
    public async Task RealPlayersStandingNearTheBoss_HoldThePull_UntilTheyLeave()
    {
        using var counts = new OutcomeCounts();
        await using GroupTestWorld world = await StartAsync(boss => { boss.PlayersNearCount = 3; boss.PlayersNearYards = 100f; boss.HoldSeconds = 900; });
        List<(Guid Id, string Name)> bots = await RaidersAsync(world);
        Creature boss = await SpawnAsync(world, bots[0].Id, Boss(), BossAt);

        // Three players 60 yards from the boss: inside PlayersNearYards, outside its aggro; none of them pulls.
        var clients = new List<WorldTestClient>();
        try
        {
            foreach (string name in new[] { "Wbnearone", "Wbneartwo", "Wbnearthree" })
                clients.Add(await world.World.EnterWorldAsync(name.ToUpperInvariant(), name, AccountSecurity.Player));
            List<Player> players = await world.OnWorldAsync(() =>
            {
                var found = new List<Player>();
                foreach (string name in new[] { "Wbnearone", "Wbneartwo", "Wbnearthree" })
                {
                    Player p = world.World.Host.World.FindOnlinePlayer(name)!;
                    p.Relocate(BossAt.X, BossAt.Y + 60f, BossAt.Z, 0f, world.World.Host.World.NowMs);
                    found.Add(p);
                }

                return found;
            });

            PlayerbotGroupCoordinator.BotGroup? group = null;
            bool held = false;
            Assert.True(await world.RunUntilAsync(300_000, () =>
            {
                group ??= Raid(world);
                if (group is not null) held |= group.WorldBoss!.PullHeld && LeaderStep(world, group) == "lead:hold-worldboss";
                return held;
            }), await world.TraceAsync());

            // Long enough for the pull gate to have opened without the players: still no member on the boss.
            await world.RunAsync(90_000);
            Assert.False(await world.OnWorldAsync(() => ThreatenedByAnyOf(boss, MembersOf(world, group!))), "the raid pulled with players near");
            Assert.True(await world.OnWorldAsync(() => boss.IsAlive));

            // The players leave: the raid pulls and kills.
            await world.OnWorldAsync(() =>
            {
                foreach (Player p in players) p.Relocate(BossAt.X, BossAt.Y + 600f, BossAt.Z, 0f, world.World.Host.World.NowMs);
                return true;
            });
            Assert.True(await world.RunUntilAsync(900_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());
            Assert.True(Noted(world, "disbanded (killed)"), await world.TraceAsync());
            Assert.Equal(1, counts.Of(PlayerbotWorldBossOutcomes.Killed));
        }
        finally
        {
            foreach (WorldTestClient client in clients) await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task APlayerWhoFinishesTheKill_WhileTheRaidHolds_IsBossTaken_WithNoGroupSucceededAndNoKillWarning()
    {
        using var counts = new OutcomeCounts();
        await using GroupTestWorld world = await StartAsync();
        List<(Guid Id, string Name)> bots = await RaidersAsync(world);
        Creature boss = await SpawnAsync(world, bots[0].Id, Boss(health: 5000), BossAt);
        await using WorldTestClient client = await world.World.EnterWorldAsync("WBKILLER1", "Wbkillerone", AccountSecurity.Player);
        Player player = await world.OnWorldAsync(() => world.World.Host.World.FindOnlinePlayer("Wbkillerone")!);
        await world.OnWorldAsync(() =>
        {
            player.Relocate(BossAt.X - 3f, BossAt.Y, BossAt.Z, 0f, world.World.Host.World.NowMs);
            return true;
        });

        PlayerbotGroupCoordinator.BotGroup? group = null;
        bool killed = false;
        Assert.True(await world.RunUntilAsync(900_000, () =>
        {
            group ??= Raid(world);
            if (group is { State: PlayerbotGroupState.Travelling } && !killed)
            {
                // Before the pull: the player taps the boss and kills it.
                player.Map!.Combat.DealDamage(player, boss, 1);
                player.Map!.Combat.DealDamage(player, boss, 1_000_000);
                killed = true;
            }

            return killed && world.Coordinator.Groups.Count == 0;
        }), await world.TraceAsync());

        Assert.False(boss.IsAlive);
        Assert.Equal(player.Guid, boss.LootTapper);
        Assert.True(Noted(world, "disbanded (boss-taken)"), await world.TraceAsync());
        Assert.False(group!.SuccessNoted);
        Assert.DoesNotContain(PlayerbotWorldBossWarning.Kill, WarningsOf(group));
        Assert.Equal(1, counts.Of(PlayerbotWorldBossOutcomes.BossTaken));
        Assert.Equal(0, counts.Of(PlayerbotWorldBossOutcomes.Killed));
    }

    // --- wipes ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithMaxWipesOne_AnUnbeatableBoss_EndsTheAttemptAfterTheFirstWipe()
    {
        using var counts = new OutcomeCounts();
        await using GroupTestWorld world = await StartAsync(boss => boss.MaxWipes = 1, templates: [Unbeatable()]);
        List<(Guid Id, string Name)> bots = await RaidersAsync(world);
        await SpawnAsync(world, bots[0].Id, Unbeatable(), BossAt);

        PlayerbotGroupCoordinator.BotGroup? group = null;
        int mostWipes = 0;
        Assert.True(await world.RunUntilAsync(1_200_000, () =>
        {
            group ??= Raid(world);
            if (group is not null) mostWipes = Math.Max(mostWipes, group.WorldBoss!.Wipes);
            return group is not null && world.Coordinator.Groups.Count == 0;
        }), await world.TraceAsync());

        Assert.True(Noted(world, "disbanded (wiped)"), await world.TraceAsync());
        Assert.Equal(1, mostWipes);
        Assert.Equal(1, counts.Of(PlayerbotWorldBossOutcomes.Wiped));
        Assert.Equal(0, counts.Of(PlayerbotWorldBossOutcomes.Killed));
    }

    [Fact]
    public async Task WithMaxWipesTwo_TheFirstWipeRegroups_AndTheSecondEndsTheAttempt()
    {
        // The generic Wiped arm gives up at the const MaxFailures (2) for every other group; this raid counts its own wipes against MaxWipes.
        using var counts = new OutcomeCounts();
        await using GroupTestWorld world = await StartAsync(boss => boss.MaxWipes = 2, templates: [Unbeatable()]);
        List<(Guid Id, string Name)> bots = await RaidersAsync(world);
        await SpawnAsync(world, bots[0].Id, Unbeatable(), BossAt);

        PlayerbotGroupCoordinator.BotGroup? group = null;
        bool regrouped = false;
        int mostWipes = 0;
        Assert.True(await world.RunUntilAsync(2_400_000, () =>
        {
            group ??= Raid(world);
            if (group is not null)
            {
                mostWipes = Math.Max(mostWipes, group.WorldBoss!.Wipes);
                regrouped |= group.WorldBoss.Wipes == 1 && group.State == PlayerbotGroupState.Regrouping;
            }

            return group is not null && world.Coordinator.Groups.Count == 0;
        }), await world.TraceAsync());

        Assert.True(regrouped, "no regroup after the first wipe: " + await world.TraceAsync());
        Assert.Equal(2, mostWipes);
        Assert.True(Noted(world, "disbanded (wiped)"), await world.TraceAsync());
        Assert.Equal(1, counts.Of(PlayerbotWorldBossOutcomes.Wiped));
    }

    // --- walking, not teleporting --------------------------------------------------------------------------------------------

    [Fact]
    public async Task AStraggler500YardsAway_WalksToTheRaid_WithTheFollowTeleportOnAndCatchUpTeleportOff()
    {
        // Six bots in all, so the straggler is in the raid. It needs over 70 s to walk in, past the 60 s after which the generic Gathering
        // teleports a straggler to its leader (Party.TeleportToLeader, on by default); a world-boss raid does not unless CatchUpTeleport is on.
        await using GroupTestWorld world = await StartAsync(boss => { boss.CatchUpTeleport = false; boss.HoldSeconds = 600; }, options => options.Party.TeleportToLeader = true, Passive());
        List<(Guid Id, string Name)> bots = await AddBotsAsync(world, "We", 1, 2, 2);
        (Guid Id, string Name) far = await world.AddBotAsync("Wbstraggler", Human, Mage, 60, [Fireball], new Vector3(StartX - 500f, StartY, StartZ));
        await SpawnAsync(world, bots[0].Id, Passive(), BossAt);

        var jumps = new List<(string Name, float Yards)>();
        var lastAt = new Dictionary<Guid, Vector3>();
        PlayerbotGroupCoordinator.BotGroup? group = null;
        Assert.True(await world.RunUntilAsync(600_000, () =>
        {
            group ??= Raid(world);
            foreach ((Guid id, string name) in bots.Append(far))
            {
                Player p = world.Player(id);
                var now = new Vector3(p.X, p.Y, p.Z);
                // Run speed is 7 yards a second: a tick of 50 ms moves 0.35 yards. A jump of 30 yards in one tick is a teleport.
                if (group?.Find(id) is not null && lastAt.TryGetValue(id, out Vector3 before) && Vector3.Distance(before, now) > 30f) jumps.Add((name, Vector3.Distance(before, now)));
                lastAt[id] = now;
            }

            return group?.Find(far.Id) is not null && Vector3.Distance(lastAt[far.Id], BossAt) < 120f;
        }), await world.TraceAsync());

        Assert.Empty(jumps);
        Assert.NotNull(group!.Find(far.Id));
    }

    // --- battlegrounds ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryRaidMember_IsHeldFromTheBattlegroundCoordinator_ForAsLongAsTheRaidLasts()
    {
        // Item 15 (rejected as a filter change, kept as a regression): the battleground coordinator sees a bot as not Free while the group coordinator
        // Involves it, and its MayQueue refuses a bot in any server group that is not a battleground group.
        await using GroupTestWorld world = await StartAsync(boss => boss.HoldSeconds = 600, templates: [Passive()]);
        List<(Guid Id, string Name)> bots = await RaidersAsync(world);
        await SpawnAsync(world, bots[0].Id, Passive(), BossAt);

        PlayerbotGroupCoordinator.BotGroup? group = null;
        int checkedTicks = 0;
        Assert.True(await world.RunUntilAsync(400_000, () =>
        {
            group ??= Raid(world);
            if (group is null || group.State == PlayerbotGroupState.Forming) return false;
            foreach (PlayerbotGroupCoordinator.Member m in group.Members.Where(m => !m.Real))
            {
                Assert.True(world.Coordinator.Involves(m.BotId), $"{m.Name} is not Involves during the raid");
                Game.Groups.Group? server = world.GroupManager.GetGroup(world.Player(m.BotId).Guid);
                Assert.True(server is { IsBattlegroundGroup: false }, $"{m.Name} is in no ordinary server group during the raid");
            }

            return ++checkedTicks >= 200;
        }), await world.TraceAsync());
    }

    // --- the caps and the switch -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TwoBossesUp_WithOneConcurrentRaidAllowed_FormOneRaid()
    {
        CreatureTemplate second = Boss(SecondBossEntry);
        await using GroupTestWorld world = await StartAsync(boss => { boss.MaxConcurrentRaids = 1; boss.IncludedEntries = $"{BossEntry}@0,{SecondBossEntry}@0"; boss.HoldSeconds = 600; },
            options => options.MaxBots = 14, Passive(), second);
        List<(Guid Id, string Name)> bots = await AddBotsAsync(world, "Wd", 4, 6, 4);
        await SpawnAsync(world, bots[0].Id, Passive(), BossAt);
        await SpawnAsync(world, bots[0].Id, second, BossAt + new Vector3(0f, 80f, 0f));

        int most = 0;
        Assert.True(await world.RunUntilAsync(240_000, () =>
        {
            most = Math.Max(most, world.Coordinator.Groups.Count(g => g.WorldBoss is not null));
            return world.Coordinator.Groups.Count(g => g.WorldBoss is not null) == 1 && world.Coordinator.Groups.Single(g => g.WorldBoss is not null).State == PlayerbotGroupState.Engaging;
        }), await world.TraceAsync());
        await world.RunAsync(120_000);
        most = Math.Max(most, world.Coordinator.Groups.Count(g => g.WorldBoss is not null));
        Assert.Equal(1, most);
    }

    [Fact]
    public async Task WithWorldBossesOff_ABossThatIsUp_RaisesNothing()
    {
        await using GroupTestWorld world = await StartAsync(boss => boss.Enabled = false);
        List<(Guid Id, string Name)> bots = await RaidersAsync(world);
        await SpawnAsync(world, bots[0].Id, Boss(), BossAt);
        await world.RunAsync(150_000);
        Assert.DoesNotContain(world.Coordinator.Groups, g => g.WorldBoss is not null);
        Assert.Empty(world.Coordinator.Groups);
    }

    [Fact]
    public async Task TooFewWillingBots_LetTheMusterWindowRunOut_AsMusterFailed_AndBackToWatchWithACooldown()
    {
        using var counts = new OutcomeCounts();
        // Nobody agrees to come (a RefuseAffinity of 0 turns every stranger (affinity 0) away), so the open muster ends with its window.
        await using GroupTestWorld world = await StartAsync(boss => { boss.MusterWindowSeconds = 30; boss.RetrySeconds = 600; }, options => options.Groups.Cooperation.RefuseAffinity = 0);
        List<(Guid Id, string Name)> bots = await RaidersAsync(world);
        await SpawnAsync(world, bots[0].Id, Boss(), BossAt);

        Assert.True(await world.RunUntilAsync(300_000, () => counts.Of(PlayerbotWorldBossOutcomes.MusterFailed) >= 1), await world.TraceAsync() + counts);
        Assert.Empty(world.Coordinator.Groups);
        PlayerbotWorldBossCampaign campaign = world.Coordinator.WorldBossCampaign(BossEntry)!;
        Assert.Equal(PlayerbotWorldBossPhase.Watch, campaign.Phase);
        Assert.True(campaign.CooldownUntilUnix > world.Coordinator.UnixNow());
        Assert.Equal(0, campaign.AttemptsToday(world.Coordinator.UnixNow()));
        Assert.Equal(0, counts.Of(PlayerbotWorldBossOutcomes.Formed));
    }
}
