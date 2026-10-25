using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Ops.MapFeed;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Ops.Control.WorldControlPipeTests;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// The web login over the map feed (docs/areas/atlas-public.md, "Login"; design 0.4): <c>login</c>, <c>logout</c> and <c>characters</c> on a
/// <c>web</c> connection, against the real ticket store and the host's in-memory account and ban stores. Every wire line goes over the real pipe.
/// The central property is that every cause of failure is the same answer: the tests compare raw lines.
/// </summary>
public sealed class MapFeedLoginTests
{
    private const string Password = "correct horse";
    private const string StaffIp = "198.51.100.7";
    private const string OtherIp = "203.0.113.9";

    private static FeedWorldSetup Setup(ManualTimeProvider time, string auditDirectory, Action<MapFeedActionsOptions>? tweak = null) => new()
    {
        Terrain = true,
        Time = time,
        WebLogin = true,
        Actions = o =>
        {
            o.Enabled = true;
            o.WebLogin = true;
            o.WebLoginMinSecurity = AccountSecurity.Moderator;
            o.AuditDirectory = auditDirectory;
            tweak?.Invoke(o);
        },
    };

    private static async Task<Account> AccountAsync(
        WorldTestHost host, string name, AccountSecurity security, string password = Password, AccountLockFlags flags = AccountLockFlags.None,
        string securityInfo = "", AccountStatus status = AccountStatus.Active)
    {
        byte[] salt = [.. Enumerable.Range(1, 32).Select(i => (byte)(i + name.Length))];
        return await host.Accounts.CreateAsync(new Account
        {
            Username = name,
            Salt = salt,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, name, password), 32),
            Security = security,
            LockFlags = flags,
            SecurityInfo = securityInfo,
            Status = status,
            SessionKey = new byte[40],
        });
    }

    private static string Login(long id, string user, string password, string? code = null, string? ip = StaffIp, int? floor = null)
    {
        var line = new JsonObject { ["op"] = "login", ["id"] = id, ["user"] = user, ["password"] = password };
        if (code is not null)
        {
            line["code"] = code;
        }

        if (ip is not null)
        {
            line["ip"] = ip;
        }

        if (floor is not null)
        {
            line["floor"] = floor;
        }

        return line.ToJsonString();
    }

    private static async Task<JsonElement> LoginAsync(FeedClient client, long id, string user, string password, string? code = null, string? ip = StaffIp, int? floor = null)
    {
        await client.SendAsync(Login(id, user, password, code, ip, floor));
        return await ActionWire.ReadResultAsync(client, id);
    }

    private static string Ticket(JsonElement ok) => ok.GetProperty("ticket").GetString()!;

    [WindowsFact]
    public async Task ARightPassword_IssuesATicket_ThatActsAsTheAccount_AtItsOwnLevel()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(Setup(new ManualTimeProvider(), directory));
            ActionSpies spies = ActionSpies.Install(world.Host);
            await AccountAsync(world.Host, "WEBGM", AccountSecurity.GameMaster);
            FeedClient web = await world.ConnectAsync("web");

            JsonElement ok = await LoginAsync(web, 1, "webgm", Password);

            Assert.True(ok.Ok(), ok.ToString());
            Assert.Equal("login", ok.GetProperty("kind").GetString());
            Assert.Equal("WEBGM", ok.GetProperty("account").GetString());
            Assert.Equal((int)AccountSecurity.GameMaster, ok.GetProperty("security").GetInt32());
            Assert.Equal(43200, ok.GetProperty("expiresIn").GetInt32());
            string ticket = Ticket(ok);
            Assert.Equal(43, ticket.Length); // 32 random bytes in base64url, no padding

            JsonElement acted = await ActionWire.ActAsync(web, 2, ActionWire.Web(ticket), command: ".zwho");
            Assert.True(acted.Ok(), acted.ToString());
            Assert.Contains("who:atlas-web:WEBGM:GameMaster", acted.Lines());
            Assert.Equal(1, spies.Count("zwho"));
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task OnALocalConnection_LoginAndCharacters_AreRoleMismatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(Setup(new ManualTimeProvider(), directory));
            await AccountAsync(world.Host, "WEBGM", AccountSecurity.GameMaster);
            FeedClient local = await world.ConnectAsync("local");

            JsonElement refused = await LoginAsync(local, 1, "WEBGM", Password);
            refused.AssertRefused("role-mismatch");
            Assert.False(refused.TryGetProperty("ticket", out _));
            await local.SendAsync("""{"op":"characters","id":2,"ticket":"x"}""");
            (await ActionWire.ReadResultAsync(local, 2)).AssertRefused("role-mismatch");
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task EveryCauseOfFailure_GivesTheIdenticalAnswerLine()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            var time = new ManualTimeProvider();
            await using FeedWorld world = await FeedWorld.StartAsync(Setup(time, directory));
            string secret = Totp.NewSecret();
            await AccountAsync(world.Host, "STAFF", AccountSecurity.GameMaster);
            await AccountAsync(world.Host, "BANNED", AccountSecurity.GameMaster);
            await AccountAsync(world.Host, "SUSPENDED", AccountSecurity.GameMaster, status: AccountStatus.Suspended);
            await AccountAsync(world.Host, "PLAYER", AccountSecurity.Player);
            await AccountAsync(world.Host, "IPLOCKED", AccountSecurity.GameMaster, flags: AccountLockFlags.IpLock);
            await AccountAsync(world.Host, "TOTPGM", AccountSecurity.GameMaster, flags: AccountLockFlags.Totp, securityInfo: secret);
            await AccountAsync(world.Host, "PINGM", AccountSecurity.GameMaster, flags: AccountLockFlags.FixedPin, securityInfo: "4821");
            Account banned = (await world.Host.Accounts.FindByUsernameAsync("BANNED"))!;
            long now = time.GetUtcNow().ToUnixTimeSeconds();
            world.Host.Bans.AddAccountRow(banned.Id, now, now); // permanent, status column still Active: the defect the realm and gateway already handle
            world.Host.Bans.AddIpRow("192.0.2.66", now, now);
            FeedClient web = await world.ConnectAsync("web");
            string goodCode = Totp.Generate(Totp.TryDecodeSecret(secret, out byte[] key) ? key : [], now).ToString("D6", CultureInfo.InvariantCulture);

            (string Case, string User, string Pass, string? Code, string? Ip)[] failures =
            [
                ("wrong password", "STAFF", "nope", null, StaffIp),
                ("unknown name", "NOSUCHNAME", Password, null, StaffIp),
                ("banned account (Active column, ban row)", "BANNED", Password, null, StaffIp),
                ("suspended account", "SUSPENDED", Password, null, StaffIp),
                ("banned address", "STAFF", Password, null, "192.0.2.66"),
                ("below the level floor", "PLAYER", Password, null, StaffIp),
                ("IP-locked account", "IPLOCKED", Password, null, StaffIp),
                ("needs a code: right password, no code", "TOTPGM", Password, null, StaffIp),
                ("needs a code: wrong password, no code", "TOTPGM", "nope", null, StaffIp),
                ("needs a code: wrong password, right code", "TOTPGM", "nope", goodCode, StaffIp),
                ("needs a code: right password, wrong code", "TOTPGM", Password, "000000", StaffIp),
                ("needs a PIN: right password, wrong PIN", "PINGM", Password, "1111", StaffIp),
            ];

            string? reference = null;
            foreach ((string Case, string User, string Pass, string? Code, string? Ip) failure in failures)
            {
                JsonElement answer = await LoginAsync(web, 7, failure.User, failure.Pass, failure.Code, failure.Ip);
                answer.AssertRefused("login-failed");
                reference ??= answer.GetRawText();
                Assert.True(reference == answer.GetRawText(), $"{failure.Case}: the answer line differs from the first failure's.\n{reference}\n{answer.GetRawText()}");
                Assert.False(answer.TryGetProperty("ticket", out _), failure.Case);
            }

            Assert.Equal(failures.Length, failures.Select(f => f.Case).Distinct().Count());
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task AFixedPin_IsAcceptedAsTheCode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(Setup(new ManualTimeProvider(), directory));
            await AccountAsync(world.Host, "PINGM", AccountSecurity.GameMaster, flags: AccountLockFlags.FixedPin, securityInfo: "4821");
            FeedClient web = await world.ConnectAsync("web");

            Assert.True((await LoginAsync(web, 1, "PINGM", Password, "4821")).Ok());
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task ATotpCode_IsAcceptedOnce_AndAReplayedStepIsRefused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            var time = new ManualTimeProvider();
            await using FeedWorld world = await FeedWorld.StartAsync(Setup(time, directory));
            string secret = Totp.NewSecret();
            await AccountAsync(world.Host, "TOTPGM", AccountSecurity.GameMaster, flags: AccountLockFlags.Totp, securityInfo: secret);
            Assert.True(Totp.TryDecodeSecret(secret, out byte[] key));
            FeedClient web = await world.ConnectAsync("web");
            string Code(int offset = 0) => Totp.Generate(key, time.GetUtcNow().ToUnixTimeSeconds(), offset).ToString("D6", CultureInfo.InvariantCulture);

            string first = Code();
            Assert.True((await LoginAsync(web, 1, "TOTPGM", Password, first)).Ok());
            (await LoginAsync(web, 2, "TOTPGM", Password, first)).AssertRefused("login-failed"); // the same step again: a replay

            time.Advance(TimeSpan.FromSeconds(30));
            Assert.True((await LoginAsync(web, 3, "TOTPGM", Password, Code())).Ok()); // the next step is fine
            time.Advance(TimeSpan.FromSeconds(60));
            (await LoginAsync(web, 4, "TOTPGM", Password, first)).AssertRefused("login-failed"); // a code two steps old is outside the +-1 window
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task ATotpStep_IsNotUsedUp_ByALoginThatFailsForAnotherReason()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            var time = new ManualTimeProvider();
            await using FeedWorld world = await FeedWorld.StartAsync(Setup(time, directory));
            string secret = Totp.NewSecret();
            await AccountAsync(world.Host, "TOTPGM", AccountSecurity.GameMaster, flags: AccountLockFlags.Totp, securityInfo: secret);
            Assert.True(Totp.TryDecodeSecret(secret, out byte[] key));
            FeedClient web = await world.ConnectAsync("web");
            string code = Totp.Generate(key, time.GetUtcNow().ToUnixTimeSeconds()).ToString("D6", CultureInfo.InvariantCulture);

            // Right password, right code, but the account is below the floor Atlas states (Administrator): the login fails.
            (await LoginAsync(web, 1, "TOTPGM", Password, code, floor: (int)AccountSecurity.Administrator)).AssertRefused("login-failed");

            // The failed login did not spend the step: the same code still works once the floor is met.
            JsonElement ok = await LoginAsync(web, 2, "TOTPGM", Password, code);
            Assert.True(ok.Ok(), ok.ToString());

            // And now it is spent.
            (await LoginAsync(web, 3, "TOTPGM", Password, code)).AssertRefused("login-failed");
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task ALevelZeroAccount_NeedsTheFloorLoweredOnBothSides()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld refused = await FeedWorld.StartAsync(Setup(new ManualTimeProvider(), directory, o => o.WebLoginMinSecurity = AccountSecurity.Player));
            await AccountAsync(refused.Host, "PLAYER", AccountSecurity.Player);
            FeedClient web = await refused.ConnectAsync("web");

            // The world's floor is Player, but Atlas states Moderator (the public listener's own floor): the higher one wins.
            (await LoginAsync(web, 1, "PLAYER", Password, floor: 1)).AssertRefused("login-failed");
            JsonElement ok = await LoginAsync(web, 2, "PLAYER", Password, floor: 0);
            Assert.True(ok.Ok(), ok.ToString());
            Assert.Equal(0, ok.GetProperty("security").GetInt32());
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }

        string second = WebAuditFile.NewDirectory();
        try
        {
            // The world's own floor (default Moderator) also holds when Atlas states 0.
            await using FeedWorld strict = await FeedWorld.StartAsync(Setup(new ManualTimeProvider(), second));
            await AccountAsync(strict.Host, "PLAYER", AccountSecurity.Player);
            FeedClient web = await strict.ConnectAsync("web");
            (await LoginAsync(web, 1, "PLAYER", Password, floor: 0)).AssertRefused("login-failed");
        }
        finally
        {
            WebAuditFile.TryDelete(second);
        }
    }

    [WindowsFact]
    public async Task ALockout_IsPerNetwork_AndEndsAfterTheWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            var time = new ManualTimeProvider();
            await using FeedWorld world = await FeedWorld.StartAsync(Setup(time, directory));
            await AccountAsync(world.Host, "STAFF", AccountSecurity.GameMaster);
            FeedClient web = await world.ConnectAsync("web");

            for (int i = 0; i < 5; i++)
            {
                (await LoginAsync(web, 10 + i, "STAFF", "wrong", ip: StaffIp)).AssertRefused("login-failed");
            }

            // Locked for that network: even the right password fails there ...
            (await LoginAsync(web, 20, "STAFF", Password, ip: "198.51.100.200")).AssertRefused("login-failed"); // same /24
            // ... but the owner on another network is not locked out by a stranger's guesses.
            Assert.True((await LoginAsync(web, 21, "STAFF", Password, ip: OtherIp)).Ok());

            time.Advance(TimeSpan.FromMinutes(16));
            Assert.True((await LoginAsync(web, 22, "STAFF", Password, ip: StaffIp)).Ok());
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task ATenthFailureFromOneAddress_ClosesThatAddress_EvenForTheRightPassword()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(Setup(new ManualTimeProvider(), directory));
            await AccountAsync(world.Host, "STAFF", AccountSecurity.GameMaster);
            FeedClient web = await world.ConnectAsync("web");

            for (int i = 0; i < 10; i++)
            {
                (await LoginAsync(web, 10 + i, "GUESS" + i.ToString(CultureInfo.InvariantCulture), "nope", ip: OtherIp)).AssertRefused("login-failed");
            }

            (await LoginAsync(web, 30, "STAFF", Password, ip: OtherIp)).AssertRefused("login-failed");
            Assert.True((await LoginAsync(web, 31, "STAFF", Password, ip: StaffIp)).Ok()); // another address is untouched
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task APasswordOrCode_NeverReachesTheLogOrTheAuditFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(Setup(new ManualTimeProvider(), directory));
            await AccountAsync(world.Host, "STAFF", AccountSecurity.GameMaster, password: "Tr0ub4dor&3-marker");
            FeedClient web = await world.ConnectAsync("web");

            (await LoginAsync(web, 1, "STAFF", "wrong-marker-password", "424242")).AssertRefused("login-failed");
            JsonElement ok = await LoginAsync(web, 2, "STAFF", "Tr0ub4dor&3-marker");
            Assert.True(ok.Ok());

            string everything = string.Join('\n', world.Logs.Entries.Select(e => e.Message));
            string audit = File.ReadAllText(Path.Combine(directory, WebAuditFile.FileName));
            foreach (string secret in new[] { "Tr0ub4dor&3-marker", "wrong-marker-password", "424242", Ticket(ok) })
            {
                Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, audit, StringComparison.Ordinal);
            }

            Assert.Contains(world.Logs.Entries, e => e.Category == MapFeedActorTicketStore.LogCategory && e.Message.Contains("login ok account=STAFF", StringComparison.Ordinal));
            Assert.Contains(world.Logs.Entries, e => e.Category == MapFeedActorTicketStore.LogCategory && e.Message.Contains("login failed account=STAFF", StringComparison.Ordinal));
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task LoginAndFailedLogin_AreAppendedToTheDurableAudit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(Setup(new ManualTimeProvider(), directory));
            await AccountAsync(world.Host, "STAFF", AccountSecurity.GameMaster);
            FeedClient web = await world.ConnectAsync("web");

            (await LoginAsync(web, 1, "STAFF", "wrong")).AssertRefused("login-failed");
            JsonElement ok = await LoginAsync(web, 2, "STAFF", Password);

            JsonElement[] records = WebAuditFile.Read(directory);
            Assert.Equal(2, records.Length);
            Assert.Equal("login", records[0].GetProperty("event").GetString());
            Assert.Equal("failed", records[0].GetProperty("result").GetString());
            Assert.Equal("login-failed", records[0].GetProperty("code").GetString());
            Assert.Equal(StaffIp, records[0].GetProperty("clientIp").GetString());
            Assert.Equal("login", records[1].GetProperty("event").GetString());
            Assert.Equal("ok", records[1].GetProperty("result").GetString());
            Assert.Equal("atlas-web:STAFF", records[1].GetProperty("actor").GetString());
            Assert.Equal(ActionWire.TicketRef(Ticket(ok)), records[1].GetProperty("ticketRef").GetString());
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task AnUnwritableAudit_RefusesTheLogin_WithTheOrdinaryLoginFailed_AndIssuesNoTicket()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        string blocker = Path.Combine(directory, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "x");
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(Setup(new ManualTimeProvider(), blocker));
            await AccountAsync(world.Host, "STAFF", AccountSecurity.GameMaster);
            FeedClient web = await world.ConnectAsync("web");

            JsonElement refused = await LoginAsync(web, 1, "STAFF", Password);

            refused.AssertRefused("login-failed"); // not "audit-unavailable": that would say the password was right
            Assert.Equal(0, world.Host.WorldServices.GetRequiredService<MapFeedActorTicketStore>().Count);
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task ALogout_EndsTheTicket_AtOnce_EvenAgainstTheThirtySecondCache()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(Setup(new ManualTimeProvider(), directory));
            ActionSpies.Install(world.Host);
            await AccountAsync(world.Host, "WEBGM", AccountSecurity.GameMaster);
            FeedClient web = await world.ConnectAsync("web");
            string ticket = Ticket(await LoginAsync(web, 1, "WEBGM", Password));
            Assert.True((await ActionWire.ActAsync(web, 2, ActionWire.Web(ticket), command: ".zplayer")).Ok()); // now cached as valid

            await web.SendAsync(new JsonObject { ["op"] = "logout", ["id"] = 3, ["ticket"] = ticket }.ToJsonString());
            JsonElement ended = await ActionWire.ReadResultAsync(web, 3);

            Assert.True(ended.Ok());
            Assert.True(ended.GetProperty("ended").GetBoolean());
            (await ActionWire.ActAsync(web, 4, ActionWire.Web(ticket), command: ".zplayer")).AssertRefused("no-ticket");
            JsonElement record = Assert.Single(WebAuditFile.Read(directory), r => r.GetProperty("event").GetString() == "logout");
            Assert.Equal("atlas-web:WEBGM", record.GetProperty("actor").GetString());
            Assert.Equal(ActionWire.TicketRef(ticket), record.GetProperty("ticketRef").GetString());
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task ARevokeByAccount_BelongsToTheLocalConnection_AndEndsEveryTicketOfThatAccount()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(Setup(new ManualTimeProvider(), directory));
            ActionSpies.Install(world.Host);
            await AccountAsync(world.Host, "WEBGM", AccountSecurity.GameMaster);
            FeedClient web = await world.ConnectAsync("web");
            FeedClient local = await world.ConnectAsync("local");
            string first = Ticket(await LoginAsync(web, 1, "WEBGM", Password));
            string second = Ticket(await LoginAsync(web, 2, "WEBGM", Password, ip: OtherIp));
            Assert.True((await ActionWire.ActAsync(web, 3, ActionWire.Web(first), command: ".zplayer")).Ok());

            await web.SendAsync("""{"op":"logout","id":4,"account":"WEBGM"}""");
            (await ActionWire.ReadResultAsync(web, 4)).AssertRefused("role-mismatch");
            Assert.True((await ActionWire.ActAsync(web, 5, ActionWire.Web(second), command: ".zplayer")).Ok()); // the web connection could not revoke

            await local.SendAsync("""{"op":"logout","id":6,"account":"webgm"}""");
            JsonElement revoked = await ActionWire.ReadResultAsync(local, 6);
            Assert.True(revoked.Ok(), revoked.ToString());
            Assert.Equal(2, revoked.GetProperty("revoked").GetInt32());
            (await ActionWire.ActAsync(web, 7, ActionWire.Web(first), command: ".zplayer")).AssertRefused("no-ticket");
            (await ActionWire.ActAsync(web, 8, ActionWire.Web(second), command: ".zplayer")).AssertRefused("no-ticket");
            JsonElement record = Assert.Single(WebAuditFile.Read(directory), r => r.GetProperty("event").GetString() == "revoke");
            Assert.Equal(2, record.GetProperty("targets").GetInt32());
            Assert.StartsWith("atlas-local:", record.GetProperty("actor").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task ABanOrDemotionAfterLogin_StopsTheTicketWithinThirtySeconds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            var time = new ManualTimeProvider();
            await using FeedWorld world = await FeedWorld.StartAsync(Setup(time, directory));
            ActionSpies.Install(world.Host);
            Account staff = await AccountAsync(world.Host, "WEBGM", AccountSecurity.GameMaster);
            Account other = await AccountAsync(world.Host, "WEBMOD", AccountSecurity.GameMaster);
            FeedClient web = await world.ConnectAsync("web");
            string banned = Ticket(await LoginAsync(web, 1, "WEBGM", Password));
            string demoted = Ticket(await LoginAsync(web, 2, "WEBMOD", Password, ip: OtherIp));
            Assert.True((await ActionWire.ActAsync(web, 3, ActionWire.Web(banned), command: ".zplayer")).Ok());
            Assert.True((await ActionWire.ActAsync(web, 4, ActionWire.Web(demoted), command: ".zplayer")).Ok());

            long now = time.GetUtcNow().ToUnixTimeSeconds();
            world.Host.Bans.AddAccountRow(staff.Id, now, now);
            await world.Host.Accounts.UpdateSecurityAsync("WEBMOD", AccountSecurity.Player);
            time.Advance(TimeSpan.FromSeconds(31));

            (await ActionWire.ActAsync(web, 5, ActionWire.Web(banned), command: ".zplayer")).AssertRefused("banned");
            Assert.False((await ActionWire.ActAsync(web, 6, ActionWire.Web(demoted), command: ".zplayer")).Ok());
            Assert.Equal(other.Id, (await world.Host.Accounts.FindByUsernameAsync("WEBMOD"))!.Id);
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task Characters_AnswersOnlyTheTicketsOwnOnlineCharacters()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(Setup(new ManualTimeProvider(), directory, o => o.WebLoginMinSecurity = AccountSecurity.Player));
            await using WorldTestClient mine = await world.AddPlayerAsync("Feedalice");
            await AccountAsync(world.Host, "WEBPLAYER", AccountSecurity.Player);
            // The character was created under an account named after it; give the web account the same id by logging in as that account.
            Account owner = (await world.Host.Accounts.FindByUsernameAsync("FEEDALICE"))!;
            owner.Salt = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];
            owner.Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(owner.Salt, "FEEDALICE", Password), 32);
            owner.Security = AccountSecurity.Player;
            FeedClient web = await world.ConnectAsync("web");
            string ticket = Ticket(await LoginAsync(web, 1, "FEEDALICE", Password, floor: 0));
            string strangers = Ticket(await LoginAsync(web, 2, "WEBPLAYER", Password, ip: OtherIp, floor: 0));

            await web.SendAsync(new JsonObject { ["op"] = "characters", ["id"] = 3, ["ticket"] = ticket }.ToJsonString());
            JsonElement own = await ActionWire.ReadResultAsync(web, 3);
            Assert.True(own.Ok(), own.ToString());
            JsonElement character = Assert.Single(own.GetProperty("characters").EnumerateArray());
            Assert.Equal("Feedalice", character.GetProperty("n").GetString());
            Assert.Equal(FeedWorld.StartX, character.GetProperty("x").GetSingle(), 0.5f);

            await web.SendAsync(new JsonObject { ["op"] = "characters", ["id"] = 4, ["ticket"] = strangers }.ToJsonString());
            JsonElement none = await ActionWire.ReadResultAsync(web, 4);
            Assert.Empty(none.GetProperty("characters").EnumerateArray());
            await web.SendAsync("""{"op":"characters","id":5,"ticket":"not-a-ticket"}""");
            (await ActionWire.ReadResultAsync(web, 5)).AssertRefused("no-ticket");
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task WithoutWebLogin_TheOpsAreUnknown_AndTheCoreProtocolIsUntouched()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup
            {
                Terrain = true,
                Tickets = new FakeFeedTickets(),
                Actions = o => { o.Enabled = true; o.AuditDirectory = directory; },
            });
            FeedClient client = await world.ConnectAsync();

            await client.SendAsync("""{"op":"login","id":1,"user":"A","password":"b"}""");
            JsonElement error = await client.ReadAsync();
            Assert.Equal("error", error.Op());
            Assert.Equal("unknown-op", error.GetProperty("code").GetString());
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task WhenWebActorsAreOff_TheLoginAnswersWebLoginOff()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(Setup(new ManualTimeProvider(), directory, o => o.WebLogin = false));
            await AccountAsync(world.Host, "STAFF", AccountSecurity.GameMaster);
            FeedClient web = await world.ConnectAsync("web");

            (await LoginAsync(web, 1, "STAFF", Password)).AssertRefused("web-login-off");
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }
}
