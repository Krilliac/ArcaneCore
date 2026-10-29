using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>The in-process world host on the manual clock: delivery barrier, game-time budgets, reproducible runs.</summary>
[Collection("World tick load")]
public sealed class WorldTestHostManualClockTests
{
    [Fact]
    public async Task ASentPacket_IsHandledOnTheNextStep_NotBefore()
    {
        await using WorldTestHost host = WorldTestHost.Start(clock: TestClock.Manual());
        WorldTestClient client = await host.EnterWorldAsync("clockacct", "Clocky");
        await host.SettleAsync();
        long gameBefore = host.World.Clock.ElapsedMs;

        await client.SendAsync(WorldOpcode.CmsgPlayedTime, []);
        await host.StepAsync(1); // the barrier waits for the packet to arrive, then the step handles it

        await client.ReadUntilAsync(WorldOpcode.SmsgPlayedTime);
        Assert.Equal(gameBefore + 50, host.World.Clock.ElapsedMs);
        Assert.Equal(0, host.InboundWorldQueueDepth());
    }

    [Fact]
    public async Task RunUntilAsync_FailsByGameBudget_WithGameAndWallInTheMessage()
    {
        await using WorldTestHost host = WorldTestHost.Start(clock: TestClock.Manual());
        long gameBefore = host.World.Clock.ElapsedMs;

        GameTimeoutException ex = await Assert.ThrowsAsync<GameTimeoutException>(
            () => host.RunUntilAsync(() => false, "something that never happens", maxGameMs: 200));

        Assert.Contains("something that never happens", ex.Message);
        Assert.Contains("200 game ms", ex.Message);
        Assert.Contains(" ticks", ex.Message);
        Assert.Contains("wall ms", ex.Message);
        Assert.Equal(gameBefore + 200, host.World.Clock.ElapsedMs);
    }

    [Fact]
    public async Task TwoRunsWithTheSameSeedAndSteps_ProduceIdenticalOutboundBytes()
    {
        // Scoped down from a full cast/combat/loot run: a scripted login, chat and played-time interaction over fixed steps.
        List<(WorldOpcode Opcode, byte[] Payload)> first = await RunScriptedAsync();
        List<(WorldOpcode Opcode, byte[] Payload)> second = await RunScriptedAsync();

        Assert.NotEmpty(first);
        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Opcode, second[i].Opcode);
            Assert.True(first[i].Payload.AsSpan().SequenceEqual(second[i].Payload), $"packet {i} ({first[i].Opcode}) differs between the runs");
        }
    }

    private static async Task<List<(WorldOpcode Opcode, byte[] Payload)>> RunScriptedAsync()
    {
        await using WorldTestHost host = WorldTestHost.Start(clock: TestClock.Manual(startMs: 1000));
        WorldTestClient client = await host.EnterWorldAsync("repro", "Repro");
        await host.StepAsync(2);
        await client.SendChatAsync(ChatType.Say, Language.Common, "hello clock");
        await client.SendAsync(WorldOpcode.CmsgPlayedTime, []);
        await host.StepAsync(3);
        await host.AdvanceAsync(120);
        byte[] chat = await client.ReadUntilAsync(WorldOpcode.SmsgMessagechat);
        byte[] played = await client.ReadUntilAsync(WorldOpcode.SmsgPlayedTime);
        return [(WorldOpcode.SmsgMessagechat, chat), (WorldOpcode.SmsgPlayedTime, played)];
    }

    [Fact]
    public async Task GameWait_OnARealClockHost_StillPollsWallTime()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        Assert.False(host.Clock.IsManual);
        int evaluations = 0;

        await GameWait.UntilAsync(host, () => ++evaluations >= 3, "three evaluations");
        await GameWait.UntilWorldAsync(host, () => evaluations >= 5 || ++evaluations >= 5, "five evaluations");

        Assert.True(evaluations >= 5);
        Assert.Equal(ArcaneCore.Game.Maps.WorldClockMode.Realtime, WorldClockModeOf(host));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StepAsync());
    }

    private static ArcaneCore.Game.Maps.WorldClockMode WorldClockModeOf(WorldTestHost host) => host.World.Clock.Mode;
}
