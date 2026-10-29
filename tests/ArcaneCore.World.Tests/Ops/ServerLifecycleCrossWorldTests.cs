using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.Protocol;
using ArcaneCore.World.Ops.Lifecycle;
using ArcaneCore.World.Tests.Cluster.Supervision;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>
/// <c>.server shutdown|restart #delay #exitcode #realmid</c>: a GM on one world server stops or restarts another through the cluster host
/// supervisor. The request leaves as one protocol line on stdout; without a supervisor the command says so and does nothing.
/// </summary>
public sealed class ServerLifecycleCrossWorldTests
{
    private static async Task<string> CommandAsync(WorldTestClient client, string line)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, line);
        ChatMessage reply = await client.ReadChatAsync();
        Assert.Equal(ChatType.System, reply.Type);
        return reply.Text;
    }

    private static string[] Lines(StringWriter writer)
    {
        lock (writer)
        {
            return writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }

    [Fact]
    public async Task ServerRestart_WithOtherRealmId_EmitsRequest_AndDoesNotStopThisWorld()
    {
        var stdout = new StringWriter();
        await using var host = WorldTestHost.Start(configureServices: s => s.AddSingleton(new SupervisorProtocolChannel(TextWriter.Synchronized(stdout), "tok", ownRealmId: 1)));
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        ServerLifecycleFeature feature = host.WorldServices.GetRequiredService<ServerLifecycleFeature>();

        string reply = await CommandAsync(admin, ".server restart 60 2 3");

        Assert.Equal(["lifecycle request token=tok realm=3 action=restart delay=60 code=2 by=Admin"], Lines(stdout));
        Assert.Contains("realm 3", reply, StringComparison.Ordinal);
        Assert.Contains("supervisor", reply, StringComparison.OrdinalIgnoreCase);
        Assert.False(feature.IsDraining); // this world keeps running
        Assert.Equal(ExitCodes.Success, ExitCodes.Current);
    }

    [Fact]
    public async Task ServerShutdown_WithOtherRealmId_EmitsShutdownRequest()
    {
        var stdout = new StringWriter();
        await using var host = WorldTestHost.Start(configureServices: s => s.AddSingleton(new SupervisorProtocolChannel(TextWriter.Synchronized(stdout), "tok", ownRealmId: 1)));
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);

        await CommandAsync(admin, ".server shutdown 0 0 2");

        Assert.Equal(["lifecycle request token=tok realm=2 action=shutdown delay=0 code=0 by=Admin"], Lines(stdout));
    }

    [Fact]
    public async Task ServerRestart_WithOtherRealmId_NoSupervisor_ReportsAndDoesNothing()
    {
        await using var host = WorldTestHost.Start(); // no supervisor channel registered, and the test process is not a supervised child
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        ServerLifecycleFeature feature = host.WorldServices.GetRequiredService<ServerLifecycleFeature>();

        string reply = await CommandAsync(admin, ".server restart 60 2 3");

        Assert.Contains("supervisor", reply, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nothing", reply, StringComparison.OrdinalIgnoreCase);
        Assert.False(feature.IsDraining);
        Assert.Equal(ExitCodes.Success, ExitCodes.Current);
    }

    [Fact]
    public async Task ServerRestart_WithThisWorldsOwnRealmId_RunsHere()
    {
        var stdout = new StringWriter();
        await using var host = WorldTestHost.Start(configureServices: s => s.AddSingleton(new SupervisorProtocolChannel(TextWriter.Synchronized(stdout), "tok", ownRealmId: 1)));
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        ServerLifecycleFeature feature = host.WorldServices.GetRequiredService<ServerLifecycleFeature>();

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".server restart 60 2 1");
        await admin.ReadUntilAsync(WorldOpcode.SmsgServerMessage);

        Assert.True(feature.IsDraining);
        Assert.Empty(Lines(stdout)); // nothing went to the supervisor
        await host.World.InvokeAsync(() =>
        {
            feature.Cancel();
            return 0;
        });
    }

    [Theory]
    [InlineData(".server restart 60 2 0")]
    [InlineData(".server restart 60 2 x")]
    [InlineData(".server restart 60 2 3 4")]
    [InlineData(".server idlerestart 60 2 3")]
    [InlineData(".server idleshutdown 60 0 3")]
    public async Task ServerLifecycle_BadRealmArguments_AreUsageErrors_AndSendNothing(string command)
    {
        var stdout = new StringWriter();
        await using var host = WorldTestHost.Start(configureServices: s => s.AddSingleton(new SupervisorProtocolChannel(TextWriter.Synchronized(stdout), "tok", ownRealmId: 1)));
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        ServerLifecycleFeature feature = host.WorldServices.GetRequiredService<ServerLifecycleFeature>();

        string reply = await CommandAsync(admin, command);

        Assert.Contains("Syntax", reply, StringComparison.Ordinal);
        Assert.Empty(Lines(stdout));
        Assert.False(feature.IsDraining);
    }

    [Fact]
    public async Task ServerRestart_WithOtherRealmId_IsStillAdministratorOnly()
    {
        var stdout = new StringWriter();
        await using var host = WorldTestHost.Start(configureServices: s => s.AddSingleton(new SupervisorProtocolChannel(TextWriter.Synchronized(stdout), "tok", ownRealmId: 1)));
        await using WorldTestClient player = await host.EnterWorldAsync("PLAYER", "Player");

        await player.SendChatAsync(ChatType.Say, Language.Common, ".server restart 60 2 3");
        await Task.Delay(100);

        Assert.Empty(Lines(stdout));
    }

    [Fact]
    public async Task ForwardedLifecycleRequest_StartsTheCountdownOnTheWorldThread()
    {
        await using var host = WorldTestHost.Start();
        ServerLifecycleFeature feature = host.WorldServices.GetRequiredService<ServerLifecycleFeature>();
        var handler = (ISupervisedLifecycleRequestHandler)feature;
        try
        {
            handler.Request(new SupervisorLifecycleRequest(string.Empty, 0, SupervisorLifecycleAction.Restart, 30, ExitCodes.Restart, "Other"));

            await TestWait.UntilAsync(() => feature.IsDraining, "the forwarded restart to start its countdown");
            handler.Result(new SupervisorLifecycleResult(4, SupervisorLifecycleStatus.UnknownRealm, "Other")); // reported, never throws
        }
        finally
        {
            await host.World.InvokeAsync(() =>
            {
                feature.Cancel();
                return 0;
            });
            ExitCodes.Current = ExitCodes.Success;
        }
    }

    [Fact]
    public void SupervisorProtocolChannel_FromEnvironment_NeedsTheProtocolAndTheToken()
    {
        var output = new StringWriter();
        Func<string, string?> Env(params (string Key, string Value)[] values) => key => values.FirstOrDefault(v => v.Key == key).Value;

        Assert.False(SupervisorProtocolChannel.FromEnvironment(Env(), output).IsSupervised);
        Assert.False(SupervisorProtocolChannel.FromEnvironment(Env((SupervisorProtocol.EnvironmentVariable, "1")), output).IsSupervised); // no token
        SupervisorProtocolChannel channel = SupervisorProtocolChannel.FromEnvironment(
            Env((SupervisorProtocol.EnvironmentVariable, "1"), (SupervisorProtocol.TokenEnvironmentVariable, "tok"), (SupervisorProtocol.RealmIdEnvironmentVariable, "4")), output);

        Assert.True(channel.IsSupervised);
        Assert.Equal(4, channel.OwnRealmId);
        Assert.Equal(0, SupervisorProtocolChannel.FromEnvironment(Env((SupervisorProtocol.EnvironmentVariable, "1"), (SupervisorProtocol.TokenEnvironmentVariable, "tok")), output).OwnRealmId);
    }
}
