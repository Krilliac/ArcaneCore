using System.Reflection;
using ArcaneCore.World.Features;
using ArcaneCore.World.Ops.Cli;
using ArcaneCore.World.Ops.Diagnostics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Architecture;

/// <summary>
/// The world's startup code set (docs/design/hot-reload-tiers.md, 4.6): the Playerbots pack is a separate assembly the host registers
/// as a built-in, and every startup discovery reads the set, so the pack's features, service module, commands and config check are
/// found exactly as when they lived in ArcaneCore.World. This process registers the pack in <see cref="BuiltInPacks"/>.
/// </summary>
public sealed class WorldCodeAssembliesTests
{
    private static Assembly Pack => typeof(ManagedPlayerbotFeature).Assembly;

    [Fact]
    public void TheStartupSet_IsTheWorldAndThePack()
    {
        Assert.Equal([typeof(WorldHost).Assembly, Pack], WorldCodeAssemblies.Ordered);
        Assert.Equal([Pack, typeof(WorldHost).Assembly], WorldCodeAssemblies.Startup.Assemblies); // CodeAssemblies order: by simple name
        Assert.Equal([Pack], WorldCodeAssemblies.BuiltInPacks);
        Assert.True(WorldCodeAssemblies.IsFixed);
        Assert.False(Pack.IsCollectible);
    }

    [Fact]
    public void EveryStartupDiscovery_FindsThePacksContributions()
    {
        Assert.Contains(typeof(ManagedPlayerbotFeature), WorldFeatures.FeatureTypes);
        Assert.Contains(typeof(PlayerbotQaFeature), WorldFeatures.FeatureTypes);
        Assert.Contains(typeof(ArcaneCore.World.Bots.ManagedBotRegistry), WorldFeatures.FeatureTypes); // the core's own still there
        Assert.Contains(WorldServiceCollectionExtensions.ServiceModules, m => m is PlayerbotServiceModule);
        Assert.Contains(Pack, OpsCli.CheckAssemblies);
        Assert.Contains(OpsCli.CreateChecks(), c => c is PlayerbotQaConfigChecks);
        Assert.Contains(Pack, WiringAudit.DefaultAssemblies());
        Assert.NotNull(ArcaneCore.World.Commands.ChatCommands.CreateTable().Find("playerbot"));
    }

    /// <summary>
    /// Default discovery orders the startup set by type full name across its assemblies, so the moved pack types (namespaces unchanged)
    /// keep the place they had inside ArcaneCore.World: feature attach and stop order, service module order and command order.
    /// </summary>
    [Fact]
    public void DefaultDiscovery_InterleavesThePackByFullName_AsBeforeTheMove()
    {
        string[] names = [.. WorldFeatures.FeatureTypes.Select(t => t.FullName!)];
        Assert.Equal<string>([.. names.Order(StringComparer.Ordinal)], names);
        int registry = Array.IndexOf(names, typeof(ArcaneCore.World.Bots.ManagedBotRegistry).FullName);
        int bots = Array.IndexOf(names, typeof(ManagedPlayerbotFeature).FullName);
        int reload = Array.IndexOf(names, typeof(ArcaneCore.World.Reload.ReloadFeature).FullName);
        Assert.True(registry >= 0 && registry < bots && bots < reload, $"registry {registry}, bots {bots}, reload {reload}");
    }

    [Fact]
    public void ARegistrationAfterTheSetWasRead_IsRefused_AndChangesNothing()
    {
        _ = WorldCodeAssemblies.Startup;
        Assembly late = typeof(FactAttribute).Assembly;
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => WorldCodeAssemblies.RegisterBuiltIn(late));
        Assert.Contains("after the world's startup code set was read", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(late, WorldCodeAssemblies.Startup.Assemblies);
        WorldCodeAssemblies.RegisterBuiltIn(Pack); // the same pack again is a no-op, even now
        Assert.Equal([Pack], WorldCodeAssemblies.BuiltInPacks);
    }

    [Fact]
    public void LoadBuiltInPack_ReturnsTheLoadedPack_AndNamesAMissingFile()
    {
        Assert.Same(Pack, WorldCodeAssemblies.LoadBuiltInPack(WorldCodeAssemblies.PlayerbotsAssemblyName));
        string empty = Directory.CreateTempSubdirectory("arcane-nopack-").FullName;
        try
        {
            FileNotFoundException ex = Assert.Throws<FileNotFoundException>(() => WorldCodeAssemblies.LoadBuiltInPack("ArcaneCore.NoSuchPack", empty));
            Assert.Contains("ArcaneCore.NoSuchPack.dll", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }
}
