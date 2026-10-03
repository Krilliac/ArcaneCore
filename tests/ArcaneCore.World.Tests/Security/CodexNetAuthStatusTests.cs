using System.Net;
using System.Net.Sockets;
using ArcaneCore.Kernel.Accounts;
using Xunit;

namespace ArcaneCore.World.Tests.Security;

/// <summary>
/// Codex finding 4 (net/auth), replayed exactly as the report words it: "Log on while Active and
/// retain the 40-byte SRP session key K. After the account status becomes Banned or Suspended, open a
/// new world TCP connection, read its server seed, and send CMSG_AUTH_SESSION for that account with
/// SHA1(account || u32(0) || clientSeed || serverSeed || K)." Unlike WorldAuthStatusTests (which seeds a
/// non-active account directly), the key here is issued while the account is Active, a world login
/// succeeds with it, and only then does the status change.
///
/// Not covered, because the code base has no path for it: disconnecting an already-connected world
/// session when the status changes. IAccountStore has no status mutation and nothing raises a
/// status-change event; see docs/security/codex-net-auth.md (residual risk).
/// </summary>
public sealed class CodexNetAuthStatusTests
{

    [Theory]
    [InlineData(AccountStatus.Banned)]
    [InlineData(AccountStatus.Suspended)]
    public async Task KeyIssuedWhileActive_CannotAuthenticateAfterTheAccountIsDisabled(AccountStatus status)
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("RETIRED");
        Account account = (await host.Accounts.FindByUsernameAsync("RETIRED"))!;

        // 1. While Active: a normal world login with the key (proves the key itself is good).
        await using (WorldTestClient first = await host.ConnectAsync())
        {
            await first.AuthenticateAsync("RETIRED", key);
        }

        await WorldTestHost.WaitForAsync(() => host.Registry.Find(account.Id) is null, "the first session unregisters");

        // 2. The account becomes Banned/Suspended; the stored SessionKey is untouched ("no logon rotated it").
        account.Status = status;
        Assert.Equal(key, (await host.Accounts.FindByUsernameAsync("RETIRED"))!.SessionKey);

        // 3. A fresh world connection sends CMSG_AUTH_SESSION with the correct SHA1(account, 0, seeds, K).
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, host.Port);
        await using NetworkStream stream = tcp.GetStream();
        byte reply = await CodexNetAuthWorldTests.SendAuthSessionAsync(stream, "RETIRED", key);

        Assert.Equal(0x1C, reply); // AUTH_BANNED, vmangos WorldSocket.cpp:333-345
        Assert.True(await CodexNetAuthWorldTests.DrainUntilClosedAsync(stream, TimeSpan.FromSeconds(20)));
        Assert.Null(host.Registry.Find(account.Id));
    }
}
