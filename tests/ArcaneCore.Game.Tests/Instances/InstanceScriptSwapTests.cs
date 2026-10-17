using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using Xunit;
using static ArcaneCore.Game.Tests.Instances.InstanceFixture;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// The hot module lane on the instance script registry: a module swap adds, replaces and removes its own scripts atomically and never
/// overrides a built-in one; it applies to instance maps created afterwards, while a loaded instance keeps the InstanceData it was created
/// with (its progress), and the instance manager counts those still running retired script code.
/// </summary>
public sealed class InstanceScriptSwapTests
{
    private sealed class BuiltIn(Map map) : ScriptedInstance(map, 1);

    private sealed class ModuleV1(Map map) : ScriptedInstance(map, 1);

    private sealed class ModuleV2(Map map) : ScriptedInstance(map, 1);

    private static Dictionary<uint, Func<Map, InstanceData>> Add(uint mapId, Func<Map, InstanceData> factory) => new() { [mapId] = factory };

    [Fact]
    public void AModuleCannotOverrideOrRemoveABuiltInScript_AndARejectedCommitChangesNothing()
    {
        InstanceScriptRegistry registry = new InstanceScriptRegistry().Register(Dungeon, map => new BuiltIn(map));
        long generation = registry.Generation;

        Assert.Contains("built in; a module cannot override it", registry.ValidateSwap([], [Dungeon]), StringComparison.Ordinal);
        Assert.Contains("built in; a module cannot remove it", registry.ValidateSwap([Dungeon], []), StringComparison.Ordinal);
        Assert.Contains("built in; a module cannot remove it", registry.ValidateSwap([Dungeon], [Dungeon]), StringComparison.Ordinal);
        Assert.Contains("no instance script to remove", registry.ValidateSwap([Raid], []), StringComparison.Ordinal);
        Assert.Null(registry.ValidateSwap([], [Raid]));

        // Adding the free raid AND overriding the dungeon fails as a whole.
        var both = new Dictionary<uint, Func<Map, InstanceData>> { [Raid] = map => new ModuleV1(map), [Dungeon] = map => new ModuleV1(map) };
        Assert.Throws<InvalidOperationException>(() => registry.CommitSwap([], both));
        Assert.False(registry.HasScript(Raid));
        Assert.True(registry.IsBuiltIn(Dungeon));
        Assert.Equal(generation, registry.Generation);
    }

    [Fact]
    public void ASwap_AppliesToNewInstancesOnly_AndLoadedOnesKeepTheirDataAsRetiredCode()
    {
        using var f = new InstanceFixture();
        InstanceScriptRegistry registry = new InstanceScriptRegistry().Register(Dungeon, map => new BuiltIn(map));
        f.Manager.Scripts = registry;

        registry.CommitSwap([], Add(Raid, map => new ModuleV1(map)));
        Assert.Equal([Raid], registry.ModuleMapIds);

        Player first = f.AddPlayer(1);
        f.RaidGroup(first, f.AddPlayer(2));
        Assert.True(f.EnterRaid(first));
        Player dungeon = f.AddPlayer(5);
        Assert.True(f.EnterDungeon(dungeon));
        ModuleV1 oldData = Assert.IsType<ModuleV1>(InstanceManager.InstanceDataOf(first.Map!));
        Assert.True(registry.RunsCurrentScript(oldData));
        Assert.Equal(0, f.Manager.RetiredScriptInstanceCount);

        // Replace the module's raid script: the loaded raid keeps its data object (its progress); a new raid instance gets v2.
        Assert.Null(registry.ValidateSwap([Raid], [Raid]));
        registry.CommitSwap([Raid], Add(Raid, map => new ModuleV2(map)));
        Assert.Same(oldData, InstanceManager.InstanceDataOf(first.Map!));
        Assert.False(registry.RunsCurrentScript(oldData));
        Assert.Equal(1, f.Manager.RetiredScriptInstanceCount);

        Player second = f.AddPlayer(3);
        f.RaidGroup(second, f.AddPlayer(4));
        Assert.True(f.EnterRaid(second));
        Assert.NotEqual(first.Map!.InstanceId, second.Map!.InstanceId);
        Assert.True(registry.RunsCurrentScript(Assert.IsType<ModuleV2>(InstanceManager.InstanceDataOf(second.Map!))));
        Assert.Equal(1, f.Manager.RetiredScriptInstanceCount);

        // Remove it: both raids now run retired code, the built-in dungeon does not, and a new raid has no data.
        registry.CommitSwap([Raid], new Dictionary<uint, Func<Map, InstanceData>>());
        Assert.False(registry.HasScript(Raid));
        Assert.Equal(2, f.Manager.RetiredScriptInstanceCount);
        Assert.True(registry.RunsCurrentScript(InstanceManager.InstanceDataOf(dungeon.Map!)!));
    }

    [Fact]
    public void DataTheRegistryDidNotCreate_IsNotItsCurrentScript()
    {
        using var f = new InstanceFixture();
        InstanceScriptRegistry registry = new InstanceScriptRegistry().Register(Dungeon, map => new BuiltIn(map));
        f.Manager.Scripts = registry;
        Player player = f.AddPlayer(1);
        Assert.True(f.EnterDungeon(player));

        Assert.False(registry.RunsCurrentScript(new BuiltIn(player.Map!)));
        Assert.True(registry.RunsCurrentScript(InstanceManager.InstanceDataOf(player.Map!)!));
    }

    [Fact]
    public void DiscoverOverSeveralAssemblies_MatchesTheDefault_AndFactoriesAreWhatAModuleHandsToASwap()
    {
        InstanceScriptRegistry twice = InstanceScriptRegistry.Discover([typeof(InstanceScriptRegistry).Assembly, typeof(InstanceScriptRegistry).Assembly]);
        Assert.Equal(InstanceScriptRegistry.Default.MapIds.Order(), twice.MapIds.Order());
        Assert.All(twice.MapIds, id => Assert.True(twice.IsBuiltIn(id)));
        Assert.Empty(InstanceScriptRegistry.Default.ModuleMapIds);

        IReadOnlyDictionary<uint, Func<Map, InstanceData>> factories = InstanceScriptRegistry.DiscoverFactories([typeof(InstanceScriptRegistry).Assembly]);
        Assert.Equal(twice.MapIds.Order(), factories.Keys.Order());
        Assert.Contains("built in; a module cannot override it", twice.ValidateSwap([], factories.Keys), StringComparison.Ordinal);
    }
}
