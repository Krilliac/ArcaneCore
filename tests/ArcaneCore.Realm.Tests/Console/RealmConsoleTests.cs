using System.Collections.Concurrent;
using System.Globalization;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Realm.Ops.ServerConsole;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using ConsoleInputSource = ArcaneCore.Kernel.Ops.ServerConsole.ConsoleInputSource;

// The namespace is ServerConsole, not Console: a namespace named Console would hide System.Console (the production namespace is
// ArcaneCore.Realm.Ops.ServerConsole for the same reason).
namespace ArcaneCore.Realm.Tests.ServerConsole;

/// <summary>The realm daemon's console (docs/areas/server-console.md): its commands against in-memory stores, and the reader that feeds them.</summary>
public sealed class RealmConsoleTests
{
    private const string Prefix = RealmConsoleCommands.Prefix;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private int _stops;

        public int Stops => Volatile.Read(ref _stops);

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Interlocked.Increment(ref _stops);
    }

    private sealed class ScriptedReader : TextReader
    {
        private readonly BlockingCollection<string?> _lines = [];
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public void Post(string? line) => _lines.Add(line);

        public override string? ReadLine()
        {
            Interlocked.Increment(ref _reads);
            return _lines.Take();
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _entries = new();

        public (string Category, LogLevel Level, string Message)[] Entries => [.. _entries];

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<(string, LogLevel, string)> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => sink.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }

    /// <summary>The admin store over the in-memory accounts: only delete is real, which is all the console command calls.</summary>
    private sealed class FakeAdmin(InMemoryAccountStore accounts) : IAccountAdmin
    {
        public int Deletes { get; private set; }

        public Task<bool> DeleteAsync(string username, CancellationToken cancellationToken = default)
        {
            Deletes++;
            return Task.FromResult(accounts.Remove(username));
        }

        public Task<bool> SetStatusAsync(string username, AccountStatus status, int? actorAccountId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> RevokeSessionKeyAsync(int accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<int, string>> GetUsernamesAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlySet<int>> FindNonActiveAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeCounts : IRealmCharacterCountStore
    {
        private readonly Dictionary<(int Realm, int Account), int> _rows = [];

        public int RowsOf(int accountId) => _rows.Keys.Count(k => k.Account == accountId);

        public Task<IReadOnlyDictionary<int, int>> GetCountsAsync(int accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<int, int>>(_rows.Where(r => r.Key.Account == accountId).ToDictionary(r => r.Key.Realm, r => r.Value));

        public Task SetCountAsync(int realmId, int accountId, int count, CancellationToken cancellationToken = default)
        {
            _rows[(realmId, accountId)] = count;
            return Task.CompletedTask;
        }

        public Task<int> DeleteByAccountAsync(int accountId, CancellationToken cancellationToken = default)
        {
            (int Realm, int Account)[] keys = [.. _rows.Keys.Where(k => k.Account == accountId)];
            foreach ((int Realm, int Account) key in keys)
            {
                _rows.Remove(key);
            }

            return Task.FromResult(keys.Length);
        }
    }

    private sealed class Rig
    {
        public InMemoryAccountStore Accounts { get; } = new();

        public FakeTime Time { get; } = new(new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero));

        public FakeLifetime Lifetime { get; } = new();

        public int Sessions { get; set; }

        public List<RealmEntry> Realms { get; } = [];

        public RealmConsoleCommands Commands { get; }

        public FakeCounts Counts { get; } = new();

        public Rig()
        {
            ServiceProvider services = new ServiceCollection()
                .AddSingleton<IAccountStore>(Accounts)
                .AddSingleton<IAccountAdmin>(new FakeAdmin(Accounts))
                .AddSingleton<IRealmCharacterCountStore>(Counts)
                .AddSingleton<IRealmStore>(new InMemoryRealmStore(Realms))
                .BuildServiceProvider();
            Commands = new RealmConsoleCommands(services.GetRequiredService<IServiceScopeFactory>(), () => Sessions, Time, Lifetime);
        }

        public async Task<string[]> RunAsync(string line)
        {
            var output = new StringWriter();
            await Commands.ExecuteAsync(line, output, CancellationToken.None);
            return output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        }
    }

    private static RealmConsole NewConsole(
        Rig rig, ConsoleInputSource source, ILoggerFactory? loggers = null, bool enabled = true)
        => new(Options.Create(new RealmConsoleOptions { Enabled = enabled }), source, rig.Commands, loggers ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for: " + what);
            await Task.Delay(5);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RealmConsole_Realmlist_PrintsEveryRealmAsServed()
    {
        var rig = new Rig();
        rig.Realms.Add(new RealmEntry { Id = 1, Name = "Arcane", Address = "127.0.0.1:8085", Type = RealmType.PvP, Flags = RealmFlags.NewPlayers, Population = 1.5f, Category = 1 });
        rig.Realms.Add(new RealmEntry { Id = 2, Name = "Second Realm", Address = "10.0.0.2:8085", Type = RealmType.RP, Flags = RealmFlags.Offline, Population = 0f, Category = 5 });

        string[] lines = await rig.RunAsync("realmlist");

        Assert.Equal(
            [
                Prefix + "2 realm(s)",
                Prefix + "id 1  Arcane  127.0.0.1:8085  type PvP  flags NewPlayers  population 1.5  category 1  state Unknown  heartbeat none  wire NewPlayers  lock 0",
                Prefix + "id 2  Second Realm  10.0.0.2:8085  type RP  flags Offline  population 0  category 5  state Unknown  heartbeat none  wire Offline  lock 0",
            ],
            lines);
    }

    [Fact]
    public async Task RealmConsole_Realmlist_ShowsStateHeartbeatAgeAndTheFlagsClientsGet()
    {
        var rig = new Rig();
        DateTime now = rig.Time.GetUtcNow().UtcDateTime;
        rig.Realms.Add(new RealmEntry { Id = 1, Name = "Alive", Address = "a:1", Flags = RealmFlags.NewPlayers, State = RealmRuntimeState.Online, HeartbeatUtc = now.AddSeconds(-4) });
        rig.Realms.Add(new RealmEntry { Id = 2, Name = "Hung", Address = "b:1", State = RealmRuntimeState.Online, HeartbeatUtc = now.AddSeconds(-90), AllowedSecurityLevel = 2 });

        string[] lines = await rig.RunAsync("realmlist");

        Assert.Equal(Prefix + "id 1  Alive  a:1  type Normal  flags NewPlayers  population 0  category 0  state Online  heartbeat 4s ago  wire NewPlayers  lock 0", lines[1]);
        Assert.Equal(Prefix + "id 2  Hung  b:1  type Normal  flags None  population 0  category 0  state Online  heartbeat 90s ago  wire Offline  lock 2", lines[2]);
    }

    [Fact]
    public async Task RealmConsole_Realmlist_WithNoRealms_SaysSo()
    {
        var rig = new Rig();

        Assert.Equal([Prefix + "0 realm(s)"], await rig.RunAsync(".realmlist"));
    }

    [Fact]
    public async Task RealmConsole_Info_PrintsUptimeSessionsAndRealmCount()
    {
        var rig = new Rig { Sessions = 4 };
        rig.Realms.Add(new RealmEntry { Id = 1, Name = "Arcane", Address = "127.0.0.1:8085" });
        var fresh = new Rig(); // the uptime clock starts when the commands are created
        Assert.Equal(Prefix + "uptime: 0d 00:00:00", (await fresh.RunAsync("info"))[0]);

        rig.Time.Now += new TimeSpan(2, 3, 4, 5); // 2 days 03:04:05

        Assert.Equal(
            [Prefix + "uptime: 2d 03:04:05", Prefix + "logon sessions: 4", Prefix + "realms: 1"],
            await rig.RunAsync("info"));
    }

    [Fact]
    public async Task RealmConsole_Info_ClampsUptimeAfterABackwardClockStep()
    {
        var rig = new Rig { Sessions = 1 };
        rig.Time.Now -= new TimeSpan(0, 1, 5, 22); // an NTP correction or VM resume moved the wall clock back

        Assert.Equal(Prefix + "uptime: 0d 00:00:00", (await rig.RunAsync("info"))[0]);
    }

    [Fact]
    public async Task RealmConsole_Online_PrintsTheLogonSessionCount()
    {
        var rig = new Rig { Sessions = 0 };
        Assert.Equal([Prefix + "0 logon session(s) connected"], await rig.RunAsync("online"));

        rig.Sessions = 17;
        Assert.Equal([Prefix + "17 logon session(s) connected"], await rig.RunAsync("ONLINE"));
    }

    [Fact]
    public async Task RealmConsole_AccountCreate_SetPassword_SetGmLevel()
    {
        var rig = new Rig();

        Assert.Equal([Prefix + "created account 'ALICE'"], await rig.RunAsync("account create alice firstpw"));
        Account created = (await rig.Accounts.FindByUsernameAsync("ALICE"))!;
        AssertVerifier(created, "firstpw");
        Assert.Equal(AccountSecurity.Player, created.Security);
        Assert.Equal(AccountStatus.Active, created.Status);

        // A duplicate is refused and leaves the credentials alone.
        byte[] verifierBefore = created.Verifier;
        Assert.Equal([Prefix + "account 'ALICE' already exists"], await rig.RunAsync("account create ALICE otherpw"));
        Assert.Equal(verifierBefore, (await rig.Accounts.FindByUsernameAsync("ALICE"))!.Verifier);

        Assert.Equal([Prefix + "updated password for 'ALICE'"], await rig.RunAsync("account set-password alice secondpw"));
        AssertVerifier((await rig.Accounts.FindByUsernameAsync("ALICE"))!, "secondpw");

        Assert.Equal([Prefix + "'ALICE' is now Administrator (3); it applies from the account's next world login"], await rig.RunAsync("account set-gmlevel alice 3"));
        Assert.Equal(AccountSecurity.Administrator, (await rig.Accounts.FindByUsernameAsync("ALICE"))!.Security);
        Assert.Equal([Prefix + "'ALICE' is now GameMaster (2); it applies from the account's next world login"], await rig.RunAsync("account set-gmlevel ALICE gamemaster"));
        Assert.Equal(AccountSecurity.GameMaster, (await rig.Accounts.FindByUsernameAsync("ALICE"))!.Security);
    }

    [Fact]
    public async Task RealmConsole_AccountCommands_RefuseMissingAccountsAndBadArguments()
    {
        var rig = new Rig();

        Assert.Equal([Prefix + "account 'NOBODY' does not exist"], await rig.RunAsync("account set-password nobody pw"));
        Assert.Equal([Prefix + "account 'NOBODY' does not exist"], await rig.RunAsync("account set-gmlevel nobody 1"));
        Assert.Null(await rig.Accounts.FindByUsernameAsync("NOBODY"));

        await rig.RunAsync("account create bob pw");
        string[] badLevel = await rig.RunAsync("account set-gmlevel bob 9");
        Assert.StartsWith(Prefix + "usage: account set-gmlevel <name>", Assert.Single(badLevel), StringComparison.Ordinal);
        Assert.Equal(AccountSecurity.Player, (await rig.Accounts.FindByUsernameAsync("BOB"))!.Security);

        // Wrong argument counts and unknown verbs print the usage and change nothing.
        foreach (string line in new[] { "account", "account create bob", "account create bob a b", "account set-password bob", "account frobnicate bob x" })
        {
            string[] usage = await rig.RunAsync(line);
            Assert.StartsWith(Prefix + "usage: account create", Assert.Single(usage), StringComparison.Ordinal);
        }

        AssertVerifier((await rig.Accounts.FindByUsernameAsync("BOB"))!, "pw");
    }

    private static async Task SeedAccountAsync(Rig rig, string name, int id, params (int Realm, int Chars)[] counts)
    {
        await rig.Accounts.CreateAsync(new Account { Id = id, Username = name, Salt = new byte[32], Verifier = new byte[32] });
        foreach ((int realm, int chars) in counts)
        {
            await rig.Counts.SetCountAsync(realm, id, chars);
        }
    }

    [Fact]
    public async Task RealmConsole_AccountDelete_NeedsTheSecondStep_ThenRemovesOnlyThatAccountAndItsRealmCharacterRows()
    {
        var rig = new Rig();
        await SeedAccountAsync(rig, "ALICE", 5, (1, 0), (2, 0));
        await SeedAccountAsync(rig, "BOB", 6, (1, 2));

        string[] armed = await rig.RunAsync("account delete alice");

        Assert.Contains("permanently deletes account 'ALICE'", Assert.Single(armed), StringComparison.Ordinal);
        Assert.NotNull(await rig.Accounts.FindByUsernameAsync("ALICE"));
        Assert.Equal(2, rig.Counts.RowsOf(5));

        Assert.Equal([Prefix + "deleted account 'ALICE'"], await rig.RunAsync("account delete alice confirm"));

        Assert.Null(await rig.Accounts.FindByUsernameAsync("ALICE"));
        Assert.Equal(0, rig.Counts.RowsOf(5));
        Assert.NotNull(await rig.Accounts.FindByUsernameAsync("BOB"));
        Assert.Equal(1, rig.Counts.RowsOf(6));
        Assert.Equal(2, (await rig.Counts.GetCountsAsync(6))[1]);
    }

    [Fact]
    public async Task RealmConsole_AccountDelete_ConfirmWithoutArming_ForAnotherName_OrAfterTheWindow_DeletesNothing()
    {
        var rig = new Rig();
        await SeedAccountAsync(rig, "ALICE", 5, (1, 0));
        await SeedAccountAsync(rig, "BOB", 6, (1, 0));

        Assert.Contains("no pending deletion of 'ALICE'", Assert.Single(await rig.RunAsync("account delete alice confirm")), StringComparison.Ordinal);

        await rig.RunAsync("account delete alice");
        Assert.Contains("no pending deletion of 'BOB'", Assert.Single(await rig.RunAsync("account delete bob confirm")), StringComparison.Ordinal);

        // The failed confirm for another name disarmed the pending delete as well.
        Assert.Contains("no pending deletion of 'ALICE'", Assert.Single(await rig.RunAsync("account delete alice confirm")), StringComparison.Ordinal);

        await rig.RunAsync("account delete alice");
        rig.Time.Now += RealmConsoleCommands.DeleteConfirmWindow + TimeSpan.FromSeconds(1);
        Assert.Contains("no pending deletion of 'ALICE'", Assert.Single(await rig.RunAsync("account delete alice confirm")), StringComparison.Ordinal);

        Assert.NotNull(await rig.Accounts.FindByUsernameAsync("ALICE"));
        Assert.NotNull(await rig.Accounts.FindByUsernameAsync("BOB"));
        Assert.Equal(1, rig.Counts.RowsOf(5));
        Assert.Equal(1, rig.Counts.RowsOf(6));
    }

    [Fact]
    public async Task RealmConsole_AccountDelete_IsRefusedWhileTheAccountHasCharacters_AndAnUnknownAccountIsReported()
    {
        var rig = new Rig();
        await SeedAccountAsync(rig, "ALICE", 5, (1, 2), (2, 1));

        Assert.Contains("still has 3 character(s)", Assert.Single(await rig.RunAsync("account delete alice")), StringComparison.Ordinal);
        Assert.Contains("still has 3 character(s)", Assert.Single(await rig.RunAsync("account delete alice confirm")), StringComparison.Ordinal);
        Assert.NotNull(await rig.Accounts.FindByUsernameAsync("ALICE"));
        Assert.Equal(2, rig.Counts.RowsOf(5));

        Assert.Equal([Prefix + "account 'NOBODY' does not exist"], await rig.RunAsync("account delete nobody"));
        Assert.StartsWith(Prefix + "usage: account create", Assert.Single(await rig.RunAsync("account delete alice maybe")), StringComparison.Ordinal);
    }

    private static void AssertVerifier(Account account, string password)
    {
        // The stored verifier must be the SRP6 verifier of (salt, USERNAME, password) that the logon handshake will check.
        byte[] expected = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(account.Salt, account.Username, password), WowSrp6.KeyLength);
        Assert.Equal(expected, account.Verifier);
        Assert.Equal(WowSrp6.SaltLength, account.Salt.Length);
    }

    [Fact]
    public async Task RealmConsole_UnknownCommand_PrintsHelp()
    {
        var rig = new Rig();

        string[] lines = await rig.RunAsync("frobnicate now");

        Assert.Equal(Prefix + "unknown command 'frobnicate'", lines[0]);
        Assert.Equal(Prefix + "commands:", lines[1]);
        string help = string.Join("\n", lines);
        foreach (string word in new[] { "help", "info", "realmlist", "online", "account create", "account set-password", "account set-gmlevel", "quit" })
        {
            Assert.Contains(word, help, StringComparison.Ordinal);
        }

        Assert.Equal(lines[1..], await rig.RunAsync(".help"));
    }

    [Theory]
    [InlineData("quit")]
    [InlineData("exit")]
    [InlineData(".shutdown")]
    public async Task RealmConsole_Quit_StopsTheHostOnce(string word)
    {
        var rig = new Rig();

        string[] lines = await rig.RunAsync(word);

        Assert.Equal([Prefix + "stopping the realm daemon"], lines);
        Assert.Equal(1, rig.Lifetime.Stops);
    }

    [Fact]
    public async Task RealmConsole_BlankLine_DoesNothing()
    {
        var rig = new Rig();

        Assert.Empty(await rig.RunAsync("   "));
        Assert.Empty(await rig.RunAsync("."));
    }

    [Fact]
    public async Task RealmConsole_AccountPassword_IsRedactedInTheLog()
    {
        var provider = new CapturingLoggerProvider();
        using ILoggerFactory loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        var rig = new Rig();
        var reader = new ScriptedReader();
        var output = new StringWriter();
        var console = NewConsole(rig, new ConsoleInputSource(reader, output, IsInputRedirected: false, IsUserInteractive: true), loggers);
        await console.StartAsync(CancellationToken.None);
        try
        {
            reader.Post("account create Carol hunter2");
            reader.Post("account set-password Carol correcthorse");
            reader.Post("account set-gmlevel Carol 1");

            await WaitAsync(() => provider.Entries.Count(e => e.Message.StartsWith("Realm console command:", StringComparison.Ordinal)) == 3, "three logged commands");
            await WaitAsync(() => output.ToString().Contains("is now Moderator", StringComparison.Ordinal), "the last command ran");
        }
        finally
        {
            reader.Post(null);
            await console.StopAsync(CancellationToken.None);
        }

        string[] logged = [.. provider.Entries.Where(e => e.Message.StartsWith("Realm console command:", StringComparison.Ordinal)).Select(e => e.Message)];
        Assert.Equal(
            [
                "Realm console command: account create Carol ***",
                "Realm console command: account set-password Carol ***",
                "Realm console command: account set-gmlevel Carol 1",
            ],
            logged);
        Assert.DoesNotContain(provider.Entries, e => e.Message.Contains("hunter2", StringComparison.Ordinal) || e.Message.Contains("correcthorse", StringComparison.Ordinal));
        Assert.DoesNotContain("hunter2", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("correcthorse", output.ToString(), StringComparison.Ordinal);
        AssertVerifier((await rig.Accounts.FindByUsernameAsync("CAROL"))!, "correcthorse");
    }

    [Fact]
    public async Task RealmConsole_RedirectedInput_StartsNoReaderThread()
    {
        var rig = new Rig();
        var reader = new ScriptedReader();
        var output = new StringWriter();
        var console = NewConsole(rig, new ConsoleInputSource(reader, output, IsInputRedirected: true, IsUserInteractive: true));

        await console.StartAsync(CancellationToken.None);

        Assert.Null(console.ReaderThread);
        await Task.Delay(50);
        Assert.Equal(0, reader.Reads);
        Assert.Equal(string.Empty, output.ToString());
        await console.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(true, true)]   // Realm:Console:Enabled=false
    [InlineData(false, false)] // a Windows service: no interactive session
    public async Task RealmConsole_DisabledOrNotInteractive_StartsNoReaderThread(bool disabled, bool interactive)
    {
        var rig = new Rig();
        var reader = new ScriptedReader();
        var console = NewConsole(
            rig, new ConsoleInputSource(reader, new StringWriter(), IsInputRedirected: false, IsUserInteractive: interactive), enabled: !disabled);

        await console.StartAsync(CancellationToken.None);

        Assert.Null(console.ReaderThread);
        await Task.Delay(50);
        Assert.Equal(0, reader.Reads);
        await console.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RealmConsole_EndOfInput_EndsTheReader()
    {
        var rig = new Rig();
        var reader = new ScriptedReader();
        var console = NewConsole(rig, new ConsoleInputSource(reader, new StringWriter(), IsInputRedirected: false, IsUserInteractive: true));
        await console.StartAsync(CancellationToken.None);
        Thread thread = Assert.IsType<Thread>(console.ReaderThread);
        Assert.True(thread.IsBackground);

        reader.Post(null);

        Assert.True(await Task.Run(() => thread.Join(Bound)), "the reader must end at end of input");
        Assert.Equal(0, rig.Lifetime.Stops); // end of input never stops the realm
        await console.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RealmConsole_Stop_DoesNotBlockOnAPendingRead()
    {
        var rig = new Rig();
        var reader = new ScriptedReader();
        var console = NewConsole(rig, new ConsoleInputSource(reader, new StringWriter(), IsInputRedirected: false, IsUserInteractive: true));
        await console.StartAsync(CancellationToken.None);
        await WaitAsync(() => reader.Reads == 1, "the reader blocks in ReadLine");
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await console.StopAsync(CancellationToken.None);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"StopAsync took {stopwatch.Elapsed}");
        }
        finally
        {
            reader.Post(null);
        }
    }

    [Fact]
    public async Task RealmConsole_TypedQuit_ThroughTheReader_StopsTheHost()
    {
        var rig = new Rig();
        var reader = new ScriptedReader();
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var console = NewConsole(rig, new ConsoleInputSource(reader, output, IsInputRedirected: false, IsUserInteractive: true));
        await console.StartAsync(CancellationToken.None);
        try
        {
            reader.Post("  quit  ");

            await WaitAsync(() => rig.Lifetime.Stops == 1, "the typed quit stops the host");
        }
        finally
        {
            reader.Post(null);
            await console.StopAsync(CancellationToken.None);
        }

        Assert.Contains(Prefix + "stopping the realm daemon", output.ToString(), StringComparison.Ordinal);
    }
}
