using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps;
using Xunit;
using static ArcaneCore.Game.Tests.Instances.InstanceFixture;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// Characterises the persistence side effects of the bind-detach paths of <see cref="InstanceManager"/> (ResetSave, UnbindAll and
/// the player-bind replacement in BindPlayer): ResetSave writes no unbind rows (the instance delete removes them), UnbindAll writes
/// the player and group unbind rows but keeps the save, and replacing a bind unbinds the old save's row.
/// </summary>
public sealed class InstanceBindDetachCharacterisationTests
{
    private const long Day = 86400;

    private const long FirstRaidReset = Start + (7 * Day) + (4 * 3600);

    private static bool IsUnbindCall(string call) => call.StartsWith("unbind ", StringComparison.Ordinal) || call.StartsWith("group unbind ", StringComparison.Ordinal);

    [Fact]
    public void ResetSave_DropsEveryPlayerAndGroupBind_AndDeletesTheSave_WithoutWritingUnbindRows()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2);
        Group group = f.RaidGroup(a, b);
        Assert.True(f.EnterRaid(a));
        uint id = a.Map!.InstanceId;
        f.Manager.PermBindAllPlayers(a.Map!, a);
        InstanceSave save = f.Manager.FindSave(id)!;
        Assert.NotNull(f.Manager.GetPlayerBind(a.Guid, Raid));
        Assert.NotNull(f.Manager.GetGroupBind(group, Raid));
        Assert.Equal([(1u, id, true)], f.Persistence.GroupBinds);
        Assert.True(f.LeaveToContinent(a));

        int mark = f.Persistence.Calls.Count;
        f.Manager.ResetSaveForTest(save);

        List<string> calls = [.. f.Persistence.Calls.Skip(mark)];
        Assert.Contains($"delete {id}", calls);
        Assert.DoesNotContain(calls, IsUnbindCall);
        Assert.True(save.IsDeleted);
        Assert.Null(f.Manager.FindSave(id));
        Assert.Empty(save.Players);
        Assert.Empty(save.Groups);
        Assert.Null(f.Manager.GetPlayerBind(a.Guid, Raid));
        Assert.Empty(f.Manager.GetPlayerBinds(a.Guid));
        Assert.Null(f.Manager.GetGroupBind(group, Raid));
        Assert.Empty(f.Manager.GetGroupBinds(group));
        Assert.Empty(f.Persistence.Binds);
        Assert.Empty(f.Persistence.GroupBinds);
    }

    [Fact]
    public void ResetSave_OfADeletedSave_DoesNothing()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1);
        Assert.True(f.EnterDungeon(a));
        uint id = a.Map!.InstanceId;
        InstanceSave save = f.Manager.FindSave(id)!;
        Assert.True(f.LeaveToContinent(a));
        f.Manager.ResetSaveForTest(save);
        int mark = f.Persistence.Calls.Count;

        f.Manager.ResetSaveForTest(save);

        Assert.Equal(mark, f.Persistence.Calls.Count);
    }

    [Fact]
    public void GlobalRaidReset_OfALoadedMap_WritesThePlayerAndGroupUnbindRows_AndKeepsTheSaveUntilTheMapUnloads()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2);
        Group group = f.RaidGroup(a, b);
        Assert.True(f.EnterRaid(a));
        Map map = a.Map!;
        uint id = map.InstanceId;
        f.Manager.PermBindAllPlayers(map, a);
        InstanceSave save = f.Manager.FindSave(id)!;
        Assert.Equal([(1u, id, true)], f.Persistence.GroupBinds);
        f.ClearAll();

        int mark = f.Persistence.Calls.Count;
        f.Now = FirstRaidReset - 60;
        f.Manager.UpdateSchedule();

        List<string> calls = [.. f.Persistence.Calls.Skip(mark)];
        Assert.Equal([$"unbind 1 {id}"], calls.Where(c => c.StartsWith("unbind ", StringComparison.Ordinal)));
        Assert.Equal([$"group unbind 1 {id}"], calls.Where(c => c.StartsWith("group unbind ", StringComparison.Ordinal)));
        Assert.DoesNotContain($"delete {id}", calls);
        Assert.False(save.IsDeleted);
        Assert.Same(save, f.Manager.FindSave(id));
        Assert.False(save.HasBinds);
        Assert.Empty(save.Players);
        Assert.Empty(save.Groups);
        Assert.Null(f.Manager.GetPlayerBind(a.Guid, Raid));
        Assert.Null(f.Manager.GetGroupBind(group, Raid));
        Assert.Empty(f.Persistence.Binds);
        Assert.Empty(f.Persistence.GroupBinds);
    }

    [Fact]
    public void ReplacingABindOfTheSameMap_UnbindsTheOldSaveRow_AndBindsTheNewSave()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2);
        Assert.True(f.EnterDungeon(a));
        uint first = a.Map!.InstanceId;
        InstanceSave oldSave = f.Manager.FindSave(first)!;
        Assert.True(f.LeaveToContinent(a));
        Assert.True(f.EnterDungeon(b));
        uint second = b.Map!.InstanceId;
        Assert.NotEqual(first, second);
        InstanceSave newSave = f.Manager.FindSave(second)!;
        Assert.Same(oldSave, f.Manager.GetPlayerBind(a.Guid, Dungeon)!.Value.Save);

        int mark = f.Persistence.Calls.Count;
        f.Manager.BindForTest(a.Guid, newSave);

        List<string> calls = [.. f.Persistence.Calls.Skip(mark)];
        Assert.Equal([$"unbind 1 {first}", $"bind 1 {second} False"], calls.Where(c => !c.StartsWith("delete ", StringComparison.Ordinal)));
        Assert.Same(newSave, f.Manager.GetPlayerBind(a.Guid, Dungeon)!.Value.Save);
        Assert.DoesNotContain(a.Guid, oldSave.Players);
        Assert.Contains(a.Guid, newSave.Players);
        Assert.Single(f.Manager.GetPlayerBinds(a.Guid));
    }
}
