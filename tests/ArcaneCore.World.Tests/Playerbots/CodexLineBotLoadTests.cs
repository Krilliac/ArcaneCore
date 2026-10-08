using System.Globalization;
using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Tests.Upgrade.CodexLine;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>A fact that runs only when <see cref="Variable"/> names a directory holding copies of Codex-line databases.</summary>
internal sealed class CodexLiveCopiesFactAttribute : FactAttribute
{
    /// <summary>A directory with before-auth.db, before-characters.db and before-world.db (copies; the test copies them again and never writes the originals).</summary>
    public const string Variable = "ARCANECORE_CODEX_LIVE_COPIES";

    public CodexLiveCopiesFactAttribute()
    {
        string? directory = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, "before-characters.db")))
        {
            Skip = $"{Variable} is not set to a directory of Codex-line database copies (before-auth.db, before-characters.db, before-world.db): the live-copy rehearsal did NOT run.";
        }
    }
}

/// <summary>
/// The world daemon's start on databases the Codex line created: the ordinary initializers migrate them to this build's
/// numbering (docs/integration/codex-merge-20261007.md), and the managed playerbots recorded there load and log into the
/// world through this build's stores. The synthetic case runs everywhere (Codex schema of commit 0e07c29b, shared with
/// ArcaneCore.Data.Tests); the live-copy case runs on copies of real Codex-line databases when
/// <see cref="CodexLiveCopiesFactAttribute.Variable"/> is set.
/// </summary>
public sealed class CodexLineBotLoadTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcane-codex-bots-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A handle still closing; the temp directory is harmless.
        }
    }

    [Fact]
    public async Task SyntheticCodexDatabases_MigrateAtWorldStartup_AndEveryManagedBotLogsIn()
    {
        Directory.CreateDirectory(_directory);
        string auth = Path.Combine(_directory, "auth.db");
        string characters = Path.Combine(_directory, "characters.db");
        await CodexLineDatabase.CreateAsync(auth, "auth");
        await CodexLineDatabase.CreateAsync(characters, "characters");
        string[] names = ["Codexone", "Codextwo", "Codexthree"];
        for (int i = 0; i < names.Length; i++)
        {
            int id = i + 1;
            string account = "PBCODEX" + id.ToString(CultureInfo.InvariantCulture);
            await CodexLineDatabase.InsertAsync(auth, "account", ("Id", id), ("Username", account), ("Salt", new byte[32]), ("Verifier", new byte[32]),
                ("Status", 0), ("Security", 0));
            await CodexLineDatabase.InsertAsync(characters, "characters", ("Id", id), ("AccountId", id), ("Name", names[i]), ("Race", 1), ("Class", 1),
                ("Level", 3), ("MapId", 0), ("ZoneId", 12), ("X", -8949.95 + i), ("Y", -132.49), ("Z", 83.53), ("Money", 100 * id));
            await CodexLineDatabase.InsertAsync(characters, "managed_playerbot", ("BotId", Guid.NewGuid().ToString().ToUpperInvariant()), ("AccountId", id),
                ("CharacterId", id), ("AccountName", account), ("DesiredEnabled", 1), ("State", (int)ManagedPlayerbotState.Running), ("Revision", 3),
                ("CreatedUnix", 1_780_000_000L), ("UpdatedUnix", 1_780_000_100L));
        }

        IConfiguration configuration = Configuration(auth, characters, world: null);
        var logs = new LogLines();
        await InitializeAsync(configuration, logs, world: false);
        Assert.Contains(logs.Lines, l => l.StartsWith("Migrated the characters database from Codex (0e07c29b) schema version 25 to schema version 33", StringComparison.Ordinal));
        Assert.Equal(CharacterDbContext.Schema.CurrentVersion, await VersionAsync(characters, "characters"));

        IReadOnlyList<PlayerbotStatus> loaded = await StartBotsAsync(configuration, names.Length, restoreOnStartup: true);
        Assert.Equal(names.Order(StringComparer.Ordinal), loaded.Select(b => b.Name).Order(StringComparer.Ordinal));
    }

    [CodexLiveCopiesFact]
    public async Task TheLiveCodexDatabaseCopies_MigrateAtWorldStartup_AndAllTheirManagedBotsLoadAndLogIn()
    {
        string source = Environment.GetEnvironmentVariable(CodexLiveCopiesFactAttribute.Variable)!;
        Directory.CreateDirectory(_directory);
        string auth = Path.Combine(_directory, "auth.db");
        string characters = Path.Combine(_directory, "characters.db");
        string world = Path.Combine(_directory, "world.db");
        File.Copy(Path.Combine(source, "before-auth.db"), auth);
        File.Copy(Path.Combine(source, "before-characters.db"), characters);
        File.Copy(Path.Combine(source, "before-world.db"), world);
        long registered = Convert.ToInt64(await CodexLineDatabase.ScalarAsync(characters, "SELECT COUNT(*) FROM \"managed_playerbot\""), CultureInfo.InvariantCulture);
        output.WriteLine($"copies of {source}: characters version {await VersionAsync(characters, "characters")}, world version {await VersionAsync(world, "world")}, {registered} managed bots");
        Assert.True(registered > 0, "the copies hold no managed bots: nothing would be proven");

        IConfiguration configuration = Configuration(auth, characters, world);
        var logs = new LogLines();
        await InitializeAsync(configuration, logs, world: true);
        foreach (string line in logs.Lines.Where(l => l.Contains("migrat", StringComparison.OrdinalIgnoreCase)))
        {
            output.WriteLine(line);
        }

        Assert.Equal(CharacterDbContext.Schema.CurrentVersion, await VersionAsync(characters, "characters"));
        Assert.Equal(WorldDbContext.Schema.CurrentVersion, await VersionAsync(world, "world"));
        Assert.Equal(AuthDbContext.Schema.CurrentVersion, await VersionAsync(auth, "auth"));

        IReadOnlyList<PlayerbotStatus> loaded = await StartBotsAsync(configuration, (int)registered, restoreOnStartup: false);
        foreach (PlayerbotStatus bot in loaded.OrderBy(b => b.Name, StringComparer.Ordinal))
        {
            output.WriteLine($"bot {bot.Name}: {bot.State}, map {bot.MapId}");
        }
    }

    /// <summary>
    /// A world host on the migrated auth and characters databases (EF stores replace the host's in-memory ones) with
    /// managed bots enabled: the bots the store registers appear in the snapshot, each is started (or restored) and
    /// enters the world. Returns the snapshot of the running bots.
    /// </summary>
    private async Task<IReadOnlyList<PlayerbotStatus>> StartBotsAsync(IConfiguration configuration, int expected, bool restoreOnStartup)
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddAuthDatabase(configuration);
            services.AddCharacterDatabase(configuration);
            services.AddSingleton<IOptions<PlayerbotOptions>>(Options.Create(new PlayerbotOptions
            {
                Enabled = true, MaxBots = Math.Max(expected, 1), AllowedMaps = [0, 1], RestoreOnStartup = restoreOnStartup,
            }));
        });
        ManagedPlayerbotFeature bots = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        try
        {
            await bots.StartupAsync(default);
            IReadOnlyList<PlayerbotStatus> registered = bots.Snapshot();
            Assert.Equal(expected, registered.Count);
            if (!restoreOnStartup)
            {
                foreach (PlayerbotStatus bot in registered)
                {
                    PlayerbotOperationResult started = await bots.StartAsync(bot.BotId.ToString());
                    Assert.True(started.Success, $"{bot.Name}: {started.Code}");
                }
            }

            foreach (PlayerbotStatus bot in registered)
            {
                await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(bot.Name) is not null, $"managed bot {bot.Name} in the world");
            }

            // The snapshot is refreshed by the world tick.
            await host.WaitForWorldAsync(() => bots.Snapshot().Count(b => b.State == ManagedPlayerbotState.Running) == expected, "every bot running in the snapshot");
            return bots.Snapshot();
        }
        finally
        {
            await bots.ShutdownBeforeWorldStopAsync();
        }
    }

    private static IConfiguration Configuration(string auth, string characters, string? world)
    {
        var values = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:Auth:Provider"] = "Sqlite",
            ["Database:Auth:ConnectionString"] = CodexLineDatabase.ConnectionString(auth),
            ["Database:Characters:Provider"] = "Sqlite",
            ["Database:Characters:ConnectionString"] = CodexLineDatabase.ConnectionString(characters),
        };
        if (world is not null)
        {
            values["Database:World:Provider"] = "Sqlite";
            values["Database:World:ConnectionString"] = CodexLineDatabase.ConnectionString(world);
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>The daemon's own initializers (DatabaseStartup order: auth, characters, world).</summary>
    private static async Task InitializeAsync(IConfiguration configuration, LogLines logs, bool world)
    {
        IServiceCollection services = new ServiceCollection()
            .AddLogging(builder => builder.AddProvider(logs).SetMinimumLevel(LogLevel.Information))
            .AddAuthDatabase(configuration)
            .AddCharacterDatabase(configuration);
        if (world)
        {
            services.AddWorldDatabase(configuration);
        }

        await using ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<AuthDbInitializer>().InitializeAsync();
        await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
        if (world)
        {
            await provider.GetRequiredService<WorldDbInitializer>().InitializeAsync();
        }
    }

    private static async Task<int> VersionAsync(string path, string component)
        => Convert.ToInt32(await CodexLineDatabase.ScalarAsync(path, $"SELECT \"Version\" FROM \"{component}_schema\" WHERE \"Id\" = 1"), CultureInfo.InvariantCulture);

    private sealed class LogLines : ILoggerProvider
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(LogLines owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Lines)
                {
                    owner.Lines.Add(formatter(state, exception));
                }
            }
        }
    }
}
