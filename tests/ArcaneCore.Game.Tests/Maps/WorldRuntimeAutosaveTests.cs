using ArcaneCore.Game.Entities;
using Xunit;

namespace ArcaneCore.Game.Tests.Persistence;

/// <summary>The periodic save skips characters whose previous snapshot is still waiting in the save queue (docs/areas/persistence.md).</summary>
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
        world.RunTick(50);
        Assert.Empty(saves.Saved); // not due yet
        world.RunTick(60);
        Assert.Equal(new[] { 1, 3 }, saves.Saved.Select(state => state.Id).Order());
        Assert.Equal(2, world.LastTickPhases.AutosaveSnapshots);

        // The queue caught up: the next autosave includes everyone again.
        saves.Saved.Clear();
        saves.PendingIds.Clear();
        world.RunTick(100);
        Assert.Equal(new[] { 1, 2, 3 }, saves.Saved.Select(state => state.Id).Order());
        Assert.Equal(3, world.LastTickPhases.AutosaveSnapshots);
        Assert.True(world.LastTickPhases.AutosaveMicros >= 0);
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
}
