using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArcaneCore.Cryptography;
using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.MockClient.Hosting;

public sealed record ClientFixtureOptions(string Directory, string AccountName, string Password);

public sealed record ClientFixtureResult(string Directory, string ConfigurationPath, string ManifestPath,
    string FactionPath, string AuthDatabasePath, string CharacterDatabasePath, string WorldDatabasePath,
    string AccountName);

/// <summary>Export persistent, disposable, repository-authored content for normal loopback daemons.</summary>
public static class ClientFixture
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task<ClientFixtureResult> PrepareAsync(ClientFixtureOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Directory);
        ValidateCredential(options.AccountName, nameof(options.AccountName));
        ValidateCredential(options.Password, nameof(options.Password));
        if (!Path.IsPathFullyQualified(options.Directory))
            throw new ArgumentException("The fixture directory must be an absolute path.", nameof(options));
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Directory));
        string? parent = Path.GetDirectoryName(directory);
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new IOException("The fixture destination already exists; choose a new disposable directory.");
        if (parent is null || !Directory.Exists(parent))
            throw new DirectoryNotFoundException("The fixture's parent directory must already exist.");
        cancellationToken.ThrowIfCancellationRequested();

        // Moving an empty sibling reserves the destination atomically. If another caller
        // creates it first, only our empty staging directory is removed; its files are untouched.
        string staging = Path.Combine(parent, ".arcane-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try { Directory.Move(staging, directory); }
        catch { Directory.Delete(staging, recursive: false); throw; }

        var result = new ClientFixtureResult(directory, Path.Combine(directory, "appsettings.json"),
            Path.Combine(directory, "manifest.json"), Path.Combine(directory, "FactionTemplate.dbc"),
            Path.Combine(directory, "auth.sqlite"), Path.Combine(directory, "characters.sqlite"),
            Path.Combine(directory, "world.sqlite"), options.AccountName.ToUpperInvariant());
        // A failed export stays available for diagnosis and cannot be overwritten by a retry.
        // No daemon starts here, and this directory is never subject to the self-test cleanup.
        await WriteFactionAsync(result.FactionPath, cancellationToken).ConfigureAwait(false);
        var databases = new Dictionary<string, object>();
        foreach ((string component, string path) in new[]
        {
            ("Auth", result.AuthDatabasePath), ("Characters", result.CharacterDatabasePath),
            ("World", result.WorldDatabasePath),
        })
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            databases[component] = new
            {
                Provider = nameof(DatabaseProvider.Sqlite),
                ConnectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
                    Cache = SqliteCacheMode.Private, DefaultTimeout = 5,
                }.ToString(),
            };
        }

        // Capitalized daemon section names are intentional; serializers leave dictionary keys intact.
        var configuration = new Dictionary<string, object>
        {
            ["Auth"] = new { BindAddress = "127.0.0.1", Port = 3724, AutocreateAccounts = false },
            ["World"] = new
            {
                BindAddress = "127.0.0.1", Port = 8085, TickIntervalMs = 50,
                AutosaveIntervalMs = 30000, InstantLogoutSecurity = "Player",
                PlayerCommands = true, Motd = "ArcaneCore disposable synthetic quest fixture.",
            },
            ["Realms"] = new { Seed = new[] { new { Name = "ArcaneCore Synthetic", Address = "127.0.0.1:8085" } } },
            ["Quests"] = new
            {
                FactionTemplateDbcPath = result.FactionPath,
                OrdinaryRewardQuestIds = new[] { SyntheticQuestContent.RewardQuestId },
            },
            ["Database"] = databases,
            ["Logging"] = new { LogLevel = new Dictionary<string, string>
                { ["Default"] = "Information", ["Microsoft.EntityFrameworkCore"] = "Warning" } },
        };
        await WriteJsonAsync(result.ConfigurationPath, configuration, cancellationToken).ConfigureAwait(false);
        IConfigurationRoot config = new ConfigurationBuilder().AddJsonFile(result.ConfigurationPath).Build();
        using IDisposable configLifetime = (IDisposable)config;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.Configure<RealmSeedOptions>(config.GetSection(RealmSeedOptions.SectionName));
        services.AddAuthDatabase(config).AddCharacterDatabase(config).AddWorldDatabase(config);
        await using (ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true, ValidateOnBuild = true,
        }))
        {
            await provider.GetRequiredService<AuthDbInitializer>().InitializeAsync(cancellationToken).ConfigureAwait(false);
            await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync(cancellationToken).ConfigureAwait(false);
            await provider.GetRequiredService<WorldDbInitializer>().InitializeAsync(cancellationToken).ConfigureAwait(false);
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            await SyntheticQuestContent.SeedAsync(scope.ServiceProvider.GetRequiredService<WorldDbContext>(),
                SyntheticQuestProfile.ManualClient, cancellationToken).ConfigureAwait(false);
            byte[] salt = WowSrp6.GenerateSalt();
            await scope.ServiceProvider.GetRequiredService<IAccountStore>().CreateAsync(new Account
            {
                Username = result.AccountName, Salt = salt,
                Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, result.AccountName, options.Password),
                    WowSrp6.KeyLength),
                Status = AccountStatus.Active, Security = AccountSecurity.Player,
            }, cancellationToken).ConfigureAwait(false);
        }

        var initialFiles = new Dictionary<string, string>();
        foreach (string file in new[] { result.ConfigurationPath, result.FactionPath, result.AuthDatabasePath,
            result.CharacterDatabasePath, result.WorldDatabasePath })
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using FileStream stream = File.OpenRead(file);
            initialFiles[Path.GetFileName(file)] = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        }
        await WriteJsonAsync(result.ManifestPath, new
        {
            fixtureDefinitionVersion = 1,
            buildInformationalVersion = typeof(ClientFixture).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            createdUtc = DateTimeOffset.UtcNow,
            profile = nameof(SyntheticQuestProfile.ManualClient),
            contentOrigin = "Repository-authored synthetic disposable fixture; no client assets or imported world content.",
            accountName = result.AccountName,
            supportedCharacter = "Fresh level-one human warrior (race 1, class 1).",
            questIds = new[] { SyntheticQuestContent.JournalQuestId, SyntheticQuestContent.NpcQuestId, SyntheticQuestContent.RewardQuestId },
            rewardAllowlist = new[] { SyntheticQuestContent.RewardQuestId },
            clientReferenceCandidates = new { guideFaction = 35, targetFaction = 14, creatureDisplay = 49, itemDisplay = 6418 },
            limits = new[]
            {
                "Actual client rendering, faction cursor behavior, icons and quest UI remain unaccepted.",
                "Quest 900002 supports acceptance and abandonment only; its objective targets are absent.",
                "Quest 900003 supports two creature kills, 1234 copper, one fixed item and one of two choice items only.",
                "No XP, reputation, spells, mail, scripts, repeatable quests or production content are supplied.",
                "The authored faction file is server-only and must never replace installed client data.",
                "Initial database hashes change during normal daemon use; record the exact checked-out server commit separately.",
            },
            initialSha256 = initialFiles,
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static void ValidateCredential(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 16 || value.Any(c => c is < '!' or > '~'))
            throw new ArgumentException("Fixture credentials require 1-16 printable ASCII characters without spaces.", parameter);
    }

    private static async Task WriteJsonAsync(string path, object value, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteFactionAsync(string path, CancellationToken cancellationToken)
    {
        // Three authored records in the existing build-5875 reader's fourteen-field WDBC format.
        // These minimal relationships are fixture data, not a reconstruction of client DBC records.
        uint[][] rows =
        [
            [1, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            [35, 0, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            [14, 0, 0, 8, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0],
        ];
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("WDBC"));
            writer.Write((uint)rows.Length); writer.Write(14u); writer.Write(56u); writer.Write(1u);
            foreach (uint[] row in rows) foreach (uint field in row) writer.Write(field);
            writer.Write((byte)0);
        }
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes.ToArray(), cancellationToken).ConfigureAwait(false);
    }
}
