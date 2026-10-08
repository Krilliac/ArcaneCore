using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Gm.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Gm.Audit.TicketTests;

namespace ArcaneCore.World.Tests.Gm.Audit;

/// <summary>The native <c>.arcane</c> operator commands: bancheck, mutes, gmlog and queues.</summary>
public sealed class ArcaneCommandTests
{
    private sealed class Clock : TimeProvider
    {
        private readonly DateTimeOffset _start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        private long _advanced;

        internal void Advance(long seconds) => Interlocked.Add(ref _advanced, seconds);

        public override DateTimeOffset GetUtcNow() => _start.AddSeconds(Interlocked.Read(ref _advanced));
    }

    private static WorldTestHost Start(Clock? clock = null, Dictionary<string, string?>? configuration = null) => WorldTestHost.Start(configureServices: services =>
    {
        services.AddSingleton<TimeProvider>(clock ?? new Clock());
        if (configuration is not null)
        {
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(configuration).Build());
        }
    });

    private static async Task<WorldTestClient> Staff(WorldTestHost host, AccountSecurity security, string account = "STAFF", string name = "Staffer")
    {
        WorldTestClient client = await host.EnterWorldAsync(account, name, security);
        await client.CollectAsync();
        return client;
    }

    [Fact]
    public async Task Levels_BancheckAndMutesNeedGameMaster_GmlogAndQueuesNeedAdministrator()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient mod = await Staff(host, AccountSecurity.Moderator, "MOD", "Moddy");
        await using WorldTestClient gm = await Staff(host, AccountSecurity.GameMaster, "GMA", "Gmaaa");

        foreach (string command in new[] { ".arcane mutes", ".arcane bancheck ip 1.2.3.4", ".arcane gmlog", ".arcane queues" })
        {
            Assert.Equal("This command is not available to you.", (await LinesAsync(mod, command))[0]);
        }

        foreach (string command in new[] { ".arcane gmlog", ".arcane queues" })
        {
            Assert.Equal("This command is not available to you.", (await LinesAsync(gm, command))[0]);
        }

        Assert.Equal("No chat mutes are in force.", (await LinesAsync(gm, ".arcane mutes"))[0]);
        Assert.Equal("IP 1.2.3.4: not blocked", (await LinesAsync(gm, ".arcane bancheck ip 1.2.3.4"))[0]);
    }

    [Fact]
    public async Task Arcane_WithoutASubcommand_ShowsTheSubcommands()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient admin = await Staff(host, AccountSecurity.Administrator);

        string[] lines = await LinesAsync(admin, ".arcane");

        Assert.Equal("There is no such subcommand", lines[0]);
        Assert.Contains("    bancheck", lines);
        Assert.Contains("    mutes", lines);
        Assert.Contains("    gmlog", lines);
        Assert.Contains("    queues", lines);
    }

    // ---- bancheck ----

    [Fact]
    public async Task BanCheck_Account_ReportsAnActiveBan_ABanEnded_AndAStatusOverride()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await Staff(host, AccountSecurity.GameMaster, "GMA", "Gmaaa");
        await host.AddAccountAsync("CLEAN");
        await host.AddAccountAsync("BANNED");
        await host.AddAccountAsync("PERMA");
        await host.AddAccountAsync("SUSPENDED");
        int banned = (await host.Accounts.FindByUsernameAsync("BANNED"))!.Id;
        int perma = (await host.Accounts.FindByUsernameAsync("PERMA"))!.Id;
        await host.Bans.BanAccountAsync(new BanRequest(banned, 3600, "botting", "Admin"));
        await host.Bans.BanAccountAsync(new BanRequest(perma, 0, "cheating", "Admin"));
        await host.Accounts.SetStatusAsync("SUSPENDED", AccountStatus.Suspended);

        Assert.Equal(["Account CLEAN (id 2): not blocked", "Chat: enabled"], await LinesAsync(gm, ".arcane bancheck account clean"));

        string[] lines = await LinesAsync(gm, ".arcane bancheck account banned");
        Assert.Equal($"Account BANNED (id {banned}): BLOCKED from logging in", lines[0]);
        Assert.Matches(@"^Ban: until \d{4}-\d\d-\d\d \d\d:\d\d:\d\d UTC, set \d{4}-\d\d-\d\d \d\d:\d\d:\d\d UTC by Admin: botting$", lines[1]);
        Assert.Equal("Chat: enabled", lines[2]);

        lines = await LinesAsync(gm, ".arcane bancheck account perma");
        Assert.Matches(@"^Ban: permanent, set \d{4}-\d\d-\d\d \d\d:\d\d:\d\d UTC by Admin: cheating$", lines[1]);

        lines = await LinesAsync(gm, ".arcane bancheck account suspended");
        Assert.Contains("BLOCKED from logging in", lines[0], StringComparison.Ordinal);
        Assert.Equal("Status override: Suspended (set on the account itself, not a ban row)", lines[1]);

        await host.Bans.UnbanAccountAsync(banned, "Admin", "appeal");
        Assert.Equal($"Account BANNED (id {banned}): not blocked", (await LinesAsync(gm, ".arcane bancheck account banned"))[0]);
    }

    [Fact]
    public async Task BanCheck_Character_ResolvesTheAccountOfTheName_AndShowsItsMute()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await Staff(host, AccountSecurity.GameMaster, "GMA", "Gmaaa");
        await using WorldTestClient target = await host.EnterWorldAsync("TARGET", "Targetone");
        int account = (await host.Accounts.FindByUsernameAsync("TARGET"))!.Id;
        AuditOf(host).Mute(account, 600, "Gmaaa", AccountSecurity.GameMaster, "spam");
        await gm.CollectAsync();

        string[] lines = await LinesAsync(gm, ".arcane bancheck character targetone");

        Assert.Equal([$"Account TARGET (id {account}): not blocked", "Chat: muted for 10 Minutes by Gmaaa (spam)"], lines);
    }

    [Fact]
    public async Task BanCheck_Ip_ReportsABanOrNone()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await Staff(host, AccountSecurity.GameMaster, "GMA", "Gmaaa");
        await host.Bans.BanIpAsync(new IpBanRequest("10.0.0.7", 3600, "bots", "Admin"));   // the in-memory store records its own author and reason ("ext")
        await host.Bans.BanIpAsync(new IpBanRequest("10.0.0.8", 0, "worse bots", "Admin"));

        Assert.Matches(@"^IP 10\.0\.0\.7: BLOCKED until \d{4}-\d\d-\d\d \d\d:\d\d:\d\d UTC, set \d{4}-\d\d-\d\d \d\d:\d\d:\d\d UTC by ext: ext$", (await LinesAsync(gm, ".arcane bancheck ip 10.0.0.7"))[0]);
        Assert.Matches(@"^IP 10\.0\.0\.8: BLOCKED permanently, set .* by ext: ext$", (await LinesAsync(gm, ".arcane bancheck ip 10.0.0.8"))[0]);
        Assert.Equal("IP 10.0.0.9: not blocked", (await LinesAsync(gm, ".arcane bancheck ip 10.0.0.9"))[0]);
    }

    [Fact]
    public async Task BanCheck_UnknownTargets_AndBadSyntax()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await Staff(host, AccountSecurity.GameMaster, "GMA", "Gmaaa");

        Assert.Equal("Account NOBODY does not exist.", (await LinesAsync(gm, ".arcane bancheck account nobody"))[0]);
        Assert.Equal("Player not found!", (await LinesAsync(gm, ".arcane bancheck character nobodyhere"))[0]);
        foreach (string bad in new[] { ".arcane bancheck", ".arcane bancheck account", ".arcane bancheck ip notanip", ".arcane bancheck realm x", ".arcane bancheck account a extra" })
        {
            Assert.StartsWith("Syntax: .arcane bancheck", (await LinesAsync(gm, bad))[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task BanCheck_ABanStoreFailure_IsAnsweredNotThrown()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await Staff(host, AccountSecurity.GameMaster, "GMA", "Gmaaa");
        await host.AddAccountAsync("TARGET");
        host.Bans.FailWith = new InvalidOperationException("ban database down");
        host.ExpectSessionFaults = true;

        Assert.Equal("The ban database is unavailable; see the server log.", (await LinesAsync(gm, ".arcane bancheck account target"))[0]);
        Assert.Equal("The ban database is unavailable; see the server log.", (await LinesAsync(gm, ".arcane bancheck ip 1.2.3.4"))[0]);
    }

    // ---- mutes ----

    [Fact]
    public async Task Mutes_ListsTheMutesInForce_WithWhoAndHowLong()
    {
        var clock = new Clock();
        await using WorldTestHost host = Start(clock);
        await using WorldTestClient gm = await Staff(host, AccountSecurity.GameMaster, "GMA", "Gmaaa");
        await using WorldTestClient target = await host.EnterWorldAsync("TARGET", "Targetone");
        int account = (await host.Accounts.FindByUsernameAsync("TARGET"))!.Id;
        GmAuditFeature audit = AuditOf(host);
        audit.Mute(account, 3600, "Gmaaa", AccountSecurity.GameMaster, "ads");
        audit.Mute(9999, 120, "Someone", AccountSecurity.Moderator, "offline one");
        audit.Mute(8888, 5, "Someone", AccountSecurity.Moderator, "about to end");
        clock.Advance(10);
        await gm.CollectAsync();

        Assert.Equal(
        [
            "Chat mutes in force: 2",
            $"Account {account} (Targetone): 59 Minutes 50 Seconds left, by Gmaaa: ads",
            "Account 9999 (offline): 1 Minute 50 Seconds left, by Someone: offline one",
        ], await LinesAsync(gm, ".arcane mutes"));
        Assert.StartsWith("Syntax: .arcane mutes", (await LinesAsync(gm, ".arcane mutes now"))[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mutes_IsCapped()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await Staff(host, AccountSecurity.GameMaster, "GMA", "Gmaaa");
        for (int i = 1; i <= ArcaneCommands.MaxMutesListed + 3; i++)
        {
            AuditOf(host).Mute(5000 + i, 600, "Someone", AccountSecurity.Moderator, "r");
        }

        string[] lines = await LinesAsync(gm, ".arcane mutes");

        Assert.Equal(ArcaneCommands.MaxMutesListed + 2, lines.Length);
        Assert.Equal($"Chat mutes in force: {ArcaneCommands.MaxMutesListed + 3}", lines[0]);
        Assert.Equal("... and 3 more (the list is capped).", lines[^1]);
    }

    // ---- gmlog ----

    [Fact]
    public async Task GmLog_TailsTheAuditedCommands_OldestFirst_AndSkipsPlayerLevelOnes()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient admin = await Staff(host, AccountSecurity.Administrator, "ADM", "Admiral");
        await using WorldTestClient mod = await Staff(host, AccountSecurity.Moderator, "MOD", "Moddy");
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".commands");        // level 0: never audited
        await mod.SendChatAsync(ChatType.Say, Language.Common, ".gmannounce first");
        await host.WaitForWorldAsync(() => AuditOf(host).Tail(10).Any(entry => entry.Command == "gmannounce first"), "first announcement audited");
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".mute nobody 5m");
        await host.WaitForWorldAsync(() => AuditOf(host).Tail(10).Any(entry => entry.Command == "mute nobody 5m"), "mute command audited");

        string[] lines = await LinesAsync(admin, ".arcane gmlog");

        Assert.Equal(3, lines.Length);
        Assert.Matches(@"^\[\d{4}-\d\d-\d\d \d\d:\d\d:\d\d UTC\] Moddy \(account 2\): \.gmannounce first$", lines[0]);
        Assert.Matches(@"^\[.*\] Admiral \(account 1\): \.mute nobody 5m$", lines[1]);
        Assert.Matches(@"^\[.*\] Admiral \(account 1\): \.arcane gmlog$", lines[2]);   // the audit line is written before the command runs

        lines = await LinesAsync(admin, ".arcane gmlog 2");
        Assert.Equal(2, lines.Length);
        Assert.EndsWith(".arcane gmlog 2", lines[1], StringComparison.Ordinal);
        Assert.EndsWith(".arcane gmlog 1000", (await LinesAsync(admin, ".arcane gmlog 1000"))[^1], StringComparison.Ordinal);   // capped, not refused
    }

    [Fact]
    public async Task GmLog_KeepsOnlyTheNewestLines_UpToTheConfiguredSize()
    {
        await using WorldTestHost host = Start(configuration: new() { ["World:GmCommands:AuditTailSize"] = "3" });
        await using WorldTestClient admin = await Staff(host, AccountSecurity.Administrator);
        for (int i = 1; i <= 5; i++)
        {
            await admin.SendChatAsync(ChatType.Say, Language.Common, $".gmannounce n{i}");
        }

        await admin.CollectAsync();
        string[] lines = await LinesAsync(admin, ".arcane gmlog");

        Assert.Equal(3, lines.Length);
        Assert.EndsWith(".gmannounce n4", lines[0], StringComparison.Ordinal);   // n1..n3 were pushed out
        Assert.EndsWith(".gmannounce n5", lines[1], StringComparison.Ordinal);
        Assert.EndsWith(".arcane gmlog", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task GmLog_WhenTheTailIsOff_SaysSo_AndBadCountsShowTheSyntax()
    {
        await using WorldTestHost host = Start(configuration: new() { ["World:GmCommands:AuditTailSize"] = "0" });
        await using WorldTestClient admin = await Staff(host, AccountSecurity.Administrator);

        Assert.Equal("The audit tail is off (World:GmCommands:AuditTailSize is 0).", (await LinesAsync(admin, ".arcane gmlog"))[0]);
        foreach (string bad in new[] { ".arcane gmlog 0", ".arcane gmlog abc", ".arcane gmlog 5 6", ".arcane gmlog -1" })
        {
            Assert.StartsWith("Syntax: .arcane gmlog", (await LinesAsync(admin, bad))[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GmLog_WithLoggingOff_RecordsNothing()
    {
        await using WorldTestHost host = Start(configuration: new() { ["World:GmCommands:LogCommands"] = "false" });
        await using WorldTestClient admin = await Staff(host, AccountSecurity.Administrator);
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".gmannounce hello");
        await admin.CollectAsync();

        Assert.Equal("No audited commands.", (await LinesAsync(admin, ".arcane gmlog"))[0]);
    }

    // ---- queues ----

    [Fact]
    public async Task Queues_ReportsEveryQueue_AndFlagsRetainedWrites()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient admin = await Staff(host, AccountSecurity.Administrator);
        await using WorldTestClient target = await host.EnterWorldAsync("TARGET", "Targetone");
        await admin.CollectAsync();

        string[] lines = await LinesAsync(admin, ".arcane queues");

        Assert.Contains("gm audit: 0 pending", lines);
        Assert.All(lines, l => Assert.Contains(" pending", l, StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("character saves: ", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("RETAINED", StringComparison.Ordinal));

        StoreOf(host).FailNext(3);
        AuditOf(host).Mute(2, 600, "Admiral", AccountSecurity.Administrator, "r");
        await AuditOf(host).Writes.FlushAsync();

        Assert.Contains("gm audit: 0 pending, RETAINED after failed writes: mute:2", await LinesAsync(admin, ".arcane queues"));

        await AuditOf(host).Writes.RetryRetainedAsync();
        Assert.Contains("gm audit: 0 pending", await LinesAsync(admin, ".arcane queues"));
        Assert.StartsWith("Syntax: .arcane queues", (await LinesAsync(admin, ".arcane queues now"))[0], StringComparison.Ordinal);
    }
}
