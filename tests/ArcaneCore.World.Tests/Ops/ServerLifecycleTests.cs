using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.Protocol;
using ArcaneCore.World.Ops.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>.server shutdown/restart on a real world thread and real sessions.</summary>
public sealed class ServerLifecycleTests
{
    private static async Task<string> CommandAsync(WorldTestClient client, string line)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, line);
        ChatMessage reply = await client.ReadChatAsync();
        Assert.Equal(ChatType.System, reply.Type);
        return reply.Text;
    }

    private static byte[] Expected(ServerMessageType type, string text)
        => [.. BitConverter.GetBytes((uint)type), .. System.Text.Encoding.UTF8.GetBytes(text), 0];

    [Fact]
    public async Task Shutdown_BroadcastsType1Packet_ToInWorldPlayersOnly()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await using WorldTestClient player = await host.EnterWorldAsync("PLAYER", "Player");

        // Authenticated with a character, but still at the character screen.
        byte[] key = await host.AddAccountAsync("SCREEN");
        await using WorldTestClient screen = await host.ConnectAsync();
        await screen.AuthenticateAsync("SCREEN", key);
        await screen.CreateCharacterAsync("Screen");
        await admin.CollectAsync();
        await player.CollectAsync();
        await screen.CollectAsync();

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".server shutdown 30");

        Assert.Equal(Expected(ServerMessageType.ShutdownTime, "30 Seconds."), await player.ReadUntilAsync(WorldOpcode.SmsgServerMessage));
        Assert.Equal(Expected(ServerMessageType.ShutdownTime, "30 Seconds."), await admin.ReadUntilAsync(WorldOpcode.SmsgServerMessage));
        Assert.DoesNotContain(await screen.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgServerMessage);

        ServerLifecycleFeature feature = host.WorldServices.GetRequiredService<ServerLifecycleFeature>();
        Assert.True(feature.IsDraining);
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".server shutdown cancel");
        Assert.Equal(Expected(ServerMessageType.ShutdownCancelled, string.Empty), await player.ReadUntilAsync(WorldOpcode.SmsgServerMessage));
        Assert.False(feature.IsDraining);
    }

    [Fact]
    public async Task Restart_WhenTheCountdownExpires_RequestsStopWithExitCode2()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        ServerLifecycleFeature feature = host.WorldServices.GetRequiredService<ServerLifecycleFeature>();
        int? stopCode = null;
        feature.StopRequested += code => stopCode = code;

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".server restart 30");
        Assert.Equal(Expected(ServerMessageType.RestartTime, "30 Seconds."), await admin.ReadUntilAsync(WorldOpcode.SmsgServerMessage));
        Assert.Null(stopCode);

        await host.World.InvokeAsync(() =>
        {
            feature.Advance(30);
            return 0;
        });

        Assert.Equal(ExitCodes.Restart, stopCode);
        Assert.Equal(ExitCodes.Restart, ExitCodes.Current);
        ExitCodes.Current = ExitCodes.Success;
    }

    [Fact]
    public async Task Commands_RequireAdministrator_AndRejectBadArguments()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("STAFF", "Staff", AccountSecurity.GameMaster);
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await admin.CollectAsync();

        Assert.StartsWith("There is no such subcommand.", await CommandAsync(gm, ".server shutdown 10"));
        Assert.StartsWith("Incorrect syntax.", await CommandAsync(admin, ".server shutdown"));
        Assert.StartsWith("Incorrect syntax.", await CommandAsync(admin, ".server shutdown soon"));
        Assert.StartsWith("Incorrect syntax.", await CommandAsync(admin, ".server restart 10 126"));
        Assert.False(host.WorldServices.GetRequiredService<ServerLifecycleFeature>().IsDraining);
    }
}
