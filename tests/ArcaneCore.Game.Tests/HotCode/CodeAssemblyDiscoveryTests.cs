using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Reflection;
using Xunit;

namespace ArcaneCore.Game.Tests.HotCode;

/// <summary>
/// Game discovery sites over a <see cref="CodeAssemblies"/> set: the built-in set finds exactly what is in force today, and a
/// second assembly registered as a pack (this test assembly) has its types found by the same rules.
/// </summary>
[Collection(DefaultMapUpdatersCollection.Name)]
public sealed class CodeAssemblyDiscoveryTests
{
    private static CodeAssemblies GameOnly => CodeAssemblies.Of(typeof(DefaultMapUpdaters).Assembly);

    [Fact]
    public void DefaultMapUpdaters_APackAssembly_HasItsUpdatersFound_AfterTheBuiltIns()
    {
        CodeAssemblies withPack = DefaultMapUpdaters.BuiltIn.WithPack(typeof(HotProbeUpdater).Assembly);

        IReadOnlyList<Type> types = DefaultMapUpdaters.DiscoverCandidate(withPack).Types;
        // A fresh scan of the built-in set, not the process-wide DefaultMapUpdaters.Types: a hot-reload test may have
        // committed HotProbeUpdater into that set while this runs.
        IReadOnlyList<Type> builtIns = DefaultMapUpdaters.DiscoverCandidate(DefaultMapUpdaters.BuiltIn).Types;

        Assert.Contains(typeof(HotProbeUpdater), types);
        Assert.DoesNotContain(typeof(HotProbeUpdater), builtIns);
        // Order int.MaxValue: it runs after every built-in, which are all still there in their order.
        Assert.Equal(builtIns.Append(typeof(HotProbeUpdater)), types);
    }

    [Fact]
    public void DefaultMapUpdaters_TheBuiltInSet_FindsExactlyTheSetInForce()
    {
        Assert.NotEmpty(DefaultMapUpdaters.Types);
        Assert.Equal(DefaultMapUpdaters.Types, DefaultMapUpdaters.DiscoverCandidate(DefaultMapUpdaters.BuiltIn).Types);
    }

    [Fact]
    public void DefaultMapUpdaters_ASetWithoutTheGameAssembly_FindsOnlyThePack()
    {
        CodeAssemblies packOnly = CodeAssemblies.Of(typeof(HotProbeUpdater).Assembly);
        Assert.Equal(new[] { typeof(HotProbeUpdater) }, DefaultMapUpdaters.DiscoverCandidate(packOnly).Types);
    }

    [Fact]
    public void SpellHandlerModules_TheGameSet_IsTheBuiltInList()
    {
        Assert.NotEmpty(SpellHandlerModules.BuiltIn);
        Assert.Equal(SpellHandlerModules.BuiltIn, SpellHandlerModules.Discover(GameOnly));
    }

    [Fact]
    public void MovementObservers_TheGameSet_IsTheRunOrderInForce()
    {
        Assert.NotEmpty(MovementObservers.Types);
        Assert.Equal(MovementObservers.Types, MovementObservers.DiscoverTypes(GameOnly));
    }

    [Fact]
    public void QuestAdapterModules_TheGameSet_IsOrderedByFullName()
    {
        IReadOnlyList<IQuestAdapterModule> modules = QuestNpcServices.DiscoverAdapterModules(GameOnly);

        Assert.NotEmpty(modules);
        string[] names = [.. modules.Select(m => m.GetType().FullName!)];
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
    }
}
