using ArcaneCore.Kernel.Accounts;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

public sealed class RealmPinMockTests
{
    [Fact]
    public async Task RealMockClient_SendsPinData_AndWrongOrMissingPinCannotIssueSessionKey()
    {
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync();
        await server.AddAccountAsync("PINTEST", "PASSWORD");
        await using (AsyncServiceScope scope = server.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IAccountLoginSecurityStore>()
                .SetAsync("PINTEST", AccountLockFlags.FixedPin | AccountLockFlags.AlwaysEnforce, "123456"));
        }

        MockAuthException missing = await Assert.ThrowsAsync<MockAuthException>(() =>
            LogonClient.AuthenticateAsync(server.RealmEndpoint, "PINTEST", "PASSWORD"));
        Assert.Equal((byte)4, missing.Result); // WOW_FAIL_UNKNOWN_ACCOUNT
        MockAuthException wrong = await Assert.ThrowsAsync<MockAuthException>(() =>
            LogonClient.AuthenticateAsync(server.RealmEndpoint, "PINTEST", "PASSWORD", pin: "654321"));
        Assert.Equal((byte)4, wrong.Result);
        await using (AsyncServiceScope scope = server.Services.CreateAsyncScope())
            Assert.Null((await scope.ServiceProvider.GetRequiredService<IAccountStore>()
                .FindByUsernameAsync("PINTEST"))!.SessionKey);

        LogonResult good = await LogonClient.AuthenticateAsync(server.RealmEndpoint, "PINTEST", "PASSWORD",
            pin: "123456");
        Assert.Single(good.Realms);
        Assert.Equal(40, good.SessionKey.Length);
        await using (AsyncServiceScope scope = server.Services.CreateAsyncScope())
        {
            Account stored = (await scope.ServiceProvider.GetRequiredService<IAccountStore>().FindByUsernameAsync("PINTEST"))!;
            Assert.Equal(good.SessionKey, stored.SessionKey);
            Assert.Equal("127.0.0.1", stored.LastIp); // the trusted address IP_LOCK compares against next time
        }
    }
}
