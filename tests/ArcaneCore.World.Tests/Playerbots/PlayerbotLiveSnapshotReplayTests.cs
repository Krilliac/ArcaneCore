using System.Globalization;
using System.Numerics;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A fact that replays a copy of a live characters database: <see cref="CharactersVariable"/> (a snapshot taken through the SQLite
/// backup API), <see cref="WorldVariable"/> (the world.db it ran with) and <c>ARCANECORE_TEST_TERRAIN_DIR</c> (maps, vmaps,
/// mmaps). Optional: <see cref="SettingsVariable"/>, the server's appsettings without its <c>Database</c> section (client DBC
/// paths for factions, talents and the like), and <see cref="NamesVariable"/>, the bots to watch. Every file is copied first and
/// never written. Without the data the test is reported Skipped, never a silent pass.
/// </summary>
internal sealed class LiveSnapshotReplayFactAttribute : FactAttribute
{
    public const string CharactersVariable = "ARCANECORE_TEST_BOT_REPLAY_DB";
    public const string WorldVariable = "ARCANECORE_TEST_WORLD_DB";
    public const string SettingsVariable = "ARCANECORE_TEST_BOT_REPLAY_SETTINGS";
    public const string NamesVariable = "ARCANECORE_TEST_BOT_REPLAY_NAMES";

    /// <summary>
    /// The moment the snapshot was taken (ISO 8601, e.g. <c>2026-10-08T16:46:00Z</c>): the game-event clock starts there and runs with
    /// game time. Unset: the characters database file's last write time.
    /// </summary>
    public const string TimeVariable = "ARCANECORE_TEST_BOT_REPLAY_TIME";

    public LiveSnapshotReplayFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable(CharactersVariable) ?? "")
            || !File.Exists(Environment.GetEnvironmentVariable(WorldVariable) ?? "")
            || !Directory.Exists(Path.Combine(Environment.GetEnvironmentVariable(RealTerrainBotFactAttribute.Variable) ?? "", "mmaps")))
            Skip = $"{CharactersVariable}, {WorldVariable} and {RealTerrainBotFactAttribute.Variable} are not all set: the live replay did NOT run.";
    }
}

/// <summary>
/// The managed bots of a live server, replayed from a copy of its characters database on the real terrain and content: each
/// watched bot is logged in where it stood, with its bags, quests and goals, and the world runs on the manual clock. A bot that is
/// alive must not keep its position (within <see cref="SamePlaceYards"/>) for <see cref="LongestStillMs"/> on end.
/// <para>
/// On the snapshot of the 2026-10-08 rehearsal (D:/ArcaneCore-lanes/_logs/w5-talents-bots/rehearsal/orig-characters.db), before the
/// fixes: Ironwander stood at Adlin Pridedrift re-selling a refused item, Graveweaver in the Deathknell crypt, Dawnrover beside
/// William Pestle, all for the whole run; Mirthblade's corpse run was judged stuck within 10 seconds.
/// </para>
/// </summary>
public sealed class PlayerbotLiveSnapshotReplayTests(ITestOutputHelper output) : IDisposable
{
    private const float SamePlaceYards = 5f;
    private const uint LongestStillMs = 180_000;
    private const uint ReplayMs = 600_000;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcane-bot-replay-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { } // a handle still closing; the temp copy is harmless
    }

    [LiveSnapshotReplayFact]
    public async Task TheLiveBots_NeverStandStillForThreeMinutesWhileAlive()
    {
        Directory.CreateDirectory(_directory);
        string characters = Path.Combine(_directory, "characters.db");
        string world = Path.Combine(_directory, "world.db");
        File.Copy(Environment.GetEnvironmentVariable(LiveSnapshotReplayFactAttribute.CharactersVariable)!, characters);
        File.Copy(Environment.GetEnvironmentVariable(LiveSnapshotReplayFactAttribute.WorldVariable)!, world);
        string[] watched = (Environment.GetEnvironmentVariable(LiveSnapshotReplayFactAttribute.NamesVariable) is { Length: > 0 } names
            ? names : "Ironwander,Graveweaver,Dawnrover,Mirthblade").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        // A name ending in '*' watches every managed bot whose name starts so (the 200 stress bots: "Stressbot*").
        if (watched.Any(name => name.EndsWith('*')))
        {
            using var nameDb = new SqliteConnection($"Data Source={characters};Pooling=False");
            nameDb.Open();
            using SqliteCommand read = nameDb.CreateCommand();
            read.CommandText = "select c.Name from managed_playerbot m join characters c on c.Id = m.CharacterId";
            var all = new List<string>();
            using (SqliteDataReader reader = read.ExecuteReader())
                while (reader.Read()) all.Add(reader.GetString(0));
            watched = [.. all.Where(name => watched.Any(pattern => pattern.EndsWith('*')
                ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
                : string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase))).Order(StringComparer.Ordinal)];
        }

        // Scale mode (ARCANECORE_TEST_BOT_REPLAY_SCALE=1): many bots, a summary of the stalls they reported instead of each bot's lines,
        // and no assertion on standing still (the stall reports are the measure, compared between builds).
        bool scale = Environment.GetEnvironmentVariable(ScaleVariable) == "1";
        uint replayMs = uint.TryParse(Environment.GetEnvironmentVariable(ReplayMsVariable), out uint configured) && configured > 0
            ? configured : ReplayMs;
        var stalls = new List<string>();

        var builder = new ConfigurationBuilder();
        if (Environment.GetEnvironmentVariable(LiveSnapshotReplayFactAttribute.SettingsVariable) is { Length: > 0 } settings)
        {
            // Never the live databases: a settings file with connection strings is refused, not overridden key by key.
            Assert.False(new ConfigurationBuilder().AddJsonFile(settings).Build().GetSection("Database").Exists(),
                "the replay settings must not carry a Database section");
            builder.AddJsonFile(settings);
        }

        IConfiguration configuration = builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Characters:Provider"] = "Sqlite",
            ["Database:Characters:ConnectionString"] = $"Data Source={characters};Pooling=False",
            ["Database:World:Provider"] = "Sqlite",
            ["Database:World:ConnectionString"] = $"Data Source={world};Pooling=False",
            ["Database:Auth:Provider"] = "Sqlite",
            ["Database:Auth:ConnectionString"] = $"Data Source={Path.Combine(_directory, "auth.db")};Pooling=False",
        }).Build();
        await using (ServiceProvider bootstrap = new ServiceCollection().AddLogging().AddCharacterDatabase(configuration).BuildServiceProvider())
            await bootstrap.GetRequiredService<CharacterDbInitializer>().InitializeAsync();

        string terrain = Environment.GetEnvironmentVariable(RealTerrainBotFactAttribute.Variable)!;
        await using WorldTestHost host = WorldTestHost.Start(configure: options => options.Maps.DataDirectory = terrain, configureServices: services =>
        {
            services.AddSingleton(configuration);
            services.AddWorldDatabase(configuration);
            services.AddCharacterDatabase(configuration);
            services.AddSingleton<IWorldFeature, ManualClock>();
            services.AddSingleton<IManagedPlayerbotProvisionStore, NoProvisions>();
            services.AddSingleton<IOptions<PlayerbotOptions>>(Options.Create(new PlayerbotOptions
            {
                Enabled = true, RestoreOnStartup = true, MaxBots = Math.Max(10, watched.Length), ThinkIntervalMs = 100, MaxActionsPerTick = 12,
            }));
        });

        // The copy's bots get owners in this host's account store; only the watched ones are desired.
        using (var db = new SqliteConnection($"Data Source={characters};Pooling=False"))
        {
            db.Open();
            var rows = new List<(int Character, string Name, string Account)>();
            using (SqliteCommand read = db.CreateCommand())
            {
                read.CommandText = "select m.CharacterId, c.Name, m.AccountName from managed_playerbot m join characters c on c.Id = m.CharacterId";
                using SqliteDataReader reader = read.ExecuteReader();
                while (reader.Read()) rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
            }

            foreach ((int character, string name, string accountName) in rows)
            {
                Account account = await host.Accounts.CreateAsync(new Account { Username = accountName, Salt = new byte[32], Verifier = new byte[32] });
                using SqliteCommand write = db.CreateCommand();
                write.CommandText = "update managed_playerbot set AccountId = $a, DesiredEnabled = $e, ErrorCode = null, State = 0 where CharacterId = $c;"
                    + " update characters set AccountId = $a where Id = $c;";
                write.Parameters.AddWithValue("$a", account.Id);
                write.Parameters.AddWithValue("$e", watched.Contains(name, StringComparer.OrdinalIgnoreCase) ? 1 : 0);
                write.Parameters.AddWithValue("$c", character);
                write.ExecuteNonQuery();
            }
        }

        // Death rules (the reclaim delay, a ghost's time) read the death clock, which is the wall clock: the replay runs game time
        // about 30 times faster, so a ghost waited out a 30-second reclaim delay for 15 minutes of game time. Tie it to game time.
        await host.OnWorldAsync(() => ArcaneCore.Game.Death.DeathHooks.Register(host.World,
            new ArcaneCore.Game.Death.DeathHooks(ArcaneCore.Game.Death.DeathHooks.For(host.World).Options, new GameTimeDeathClock(host.World))));

        // Game events (holidays, the Darkmoon Faire, day and night) read the wall clock too: a replay run in December would turn Winter
        // Veil on for an October snapshot, and the events would not move with the replay's fast game time. Their clock starts at the
        // snapshot's moment and runs with game time; the service is rebuilt on it with the events the snapshot recorded as running.
        string source = Environment.GetEnvironmentVariable(LiveSnapshotReplayFactAttribute.CharactersVariable)!;
        DateTimeOffset taken = Environment.GetEnvironmentVariable(LiveSnapshotReplayFactAttribute.TimeVariable) is { Length: > 0 } at
            ? DateTimeOffset.Parse(at, CultureInfo.InvariantCulture) : new DateTimeOffset(File.GetLastWriteTimeUtc(source), TimeSpan.Zero);
        HashSet<ushort> running = [];
        using (IServiceScope scope = host.WorldServices.CreateScope())
            if (scope.ServiceProvider.GetService<ArcaneCore.Kernel.WorldData.WorldState.IGameEventStatusStore>() is { } status)
                foreach (int id in await status.LoadActiveAsync())
                    if (id is > 0 and <= ushort.MaxValue) running.Add((ushort)id);
        await host.OnWorldAsync(() =>
        {
            ArcaneCore.Game.WorldState.WorldStateHooks hooks = ArcaneCore.Game.WorldState.WorldStateHooks.For(host.World);
            hooks.Time = new GameTimeClock(host.World, taken, hooks.Time.Zone);
            if (host.WorldServices.GetService<ArcaneCore.World.WorldState.GameEventFeature>() is { } events)
                events.UseContent(events.Content, running);
            return true;
        });

        ManagedPlayerbotFeature bots = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await bots.StartupAsync(default);
        try
        {
            Dictionary<string, Watch> watches = bots.Snapshot().Where(s => watched.Contains(s.Name, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(s => s.Name, s => new Watch(s.BotId));
            Assert.Equal(watched.Length, watches.Count);
            await host.World.AdvanceClockAsync(100); // the status snapshot is refreshed by the world tick
            Assert.All(bots.Snapshot().Where(s => watches.ContainsKey(s.Name)), s => Assert.Equal(ManagedPlayerbotState.Running, s.State));
            ArcaneCore.World.Npc.QuestNpcFeature questFeature = host.WorldServices.GetRequiredService<ArcaneCore.World.Npc.QuestNpcFeature>();
            output.WriteLine(await host.OnWorldAsync(() => string.Create(CultureInfo.InvariantCulture,
                $"game events at {ArcaneCore.Game.WorldState.WorldStateHooks.For(host.World).Time.UtcNow:u}: {(host.WorldServices.GetService<ArcaneCore.World.WorldState.GameEventFeature>() is { } e ? $"{e.Content.Events.Count} loaded, running {string.Join(",", e.ActiveEvents.Order())}" : "none")}")));
            Dictionary<string, Dictionary<uint, QuestRow>> questsAtStart = await host.OnWorldAsync(() =>
                watches.ToDictionary(pair => pair.Key, pair => QuestLog(questFeature, bots.FindSession(pair.Value.BotId)?.Player)));

            for (uint elapsed = 0; elapsed < replayMs; elapsed += 1_000)
            {
                await host.World.AdvanceClockAsync(1_000);
                // A quest reward is written to the characters database in the background; on the fast manual clock the bot's 15-second
                // exchange deadline would otherwise run out while the write is on its way, and the reward be counted as refused.
                foreach (int character in await host.OnWorldAsync(() => watches.Values
                             .Select(w => bots.FindSession(w.BotId)?.Player is { } p ? (int)p.Guid.Low : 0).Where(id => id != 0).ToArray()))
                    await questFeature.WaitForSettlementAsync(character);
                await host.OnWorldAsync(() =>
                {
                    foreach ((string name, Watch watch) in watches)
                    {
                        if (bots.FindSession(watch.BotId)?.Player is not { } player) continue;
                        if (bots.FindBrain(watch.BotId) is { } watchedBrain && watchedBrain.StallCount != watch.Stalls)
                        {
                            watch.Stalls = watchedBrain.StallCount;
                            if ((watchedBrain.StallReport ?? watchedBrain.LastStall) is { } stall)
                                stalls.Add($"{elapsed / 1000,5}s {name} {stall}");
                        }
                        if (scale) continue;
                        watch.Observe(new Vector3(player.X, player.Y, player.Z), player.IsAlive, host.World.NowMs);
                        if (!player.IsAlive && elapsed % 5_000 == 0 && bots.FindBrain(watch.BotId) is { } dead)
                            output.WriteLine($"{elapsed / 1000,4}s {name} recovery: {dead.Recovery.LastStep}/{dead.Recovery.LastSpiritHealerStep} spot={dead.Recovery.ReviveSpot} ({player.X:F1}, {player.Y:F1}, {player.Z:F1})");
                        if (player.IsAlive && (player.Combat.IsInCombat || player.Health < player.MaxHealth || elapsed % 5_000 == 0) && string.Equals(Environment.GetEnvironmentVariable("ARCANECORE_TEST_BOT_REPLAY_TRACE"), name, StringComparison.OrdinalIgnoreCase)
                            && bots.FindBrain(watch.BotId) is { } traced)
                            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                                $"{elapsed / 1000,4}s {name} L{player.Level} hp {player.Health}/{player.MaxHealth} attackers {string.Join(",", player.Combat.Attackers.OfType<ArcaneCore.Game.Creatures.Creature>().Select(c => $"{c.Entry}/L{c.Level}/{c.Health}/{c.MaxHealth}/dmg{c.Template.MinMeleeDamage}-{c.Template.MaxMeleeDamage}"))} goal {traced.Goal} at ({player.X:F1}, {player.Y:F1}, {player.Z:F1}) [{traced.RiskReport}]{Melee(player, traced.InspectionTarget)}"));
                        if (!player.IsAlive && !watch.Dead && bots.FindBrain(watch.BotId) is { } fallen)
                            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                                $"{elapsed / 1000,4}s {name} died at ({player.X:F1}, {player.Y:F1}, {player.Z:F1}): last enemies {string.Join(",", fallen.Risk.Tracker.LastEnemies.Select(c => $"{c.Entry}/L{c.Level}"))}; retreats {fallen.Risk.Retreat.Count} last {fallen.Risk.Retreat.Reason}/{fallen.Risk.Retreat.Outcome}; {fallen.Risk.LastEngagement}"));
                        if (!player.IsAlive)
                        {
                            if (player.Combat.Corpse is { } corpse) watch.Body = new Vector3(corpse.X, corpse.Y, corpse.Z);
                            watch.Healer |= bots.FindBrain(watch.BotId)?.Recovery.UsingSpiritHealer == true;
                            watch.Dead = true;
                        }
                        else if (watch.Dead)
                        {
                            watch.Dead = false;
                            string body = watch.Body is { } at ? string.Create(CultureInfo.InvariantCulture,
                                $"{Vector3.Distance(at, new Vector3(player.X, player.Y, player.Z)):F1} yards from its body") : "no body seen";
                            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                                $"{elapsed / 1000,4}s {name} revived at ({player.X:F1}, {player.Y:F1}, {player.Z:F1}), {body}, {(watch.Healer ? "through the spirit healer" : "at its body")}"));
                            watch.Healer = false;
                            watch.Body = null;
                        }
                        if (elapsed % 30_000 == 0)
                        {
                            PlayerbotBrain? brain = bots.FindBrain(watch.BotId);
                            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                                $"{elapsed / 1000,4}s {name}: {brain?.Goal} {brain?.TargetEntry} q{brain?.QuestId} ({player.X:F1}, {player.Y:F1}, {player.Z:F1}) {(player.IsAlive ? "alive" : "dead")} {brain?.StallReport} [{brain?.RiskReport}]"));
                        }
                    }

                    return true;
                });
            }

            output.WriteLine($"stall reports: {stalls.Count} from {stalls.Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]).Distinct().Count()} of {watches.Count} bots over {replayMs / 1000} s");
            foreach (IGrouping<string, string> pattern in stalls.GroupBy(StallPattern).OrderByDescending(group => group.Count()))
                output.WriteLine($"  {pattern.Count(),4} {pattern.Key}");
            foreach (string line in stalls) output.WriteLine("stall " + line);
            if (scale) return;
            foreach ((string name, Watch watch) in watches)
                output.WriteLine($"{name}: longest still while alive {watch.LongestStillMs / 1000} s, travelled {watch.Travelled:F0} yards, deaths {watch.Deaths}");
            foreach ((string name, Watch watch) in watches)
            {
                Dictionary<uint, QuestRow> before = questsAtStart[name];
                Dictionary<uint, QuestRow> after = await host.OnWorldAsync(() => QuestLog(questFeature, bots.FindSession(watch.BotId)?.Player));
                output.WriteLine($"{name}: quests taken {string.Join(",", after.Keys.Where(q => !before.ContainsKey(q)).Order())}; "
                    + $"rewarded {string.Join(",", after.Where(q => q.Value.Rewarded && !(before.TryGetValue(q.Key, out QuestRow was) && was.Rewarded)).Select(q => q.Key).Order())}; "
                    + $"open {string.Join(",", after.Where(q => !q.Value.Rewarded).OrderBy(q => q.Key).Select(q => $"{q.Key}:{q.Value.Status}"))}");
            }
            // Bot groups (PlayerbotGroupCoordinator): what the coordinator saw and did over the replay.
            if (host.WorldServices.GetService<ArcaneCore.World.Playerbots.Groups.PlayerbotGroupCoordinator>() is { } groups)
                foreach (string line in await host.OnWorldAsync(() => groups.Report().Concat(groups.Events).ToArray()))
                    output.WriteLine("groups: " + line);
            Assert.All(watches, pair => Assert.True(pair.Value.LongestStillMs < LongestStillMs,
                $"{pair.Key} stood within {SamePlaceYards} yards of one place for {pair.Value.LongestStillMs / 1000} s while alive"));
        }
        finally { await bots.ShutdownBeforeWorldStopAsync(); }
    }

    /// <summary>The melee geometry against the bot's target: 2D distance, height difference, whether the server lets it swing, the facing,
    /// its victim and the last swing error (the "behind" stalls of the 2026-10-08 rehearsal: no damage dealt while standing).</summary>
    private static string Melee(ArcaneCore.Game.Entities.Player player, ArcaneCore.Game.Creatures.Creature? target)
    {
        if (target is null) return "";
        float d2 = MathF.Sqrt(((target.X - player.X) * (target.X - player.X)) + ((target.Y - player.Y) * (target.Y - player.Y)));
        float dz = target.Z - player.Z;
        return string.Create(CultureInfo.InvariantCulture,
            $" melee[{target.Entry} d2={d2:F1} dz={dz:F1} d3={MathF.Sqrt((d2 * d2) + (dz * dz)):F1} reach={ArcaneCore.Game.Combat.MapCombat.CanReachWithMeleeAutoAttack(player, target)} "
            + $"arc={ArcaneCore.Game.Combat.MapCombat.HasInArc(player, target, ArcaneCore.Game.Combat.CombatConstants.AutoAttackArc)} victim={(player.Combat.Victim as ArcaneCore.Game.Creatures.Creature)?.Entry} "
            + $"err={player.Combat.LastSwingError} moving={(player.Movement.Flags & ArcaneCore.Protocol.MovementFlags.MaskMoving) != 0}]");
    }

    /// <summary>The replay in scale mode (many bots, stall summary, no assertion).</summary>
    public const string ScaleVariable = "ARCANECORE_TEST_BOT_REPLAY_SCALE";

    /// <summary>The game time replayed, in milliseconds (default <see cref="ReplayMs"/>).</summary>
    public const string ReplayMsVariable = "ARCANECORE_TEST_BOT_REPLAY_MS";

    /// <summary>A stall report without its bot, time and position: "goal=Quest target=6747 quest=1656", or "death loop attackers=94".</summary>
    private static string StallPattern(string line)
    {
        System.Text.RegularExpressions.Match goal = System.Text.RegularExpressions.Regex.Match(line, @"goal=\S+ target=\d+ quest=\d+");
        if (goal.Success) return goal.Value;
        System.Text.RegularExpressions.Match loop = System.Text.RegularExpressions.Regex.Match(line, @"attackers=\S+");
        return loop.Success ? "death loop " + loop.Value : line;
    }

    private readonly record struct QuestRow(ArcaneCore.Game.Quests.QuestStatus Status, bool Rewarded);

    private static Dictionary<uint, QuestRow> QuestLog(ArcaneCore.World.Npc.QuestNpcFeature quests, ArcaneCore.Game.Entities.Player? player)
        => player is not null && quests.Services.StateOf(player) is { Loaded: true } state
            ? state.Quests.Statuses.ToDictionary(row => row.Key, row => new QuestRow(row.Value.Status, row.Value.Rewarded))
            : [];

    /// <summary>The game-event clock: the snapshot's moment plus the world's game time since the replay started.</summary>
    private sealed class GameTimeClock(ArcaneCore.Game.Maps.WorldRuntime world, DateTimeOffset start, TimeZoneInfo zone) : ArcaneCore.Game.WorldState.Time.IGameTime
    {
        private readonly TimeSpan _startUptime = world.Uptime;

        public DateTimeOffset UtcNow => start + (world.Uptime - _startUptime);

        public TimeZoneInfo Zone => zone;
    }

    /// <summary>Unix seconds that advance with the world's game time from the moment the replay starts.</summary>
    private sealed class GameTimeDeathClock(ArcaneCore.Game.Maps.WorldRuntime world) : ArcaneCore.Game.Death.DeathClock
    {
        private readonly long _startSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)world.Uptime.TotalSeconds;

        public override long UnixSeconds => _startSeconds + (long)world.Uptime.TotalSeconds;
    }

    private sealed class Watch(Guid botId)
    {
        private Vector3? _place;
        private uint _sinceMs;
        private Vector3? _last;

        public Guid BotId { get; } = botId;

        /// <summary>The death being watched: the body last seen, and whether the recovery took the spirit healer.</summary>
        public bool Dead { get; set; }

        public Vector3? Body { get; set; }

        public bool Healer { get; set; }

        public uint LongestStillMs { get; private set; }

        /// <summary>The brain's stall count last seen (a change is a new stall report).</summary>
        public int Stalls { get; set; }

        public float Travelled { get; private set; }

        /// <summary>Times the bot was seen dead after being seen alive (each death of the replay counts once).</summary>
        public int Deaths { get; private set; }

        private bool? _wasAlive; // unknown before the first look: a bot already dead at the start is not counted

        public void Observe(Vector3 position, bool alive, uint nowMs)
        {
            if (_last is { } last) Travelled += Vector3.Distance(last, position);
            _last = position;
            if (_wasAlive == true && !alive) Deaths++;
            _wasAlive = alive;
            if (!alive || _place is not { } place || Vector3.Distance(place, position) > SamePlaceYards)
            {
                _place = alive ? position : null;
                _sinceMs = nowMs;
                return;
            }

            LongestStillMs = Math.Max(LongestStillMs, unchecked(nowMs - _sinceMs));
        }
    }

    private sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    private sealed class NoProvisions : IManagedPlayerbotProvisionStore
    {
        public Task<Account> CreateAsync(Guid botId, Account account, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ManagedPlayerbotProvision>> LoadPendingAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ManagedPlayerbotProvision>>([]);
        public Task<bool> CompleteAsync(Guid botId, int accountId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> RollbackEmptyOwnerAsync(Guid botId, int accountId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
