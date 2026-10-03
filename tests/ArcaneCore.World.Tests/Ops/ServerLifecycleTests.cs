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

    // The real one second timer, the elapsed-seconds arithmetic and the stop hand-off: no Advance call.
    [Fact]
    public async Task RealTimer_CountsDownAnnouncesAndStopsWithExitCode2()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        ServerLifecycleFeature feature = host.WorldServices.GetRequiredService<ServerLifecycleFeature>();
        var stopped = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        feature.StopRequested += code => stopped.TrySetResult(code);
        try
        {
            await admin.CollectAsync();
            await admin.SendChatAsync(ChatType.Say, Language.Common, ".server restart 2");

            Task winner = await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.Same(stopped.Task, winner);
            Assert.Equal(ExitCodes.Restart, await stopped.Task);
            Assert.Equal(ExitCodes.Restart, ExitCodes.Current);

            // The stop is observed on the world thread; the last announcement may still be in flight to the client socket,
            // so accumulate until both have arrived (or a generous deadline) rather than trusting one collect.
            List<byte[]> messages = [];
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (messages.Count < 2 && DateTime.UtcNow < deadline)
            {
                messages.AddRange((await admin.CollectAsync()).Where(p => p.Opcode == WorldOpcode.SmsgServerMessage).Select(p => p.Payload));
            }

            Assert.Equal(
                [Expected(ServerMessageType.RestartTime, "2 Seconds."), Expected(ServerMessageType.RestartTime, "1 Second.")],
                messages);
        }
        finally
        {
            ExitCodes.Current = ExitCodes.Success;
        }
    }

    // Cancel then restart around the moment the previous timer would see the empty countdown.
    [Theory]
    [InlineData(950)]
    [InlineData(1000)]
    [InlineData(1010)]
    [InlineData(1030)]
    public async Task CancelThenRestart_AroundTheTimerTick_StillCountsDown(int cancelAfterMs)
    {
        await using var host = WorldTestHost.Start();
        ServerLifecycleFeature feature = host.WorldServices.GetRequiredService<ServerLifecycleFeature>();
        var stopped = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        feature.StopRequested += code => stopped.TrySetResult(code);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                feature.Request(60, ShutdownMask.None, ExitCodes.Success);
                return 0;
            });
            await Task.Delay(cancelAfterMs);
            await host.World.InvokeAsync(() =>
            {
                feature.Cancel();
                feature.Request(2, ShutdownMask.Restart, ExitCodes.Restart);
                return 0;
            });

            Task winner = await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.Same(stopped.Task, winner);
            Assert.Equal(ExitCodes.Restart, await stopped.Task);
        }
        finally
        {
            ExitCodes.Current = ExitCodes.Success;
        }
    }

    [Fact]
    public async Task Commands_RequireAdministrator_AndRejectBadArguments()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("STAFF", "Staff", AccountSecurity.GameMaster);
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await admin.CollectAsync();

        Assert.StartsWith("This command is not available to you.", await CommandAsync(gm, ".server shutdown 10")); // Wave-2 integration: the GM lane's retail command texts (no trailing period; below-level commands answer CommandUnavailable).
        Assert.StartsWith("Syntax: .server shutdown", await CommandAsync(admin, ".server shutdown")); await admin.CollectAsync(); // the GM lane prints the help text in place of "Incorrect syntax."
        Assert.StartsWith("Syntax: .server shutdown", await CommandAsync(admin, ".server shutdown soon")); await admin.CollectAsync();
        Assert.StartsWith("Syntax: .server restart", await CommandAsync(admin, ".server restart 10 126"));
        Assert.False(host.WorldServices.GetRequiredService<ServerLifecycleFeature>().IsDraining);
    }
}
