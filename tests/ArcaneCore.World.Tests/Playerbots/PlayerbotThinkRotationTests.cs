using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The bots' think rotation at scale (scale campaign 2026-10-08): 300 bots live, about 30 thinks per tick, and every ghost walking to
/// a spirit healer faulted because it went 13.5 s between thinks (PlayerbotRecovery.StuckMs is 10 s).
/// </summary>
public sealed class PlayerbotThinkRotationTests
{
    /// <summary>The longest run of ticks any bot waits between two of its thinks, over <paramref name="ticks"/> ticks.</summary>
    private static int LongestWait(int bots, int perTick, int ticks)
    {
        var rotation = new PlayerbotThinkRotation();
        var last = new int[bots];
        Array.Fill(last, -1);
        int longest = 0;
        for (int tick = 0; tick < ticks; tick++)
        {
            int budget = perTick;
            rotation.Tick(bots, index =>
            {
                if (budget == 0) return false;
                budget--;
                if (last[index] >= 0) longest = Math.Max(longest, tick - last[index]);
                last[index] = tick;
                return true;
            });
        }

        Assert.DoesNotContain(-1, last);
        return longest;
    }

    [Theory]
    [InlineData(300, 30)]   // the live case: 300 bots, ~30 thinks per tick
    [InlineData(1000, 12)]  // MaxBots 1000 with only the action budget (MaxActionsPerTick 12) per tick
    [InlineData(205, 30)]
    [InlineData(7, 3)]
    public void EveryBotThinksAtLeastOncePerCeilOfBotsOverThinksPerTick(int bots, int perTick)
    {
        int bound = (bots + perTick - 1) / perTick;
        int longest = LongestWait(bots, perTick, 20 * bound);
        Assert.True(longest <= bound, $"a bot waited {longest} ticks between thinks (bound {bound})");
        // 300 bots at 30 per tick: 10 ticks, 0.5 s. Before, the cursor moved one bot per tick: 271 ticks, 13.5 s.
        Assert.True(longest * 50 < PlayerbotRecovery.StuckMs, $"{longest * 50} ms between thinks reaches the recovery's stuck bound");
    }

    [Fact]
    public void AllBotsFit_EachThinksEveryTick()
    {
        Assert.Equal(1, LongestWait(bots: 10, perTick: 12, ticks: 50));
    }

    [Fact]
    public void ATickThatVisitsNobody_StillMovesOn()
    {
        var rotation = new PlayerbotThinkRotation();
        rotation.Tick(5, _ => false);
        Assert.Equal(1, rotation.Cursor);
        Assert.Equal(0, rotation.Tick(5, _ => false));
        Assert.Equal(2, rotation.Cursor);
    }

    [Fact]
    public void TheSetShrinking_KeepsTheCursorInRange()
    {
        var rotation = new PlayerbotThinkRotation();
        int seen = 0;
        rotation.Tick(10, _ => seen++ < 8);
        Assert.Equal(8, rotation.Cursor);
        var visited = new List<int>();
        rotation.Tick(3, index => { visited.Add(index); return true; });
        Assert.Equal([2, 0, 1], visited);
        Assert.Equal(0, rotation.Tick(0, _ => true));
        Assert.Equal(0, rotation.Cursor);
    }

    private static Guid[] Ids(params int[] values) => values.Select(v => new Guid(v, 0, 0, new byte[8])).ToArray();

    /// <summary>One <see cref="PlayerbotThinkRotation.TickLanes"/> over <paramref name="bots"/> that thinks for <paramref name="perTick"/> bots; returns the ids it thought for.</summary>
    private static List<Guid> LaneTick(PlayerbotThinkRotation rotation, Guid[] bots, int perTick)
    {
        rotation.Track(bots, id => id);
        var thought = new List<Guid>();
        int budget = perTick;
        rotation.TickLanes(bots.Length, 100, 0.5, _ => PlayerbotThinkRotation.ThinkLane.Normal, (index, _) =>
        {
            if (budget == 0) return false;
            budget--;
            thought.Add(bots[index]);
            return true;
        }, () => 0);
        return thought;
    }

    [Fact]
    public void ABotLeavingBeforeTheCursor_DoesNotMakeTheBotAtTheCursorSkipAPass()
    {
        var rotation = new PlayerbotThinkRotation();
        Guid[] bots = Ids(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
        Assert.Equal(Ids(1, 2, 3), LaneTick(rotation, bots, 3));
        Assert.Equal(3, rotation.Cursor); // bot 4 is next

        // Bot 1 (index 0) leaves between ticks; the population is rebuilt in id order.
        Guid[] rebuilt = bots[1..];
        List<Guid> next = LaneTick(rotation, rebuilt, 3);
        Assert.True(next.Contains(bots[3]) && next[0] == bots[3], $"bot 4 was skipped; the tick thought for {string.Join(",", next.Select(g => g.ToString()[..8]))}");
    }

    [Fact]
    public void ABotJoiningBeforeTheCursor_DoesNotMakeABotThinkTwiceInAPass()
    {
        var rotation = new PlayerbotThinkRotation();
        Guid[] bots = Ids(10, 20, 30, 40, 50, 60);
        Assert.Equal(Ids(10, 20, 30), LaneTick(rotation, bots, 3));
        Guid[] joined = Ids(5, 10, 20, 30, 40, 50, 60);
        Assert.Equal(Ids(40, 50, 60), LaneTick(rotation, joined, 3));
        Assert.Equal(Ids(5, 10, 20), LaneTick(rotation, joined, 3));
    }

    [Fact]
    public void WhenTheLastBotVisitedLeavesAndNoneIsAfterIt_TheRotationWrapsToTheFirst()
    {
        var rotation = new PlayerbotThinkRotation();
        Guid[] bots = Ids(1, 2, 3, 4);
        Assert.Equal(Ids(1, 2, 3, 4), LaneTick(rotation, bots, 4));
        Assert.Equal(Ids(1, 2, 3), LaneTick(rotation, Ids(1, 2, 3), 3));
        Assert.Equal(Ids(1, 2), LaneTick(rotation, Ids(1, 2), 2));
        Assert.Empty(LaneTick(rotation, [], 2));
        Assert.Equal(Ids(7), LaneTick(rotation, Ids(7), 2));
    }
}
