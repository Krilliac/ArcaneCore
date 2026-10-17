using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.Game.Tests.Persistence;

/// <summary>
/// The periodic save (docs/areas/persistence.md): each online character is saved once per interval on its own timer (vmangos
/// <c>Player::m_nextSave</c>), staggered so characters that entered the world together are not all snapshotted in one tick, and a
/// character whose previous snapshot is still waiting in the save queue is skipped.
/// </summary>
public sealed class WorldRuntimeAutosaveTests
{
    [Fact]
    public void AutosaveSkipsCharactersWithAPendingSnapshot()
    {
        var saves = new RecordingSaveQueue();
        using var world = TestWorld.CreateRuntime(saves, autosaveMs: 100);
        Player[] players = [.. Enumerable.Range(1, 3).Select(id => TestWorld.CreatePlayer((uint)id, 10 * id, 10, new FakeSession()))];
        foreach (Player player in players)
        {
            world.AddPlayer(player);
        }

        saves.PendingIds.Add(2);
        world.RunTick(10);
        Assert.Empty(saves.Saved); // not due before half an interval
        RunFor(world, 10, 190); // every character's first save falls inside the first interval
        Assert.Equal(new[] { 1, 3 }, saves.Saved.Select(state => state.Id).Distinct().Order());
        Assert.True(world.LastTickPhases.AutosaveMicros >= 0);

        // The queue caught up: character 2 is saved on its next turn.
        saves.PendingIds.Clear();
        RunFor(world, 10, 200);
        Assert.Contains(saves.Saved, state => state.Id == 2);
    }

    [Fact]
    public void AutosaveStaggersCharactersThatEnteredTheWorldTogether()
    {
        // The live incident (2026-10-16 23:54Z): about 1000 bots entered together and every autosave queued all of them in one tick
        // (a backlog of 1001), which then held the characters database's write lock back to back for tens of seconds.
        const int count = 200;
        const int interval = 2000;
        const uint tick = 50;
        var saves = new RecordingSaveQueue();
        using var world = TestWorld.CreateRuntime(saves, autosaveMs: interval);
        for (uint id = 1; id <= count; id++)
        {
            world.AddPlayer(TestWorld.CreatePlayer(id, 300 * (id % 15), 300 * (id / 15), new FakeSession()));
        }

        int largest = 0;
        int ticks = 0;
        for (uint elapsed = 0; elapsed < interval + tick; elapsed += tick)
        {
            int before = saves.Saved.Count;
            world.RunTick(tick);
            ticks++;
            int queued = saves.Saved.Count - before;
            Assert.Equal(queued, world.LastTickPhases.AutosaveSnapshots);
            largest = Math.Max(largest, queued);
            if (elapsed + tick <= interval / 2)
            {
                Assert.True(queued == 0, $"{queued} character(s) were autosaved {elapsed + tick} ms after entering the world, before half an interval");
            }
        }

        // Within one interval (plus the tick that first saw them) every character was saved exactly once...
        Assert.Equal(Enumerable.Range(1, count), saves.Saved.Select(state => state.Id).Order());

        // ...spread over the second half of it: an even spread is count / (ticks / 2) = 10 per tick; one tick holding them all is the burst.
        Assert.True(largest <= 3 * count / (ticks / 2), $"one tick queued {largest} of {count} snapshots: the autosave was not staggered");

        // And the stagger holds on the next interval: each character's next save is one interval after its last.
        saves.Saved.Clear();
        largest = 0;
        for (int i = 0; i < interval / tick; i++)
        {
            int before = saves.Saved.Count;
            world.RunTick(tick);
            largest = Math.Max(largest, saves.Saved.Count - before);
        }

        Assert.Equal(Enumerable.Range(1, count), saves.Saved.Select(state => state.Id).Order());
        Assert.True(largest <= 3 * count / (ticks / 2), $"one tick of the second interval queued {largest} of {count} snapshots");
    }

    [Fact]
    public void ExplicitSavePlayerStillCoalescesWhenPending()
    {
        var saves = new RecordingSaveQueue();
        using var world = TestWorld.CreateRuntime(saves);
        Player player = TestWorld.CreatePlayer(2, 10, 10, new FakeSession());
        world.AddPlayer(player);
        saves.PendingIds.Add(2);
        world.SavePlayer(player);
        world.SaveAll();
        Assert.Equal(new[] { 2, 2 }, saves.Saved.Select(state => state.Id)); // explicit saves are always handed to the (coalescing) queue
    }

    private static void RunFor(WorldRuntime world, uint tick, uint total)
    {
        for (uint elapsed = 0; elapsed < total; elapsed += tick)
        {
            world.RunTick(tick);
        }
    }
}
