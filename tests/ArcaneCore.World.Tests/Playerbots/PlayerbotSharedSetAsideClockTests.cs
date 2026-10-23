using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The shared set-asides keep a record until its key is evicted, so a deadline can be weeks old when it is asked. Read as a 32-bit
/// difference, a deadline 2^31 ms (24.8 days) or more in the past looked like one in the future and the set-aside came back to life.
/// </summary>
public sealed class PlayerbotSharedSetAsideClockTests
{
    [Fact]
    public async Task AnEndedSetAside_StaysEnded_WhenTheWorldClockMovesTwoToThe31OrMoreMillisecondsOn()
    {
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
        world.UseManualClock(uint.MaxValue);
        world.Start();
        var shared = new PlayerbotSharedSetAsides { World = world };

        // 3e9 ms later (past 2^31): the 30 minute set-aside ended long ago.
        SetAsideByThreeBots(shared, world, 1);
        await world.AdvanceClockAsync(1_000_000);
        Assert.True(shared.IsQuestSetAside(1, world.NowMs), "still inside its 30 minutes");
        Assert.Single(shared.Export(world.NowMs, 1_000_000));
        await world.AdvanceClockAsync(3_000_000_000);
        Assert.False(shared.IsQuestSetAside(1, world.NowMs));
        Assert.Empty(shared.Export(world.NowMs, 1_000_000));

        // 2^32 - 900 s later: the 32-bit difference to its end reads as 900 s still to go.
        SetAsideByThreeBots(shared, world, 2);
        await world.AdvanceClockAsync(uint.MaxValue - 900_000);
        Assert.False(shared.IsQuestSetAside(2, world.NowMs));
        Assert.Empty(shared.Export(world.NowMs, 1_000_000));

        // The same record set aside again once the clock is past both marks counts from the present.
        SetAsideByThreeBots(shared, world, 2);
        Assert.True(shared.IsQuestSetAside(2, world.NowMs));
        await world.AdvanceClockAsync(PlayerbotSharedSetAsides.SetAsideMs + 1);
        Assert.False(shared.IsQuestSetAside(2, world.NowMs));
    }

    private static void SetAsideByThreeBots(PlayerbotSharedSetAsides shared, WorldRuntime world, uint quest)
    {
        for (ulong bot = 1; bot <= PlayerbotSharedSetAsides.BotsToShare; bot++)
            shared.Report(PlayerbotGoalKind.Quest, 0, quest, bot, world.NowMs);
        Assert.True(shared.IsQuestSetAside(quest, world.NowMs));
    }
}
