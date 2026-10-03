using System.Reflection;
using System.Reflection.Emit;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.Game.Tests.HotCode;

/// <summary>
/// An updater that exists only in this test assembly, so the real <see cref="DefaultMapUpdaters"/>
/// scan (the Game assembly) never finds it; the tests present it as "added by an edit".
/// Harmless on any map that happens to get it.
/// </summary>
[DefaultMapUpdater(Order = int.MaxValue)]
public sealed class HotProbeUpdater(Map map, WorldRuntime world) : IMapUpdater
{
    public Map Map { get; } = map;

    public WorldRuntime World { get; } = world;

    public void Update(Map map, uint diffMs)
    {
    }

    public void OnPlayerRemoved(Map map, Player player)
    {
    }
}

/// <summary>
/// <see cref="DefaultMapUpdaters"/> rescan and commit, the map-updater part of a code hot reload:
/// maps created after the commit get a newly added default updater, maps that already exist keep
/// what they have, and an invalid marked type fails the scan and changes nothing.
/// </summary>
public sealed class DefaultMapUpdaterRefreshTests
{
    [Fact]
    public void RescanningTheGameAssembly_FindsExactlyTheSetInForce()
    {
        Assert.Equal(DefaultMapUpdaters.Types, DefaultMapUpdaters.DiscoverCandidate().Types);
    }

    [Fact]
    public void ACommittedUpdater_IsAttachedToNewMapsOnly()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map existing = world.GetMap(0);
        DefaultMapUpdaterCandidate original = DefaultMapUpdaters.DiscoverCandidate();
        DefaultMapUpdaterCandidate edited = DefaultMapUpdaters.DiscoverCandidate(typeof(HotProbeUpdater).Assembly);
        Assert.Contains(typeof(HotProbeUpdater), edited.Types);
        Assert.DoesNotContain(typeof(HotProbeUpdater), DefaultMapUpdaters.Types); // not in force until committed

        try
        {
            IReadOnlyList<Type> added = DefaultMapUpdaters.Commit(edited);

            Assert.Equal([typeof(HotProbeUpdater)], added);
            Assert.Equal(typeof(HotProbeUpdater), DefaultMapUpdaters.Types[^1]);
            Map created = world.GetMap(1);
            Assert.NotNull(created.FindUpdater<HotProbeUpdater>());
            Assert.Null(existing.FindUpdater<HotProbeUpdater>());
            Assert.NotNull(existing.FindUpdater<MapCombat>()); // what it had stays
            Assert.Empty(DefaultMapUpdaters.Commit(edited));   // a second commit adds nothing
        }
        finally
        {
            DefaultMapUpdaters.Commit(original);
        }

        Assert.DoesNotContain(typeof(HotProbeUpdater), DefaultMapUpdaters.Types);
    }

    [Fact]
    public void AnInvalidMarkedType_FailsTheScan_AndLeavesTheSetInForceUntouched()
    {
        IReadOnlyList<Type> before = DefaultMapUpdaters.Types;

        var ex = Assert.Throws<InvalidOperationException>(() => DefaultMapUpdaters.DiscoverCandidate(BuildAssemblyWithAMarkedNonUpdater()));

        Assert.Contains("is marked [DefaultMapUpdater] but is not a concrete IMapUpdater", ex.Message);
        Assert.Equal(before, DefaultMapUpdaters.Types);
    }

    private static Assembly BuildAssemblyWithAMarkedNonUpdater()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("HotCodeBadUpdaterAssembly"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule("HotCodeBadUpdaterAssembly");
        TypeBuilder type = module.DefineType("NotAnUpdater", TypeAttributes.Public | TypeAttributes.Class);
        type.SetCustomAttribute(new CustomAttributeBuilder(typeof(DefaultMapUpdaterAttribute).GetConstructor(Type.EmptyTypes)!, []));
        type.CreateType();
        return assembly;
    }
}
