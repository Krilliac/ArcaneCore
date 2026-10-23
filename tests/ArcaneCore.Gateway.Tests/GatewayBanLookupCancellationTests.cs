using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster.Gateway;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// Bug #152: the gateway's account-ban and IP-ban reads during CMSG_AUTH_SESSION must carry the connection's cancellation token,
/// so a kicked or shut-down connection stops waiting on the database instead of stalling GatewayServer shutdown.
/// </summary>
public sealed class GatewayBanLookupCancellationTests
{
    private const string AccountName = "BANWAIT";

    [Fact]
    public async Task AccountBanLookup_ReceivesTokenCancelledByGatewayShutdown()
    {
        using var bans = new BlockingBanStore(blockAccountLookup: true);
        await RunBlockedLoginAsync(bans);
    }

    [Fact]
    public async Task IpBanLookup_ReceivesTokenCancelledByGatewayShutdown()
    {
        using var bans = new BlockingBanStore(blockAccountLookup: false);
        await RunBlockedLoginAsync(bans);
    }

    private static async Task RunBlockedLoginAsync(BlockingBanStore bans)
    {
        byte[] key = new byte[WowSrp6.SessionKeyLength];
        for (int i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(0x30 + i);
        }

        var services = new ServiceCollection();
        services.AddSingleton<IAccountStore>(new SingleAccountStore(key));
        services.AddSingleton<IBanStore>(bans);
        await using ServiceProvider provider = services.BuildServiceProvider();

        using var tempListener = new TcpListener(IPAddress.Loopback, 0);
        tempListener.Start();
        int unusedWorkerPort = ((IPEndPoint)tempListener.LocalEndpoint).Port;
        tempListener.Stop();

        var gatewayOptions = new GatewayOptions
        {
            BindAddress = "127.0.0.1",
            Port = 0,
            WorkerAddress = "127.0.0.1",
            WorkerPort = unusedWorkerPort,
            LinkSecret = "0123456789abcdef-gateway-ban-secret",
            LinkRetryDelay = TimeSpan.FromMilliseconds(25),
        };
        var netOptions = new NetProtectionOptions { ExemptLoopbackOnLoopbackBind = false };
        using var gateway = new GatewayServer(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(gatewayOptions),
            NullLoggerFactory.Instance,
            Options.Create(netOptions));
        await gateway.StartAsync(CancellationToken.None);

        using var tcp = new TcpClient();
        Task? stop = null;
        try
        {
            await tcp.ConnectAsync(gateway.ListenEndPoint);
            NetworkStream stream = tcp.GetStream();
            uint serverSeed = await ReadChallengeAsync(stream);
            await WriteAuthSessionAsync(stream, serverSeed, key);

            CancellationToken seen = await bans.BlockedLookupToken.WaitAsync(TimeSpan.FromSeconds(5));

            stop = gateway.StopAsync(CancellationToken.None);

            // The shutdown token must reach the blocked lookup; with the token omitted it waits until the test releases it.
            Assert.True(seen.CanBeCanceled, "the ban lookup was called without a cancellation token");
            for (int i = 0; i < 200 && !seen.IsCancellationRequested; i++)
            {
                await Task.Delay(15);
            }

            Assert.True(seen.IsCancellationRequested, "gateway shutdown did not cancel the in-flight ban lookup");
        }
        finally
        {
            bans.Release();
            await (stop ?? gateway.StopAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task<uint> ReadChallengeAsync(NetworkStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cts.Token);
        ushort size = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
        Assert.Equal((ushort)WorldOpcode.SmsgAuthChallenge, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2)));
        byte[] payload = new byte[size - 2];
        await stream.ReadExactlyAsync(payload, cts.Token);
        return BinaryPrimitives.ReadUInt32LittleEndian(payload);
    }

    private static async Task WriteAuthSessionAsync(NetworkStream stream, uint serverSeed, byte[] sessionKey)
    {
        const uint clientSeed = 0xCAFEBABEu;
        byte[] accountBytes = Encoding.ASCII.GetBytes(AccountName);
        byte[] seedBytes = new byte[4];
        byte[] serverSeedBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(seedBytes, clientSeed);
        BinaryPrimitives.WriteUInt32LittleEndian(serverSeedBytes, serverSeed);
        byte[] digest = Sha1.Hash(accountBytes, new byte[4], seedBytes, serverSeedBytes, sessionKey);

        byte[] payload = new byte[8 + accountBytes.Length + 1 + 4 + 20];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), 5875);
        accountBytes.CopyTo(payload.AsSpan(8));
        int offset = 8 + accountBytes.Length + 1;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), clientSeed);
        digest.CopyTo(payload.AsSpan(offset + 4, 20));

        byte[] header = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0, 2), (ushort)(payload.Length + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2, 4), (uint)WorldOpcode.CmsgAuthSession);
        await stream.WriteAsync(header);
        await stream.WriteAsync(payload);
    }

    private sealed class SingleAccountStore(byte[] sessionKey) : IAccountStore
    {
        public Task<Account?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
            Task.FromResult<Account?>(string.Equals(username, AccountName, StringComparison.OrdinalIgnoreCase)
                ? new Account { Id = 1, Username = AccountName, Salt = new byte[32], Verifier = new byte[32], SessionKey = sessionKey, Status = AccountStatus.Active }
                : null);

        public Task<Account> CreateAsync(Account account, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UpdateCredentialsAsync(string username, byte[] salt, byte[] verifier, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UpdateSessionKeyAsync(string username, byte[] sessionKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UpdateLoginAsync(string username, byte[] sessionKey, string? address, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> UpdateSecurityAsync(string username, AccountSecurity security, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Reports no ban, but parks the chosen lookup until its token is cancelled or the test releases it.</summary>
    private sealed class BlockingBanStore(bool blockAccountLookup) : IBanStore, IDisposable
    {
        private readonly CancellationTokenSource _release = new();
        private readonly TaskCompletionSource<CancellationToken> _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CancellationToken> BlockedLookupToken => _blocked.Task;

        public void Release() => _release.Cancel();

        public void Dispose() => _release.Dispose();

        public async Task<AccountBanRecord?> GetActiveAccountBanAsync(int accountId, CancellationToken cancellationToken = default)
        {
            if (blockAccountLookup)
            {
                await ParkAsync(cancellationToken);
            }

            return null;
        }

        public async Task<IpBanRecord?> GetActiveIpBanAsync(string ip, CancellationToken cancellationToken = default)
        {
            if (!blockAccountLookup)
            {
                await ParkAsync(cancellationToken);
            }

            return null;
        }

        private async Task ParkAsync(CancellationToken cancellationToken)
        {
            _blocked.TrySetResult(cancellationToken);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _release.Token);
            await Task.Delay(Timeout.Infinite, linked.Token);
        }

        public Task<IReadOnlySet<int>> FindBannedAccountsAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlySet<string>> FindBannedIpsAsync(IReadOnlyCollection<string> ips, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task BanAccountAsync(BanRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> BanIpAsync(IpBanRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> UnbanAccountAsync(int accountId, string source, string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> UnbanIpAsync(string ip, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<AccountBanRecord>> GetHistoryAsync(int accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlySet<int>> FindAccountsWithHistoryAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<AccountBanRecord>> ListActiveAccountBansAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<IpBanRecord>> ListIpBansAsync(string prefix, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
