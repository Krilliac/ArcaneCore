using System.Net;
using System.Net.Sockets;
using System.Text;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Cryptography;
using ArcaneCore.World.Net;
using ArcaneCore.World.Tests.Ops.MapFeed;
using ArcaneCore.World.Ops.MapFeed;
using ArcaneCore.World.Ops.RemoteAdmin;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static ArcaneCore.World.Tests.Ops.Control.WorldControlPipeTests;

namespace ArcaneCore.World.Tests.Ops.RemoteAdmin;

/// <summary>
/// Remote administration (docs/ops/remote-admin.md): a loopback TCP console for operators and automation; token or account login,
/// every command checked against the login's security, and in automation mode exactly one <c>#done ok</c> / <c>#done error reason</c>
/// per command.
/// </summary>
public sealed class RemoteAdminServerTests
{
    private static RemoteAdminServer NewServer(WorldTestHost host, RemoteAdminOptions options)
        => new(Options.Create(options), host.WorldServices.GetRequiredService<IServiceScopeFactory>(), host.Opcodes, host.World, host.Registry,
            Options.Create(new WorldSessionOptions()), NullLoggerFactory.Instance);

    private static RemoteAdminOptions Options0(string? tokenFile = null) => new()
    {
        Port = 0,
        AgentLoginFile = tokenFile ?? Path.Combine(Path.GetTempPath(), "ra-token-" + Guid.NewGuid().ToString("N") + ".txt"),
        FailedLoginDelayMs = 0,
    };

    private sealed class Client : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private Client(TcpClient tcp)
        {
            _tcp = tcp;
            NetworkStream stream = tcp.GetStream();
            _reader = new StreamReader(stream, Encoding.UTF8);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        }

        public static async Task<Client> ConnectAsync(int port)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            return new Client(tcp);
        }

        public Task SendAsync(string line) => _writer.WriteLineAsync(line);

        public async Task<string?> ReadLineAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            string? line = await _reader.ReadLineAsync(timeout.Token);

            // An interactive login gets the "AC> " prompt (no newline) before each command's reply.
            while (line is not null && line.StartsWith(RemoteAdminServer.Prompt + " ", StringComparison.Ordinal)) line = line[(RemoteAdminServer.Prompt.Length + 1)..];
            return line;
        }

        /// <summary>Read until a line starts with <paramref name="prefix"/>; returns every line read, that one last.</summary>
        public async Task<List<string>> ReadUntilAsync(string prefix)
        {
            var lines = new List<string>();
            while (true)
            {
                string? line = await ReadLineAsync() ?? throw new EndOfStreamException("closed after: " + string.Join(" | ", lines));
                lines.Add(line);
                if (line.StartsWith(prefix, StringComparison.Ordinal)) return lines;
            }
        }

        public ValueTask DisposeAsync()
        {
            _tcp.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task TokenLogin_RunsCommands_WithOneDoneMarkerEach()
    {
        await using WorldTestHost host = PartyTestHost.Start();
        RemoteAdminServer server = NewServer(host, Options0());
        await server.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(server.IsListening);
            Assert.True(File.Exists(server.TokenPath));
            Assert.Equal(server.Token, File.ReadAllText(server.TokenPath!).Trim());
            await using Client client = await Client.ConnectAsync(server.Port);
            await client.ReadUntilAsync("Username:");
            await client.SendAsync("@token " + server.Token);
            Assert.Equal("+Logged in as Console (Administrator).", await client.ReadLineAsync());

            await client.SendAsync(".botctl help");
            List<string> help = await client.ReadUntilAsync("#done");
            Assert.Equal("#done ok", help[^1]);

            await client.SendAsync("nosuchcommandatall");
            Assert.Equal("#done error unknown-command", (await client.ReadUntilAsync("#done"))[^1]);

            await client.SendAsync("@id t7 .botctl take Nobodyhere");
            Assert.Equal("#done t7 error no-match", (await client.ReadUntilAsync("#done"))[^1]);

            await client.SendAsync("@ping");
            Assert.Equal("#pong", await client.ReadLineAsync());

            await client.SendAsync("quit");
            Assert.Equal("+Bye.", await client.ReadLineAsync());
            Assert.True(server.IsListening);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }

        Assert.False(File.Exists(server.TokenPath));
    }

    [Fact]
    public async Task AccountLogin_ChecksThePassword_AndTheSecurity()
    {
        await using WorldTestHost host = PartyTestHost.Start();
        byte[] salt = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        await host.Accounts.CreateAsync(new Account
        {
            Username = "RAADMIN", Salt = salt, Security = AccountSecurity.Administrator,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, "RAADMIN", "secret"), 32), SessionKey = new byte[40],
        });
        await host.Accounts.CreateAsync(new Account
        {
            Username = "RAPLAYER", Salt = salt, Security = AccountSecurity.Player,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, "RAPLAYER", "secret"), 32), SessionKey = new byte[40],
        });
        RemoteAdminServer server = NewServer(host, Options0());
        await server.StartAsync(CancellationToken.None);
        try
        {
            await using (Client wrong = await Client.ConnectAsync(server.Port))
            {
                await wrong.ReadUntilAsync("Username:");
                await wrong.SendAsync("@login RAADMIN nope");
                Assert.Equal("-Wrong username or password.", await wrong.ReadLineAsync());
            }

            await using (Client player = await Client.ConnectAsync(server.Port))
            {
                await player.ReadUntilAsync("Username:");
                await player.SendAsync("@login RAPLAYER secret");
                Assert.Equal("-Wrong username or password.", await player.ReadLineAsync());
            }

            await using Client admin = await Client.ConnectAsync(server.Port);
            await admin.ReadUntilAsync("Username:");
            await admin.SendAsync("RAADMIN");
            Assert.Equal("Password:", await admin.ReadLineAsync());
            await admin.SendAsync("secret");
            Assert.Equal("Code:", await admin.ReadLineAsync());
            await admin.SendAsync(string.Empty);
            Assert.Equal("+Logged in as RAADMIN (Administrator).", await admin.ReadLineAsync());
            await admin.SendAsync("@auto on");
            await admin.ReadUntilAsync("#done");
            await admin.SendAsync(".botctl help");
            Assert.Equal("#done ok", (await admin.ReadUntilAsync("#done"))[^1]);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AccountLogin_RefusesAnAdministrator_WhoseStatusColumnIsActive_ButWhoHasAnActiveBanRow()
    {
        // The defect found in the Atlas dev UI design (S.3): RA read only the status column, while the realm and the gateway also read the ban rows.
        await using WorldTestHost host = PartyTestHost.Start();
        byte[] salt = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        Account banned = await host.Accounts.CreateAsync(new Account
        {
            Username = "RABANNED", Salt = salt, Security = AccountSecurity.Administrator, Status = AccountStatus.Active,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, "RABANNED", "secret"), 32), SessionKey = new byte[40],
        });
        await host.Accounts.CreateAsync(new Account
        {
            Username = "RAFREE", Salt = salt, Security = AccountSecurity.Administrator, Status = AccountStatus.Active,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, "RAFREE", "secret"), 32), SessionKey = new byte[40],
        });
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        host.Bans.AddAccountRow(banned.Id, now, now); // permanent: bandate == unbandate
        RemoteAdminServer server = NewServer(host, Options0());
        await server.StartAsync(CancellationToken.None);
        try
        {
            await using (Client refused = await Client.ConnectAsync(server.Port))
            {
                await refused.ReadUntilAsync("Username:");
                await refused.SendAsync("@login RABANNED secret");
                Assert.Equal("-Wrong username or password.", await refused.ReadLineAsync());
            }

            await using Client free = await Client.ConnectAsync(server.Port);
            await free.ReadUntilAsync("Username:");
            await free.SendAsync("@login RAFREE secret");
            Assert.Equal("+Logged in as RAFREE (Administrator).", await free.ReadLineAsync());
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<Account> CreateAdminAsync(WorldTestHost host, string name, AccountLockFlags flags = AccountLockFlags.None, string securityInfo = "", string lastIp = "")
    {
        byte[] salt = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        return await host.Accounts.CreateAsync(new Account
        {
            Username = name, Salt = salt, Security = AccountSecurity.Administrator, Status = AccountStatus.Active,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, name, "secret"), 32), SessionKey = new byte[40],
            LockFlags = flags, SecurityInfo = securityInfo, LastIp = lastIp,
        });
    }

    private static async Task<string> TryLoginAsync(RemoteAdminServer server, string line)
    {
        await using Client client = await Client.ConnectAsync(server.Port);
        await client.ReadUntilAsync("Username:");
        await client.SendAsync(line);
        return (await client.ReadLineAsync())!;
    }

    [Fact]
    public async Task AccountLogin_RequiresTheFixedPin_OfAnAccountThatHasOne()
    {
        // The RA login skipped the second factor the realm and the web login enforce (vmangos AuthSocket.cpp:697-749).
        await using WorldTestHost host = PartyTestHost.Start();
        await CreateAdminAsync(host, "RAPIN", AccountLockFlags.FixedPin, "1234");
        RemoteAdminServer server = NewServer(host, Options0());
        await server.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal("-Wrong username or password.", await TryLoginAsync(server, "@login RAPIN secret"));
            Assert.Equal("-Wrong username or password.", await TryLoginAsync(server, "@login RAPIN secret 9999"));
            Assert.Equal("+Logged in as RAPIN (Administrator).", await TryLoginAsync(server, "@login RAPIN secret 1234"));

            Assert.Equal("+Logged in as RAPIN (Administrator).", await InteractiveLoginAsync(server, "RAPIN", "secret", "1234"));
            Assert.Equal("-Wrong username or password.", await InteractiveLoginAsync(server, "RAPIN", "secret", ""));
            Assert.Equal("-Wrong username or password.", await InteractiveLoginAsync(server, "RAPIN", "wrong", "1234"));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The interactive login. The server asks for <c>Code:</c> after the password whatever the account and the password were (so the prompt is no
    /// password oracle); returns the one answer line.
    /// </summary>
    private static async Task<string> InteractiveLoginAsync(RemoteAdminServer server, string user, string password, string code)
    {
        await using Client client = await Client.ConnectAsync(server.Port);
        await client.ReadUntilAsync("Username:");
        await client.SendAsync(user);
        Assert.Equal("Password:", await client.ReadLineAsync());
        await client.SendAsync(password);
        Assert.Equal("Code:", await client.ReadLineAsync());
        await client.SendAsync(code);
        return (await client.ReadLineAsync())!;
    }

    [Fact]
    public async Task InteractiveLogin_AsksForTheCodeOfAnyAccount_AndAnAccountWithoutAFactorTakesAnEmptyAnswer()
    {
        await using WorldTestHost host = PartyTestHost.Start();
        await CreateAdminAsync(host, "RAPLAIN2");
        RemoteAdminServer server = NewServer(host, Options0());
        await server.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal("+Logged in as RAPLAIN2 (Administrator).", await InteractiveLoginAsync(server, "RAPLAIN2", "secret", ""));
            Assert.Equal("-Wrong username or password.", await InteractiveLoginAsync(server, "RAPLAIN2", "wrong", ""));
            Assert.Equal("-Wrong username or password.", await InteractiveLoginAsync(server, "NOSUCHACCOUNT", "secret", ""));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AccountLogin_AnIpLockedAccountFromAnotherAddress_NeedsItsFactor_AndAcceptsIt()
    {
        await using WorldTestHost host = PartyTestHost.Start();
        await CreateAdminAsync(host, "RAMOVED", AccountLockFlags.IpLock | AccountLockFlags.FixedPin, "4321", lastIp: "10.9.9.9");
        RemoteAdminServer server = NewServer(host, Options0());
        await server.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal("-Wrong username or password.", await TryLoginAsync(server, "@login RAMOVED secret"));
            Assert.Equal("+Logged in as RAMOVED (Administrator).", await TryLoginAsync(server, "@login RAMOVED secret 4321"));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AccountLogin_AnAccountWithoutAFactor_DoesNotIgnoreATrailingWord()
    {
        // "@login u secret junk": the account has no PIN/TOTP, so the whole text is its password ("secret junk" is wrong); the last word must not be
        // peeled off as a code that is then ignored.
        await using WorldTestHost host = PartyTestHost.Start();
        await CreateAdminAsync(host, "RANOFACTOR");
        RemoteAdminServer server = NewServer(host, Options0());
        await server.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal("-Wrong username or password.", await TryLoginAsync(server, "@login RANOFACTOR secret junk"));
            Assert.Equal("+Logged in as RANOFACTOR (Administrator).", await TryLoginAsync(server, "@login RANOFACTOR secret"));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AccountLogin_AnAccountWithAFactor_RefusesAWrongCodeAndAWordlessLine()
    {
        await using WorldTestHost host = PartyTestHost.Start();
        await CreateAdminAsync(host, "RAPINWORD", AccountLockFlags.FixedPin, "1234");
        RemoteAdminServer server = NewServer(host, Options0());
        await server.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal("-Wrong username or password.", await TryLoginAsync(server, "@login RAPINWORD secret"));
            Assert.Equal("-Wrong username or password.", await TryLoginAsync(server, "@login RAPINWORD secret 9999"));
            Assert.Equal("-Wrong username or password.", await TryLoginAsync(server, "@login RAPINWORD secret 1234 extra"));
            Assert.Equal("+Logged in as RAPINWORD (Administrator).", await TryLoginAsync(server, "@login RAPINWORD secret 1234"));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AccountLogin_ATotpCodeSpentThroughTheSharedChecker_IsRefusedHere()
    {
        // The web login and RA share one SecondFactorChecker, so a step spent on one surface is spent on the other.
        await using WorldTestHost host = PartyTestHost.Start();
        string secret = Totp.NewSecret();
        Account account = await CreateAdminAsync(host, "RASHARED", AccountLockFlags.Totp, secret);
        Totp.TryDecodeSecret(secret, out byte[] key);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string code = Totp.Generate(key, now).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        var shared = new ArcaneCore.World.Ops.Accounts.SecondFactorChecker();
        Assert.True(shared.CodeAccepted(account, code, TimeProvider.System, out long step));
        Assert.True(shared.ClaimTotpStep(account.Id, step));
        RemoteAdminServer server = new(Options.Create(Options0()), host.WorldServices.GetRequiredService<IServiceScopeFactory>(), host.Opcodes, host.World, host.Registry,
            Options.Create(new WorldSessionOptions()), NullLoggerFactory.Instance, shared);
        await server.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal("-Wrong username or password.", await TryLoginAsync(server, "@login RASHARED secret " + code));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AccountLogin_RequiresATotpCode_AndRefusesItsReplay()
    {
        await using WorldTestHost host = PartyTestHost.Start();
        string secret = Totp.NewSecret();
        await CreateAdminAsync(host, "RATOTP", AccountLockFlags.Totp, secret);
        Totp.TryDecodeSecret(secret, out byte[] key);
        string code = Totp.Generate(key, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        RemoteAdminServer server = NewServer(host, Options0());
        await server.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal("-Wrong username or password.", await TryLoginAsync(server, "@login RATOTP secret"));
            Assert.Equal("+Logged in as RATOTP (Administrator).", await TryLoginAsync(server, "@login RATOTP secret " + code));
            Assert.Equal("-Wrong username or password.", await TryLoginAsync(server, "@login RATOTP secret " + code));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AccountLogin_RefusesAnIpLockedAccount_FromAnotherAddress_AndAnIpBannedAddress()
    {
        await using WorldTestHost host = PartyTestHost.Start();
        await CreateAdminAsync(host, "RALOCKED", AccountLockFlags.IpLock, lastIp: "10.9.9.9");
        await CreateAdminAsync(host, "RAHOME", AccountLockFlags.IpLock, lastIp: "127.0.0.1");
        await CreateAdminAsync(host, "RAPLAIN");
        RemoteAdminServer server = NewServer(host, Options0());
        await server.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal("-Wrong username or password.", await TryLoginAsync(server, "@login RALOCKED secret"));
            Assert.Equal("+Logged in as RAHOME (Administrator).", await TryLoginAsync(server, "@login RAHOME secret"));

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            host.Bans.AddIpRow("127.0.0.1", now, now); // permanent
            Assert.Equal("-Wrong username or password.", await TryLoginAsync(server, "@login RAPLAIN secret"));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TokenFile_IsReadableByTheDaemonAccountOnly_EvenWhenAnOpenFileWasThere()
    {
        // Not proven: the effective access of a particular other account (needs a second OS user); this checks the descriptor and mode that grant it.
        await using WorldTestHost host = PartyTestHost.Start();
        string path = Path.Combine(Path.GetTempPath(), "ra-token-acl-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "stale\n"); // a file from an earlier run, with whatever permissions the directory gave it
        RemoteAdminServer server = NewServer(host, Options0(path));
        await server.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(server.Token, File.ReadAllText(path).Trim());
            if (OperatingSystem.IsWindows())
            {
                System.Security.AccessControl.FileSecurity security = new FileInfo(path).GetAccessControl();
                Assert.True(security.AreAccessRulesProtected, "the token file inherits the directory's ACL");
                foreach (System.Security.AccessControl.FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
                {
                    var sid = (System.Security.Principal.SecurityIdentifier)rule.IdentityReference;
                    Assert.True(sid == System.Security.Principal.WindowsIdentity.GetCurrent().User || sid.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid),
                        "unexpected ACL entry for " + sid);
                }
            }
            else
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            File.Delete(path); // the token file lives in the shared temp directory: no run leaves one behind
        }
    }

    [Fact]
    public void ProductionRegistration_AddsRemoteAdmin_OnByDefault_OnLoopback()
    {
        var services = new ServiceCollection();
        services.AddRemoteAdmin(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        using ServiceProvider provider = services.BuildServiceProvider();
        RemoteAdminOptions options = provider.GetRequiredService<IOptions<RemoteAdminOptions>>().Value;
        Assert.True(options.Enabled);
        Assert.Equal("127.0.0.1", options.BindAddress);
        Assert.Equal(AccountSecurity.Administrator, options.MinSecurity);
        Assert.Contains(services, d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && d.ImplementationType == typeof(RemoteAdminServer));
    }

    [WindowsFact]
    public async Task ATotpCodeSpentOnTheWebLogin_IsRefusedByRemoteAdmin_BuiltFromTheSameContainer()
    {
        // Both real consumers come from one container: the web login (MapFeedLogin) spends a code, then a RemoteAdminServer activated from that
        // container is shown the same code. Both fall back silently to a private checker when the container has none, so only this proves they share one.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            var time = new FeedTestClock();
            await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup
            {
                Terrain = true,
                Time = time,
                WebLogin = true,
                Actions = o =>
                {
                    o.Enabled = true;
                    o.WebLogin = true;
                    o.WebLoginMinSecurity = AccountSecurity.Moderator;
                    o.AuditDirectory = directory;
                },
            });
            WorldTestHost host = world.Host;
            IServiceProvider container = host.WorldServices;
            string secret = Totp.NewSecret();
            await CreateAdminAsync(host, "RAXSURFACE", AccountLockFlags.Totp, secret);
            Assert.True(Totp.TryDecodeSecret(secret, out byte[] key));
            string code = Totp.Generate(key, time.GetUtcNow().ToUnixTimeSeconds()).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

            FeedClient web = await world.ConnectAsync("web");
            await web.SendAsync($$"""{"op":"login","id":1,"user":"RAXSURFACE","password":"secret","code":"{{code}}","ip":"198.51.100.7"}""");
            System.Text.Json.JsonElement ok = await ActionWire.ReadResultAsync(web, 1);
            Assert.True(ok.Ok(), ok.ToString());

            var registered = container.GetRequiredService<ArcaneCore.World.Ops.Accounts.SecondFactorChecker>();
            RemoteAdminServer shared = ActivatorUtilities.CreateInstance<RemoteAdminServer>(
                container, Options.Create(Options0()), host.Opcodes, host.World, host.Registry, Options.Create(new WorldSessionOptions()), NullLoggerFactory.Instance);
            Assert.Same(registered, FactorOf(shared));
            Assert.Same(registered, FactorOf(container.GetRequiredService<MapFeedLogin>()));

            await shared.StartAsync(CancellationToken.None);
            try
            {
                Assert.Equal("-Wrong username or password.", await TryLoginAsync(shared, "@login RAXSURFACE secret " + code));
            }
            finally
            {
                await shared.StopAsync(CancellationToken.None);
            }

            // Control: with a private checker the very same code is accepted, so the refusal above is the shared replay record and nothing else.
            RemoteAdminServer isolated = new(Options.Create(Options0()), container.GetRequiredService<IServiceScopeFactory>(), host.Opcodes, host.World, host.Registry,
                Options.Create(new WorldSessionOptions()), NullLoggerFactory.Instance, new ArcaneCore.World.Ops.Accounts.SecondFactorChecker());
            await isolated.StartAsync(CancellationToken.None);
            try
            {
                Assert.Equal("+Logged in as RAXSURFACE (Administrator).", await TryLoginAsync(isolated, "@login RAXSURFACE secret " + code));
            }
            finally
            {
                await isolated.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    private static object? FactorOf(object consumer)
        => consumer.GetType().GetField("_factor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(consumer);

    [Fact]
    public void RemoteAdminAndTheWebLogin_RegisterOneSecondFactorCheckerSingleton()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        // AddRemoteAdmin alone must register the checker (one singleton): without the registration the RA server falls back to a private SecondFactorChecker: the factor is still enforced, but its replay record is not shared with the web login.
        var remoteOnly = new ServiceCollection();
        remoteOnly.AddRemoteAdmin(configuration);
        ServiceDescriptor own = Assert.Single(remoteOnly, d => d.ServiceType == typeof(ArcaneCore.World.Ops.Accounts.SecondFactorChecker));
        Assert.Equal(ServiceLifetime.Singleton, own.Lifetime);

        var services = new ServiceCollection();
        services.AddRemoteAdmin(configuration);
        services.AddMapFeedWebLogin();
        ServiceDescriptor descriptor = Assert.Single(services, d => d.ServiceType == typeof(ArcaneCore.World.Ops.Accounts.SecondFactorChecker));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    /// <summary>An account store whose lookup by name never answers on its own: it ends only when the caller's token is cancelled.</summary>
    public class HangingAccountStore : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != nameof(IAccountStore.FindByUsernameAsync))
            {
                throw new NotSupportedException(targetMethod.Name);
            }

            return Hang((CancellationToken)args![1]!);
        }

        private static async Task<Account?> Hang(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return null;
        }
    }

    [Fact]
    public async Task OneLineLogin_WithAStalledAccountLookup_IsEndedByTheLoginTimeout()
    {
        // The one-line @login did its store lookups on the connection's token, not the login timeout's, so a stalled store held the unauthenticated
        // connection open for good. The lookup here never answers; only the login timeout (5 s, the lowest the option allows) can end it.
        await using WorldTestHost host = PartyTestHost.Start();
        var services = new ServiceCollection();
        services.AddSingleton(System.Reflection.DispatchProxy.Create<IAccountStore, HangingAccountStore>());
        using ServiceProvider provider = services.BuildServiceProvider();
        RemoteAdminOptions options = Options0();
        options.LoginTimeoutSeconds = 5;
        RemoteAdminServer server = new(Options.Create(options), provider.GetRequiredService<IServiceScopeFactory>(), host.Opcodes, host.World, host.Registry,
            Options.Create(new WorldSessionOptions()), NullLoggerFactory.Instance);
        await server.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal("-Login timed out; closing.", await TryLoginAsync(server, "@login RASTALLED secret"));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            File.Delete(options.AgentLoginFile);
        }
    }

    /// <summary>Forwards every call to the real store and records the method name, so a test can compare the store work of two logins.</summary>
    public class CallRecorder : System.Reflection.DispatchProxy
    {
        private object _target = null!;
        private System.Collections.Concurrent.ConcurrentQueue<string> _calls = null!;

        public static T Wrap<T>(T target, System.Collections.Concurrent.ConcurrentQueue<string> calls) where T : class
        {
            T proxy = Create<T, CallRecorder>();
            var recorder = (CallRecorder)(object)proxy;
            recorder._target = target;
            recorder._calls = calls;
            return proxy;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            _calls.Enqueue(targetMethod!.Name);
            try
            {
                return targetMethod.Invoke(_target, args);
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }

    [Fact]
    public async Task OneLineLogin_DoesTheSameStoreWork_ForAFactorAccount_ANoFactorAccount_AndAnUnknownName()
    {
        // A stranger who knows no password must not tell accounts, or accounts with a second factor, from invented names by the work the server
        // does (the SRP verifier, the lookups and the ban round trip all run for every name). The wrong password keeps every attempt on the
        // pre-authentication path; two line shapes: a text without a space and a text over 128 characters that has one (a factor account splits it).
        await using WorldTestHost host = PartyTestHost.Start();
        await CreateAdminAsync(host, "RASPYPIN", AccountLockFlags.FixedPin, "1234");
        await CreateAdminAsync(host, "RASPYPLAIN");
        var calls = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var services = new ServiceCollection();
        services.AddSingleton(CallRecorder.Wrap<IAccountStore>(host.Accounts, calls));
        services.AddSingleton(CallRecorder.Wrap<IBanStore>(host.Bans, calls));
        using ServiceProvider provider = services.BuildServiceProvider();
        RemoteAdminServer server = new(Options.Create(Options0()), provider.GetRequiredService<IServiceScopeFactory>(), host.Opcodes, host.World, host.Registry,
            Options.Create(new WorldSessionOptions()), NullLoggerFactory.Instance);
        await server.StartAsync(CancellationToken.None);
        try
        {
            foreach (string shape in new[] { "wrongpw", new string('a', 100) + " " + new string('b', 40) })
            {
                var sequences = new List<string[]>();
                foreach (string name in new[] { "RASPYPIN", "RASPYPLAIN", "RASPYNOBODY" })
                {
                    calls.Clear();
                    Assert.Equal("-Wrong username or password.", await TryLoginAsync(server, $"@login {name} {shape}"));
                    sequences.Add([.. calls]);
                }

                Assert.Contains("FindByUsernameAsync", sequences[0]);
                Assert.Contains("GetActiveAccountBanAsync", sequences[0]);
                Assert.Equal(sequences[0], sequences[1]);
                Assert.Equal(sequences[0], sequences[2]);
            }
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }
}
