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
                Enabled = true, RestoreOnStartup = true, MaxBots = 10, ThinkIntervalMs = 100, MaxActionsPerTick = 12,
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

        ManagedPlayerbotFeature bots = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await bots.StartupAsync(default);
        try
        {
            Dictionary<string, Watch> watches = bots.Snapshot().Where(s => watched.Contains(s.Name, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(s => s.Name, s => new Watch(s.BotId));
            Assert.Equal(watched.Length, watches.Count);
            await host.World.AdvanceClockAsync(100); // the status snapshot is refreshed by the world tick
            Assert.All(bots.Snapshot().Where(s => watches.ContainsKey(s.Name)), s => Assert.Equal(ManagedPlayerbotState.Running, s.State));

            for (uint elapsed = 0; elapsed < ReplayMs; elapsed += 1_000)
            {
                await host.World.AdvanceClockAsync(1_000);
                await host.OnWorldAsync(() =>
                {
                    foreach ((string name, Watch watch) in watches)
                    {
                        if (bots.FindSession(watch.BotId)?.Player is not { } player) continue;
                        watch.Observe(new Vector3(player.X, player.Y, player.Z), player.IsAlive, host.World.NowMs);
                        if (!player.IsAlive && elapsed % 5_000 == 0 && bots.FindBrain(watch.BotId) is { } dead)
                            output.WriteLine($"{elapsed / 1000,4}s {name} recovery: {dead.Recovery.LastStep}/{dead.Recovery.LastSpiritHealerStep} spot={dead.Recovery.ReviveSpot} ({player.X:F1}, {player.Y:F1}, {player.Z:F1})");
                        if (elapsed % 30_000 == 0)
                        {
                            PlayerbotBrain? brain = bots.FindBrain(watch.BotId);
                            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                                $"{elapsed / 1000,4}s {name}: {brain?.Goal} {brain?.TargetEntry} q{brain?.QuestId} ({player.X:F1}, {player.Y:F1}, {player.Z:F1}) {(player.IsAlive ? "alive" : "dead")} {brain?.StallReport}"));
                        }
                    }

                    return true;
                });
            }

            foreach ((string name, Watch watch) in watches)
                output.WriteLine($"{name}: longest still while alive {watch.LongestStillMs / 1000} s, travelled {watch.Travelled:F0} yards, deaths {watch.Deaths}");
            Assert.All(watches, pair => Assert.True(pair.Value.LongestStillMs < LongestStillMs,
                $"{pair.Key} stood within {SamePlaceYards} yards of one place for {pair.Value.LongestStillMs / 1000} s while alive"));
        }
        finally { await bots.ShutdownBeforeWorldStopAsync(); }
    }

    private sealed class Watch(Guid botId)
    {
        private Vector3? _place;
        private uint _sinceMs;
        private Vector3? _last;

        public Guid BotId { get; } = botId;

        public uint LongestStillMs { get; private set; }

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
