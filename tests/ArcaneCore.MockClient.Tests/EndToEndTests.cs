using System.Net.Sockets;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>Real realm/world sessions and SQLite stores, reached only through owned loopback sockets.</summary>
public sealed class EndToEndTests
{
    [Fact]
    public async Task Lifecycle_CombatRewardsPersistExactlyOnceAndPreserveExistingNpcAndJournalChecks()
    {
        MockScenarioReport report = await MockScenarios.RunAsync();
        Assert.Equal("passed", report.Outcome);
        Assert.Equal((ushort)5875, report.ClientBuild);
        Assert.True(report.CharacterGuid > 0);
        Assert.True(report.FramesReceived >= 30);
        Assert.Equal(59, report.CheckCount);
        Assert.Equal(report.CheckCount, report.Checks.Select(check => check.Name).Distinct().Count());
        Assert.All(report.Checks, check => Assert.True(check.Passed, check.Detail));
        Assert.Contains(report.Checks, check => check.Name == "realm.srp");
        Assert.Contains(report.Checks, check => check.Name == "journal.initial-fields");
        Assert.Contains(report.Checks, check => check.Name == "journal.relogin-fields");
        Assert.Contains(report.Checks, check => check.Name == "npc.status");
        Assert.Contains(report.Checks, check => check.Name == "npc.details");
        Assert.Contains(report.Checks, check => check.Name == "npc.accept-fields");
        Assert.Contains(report.Checks, check => check.Name == "npc.accept-persisted");
        Assert.Contains(report.Checks, check => check.Name == "npc.relogin-fields");
        Assert.Contains(report.Checks, check => check.Name == "npc.abandon-fields");
        Assert.Contains(report.Checks, check => check.Name == "npc.abandon-persisted");
        Assert.Contains(report.Checks, check => check.Name == "npc.abandon-relogin");
        Assert.Contains(report.Checks, check => check.Name == "reward.live-targets");
        Assert.Contains(report.Checks, check => check.Name == "reward.accept-fields");
        Assert.Contains(report.Checks, check => check.Name == "reward.kill-partial");
        Assert.Contains(report.Checks, check => check.Name == "reward.kill-complete");
        Assert.Contains(report.Checks, check => check.Name == "reward.complete-offer");
        Assert.Contains(report.Checks, check => check.Name == "reward.request-offer");
        Assert.Contains(report.Checks, check => check.Name == "reward.choose-complete");
        Assert.Contains(report.Checks, check => check.Name == "reward.inventory-money-fields");
        Assert.Contains(report.Checks, check => check.Name == "reward.atomic-store");
        Assert.Contains(report.Checks, check => check.Name == "reward.duplicate-choice");
        Assert.Contains(report.Checks, check => check.Name == "reward.relogin-fields");
        Assert.Contains(report.Checks, check => check.Name == "reward.relogin-history");
        Assert.Contains(report.Checks, check => check.Name == "npc.greeting-list");
        Assert.Contains(report.Checks, check => check.Name == "npc.questgiver-hello");
        Assert.Contains(report.Checks, check => check.Name == "npc.greeting-selection");
        Assert.Contains(report.Checks, check => check.Name == "npc.greeting-relogin");
        Assert.Contains(report.Checks, check => check.Name == "npc.greeting-abandon");
        Assert.Contains(report.Checks, check => check.Name == "reward.greeting-mixed-incomplete");
        Assert.Contains(report.Checks, check => check.Name == "reward.greeting-current-selection");
        Assert.Contains(report.Checks, check => check.Name == "reward.greeting-incomplete");
        Assert.Contains(report.Checks, check => check.Name == "reward.greeting-partial");
        Assert.Contains(report.Checks, check => check.Name == "reward.greeting-complete");
        Assert.Contains(report.Checks, check => check.Name == "reward.greeting-mixed-complete");
        Assert.Contains(report.Checks, check => check.Name == "reward.greeting-complete-selection");
        Assert.Contains(report.Checks, check => check.Name == "reward.greeting-restore-slot");
        Assert.Contains(report.Checks, check => check.Name == "reward.greeting-rewarded");
        Assert.Contains(report.Checks, check => check.Name == "reward.greeting-relogin");
        Assert.Contains(report.Checks, check => check.Name == "fixture.disposed");
    }

    [Theory]
    [InlineData("KNOWN", "WRONGPASSWORD", 5875, 0x04)]
    [InlineData("UNKNOWN", "PASSWORD", 5875, 0x04)]
    [InlineData("KNOWN", "PASSWORD", 12340, 0x09)]
    public async Task RealmAuthentication_RejectsBadPasswordUnknownAccountAndWrongBuild(
        string account, string password, int build, int expectedResult)
    {
        using var deadline = TestDeadline();
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(deadline.Token);
        await server.AddAccountAsync("KNOWN", "PASSWORD", deadline.Token);

        MockAuthException rejection = await Assert.ThrowsAsync<MockAuthException>(() =>
            LogonClient.AuthenticateAsync(server.RealmEndpoint, account, password, deadline.Token, (ushort)build));
        Assert.Equal((byte)expectedResult, rejection.Result);
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        Account? stored = await scope.ServiceProvider.GetRequiredService<IAccountStore>().FindByUsernameAsync("KNOWN", deadline.Token);
        Assert.NotNull(stored);
        Assert.Null(stored.SessionKey);
        Assert.Equal(0, server.World.OnlinePlayerCount);
    }

    [Theory]
    [InlineData(true, 5875, 0x0D)]
    [InlineData(false, 12340, 0x14)]
    public async Task WorldAuthentication_RejectsIncorrectProofAndWrongBuildWithPlaintextReply(
        bool corruptKey, int build, int expectedResult)
    {
        using var deadline = TestDeadline();
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(deadline.Token);
        await server.AddAccountAsync("WORLDTEST", "PASSWORD", deadline.Token);
        LogonResult logon = await LogonClient.AuthenticateAsync(server.RealmEndpoint, "WORLDTEST", "PASSWORD", deadline.Token);
        byte[] key = (byte[])logon.SessionKey.Clone();
        if (corruptKey)
        {
            key[0] ^= 0x01;
        }

        await using WorldClient client = await WorldClient.ConnectAsync(Assert.Single(logon.Realms).GetLoopbackEndpoint(), deadline.Token);
        byte result = await client.AuthenticateAsync("WORLDTEST", key, deadline.Token, (uint)build);
        Assert.Equal((byte)expectedResult, result);
        Assert.Equal(0, server.World.OnlinePlayerCount);
    }

    [Fact]
    public async Task CharacterEnumeration_BeforeAuthenticationDisconnectsTheOwnedClient()
    {
        using var deadline = TestDeadline();
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(deadline.Token);
        await using WorldClient client = await WorldClient.ConnectAsync(server.WorldEndpoint, deadline.Token);
        WorldFrame challenge = await client.ReadAsync(deadline.Token);
        Assert.Equal((ushort)WorldOpcode.SmsgAuthChallenge, challenge.Opcode);
        Assert.Equal(4, challenge.Payload.Length);
        await client.SendAsync((ushort)WorldOpcode.CmsgCharEnum, ReadOnlyMemory<byte>.Empty, deadline.Token);
        await AssertDisconnectedAsync(client, deadline.Token);
        Assert.Equal(0, server.World.OnlinePlayerCount);
    }

    [Fact]
    public async Task ForeignCharacterLogin_FailsWithoutEnteringTheWorld()
    {
        using var deadline = TestDeadline();
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(deadline.Token);
        await server.AddAccountAsync("OWNER", "PASSWORD", deadline.Token);
        await server.AddAccountAsync("OTHER", "PASSWORD", deadline.Token);
        await using WorldClient owner = await AuthenticateWorldAsync(server, "OWNER", deadline.Token);
        var ownerSession = new ScenarioConnection(owner);
        await ownerSession.CreateCharacterAsync("Ownedhero", deadline.Token);
        ulong guid = Assert.Single(await ownerSession.EnumerateAsync(deadline.Token)).Guid;

        await using WorldClient other = await AuthenticateWorldAsync(server, "OTHER", deadline.Token);
        var otherSession = new ScenarioConnection(other);
        await otherSession.SendAsync(WorldOpcode.CmsgPlayerLogin, ScenarioWire.Guid(guid), deadline.Token);
        byte[] result = await otherSession.ExpectAsync(WorldOpcode.SmsgCharacterLoginFailed, deadline.Token);
        Assert.Equal(new byte[] { 0x43 }, result);
        Assert.Equal(0, server.World.OnlinePlayerCount);
        Assert.Empty(await otherSession.EnumerateAsync(deadline.Token));
    }

    [Fact]
    public async Task QuestQuery_WithThreeBytesWhileLoggedInDisconnectsAndRemovesThePlayer()
    {
        using var deadline = TestDeadline();
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(deadline.Token);
        await server.AddAccountAsync("QUERYTEST", "PASSWORD", deadline.Token);
        await using WorldClient client = await AuthenticateWorldAsync(server, "QUERYTEST", deadline.Token);
        var session = new ScenarioConnection(client);
        await session.CreateCharacterAsync("Queryhero", deadline.Token);
        ulong guid = Assert.Single(await session.EnumerateAsync(deadline.Token)).Guid;
        await session.LoginAsync(guid, deadline.Token);
        Assert.Equal(1, server.World.OnlinePlayerCount);

        await session.SendAsync(WorldOpcode.CmsgQuestQuery, [1, 2, 3], deadline.Token);
        await AssertDisconnectedAsync(client, deadline.Token);
        // Disconnect cleanup is a command on the real simulation thread; this barrier waits for it.
        int online = await server.World.InvokeAsync(() => server.World.OnlinePlayerCount).WaitAsync(deadline.Token);
        Assert.Equal(0, online);
    }

    private static async Task<WorldClient> AuthenticateWorldAsync(SyntheticArcaneServer server, string account, CancellationToken token)
    {
        LogonResult logon = await LogonClient.AuthenticateAsync(server.RealmEndpoint, account, "PASSWORD", token);
        WorldClient client = await WorldClient.ConnectAsync(Assert.Single(logon.Realms).GetLoopbackEndpoint(), token);
        try
        {
            Assert.Equal((byte)0x0C, await client.AuthenticateAsync(account, logon.SessionKey, token));
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private static async Task AssertDisconnectedAsync(WorldClient client, CancellationToken token)
    {
        for (int index = 0; index < 128; index++)
        {
            try
            {
                await client.ReadAsync(token);
            }
            catch (EndOfStreamException)
            {
                return;
            }
            catch (IOException exception) when (exception.InnerException is SocketException)
            {
                return; // a reset of this owned socket is also a server disconnect
            }
            catch (SocketException)
            {
                return;
            }
        }

        Assert.Fail("Owned server did not disconnect the client within 128 frames.");
    }

    private static CancellationTokenSource TestDeadline() => new(TimeSpan.FromSeconds(20));
}
