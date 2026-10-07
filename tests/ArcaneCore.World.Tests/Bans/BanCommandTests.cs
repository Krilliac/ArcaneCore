using ArcaneCore.Game;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Bans;
using Xunit;

namespace ArcaneCore.World.Tests.Bans;

/// <summary>
/// The .ban / .unban / .baninfo / .banlist commands end to end against the real world host, with the retail
/// texts (mangos_string 408-428) and mechanics (vmangos AccountCommands.cpp:516-1010, World.cpp:2461-2665). The
/// store call is asynchronous; the reply arrives when it completes, like retail's BanQueryHolder.
/// </summary>
public sealed class BanCommandTests
{
    [Fact]
    public async Task BanAccount_Temporary_RepliesWithTheRetailText_KicksTheVictim_AndWritesTheRow()
    {
        await using var host = WorldTestHost.Start(banOptions: new BanOptions { RealmId = 7 });
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await using WorldTestClient victim = await host.EnterWorldAsync("VICTIM", "Victim");
        await Drain(admin, victim);

        Assert.Equal("VICTIM is banned for 1d. Reason: spam.", await CommandAsync(admin, ".ban account victim 1d spam"));

        Assert.True(await victim.IsClosedByServerAsync());
        int id = (await host.Accounts.FindByUsernameAsync("VICTIM"))!.Id;
        AccountBanRecord row = (await host.Bans.GetActiveAccountBanAsync(id))!;
        Assert.False(row.IsPermanent);
        Assert.Equal(86400, row.UnbanDate - row.BanDate);
        Assert.Equal("spam", row.Reason);
        Assert.Equal("Admin", row.BannedBy);
        Assert.Equal(7, row.Realm); // Bans:RealmId
    }

    [Theory]
    [InlineData("0")]
    [InlineData("forever")] // retail quirk: an unparseable duration is a PERMANENT ban
    [InlineData("5")]       // digits without a unit contribute nothing
    public async Task BanAccount_ZeroOrUnparseableDuration_IsPermanent(string duration)
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await host.AddAccountAsync("TARGET");
        await Drain(admin);

        Assert.Equal("TARGET is banned permanently for cheating.", await CommandAsync(admin, $".ban account target {duration} cheating"));

        int id = (await host.Accounts.FindByUsernameAsync("TARGET"))!.Id;
        Assert.True((await host.Bans.GetActiveAccountBanAsync(id))!.IsPermanent);
    }

    [Fact]
    public async Task RejectUnparseableDuration_TurnsATypoIntoASyntaxError()
    {
        await using var host = WorldTestHost.Start(banOptions: new BanOptions { RejectUnparseableDuration = true });
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await host.AddAccountAsync("TARGET");
        await Drain(admin);

        Assert.StartsWith("Syntax: .ban account", await CommandAsync(admin, ".ban account target forever x"));
        Assert.Equal("TARGET is banned for 1h. Reason: x.", await CommandAsync(admin, ".ban account target 1h x"));
    }

    [Fact]
    public async Task BanAccount_UnknownAccount_AndMissingArguments()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await Drain(admin);

        Assert.Equal("account NOBODY not found", await CommandAsync(admin, ".ban account nobody 1d spam"));
        Assert.StartsWith("Syntax: .ban account", await CommandAsync(admin, ".ban account nobody 1d")); // no reason
        Assert.Equal("Account not exist: ABCDEFGHIJKLMNOPQ", await CommandAsync(admin, ".ban account abcdefghijklmnopq 1d x")); // normalizeString fails
    }

    [Fact]
    public async Task BanAccount_TheReasonIsOneToken_UnlessQuoted()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await host.AddAccountAsync("ONE");
        await host.AddAccountAsync("TWO");
        await Drain(admin);

        Assert.Equal("ONE is banned for 1m. Reason: cheating.", await CommandAsync(admin, ".ban account one 1m cheating and more"));
        Assert.Equal("TWO is banned for 1m. Reason: two words.", await CommandAsync(admin, ".ban account two 1m 'two words'"));
    }

    [Fact]
    public async Task BanningYourOwnAccount_BansButDoesNotKickTheInvoker()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("SELFBAN", "Selfban", AccountSecurity.Administrator);
        await Drain(admin);

        Assert.Equal("SELFBAN is banned for 1h. Reason: oops.", await CommandAsync(admin, ".ban account selfban 1h oops"));

        int id = (await host.Accounts.FindByUsernameAsync("SELFBAN"))!.Id;
        Assert.NotNull(await host.Bans.GetActiveAccountBanAsync(id));
        Assert.NotNull(host.Registry.Find(id)); // World.cpp:2552-2553: the author is not kicked
    }

    [Fact]
    public async Task BanCharacter_ResolvesOfflineCharacters_ThroughTheDirectory_AndBansTheOwningAccount()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        WorldTestClient sleeper = await host.EnterWorldAsync("SLEEPER", "Sleeper");
        await sleeper.DisposeAsync();
        await WorldTestHost.WaitForAsync(() => host.World.FindOnlinePlayer("Sleeper") is null, "Sleeper to go offline");
        await Drain(admin);

        Assert.Equal("Sleeper is banned for 2h. Reason: x.", await CommandAsync(admin, ".ban character sleeper 2h x"));
        Assert.Equal("character Ghost not found", await CommandAsync(admin, ".ban character ghost 2h x"));

        int id = (await host.Accounts.FindByUsernameAsync("SLEEPER"))!.Id;
        Assert.NotNull(await host.Bans.GetActiveAccountBanAsync(id));
    }

    [Fact]
    public async Task BanIp_KicksSessionsFromThatAddress_AndRejectsAMalformedAddress()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await using WorldTestClient other = await host.EnterWorldAsync("OTHER", "Other");
        await Drain(admin, other);

        Assert.StartsWith("Syntax: .ban ip", await CommandAsync(admin, ".ban ip not-an-ip 1d x"));
        Assert.Equal("127.0.0.1 is banned permanently for lan.", await CommandAsync(admin, ".ban ip 127.0.0.1 0 lan"));

        // Both clients connect from the loopback address; the invoker's own account is spared, the other is kicked.
        Assert.True(await other.IsClosedByServerAsync());
        Assert.NotNull(await host.Bans.GetActiveIpBanAsync("127.0.0.1"));
        int adminId = (await host.Accounts.FindByUsernameAsync("ADMIN"))!.Id;
        Assert.NotNull(host.Registry.Find(adminId));
    }

    [Fact]
    public async Task Unban_LiftsTheBan_AndTheAccountCanAuthenticateAgain()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        byte[] key = await host.AddAccountAsync("LIFTED");
        await Drain(admin);
        await CommandAsync(admin, ".ban account lifted 1d x");

        Assert.StartsWith("Syntax: .unban account", await CommandAsync(admin, ".unban account lifted")); // the message is required
        Assert.Equal("LIFTED unbanned.", await CommandAsync(admin, ".unban account lifted appealed"));
        Assert.Equal("There was an error removing the ban on NOBODY.", await CommandAsync(admin, ".unban account nobody m"));

        await using WorldTestClient again = await host.ConnectAsync();
        await again.AuthenticateAsync("LIFTED", key); // asserts AUTH_OK
        int id = (await host.Accounts.FindByUsernameAsync("LIFTED"))!.Id;
        Assert.Contains(await host.Bans.GetHistoryAsync(id), r => r.Reason == "UNBAN: appealed" && !r.Active);
    }

    [Fact]
    public async Task UnbanIp_DeletesTheRow_AndAlwaysReportsSuccess()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await Drain(admin);
        host.Bans.AddIpRow("10.0.0.9", 1, 1);

        Assert.Equal("10.0.0.9 unbanned.", await CommandAsync(admin, ".unban ip 10.0.0.9 done"));
        Assert.Null(await host.Bans.GetActiveIpBanAsync("10.0.0.9"));
        Assert.Equal("10.0.0.9 unbanned.", await CommandAsync(admin, ".unban ip 10.0.0.9 again"));
    }

    [Fact]
    public async Task BanInfo_ShowsTheHistory_OrThatTheAccountWasNeverBanned()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await host.AddAccountAsync("CLEAN");
        await host.AddAccountAsync("HISTORY");
        await Drain(admin);

        Assert.Equal("Account CLEAN has never been banned", await CommandAsync(admin, ".baninfo account clean"));
        Assert.Equal("Account not exist: GHOST", await CommandAsync(admin, ".baninfo account ghost"));

        await CommandAsync(admin, ".ban account history 1d noisy");
        await CommandAsync(admin, ".unban account history sorry");
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".baninfo account history");
        string[] lines = await ReadLinesAsync(admin, 3);

        Assert.Equal("Ban history for account HISTORY:", lines[0]);
        Assert.Matches(@"^Ban Date: \d{4}-\d\d-\d\d \d\d:\d\d:\d\d Bantime: 1d Still active: No  Reason: noisy Set by: Admin \(NoRealm\)$", lines[1]);
        Assert.Matches(@"^Ban Date: \S+ \S+ Bantime: 1s Still active: (Yes|No)  Reason: UNBAN: sorry Set by: Admin \(NoRealm\)$", lines[2]);

        int id = (await host.Accounts.FindByUsernameAsync("HISTORY"))!.Id;
        await admin.SendChatAsync(ChatType.Say, Language.Common, $".baninfo account {id}"); // by id
        Assert.Equal("Ban history for account HISTORY:", (await ReadLinesAsync(admin, 3))[0]);
    }

    [Fact]
    public async Task BanInfoIp_ShowsTheEntry_OrThatThereIsNone()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await Drain(admin);

        Assert.Equal("There is no such IP in banlist.", await CommandAsync(admin, ".baninfo ip 10.9.8.7"));
        host.Bans.AddIpRow("10.9.8.7", 1, 1);
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".baninfo ip 10.9.8.7");
        string[] lines = await ReadLinesAsync(admin, 6);
        Assert.Equal("IP: 10.9.8.7", lines[0]);
        Assert.StartsWith("Ban Date: ", lines[1]);
        Assert.Equal("Unban Date: Never", lines[2]);
        Assert.Equal("Remaining: Inf.", lines[3]);
        Assert.Equal("Reason: ext", lines[4]);
        Assert.Equal("Set by: ext", lines[5]);
    }

    [Fact] // Security (wave-3 scan finding 5): a list reply is bounded by Bans:MaxListedEntries
    public async Task BanList_StopsAtMaxListedEntries_AndSaysSo()
    {
        await using var host = WorldTestHost.Start(banOptions: new BanOptions { MaxListedEntries = 2 });
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await Drain(admin);
        host.Bans.AddIpRow("1.2.3.1", 100, 100);
        host.Bans.AddIpRow("1.2.3.2", 100, 100);
        host.Bans.AddIpRow("1.2.3.3", 100, 100);

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".banlist ip 1.2.3");
        Assert.Equal(
            ["The following IPs match your pattern:", "1.2.3.1", "1.2.3.2", "... more entries exist; only the first 2 are shown."],
            await ReadLinesAsync(admin, 4));
    }

    [Fact] // the cap bounds the history work of .banlist character, not only the printed lines
    public async Task BanListCharacter_StopsQueryingOnceTheCapIsExceeded()
    {
        await using var host = WorldTestHost.Start(banOptions: new BanOptions { MaxListedEntries = 2 });
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        const int accounts = 450; // three batches of 200 when walked to the end
        for (int i = 0; i < accounts; i++)
        {
            await host.AddAccountAsync($"ACC{i:D3}");
            int id = (await host.Accounts.FindByUsernameAsync($"ACC{i:D3}"))!.Id;
            host.Directory.Add(new CharacterIdentity(1000 + i, id, $"Zed{i:D3}", 1, 0, 1));
            if (i < 3)
            {
                host.Bans.AddAccountRow(id, 100, 100); // only the first three have history, all in the first batch
            }
        }

        await Drain(admin);
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".banlist character zed");
        Assert.Equal(
            ["The following accounts match your query:", "ACC000", "ACC001", "... more entries exist; only the first 2 are shown."],
            await ReadLinesAsync(admin, 4));
        Assert.Equal(1, host.Bans.FindHistoryCalls); // not 3 batches, and never one query per account
    }

    [Fact] // a small listing is identical to before: every account with history, in id order
    public async Task BanListCharacter_UnderTheCap_ListsEveryAccountWithHistoryAcrossBatches()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        for (int i = 0; i < 250; i++)
        {
            await host.AddAccountAsync($"ACC{i:D3}");
            int id = (await host.Accounts.FindByUsernameAsync($"ACC{i:D3}"))!.Id;
            host.Directory.Add(new CharacterIdentity(1000 + i, id, $"Zed{i:D3}", 1, 0, 1));
            if (i is 5 or 230)
            {
                host.Bans.AddAccountRow(id, 100, 200, active: false);
            }
        }

        await Drain(admin);
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".banlist character zed");
        Assert.Equal(["The following accounts match your query:", "ACC005", "ACC230"], await ReadLinesAsync(admin, 3));
        Assert.Equal(2, host.Bans.FindHistoryCalls);
    }

    [Fact]
    public async Task BanIp_AnAddressAlreadyBanned_ReportsItInsteadOfBanned()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await Drain(admin);

        Assert.Equal("10.1.1.1 is banned permanently for first.", await CommandAsync(admin, ".ban ip 10.1.1.1 0 first"));
        Assert.Equal("10.1.1.1 is already banned; the existing ban is unchanged.", await CommandAsync(admin, ".ban ip 10.1.1.1 1d second"));
        Assert.True((await host.Bans.GetActiveIpBanAsync("10.1.1.1"))!.IsPermanent); // the 1d retry did not replace it
    }

    [Fact] // the default is a deliberate GM-visible deviation from retail (0 = unbounded, as retail): pin the number and the unconfigured path
    public async Task BanList_WithTheDefaultOptions_StopsAt200_AndTheDefaultIsPinned()
    {
        Assert.Equal(200, new BanOptions().MaxListedEntries);

        await using var host = WorldTestHost.Start(); // no banOptions: the command falls back to new BanOptions()
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await Drain(admin);
        for (int i = 1; i <= 201; i++)
        {
            host.Bans.AddIpRow($"7.0.0.{i}", 100, 100);
        }

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".banlist ip 7.");
        string[] lines = await ReadLinesAsync(admin, 202);

        Assert.Equal("The following IPs match your pattern:", lines[0]);
        Assert.Equal("7.0.0.1", lines[1]);
        Assert.Equal("7.0.0.200", lines[200]);
        Assert.Equal("... more entries exist; only the first 200 are shown.", lines[201]);
    }

    [Fact]
    public async Task BanList_Account_StopsAtMaxListedEntries_AndSaysSo()
    {
        await using var host = WorldTestHost.Start(banOptions: new BanOptions { MaxListedEntries = 2 });
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        foreach (string name in new[] { "CAPA", "CAPB", "CAPC" })
        {
            await host.AddAccountAsync(name);
            host.Bans.AddAccountRow((await host.Accounts.FindByUsernameAsync(name))!.Id, 100, 100);
        }

        await Drain(admin);
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".banlist account cap");

        Assert.Equal(
            ["The following accounts match your query:", "CAPA", "CAPB", "... more entries exist; only the first 2 are shown."],
            await ReadLinesAsync(admin, 4));
    }

    [Fact]
    public async Task BanList_Character_StopsAtMaxListedEntries_AndSaysSo()
    {
        await using var host = WorldTestHost.Start(banOptions: new BanOptions { MaxListedEntries = 2 });
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        foreach ((string account, string character) in new[] { ("CHARA", "Capone"), ("CHARB", "Capetown"), ("CHARC", "Capsule") })
        {
            WorldTestClient other = await host.EnterWorldAsync(account, character);
            await other.DisposeAsync();
            host.Bans.AddAccountRow((await host.Accounts.FindByUsernameAsync(account))!.Id, 100, 100);
        }

        await Drain(admin);
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".banlist character cap");

        Assert.Equal(
            ["The following accounts match your query:", "CHARA", "CHARB", "... more entries exist; only the first 2 are shown."],
            await ReadLinesAsync(admin, 4));
    }

    [Fact]
    public async Task BanInfo_Account_HistoryStopsAtMaxListedEntries_AndSaysSo()
    {
        await using var host = WorldTestHost.Start(banOptions: new BanOptions { MaxListedEntries = 2 });
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await host.AddAccountAsync("MANYBANS");
        int id = (await host.Accounts.FindByUsernameAsync("MANYBANS"))!.Id;
        host.Bans.AddAccountRow(id, 100, 100, active: false);
        host.Bans.AddAccountRow(id, 200, 200, active: false);
        host.Bans.AddAccountRow(id, 300, 300, active: false);
        await Drain(admin);

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".baninfo account manybans");
        string[] lines = await ReadLinesAsync(admin, 4);

        Assert.Equal("Ban history for account MANYBANS:", lines[0]);
        Assert.StartsWith("Ban Date: ", lines[1]);
        Assert.StartsWith("Ban Date: ", lines[2]);
        Assert.Equal("... more entries exist; only the first 2 are shown.", lines[3]);
    }

    [Fact]
    public async Task BanList_Account_Character_Ip_ListMatchesAndPurgeExpiredIpsFirst()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        WorldTestClient pat = await host.EnterWorldAsync("PATRICK", "Patty");
        await pat.DisposeAsync();
        await host.AddAccountAsync("PETER");
        await Drain(admin);
        await CommandAsync(admin, ".ban account patrick 1d x");
        await CommandAsync(admin, ".ban account peter 1d x");

        Assert.Equal("There is no matching account.", await CommandAsync(admin, ".banlist account zzz"));
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".banlist account pa");
        Assert.Equal(["The following accounts match your query:", "PATRICK"], await ReadLinesAsync(admin, 2));
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".banlist account");
        Assert.Equal(["The following accounts match your query:", "PATRICK", "PETER"], await ReadLinesAsync(admin, 3));

        Assert.Equal("There is no banned account owning a character matching this part.", await CommandAsync(admin, ".banlist character zzz"));
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".banlist character patt");
        Assert.Equal(["The following accounts match your query:", "PATRICK"], await ReadLinesAsync(admin, 2));

        host.Bans.AddIpRow("1.2.3.4", 100, 200);       // expired long ago: purged first
        host.Bans.AddIpRow("1.2.3.5", 100, 100);       // permanent
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".banlist ip 1.2.3");
        Assert.Equal(["The following IPs match your pattern:", "1.2.3.5"], await ReadLinesAsync(admin, 2));
        Assert.Equal("There is no matching IPban.", await CommandAsync(admin, ".banlist ip 9."));
        Assert.True(host.Bans.PurgeCalls >= 1);
    }

    [Fact]
    public async Task SecurityTiers_FollowVmangosChatCpp()
    {
        // vmangos Chat.cpp:170-191, 1022-1024, 1263-1266 mapped onto ArcaneCore's levels: ban account/character and
        // baninfo/banlist ip are GAMEMASTER, baninfo/banlist account/character are TICKETMASTER (Moderator), and
        // ban ip, ban allip and unban are ADMINISTRATOR.
        await using var host = WorldTestHost.Start();
        await using WorldTestClient mod = await host.EnterWorldAsync("MOD", "Moderator", AccountSecurity.Moderator);
        await using WorldTestClient gm = await host.EnterWorldAsync("GM", "Gamemaster", AccountSecurity.GameMaster);
        await host.AddAccountAsync("TARGET");
        await Drain(mod, gm);

        // A command above the invoker's level answers "This command is not available to you." (the GM lane's retail text; hidden-command mode answers "no such command"); either way the verb did not run.
        static bool Refused(string reply) => reply.StartsWith("This command is not available to you", StringComparison.Ordinal) || reply.StartsWith("There is no such command", StringComparison.Ordinal) || reply.StartsWith("There is no such subcommand", StringComparison.Ordinal);
        Assert.True(Refused(await CommandAsync(mod, ".ban account target 1d x")));
        Assert.True(Refused(await CommandAsync(mod, ".baninfo ip 1.2.3.4")));
        Assert.True(Refused(await CommandAsync(mod, ".banlist ip")));
        Assert.True(Refused(await CommandAsync(mod, ".unban account target x")));
        Assert.False(Refused(await CommandAsync(mod, ".baninfo account target")));
        Assert.False(Refused(await CommandAsync(mod, ".banlist account")));

        Assert.True(Refused(await CommandAsync(gm, ".unban account target x")));
        Assert.True(Refused(await CommandAsync(gm, ".ban ip 1.2.3.4 1d x")));
        Assert.False(Refused(await CommandAsync(gm, ".baninfo ip 1.2.3.4")));
        Assert.False(Refused(await CommandAsync(gm, ".banlist ip")));
        Assert.Equal("TARGET is banned for 1d. Reason: x.", await CommandAsync(gm, ".ban account target 1d x"));
    }

    [Fact]
    public async Task AStoreFault_IsLoggedAndAnswered_NotThrownIntoTheWorld()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await host.AddAccountAsync("TARGET");
        await Drain(admin);
        host.Bans.FailWriteWith = new InvalidOperationException("database down");

        Assert.Equal("The ban database is unavailable; see the server log.", await CommandAsync(admin, ".ban account target 1d x"));
        host.Bans.FailWriteWith = null;
        Assert.Equal("TARGET is banned for 1d. Reason: x.", await CommandAsync(admin, ".ban account target 1d x")); // the session survived
    }

    [Fact]
    public async Task BanAccount_RefusesATargetOfEqualOrHigherSecurity_ByDefault()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GM", "Gm", AccountSecurity.GameMaster);
        await host.AddAccountAsync("BOSS", AccountSecurity.Administrator);
        await host.AddAccountAsync("PEER", AccountSecurity.GameMaster);
        await host.AddAccountAsync("PLAIN");
        await Drain(gm);

        Assert.Equal(BanCommandText.TargetSecurityTooHigh, await CommandAsync(gm, ".ban account boss 1d x"));
        Assert.Equal(BanCommandText.TargetSecurityTooHigh, await CommandAsync(gm, ".ban account peer 1d x"));
        Assert.Equal("PLAIN is banned for 1d. Reason: x.", await CommandAsync(gm, ".ban account plain 1d x"));

        Assert.Null(await host.Bans.GetActiveAccountBanAsync((await host.Accounts.FindByUsernameAsync("BOSS"))!.Id));
        Assert.Null(await host.Bans.GetActiveAccountBanAsync((await host.Accounts.FindByUsernameAsync("PEER"))!.Id));
    }

    [Fact]
    public async Task BanCharacter_RefusesATargetOfHigherSecurity_ByDefault()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GM", "Gm", AccountSecurity.GameMaster);
        await using WorldTestClient boss = await host.EnterWorldAsync("BOSS", "Boss", AccountSecurity.Administrator);
        await Drain(gm, boss);

        Assert.Equal(BanCommandText.TargetSecurityTooHigh, await CommandAsync(gm, ".ban character boss 1d x"));
        Assert.Null(await host.Bans.GetActiveAccountBanAsync((await host.Accounts.FindByUsernameAsync("BOSS"))!.Id));
    }

    [Fact]
    public async Task ProtectHigherSecurity_Off_AllowsTheVmangosBehaviour()
    {
        await using var host = WorldTestHost.Start(banOptions: new BanOptions { ProtectHigherSecurity = false });
        await using WorldTestClient gm = await host.EnterWorldAsync("GM", "Gm", AccountSecurity.GameMaster);
        await host.AddAccountAsync("BOSS", AccountSecurity.Administrator);
        await Drain(gm);

        Assert.Equal("BOSS is banned for 1d. Reason: x.", await CommandAsync(gm, ".ban account boss 1d x"));
    }

    [Theory]
    [InlineData("50000d")]                 // 4.32e9 s: above uint.MaxValue, used to wrap to about 25,000 s
    [InlineData("49710d49710d49710d")]     // sums past uint.MaxValue
    [InlineData("99999999999999999999s")]  // digit run overflows
    public async Task BanAccount_DurationOverflow_IsRefused_AndNeverWrapsToAShortOrPermanentBan(string duration)
    {
        await using var host = WorldTestHost.Start(); // RejectUnparseableDuration stays at its default (off)
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await host.AddAccountAsync("TARGET");
        await Drain(admin);

        Assert.StartsWith("Syntax: .ban account", await CommandAsync(admin, $".ban account target {duration} x"));
        Assert.Null(await host.Bans.GetActiveAccountBanAsync((await host.Accounts.FindByUsernameAsync("TARGET"))!.Id));
    }

    private static async Task Drain(params WorldTestClient[] clients)
    {
        foreach (WorldTestClient client in clients)
        {
            await client.CollectAsync();
        }
    }

    private static async Task<string> CommandAsync(WorldTestClient client, string line)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, line);
        ChatMessage reply = await client.ReadChatAsync();
        Assert.Equal(ChatType.System, reply.Type);
        return reply.Text;
    }

    private static async Task<string[]> ReadLinesAsync(WorldTestClient client, int count)
    {
        string[] lines = new string[count];
        for (int i = 0; i < count; i++)
        {
            lines[i] = (await client.ReadChatAsync()).Text;
        }

        return lines;
    }
}
