using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.WorldBossTestKit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The guards of the world-boss raids that each have a fix of their own, one test per fix, each failing when its fix is taken out: the raids switch, the Mark of Frost rule,
/// the tap that lands between two coordinator checks, the three anomalies, the bot-QA report of a failed raid, the boss guid cache with its scan cap, the own-raid tap
/// exclusion of the watch, and that the respawn ledger is read by a point lookup and only for a boss that is not alive.
/// </summary>
[Collection(MeterCollection)] // its raids move the process-wide world-boss meter that the other world-boss tests count
public sealed class PlayerbotWorldBossGuardTests
{
    private static Task<List<(Guid Id, string Name)>> RaidersAsync(GroupTestWorld world, string prefix) => AddBotsAsync(world, prefix, 2, 3, 4);

    private static string Key(string code) => PlayerbotGroupAnomalies.Key(code);

    /// <summary>A real player at the boss that taps it, as <c>PlayerbotWorldBossWorldTests</c> does: the passive boss fights it for as long as the tap is kept fresh.</summary>
    /// <remarks>The caller prepares <paramref name="account"/>'s character (PrepareCharacterAsync) before RaidersAsync, so its create does not overlap the bots' saves.</remarks>
    private static async Task<(WorldTestClient Client, Player Player)> TapAsync(GroupTestWorld world, Creature boss, string account, string name)
    {
        WorldTestClient client = await world.World.EnterWorldAsync(account, name, AccountSecurity.Player);
        Player player = await world.OnWorldAsync(() => world.World.Host.World.FindOnlinePlayer(name)!);
        await world.OnWorldAsync(() =>
        {
            player.Relocate(BossAt.X - 3f, BossAt.Y, BossAt.Z, 0f, world.World.Host.World.NowMs);
            player.Map!.Combat.DealDamage(player, boss, 1);
            return true;
        });
        return (client, player);
    }

    private static void KeepTap(GroupTestWorld world, Player player, Creature boss)
    {
        if (boss.IsAlive && world.World.Host.World.NowMs % 1000 < 50) player.Map!.Combat.DealDamage(player, boss, 1);
    }

    private static SpellInfo MarkOfFrostSpell() => new()
    {
        Id = PlayerbotGroupAI.MarkOfFrost, Name = "Mark of Frost", RangeIndex = SpellConstants.RangeIndexSelfOnly, Duration = new SpellDuration(-1, 0, -1),
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Dummy, TargetA = SpellImplicitTarget.UnitCaster }],
    };

    // --- the raids switch ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheWatchRuns_OnlyWhileRaidsAreEnabled(bool raidsEnabled)
    {
        await using GroupTestWorld world = await StartAsync(configure: options => options.Groups.RaidsEnabled = raidsEnabled);
        List<(Guid Id, string Name)> bots = await RaidersAsync(world, "Wr");
        await SpawnAsync(world, bots[0].Id, Boss(), BossAt);
        await world.RunAsync(30_000);

        if (raidsEnabled) Assert.NotNull(world.Coordinator.WorldBossStatus(BossEntry));
        else
        {
            Assert.Null(world.Coordinator.WorldBossStatus(BossEntry)); // never watched: the boss is never seen, so no muster can open
            Assert.DoesNotContain(world.Coordinator.Groups, g => g.WorldBoss is not null);
            Assert.Equal(PlayerbotWorldBossPhase.Watch, world.Coordinator.WorldBossCampaign(BossEntry)!.Phase);
        }
    }

    // --- the Mark of Frost ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMemberWithTheMarkOfFrost_HoldsAtTheRally_AndNeverFightsAgain()
    {
        await using GroupTestWorld world = await StartAsync();
        List<(Guid Id, string Name)> bots = await RaidersAsync(world, "Wm");
        Creature boss = await SpawnAsync(world, bots[0].Id, Boss(), BossAt);
        await world.OnWorldAsync(() =>
        {
            SpellFeature spells = world.World.Services.GetRequiredService<SpellFeature>();
            spells.System.Store = new SpellStore([.. spells.System.Store.All, MarkOfFrostSpell()], [], []);
            return true;
        });
        Assert.True(await world.RunUntilAsync(300_000, () => Raid(world) is { State: not PlayerbotGroupState.Forming }), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = Raid(world)!;
        PlayerbotGroupCoordinator.Member marked = group.Members.First(m => !m.Real && m.BotId != group.LeaderBotId && m.Name.StartsWith("Wmmage", StringComparison.Ordinal));
        Assert.True(await world.OnWorldAsync(() => world.World.Services.GetRequiredService<SpellFeature>().System.AddAura(world.Player(marked.BotId), PlayerbotGroupAI.MarkOfFrost, permanent: true)));

        bool sat = false;
        var offences = new List<string>();
        Assert.True(await world.RunUntilAsync(900_000, () =>
        {
            PlayerbotGroupAI? ai = world.Coordinator.FindAI(marked.BotId);
            if (ai is not null && boss.IsAlive)
            {
                bool markedStep = ai.Step.StartsWith("worldboss:", StringComparison.Ordinal) && ai.Step.EndsWith("-marked", StringComparison.Ordinal);
                if (ai.Step == "worldboss:hold-marked") sat = true;
                if (sat && (!markedStep || ai.TargetUnit is not null)) offences.Add($"{ai.Step} target={ai.TargetUnit?.Guid}");
            }

            return world.Coordinator.Groups.Count == 0;
        }), await world.TraceAsync());

        Assert.True(sat, "the marked member never held at the rally: " + await world.TraceAsync());
        Assert.Empty(offences);
        Assert.False(await world.OnWorldAsync(() => boss.IsAlive), "the rest of the raid did not kill the boss: " + await world.TraceAsync());
    }

    // --- a tap between two checks -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATapThatLandsBetweenTwoCoordinatorChecks_StopsTheLeadersPull()
    {
        await using GroupTestWorld world = await StartAsync(boss => boss.HoldSeconds = 600, templates: [Passive()]);
        await world.World.PrepareCharacterAsync("WBTAPPER1", "Wbtapperone", AccountSecurity.Player); // before any bot: no overlap with their saves
        List<(Guid Id, string Name)> bots = await RaidersAsync(world, "Wt");
        Creature boss = await SpawnAsync(world, bots[0].Id, Passive(), BossAt);
        (WorldTestClient client, Player player) = await TapAsync(world, boss, "WBTAPPER1", "Wbtapperone");
        await using WorldTestClient tapper = client;

        PlayerbotGroupCoordinator.BotGroup? group = null;
        Assert.True(await world.RunUntilAsync(900_000, () =>
        {
            group ??= Raid(world);
            KeepTap(world, player, boss);
            return group is { State: PlayerbotGroupState.Engaging } && group.WorldBoss!.PullHeld && LeaderStep(world, group) == "lead:hold-worldboss";
        }), await world.TraceAsync());

        // The coordinator released the pull on its last check, and the player taps after it: the next check is held off, so only the leader's own look at the boss stands between it and the pull.
        PlayerbotWorldBossRaid raid = group!.WorldBoss!;
        uint startMs = 0;
        var offences = new List<string>();
        Assert.True(await world.RunUntilAsync(5_000, () =>
        {
            uint nowMs = (uint)world.World.Host.World.NowMs;
            KeepTap(world, player, boss);
            if (startMs == 0)
            {
                startMs = nowMs;
                raid.PullHeld = false;
                raid.Waiting = false;
                raid.Hold = PlayerbotWorldBossHold.None;
                raid.Released = true;
            }

            raid.CheckedMs = Math.Max(1u, nowMs);
            PlayerbotGroupAI? ai = world.Coordinator.FindAI(group.LeaderBotId);
            if (ai?.TargetUnit is Creature { } target && target.Entry == BossEntry) offences.Add($"{ai.Step} at {nowMs}");
            return unchecked(nowMs - startMs) >= 1000;
        }), await world.TraceAsync());

        Assert.False(raid.PullHeld, "the coordinator held the pull again: the test no longer stands between two checks");
        Assert.Empty(offences);
    }

    // --- the anomalies ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APullWithSomeRealPlayersNear_ButTooFewToHoldIt_RecordsThePullNearPlayersAnomaly()
    {
        await using GroupTestWorld world = await StartAsync(boss => { boss.PlayersNearCount = 3; boss.PlayersNearYards = 100f; });
        await world.World.PrepareCharacterAsync("WBNEAR1", "Wbnearsolo", AccountSecurity.Player); // before any bot: no overlap with their saves
        List<(Guid Id, string Name)> bots = await RaidersAsync(world, "Wn");
        Creature boss = await SpawnAsync(world, bots[0].Id, Boss(), BossAt);
        await using WorldTestClient near = await world.World.EnterWorldAsync("WBNEAR1", "Wbnearsolo", AccountSecurity.Player);
        await world.OnWorldAsync(() =>
        {
            world.World.Host.World.FindOnlinePlayer("Wbnearsolo")!.Relocate(BossAt.X, BossAt.Y + 60f, BossAt.Z, 0f, world.World.Host.World.NowMs);
            return true;
        });

        using AnomalyCapture cap = PlayerbotAnomalies.CaptureIn(world.World.Host.World);
        PlayerbotGroupCoordinator.BotGroup? group = null;
        Assert.True(await world.RunUntilAsync(900_000, () =>
        {
            group ??= Raid(world);
            return group is not null && world.Coordinator.Groups.Count == 0;
        }), await world.TraceAsync());

        Assert.True(Noted(world, "pulls Test Wyrm"), await world.TraceAsync());
        Assert.Equal(1, cap.Count(Key(PlayerbotGroupAnomalies.WorldBossPullNearPlayers)));
        Assert.Contains("1 real player(s)", cap.Describe(), StringComparison.Ordinal);
        Assert.False(await world.OnWorldAsync(() => boss.IsAlive), await world.TraceAsync());
    }

    [Fact]
    public async Task AWipeOutAtFullSize_RecordsTheWipedAnomaly_AndSendsOneRunFailedToBotQa()
    {
        var sink = new RecordingQaSink();
        await using GroupTestWorld world = await StartCoreAsync(boss => boss.MaxWipes = 1, null, services => services.AddSingleton<IPlayerbotQaSink>(sink), null, [Unbeatable()]);
        List<(Guid Id, string Name)> bots = await RaidersAsync(world, "Ww");
        await SpawnAsync(world, bots[0].Id, Unbeatable(), BossAt);

        using AnomalyCapture cap = PlayerbotAnomalies.CaptureIn(world.World.Host.World);
        PlayerbotGroupCoordinator.BotGroup? group = null;
        bool full = false;
        Assert.True(await world.RunUntilAsync(1_200_000, () =>
        {
            group ??= Raid(world);
            if (group is not null && world.Coordinator.Groups.Count > 0) full = group.Members.Count(m => m.Joined) >= group.Goal.Size;
            return group is not null && world.Coordinator.Groups.Count == 0;
        }), await world.TraceAsync());

        Assert.True(full, "the raid was not at its full size: " + await world.TraceAsync());
        Assert.True(Noted(world, "disbanded (wiped)"), await world.TraceAsync());
        Assert.Equal(1, cap.Count(Key(PlayerbotGroupAnomalies.WorldBossWipedAtFullSize)));

        QaSignal signal = Assert.Single(sink.Signals, s => s.Kind == QaSignalKind.RunFailed);
        Assert.Equal("wiped", signal.Reason);
        Assert.Equal(BossEntry, signal.Target);
        Assert.Equal(0u, signal.Map);
        Assert.Equal("raid", signal.Stage);
        Assert.Null(signal.BotKey);
        Assert.Equal(0, signal.BotClass);
        string everything = signal.Message + "|" + signal.Detail + "|" + signal.Stage + "|" + signal.Reason;
        Assert.All(bots, bot => Assert.DoesNotContain(bot.Name, everything, StringComparison.Ordinal));
    }

    // --- bot QA --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARaidGivenUpOnATimeout_SendsOneRunFailedToBotQa_WithNoBotOrGroupIdentity()
    {
        var sink = new RecordingQaSink();
        await using GroupTestWorld world = await StartCoreAsync(boss => boss.HoldSeconds = 600, null, services => services.AddSingleton<IPlayerbotQaSink>(sink), null, [Passive()]);
        List<(Guid Id, string Name)> bots = await RaidersAsync(world, "Wq");
        await SpawnAsync(world, bots[0].Id, Passive(), BossAt);

        Assert.True(await world.RunUntilAsync(900_000, () => Raid(world) is { State: PlayerbotGroupState.Engaging }), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = Raid(world)!;
        await world.OnWorldAsync(() =>
        {
            group.RunFault = "travel-timeout"; // the fault the group AI raises, applied by Advance: the same Finish, disband and report path as a real timeout
            return true;
        });
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Groups.Count == 0), await world.TraceAsync());

        QaSignal signal = Assert.Single(sink.Signals, s => s.Kind == QaSignalKind.RunFailed);
        Assert.Equal("travel-timeout", signal.Reason);
        Assert.Equal(BossEntry, signal.Target);
        Assert.Equal("raid", signal.Stage);
        Assert.Null(signal.BotKey);
        Assert.All(bots, bot => Assert.DoesNotContain(bot.Name, signal.Message + "|" + signal.Detail, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AKilledBoss_SendsNothingToBotQa()
    {
        var sink = new RecordingQaSink();
        await using GroupTestWorld world = await StartCoreAsync(null, null, services => services.AddSingleton<IPlayerbotQaSink>(sink), null, []);
        List<(Guid Id, string Name)> bots = await RaidersAsync(world, "Wz");
        Creature boss = await SpawnAsync(world, bots[0].Id, Boss(), BossAt);

        PlayerbotGroupCoordinator.BotGroup? group = null;
        Assert.True(await world.RunUntilAsync(900_000, () =>
        {
            group ??= Raid(world);
            return group is not null && world.Coordinator.Groups.Count == 0;
        }), await world.TraceAsync());

        Assert.False(await world.OnWorldAsync(() => boss.IsAlive), await world.TraceAsync());
        Assert.True(group!.Succeeded, await world.TraceAsync());
        Assert.DoesNotContain(sink.Signals, s => s.Kind == QaSignalKind.RunFailed);
    }

    // --- the boss guid cache and the scan cap -----------------------------------------------------------------------------------

    [Fact]
    public async Task TheRaidKnowsItsBossByGuidFromTheWatch_AndScansForAMissingOneAtMostOncePerWatchInterval()
    {
        await using GroupTestWorld world = await StartAsync(boss => boss.HoldSeconds = 600, templates: [Passive()]);
        List<(Guid Id, string Name)> bots = await RaidersAsync(world, "Wg");
        Creature boss = await SpawnAsync(world, bots[0].Id, Passive(), BossAt);

        Assert.True(await world.RunUntilAsync(300_000, () => Raid(world) is not null), await world.TraceAsync());
        PlayerbotWorldBossRaid raid = Raid(world)!.WorldBoss!;
        Assert.Equal(boss.Guid, raid.BossGuid); // the guid the watch cached when it saw the boss, handed to the raid at its forming
        Assert.Equal(0, raid.BossScans);

        // On the world thread in one step, so the coordinator's own checks do not count: a cached boss costs a lookup; a boss that is gone costs one scan per watch interval (5 s).
        (bool Cached, int ScansAfterCached, int Misses, int ScansAfterMisses, int ScansAfterInterval) result = await world.OnWorldAsync(() =>
        {
            CreatureMapSystem system = boss.Map!.FindUpdater<CreatureMapSystem>()!;
            const uint t = 1_000_000;
            bool cached = ReferenceEquals(world.Coordinator.FindRaidBoss(system, raid, BossAt, t), boss);
            int afterCached = raid.BossScans;
            system.Despawn(boss);
            int misses = 0;
            for (uint i = 0; i < 25; i++) // 0 .. 4800 ms after the first look
                if (world.Coordinator.FindRaidBoss(system, raid, BossAt, t + (i * 200u)) is null) misses++;
            int afterMisses = raid.BossScans;
            world.Coordinator.FindRaidBoss(system, raid, BossAt, t + 5_000u);
            return (cached, afterCached, misses, afterMisses, raid.BossScans);
        });

        Assert.True(result.Cached);
        Assert.Equal(0, result.ScansAfterCached);
        Assert.Equal(25, result.Misses);
        Assert.Equal(1, result.ScansAfterMisses);
        Assert.Equal(2, result.ScansAfterInterval);
    }

    // --- the watch and the raid's own tap ---------------------------------------------------------------------------------------

    [Fact]
    public async Task APlayersTap_MakesTheWatchSeeABossTakenByOthers()
    {
        await using GroupTestWorld world = await StartAsync(templates: [Passive()]);
        await world.World.PrepareCharacterAsync("WBTAPPER2", "Wbtappertwo", AccountSecurity.Player); // before any bot: no overlap with their saves
        List<(Guid Id, string Name)> bots = await RaidersAsync(world, "Wo");
        Creature boss = await SpawnAsync(world, bots[0].Id, Passive(), BossAt);
        (WorldTestClient client, Player player) = await TapAsync(world, boss, "WBTAPPER2", "Wbtappertwo");
        await using WorldTestClient tapper = client;

        Assert.True(await world.RunUntilAsync(120_000, () =>
        {
            KeepTap(world, player, boss);
            return world.Coordinator.WorldBossStatus(BossEntry) is { TappedOrFightingOthers: true };
        }), await world.TraceAsync());
    }

    [Fact]
    public async Task ALootTapHeldByAPlayer_MakesTheWatchSeeABossTakenByOthers_EvenWhenTheBossFightsNoOne()
    {
        await using GroupTestWorld world = await StartAsync(templates: [Passive()]);
        await world.World.PrepareCharacterAsync("WBTAPPER4", "Wbtapperfor", AccountSecurity.Player); // before any bot: no overlap with their saves
        List<(Guid Id, string Name)> bots = await RaidersAsync(world, "Wq");
        Creature boss = await SpawnAsync(world, bots[0].Id, Passive(), BossAt);
        await using WorldTestClient tapper = await world.World.EnterWorldAsync("WBTAPPER4", "Wbtapperfor", AccountSecurity.Player);
        Player player = await world.OnWorldAsync(() => world.World.Host.World.FindOnlinePlayer("Wbtapperfor")!);

        // The tap alone, as an earlier hit would leave it: the boss has no victim, so the fight branch of the status cannot be what reports it.
        await world.OnWorldAsync(() =>
        {
            boss.LootTapPlayerGuid = player.Guid;
            return true;
        });

        bool noVictimWhenSeen = false;
        Assert.True(await world.RunUntilAsync(120_000, () =>
        {
            if (world.Coordinator.WorldBossStatus(BossEntry) is not { TappedOrFightingOthers: true }) return false;
            noVictimWhenSeen = boss.Combat.Victim is null;
            return true;
        }), await world.TraceAsync());
        Assert.True(noVictimWhenSeen, "the boss fought someone: the loot-tap branch was not what the watch saw");
    }

    [Fact]
    public async Task TheTapOfTheRaidItself_IsNotTakenByOthers_InTheWatch()
    {
        await using GroupTestWorld world = await StartAsync();
        List<(Guid Id, string Name)> bots = await RaidersAsync(world, "Wp");
        Creature boss = await SpawnAsync(world, bots[0].Id, Boss(), BossAt);

        PlayerbotWorldBossStatus? first = null;
        int watchesWhileTapped = 0, tappedTicks = 0, statusTicks = 0;
        bool takenByOthers = false, formed = false;
        Assert.True(await world.RunUntilAsync(900_000, () =>
        {
            PlayerbotGroupCoordinator.BotGroup? raid = Raid(world);
            if (boss.IsAlive && !boss.LootTapper.IsEmpty) tappedTicks++;
            if (raid is not null && boss.IsAlive && !boss.LootTapper.IsEmpty && raid.Members.Any(m => !m.Real && world.Player(m.BotId).Guid == boss.LootTapper)
                && world.Coordinator.WorldBossStatus(BossEntry) is { } status)
            {
                statusTicks++;
                first ??= status;
                if (!ReferenceEquals(status, first)) // a watch that ran after the raid tapped the boss
                {
                    watchesWhileTapped++;
                    takenByOthers |= status.TappedOrFightingOthers;
                }
            }

            formed |= raid is not null;
            return formed && world.Coordinator.Groups.Count == 0;
        }), await world.TraceAsync());

        Assert.True(watchesWhileTapped >= 1, $"no watch ran while the raid held the tap (tapped ticks {tappedTicks}, with a status {statusTicks}): " + await world.TraceAsync());
        Assert.False(takenByOthers, "the watch took the raid's own tap for another's claim");
    }

    // --- the respawn ledger ------------------------------------------------------------------------------------------------------

    /// <summary>A ledger that counts how it is read: <see cref="Copies"/> are the whole-map copies of <see cref="GetPending"/>, <see cref="Lookups"/> the point lookups.</summary>
    private sealed class CountingLedger : ICreatureRespawnPersistence
    {
        public Dictionary<uint, long> Due { get; } = [];

        public int Copies { get; private set; }

        public int Lookups { get; private set; }

        public IReadOnlyDictionary<uint, long> GetPending(uint mapId, uint instanceId)
        {
            Copies++;
            return new Dictionary<uint, long>(Due);
        }

        public bool TryGetPending(uint mapId, uint instanceId, uint spawnGuid, out long respawnUnixSeconds)
        {
            Lookups++;
            return Due.TryGetValue(spawnGuid, out respawnUnixSeconds);
        }

        public void Save(uint mapId, uint instanceId, uint spawnGuid, long respawnUnixSeconds)
        {
        }

        public void Delete(uint mapId, uint instanceId, uint spawnGuid)
        {
        }
    }

    /// <summary>The boss as a database spawn on the continent, listed with a <c>Spawn</c> locator, and the counting ledger in place of the world's.</summary>
    private static async Task<(GroupTestWorld World, CountingLedger Ledger)> StartSpawnedAsync(CreatureTemplate template, Action<PlayerbotWorldBossOptions>? boss = null)
    {
        (CreatureTemplate, CreatureSpawn)[] spawned =
        [
            (template, new CreatureSpawn { Guid = BossEntry, Entry = BossEntry, MapId = 0, X = BossAt.X, Y = BossAt.Y, Z = BossAt.Z, SpawnTimeMinSeconds = 600, SpawnTimeMaxSeconds = 600 }),
        ];
        GroupTestWorld world = await StartCoreAsync(w =>
        {
            w.IncludedEntries = $"{BossEntry}";
            boss?.Invoke(w);
        }, null, null, spawned, []);
        var ledger = new CountingLedger();
        world.Coordinator.RespawnLedgerOverride = ledger;
        return (world, ledger);
    }

    private static Task<Creature> FindSpawnedAsync(GroupTestWorld world, Guid onMapOf) => world.OnWorldAsync(() => world.Player(onMapOf).Map!.FindUpdater<CreatureMapSystem>()!.FindBySpawnGuid(BossEntry)!);

    [Fact]
    public async Task WhileTheBossIsAlive_TheRespawnLedgerIsNotReadAtAll_NotEvenForTheEngageCheck()
    {
        (GroupTestWorld world, CountingLedger ledger) = await StartSpawnedAsync(Passive(), boss => boss.HoldSeconds = 600);
        await using GroupTestWorld _ = world;
        await world.World.PrepareCharacterAsync("WBTAPPER3", "Wbtappertre", AccountSecurity.Player); // before any bot: no overlap with their saves
        List<(Guid Id, string Name)> bots = await RaidersAsync(world, "Wl");
        await world.RunAsync(2_000);
        Creature boss = await FindSpawnedAsync(world, bots[0].Id);
        (WorldTestClient client, Player player) = await TapAsync(world, boss, "WBTAPPER3", "Wbtappertre");
        await using WorldTestClient tapper = client;

        PlayerbotGroupCoordinator.BotGroup? group = null;
        Assert.True(await world.RunUntilAsync(900_000, () =>
        {
            group ??= Raid(world);
            KeepTap(world, player, boss);
            return group is { State: PlayerbotGroupState.Engaging } && LeaderStep(world, group) == "lead:hold-worldboss";
        }), await world.TraceAsync());

        // A hundred more engage checks (every 200 ms) with the boss alive and the raid holding.
        await world.RunUntilAsync(30_000, () =>
        {
            KeepTap(world, player, boss);
            return false;
        });
        Assert.NotNull(Raid(world));
        Assert.Equal(0, ledger.Copies);
        Assert.Equal(0, ledger.Lookups);
    }

    [Fact]
    public async Task ADeadBoss_IsLookedUpInTheRespawnLedgerByPointLookup_NeverByCopyingTheMap()
    {
        (GroupTestWorld world, CountingLedger ledger) = await StartSpawnedAsync(Boss());
        await using GroupTestWorld _ = world;
        long due = BaseUnix + 100_000;
        ledger.Due[BossEntry] = due;
        await world.World.PrepareCharacterAsync("WBKILLER2", "Wbkillertwo", AccountSecurity.Player); // before any bot: no overlap with their saves
        List<(Guid Id, string Name)> bots = await RaidersAsync(world, "Wy");
        await world.RunAsync(500);
        Creature boss = await FindSpawnedAsync(world, bots[0].Id);
        await using WorldTestClient killer = await world.World.EnterWorldAsync("WBKILLER2", "Wbkillertwo", AccountSecurity.Player);
        await world.OnWorldAsync(() =>
        {
            Player player = world.World.Host.World.FindOnlinePlayer("Wbkillertwo")!;
            player.Relocate(BossAt.X - 3f, BossAt.Y, BossAt.Z, 0f, world.World.Host.World.NowMs);
            player.Map!.Combat.DealDamage(player, boss, 1);
            player.Map!.Combat.DealDamage(player, boss, 1_000_000);
            return true;
        });
        Assert.False(await world.OnWorldAsync(() => boss.IsAlive));

        await world.RunAsync(12_000);

        PlayerbotWorldBossStatus? status = world.Coordinator.WorldBossStatus(BossEntry);
        Assert.NotNull(status);
        Assert.Equal(PlayerbotWorldBossState.Dead, status.State);
        Assert.Equal(due, status.DeadUntilUnix);
        Assert.True(ledger.Lookups > 0);
        Assert.Equal(0, ledger.Copies);
    }

    // --- the corpse a raid never saw alive, and the fault sites --------------------------------------------------------------------

    [Fact]
    public async Task ACorpseTheRaidHoldsTheTapOf_ThatItNeverSawAlive_RecordsTheAnomalyOnce()
    {
        (GroupTestWorld world, CountingLedger _) = await StartSpawnedAsync(Boss());
        await using GroupTestWorld __ = world;
        List<(Guid Id, string Name)> bots = await RaidersAsync(world, "Wc");
        await world.RunAsync(500);
        Creature boss = await FindSpawnedAsync(world, bots[0].Id);

        using AnomalyCapture cap = PlayerbotAnomalies.CaptureIn(world.World.Host.World);
        bool killed = false, sawAlive = false;
        Assert.True(await world.RunUntilAsync(900_000, () =>
        {
            PlayerbotGroupCoordinator.BotGroup? raid = Raid(world);
            if (raid is not null && !killed && raid.State < PlayerbotGroupState.Travelling && raid.Members.FirstOrDefault(m => m.Joined && !m.Real) is { } member)
            {
                // A member of the forming raid kills the boss before the raid's first look at it: the corpse carries the raid's tap and the raid never saw the boss alive.
                Player killer = world.Player(member.BotId);
                killer.Map!.Combat.DealDamage(killer, boss, 1);
                killer.Map!.Combat.DealDamage(killer, boss, 1_000_000);
                killed = !boss.IsAlive;
            }

            if (raid?.WorldBoss is { } wb) sawAlive |= wb.SawAlive;
            return killed && world.Coordinator.Groups.Count == 0;
        }), await world.TraceAsync());

        Assert.False(sawAlive, "the raid saw the boss alive: it is not the case this anomaly is about");
        Assert.Equal(1, cap.Count(Key(PlayerbotGroupAnomalies.WorldBossCorpseTapUnseen)));
    }

    /// <summary>A ledger whose point lookup fails, as a broken store would.</summary>
    private sealed class LedgerFault : Exception
    {
        public LedgerFault() : base("ledger fault") { }
    }

    private sealed class ThrowingLedger : ICreatureRespawnPersistence
    {
        public IReadOnlyDictionary<uint, long> GetPending(uint mapId, uint instanceId) => throw new LedgerFault();

        public bool TryGetPending(uint mapId, uint instanceId, uint spawnGuid, out long respawnUnixSeconds) => throw new LedgerFault();

        public void Save(uint mapId, uint instanceId, uint spawnGuid, long respawnUnixSeconds)
        {
        }

        public void Delete(uint mapId, uint instanceId, uint spawnGuid)
        {
        }
    }

    [Fact]
    public async Task AFaultInTheWatch_IsRecordedUnderTheWatchSite_AndTheWatchGoesOn()
    {
        (GroupTestWorld world, CountingLedger _) = await StartSpawnedAsync(Boss());
        await using GroupTestWorld __ = world;
        await world.World.PrepareCharacterAsync("WBKILLER3", "Wbkillerthr", AccountSecurity.Player); // before any bot: no overlap with their saves
        List<(Guid Id, string Name)> bots = await RaidersAsync(world, "Wf");
        await world.RunAsync(500);
        Creature boss = await FindSpawnedAsync(world, bots[0].Id);
        world.Coordinator.RespawnLedgerOverride = new ThrowingLedger();
        await using WorldTestClient killer = await world.World.EnterWorldAsync("WBKILLER3", "Wbkillerthr", AccountSecurity.Player);
        await world.OnWorldAsync(() =>
        {
            Player player = world.World.Host.World.FindOnlinePlayer("Wbkillerthr")!;
            player.Relocate(BossAt.X - 3f, BossAt.Y, BossAt.Z, 0f, world.World.Host.World.NowMs);
            player.Map!.Combat.DealDamage(player, boss, 1);
            player.Map!.Combat.DealDamage(player, boss, 1_000_000);
            return true;
        });
        Assert.False(await world.OnWorldAsync(() => boss.IsAlive));

        // A dead boss is the only one whose status reads the ledger, so the watch now throws, every interval.
        Assert.True(await world.RunUntilAsync(60_000, () => FaultLedger.Shared.Snapshot()
            .Any(f => f.Site == PlayerbotGroupCoordinator.WorldBossWatchSite && f.ExceptionType.Contains(nameof(LedgerFault), StringComparison.Ordinal))), await world.TraceAsync());
        long first = FaultLedger.Shared.Snapshot().Where(f => f.Site == PlayerbotGroupCoordinator.WorldBossWatchSite && f.ExceptionType.Contains(nameof(LedgerFault), StringComparison.Ordinal)).Sum(f => (long)f.Count);
        await world.RunAsync(20_000);
        long later = FaultLedger.Shared.Snapshot().Where(f => f.Site == PlayerbotGroupCoordinator.WorldBossWatchSite && f.ExceptionType.Contains(nameof(LedgerFault), StringComparison.Ordinal)).Sum(f => (long)f.Count);
        Assert.True(later > first, "the watch did not run again after the fault");
    }
}
