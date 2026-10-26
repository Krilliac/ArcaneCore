using System.Net;
using System.Net.Sockets;
using System.Text;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Cryptography;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.RemoteAdmin;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

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
        // The RA login skipped the second factor the realm and the web login enforce (vmangos AuthSocket.cpp:497-517).
        await using WorldTestHost host = PartyTestHost.Start();
        await CreateAdminAsync(host, "RAPIN", AccountLockFlags.FixedPin, "1234");
        RemoteAdminServer server = NewServer(host, Options0());
        await server.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal("-Wrong username or password.", await TryLoginAsync(server, "@login RAPIN secret"));
            Assert.Equal("-Wrong username or password.", await TryLoginAsync(server, "@login RAPIN secret 9999"));
            Assert.Equal("+Logged in as RAPIN (Administrator).", await TryLoginAsync(server, "@login RAPIN secret 1234"));

            await using Client interactive = await Client.ConnectAsync(server.Port);
            await interactive.ReadUntilAsync("Username:");
            await interactive.SendAsync("RAPIN");
            Assert.Equal("Password:", await interactive.ReadLineAsync());
            await interactive.SendAsync("secret");
            Assert.Equal("Code:", await interactive.ReadLineAsync());
            await interactive.SendAsync("1234");
            Assert.Equal("+Logged in as RAPIN (Administrator).", await interactive.ReadLineAsync());
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
}