using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Combat.Duel;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Duel;

public sealed class DuelFeatureTests
{
    private static WorldRuntime NewWorld(ServiceProvider sp)
    {
        var saves = new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        return new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
    }

    [Fact]
    public void FeatureAndHandlersAreDiscovered()
    {
        Assert.Contains(typeof(DuelFeature), WorldFeatures.FeatureTypes);
        var table = WorldServiceCollectionExtensions.BuildOpcodeTable();
        Assert.True(table.TryGet(WorldOpcode.CmsgDuelAccepted, out _));
        Assert.True(table.TryGet(WorldOpcode.CmsgDuelCancelled, out _));
    }

    [Fact]
    public async Task Attach_BindsTheWorldDuelSection_AndInstallsOnTheSpellSystem()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:Duel:Enabled"] = "false",
            ["World:Duel:StartDelaySeconds"] = "5",
            ["World:Duel:OutOfBoundsYards"] = "75",
            ["World:Duel:ReturnInBoundsYards"] = "70",
            ["World:Duel:OutOfBoundsGraceSeconds"] = "12",
            ["World:Duel:RequireKnownArea"] = "true",
            ["World:Duel:ExpiredRequestIsSilent"] = "true",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddSingleton<SpellFeature>();
        await using ServiceProvider sp = services.BuildServiceProvider();
        using WorldRuntime world = NewWorld(sp);
        var feature = new DuelFeature(sp, NullLogger<DuelFeature>.Instance);

        feature.Attach(world);

        DuelOptions o = feature.Options;
        Assert.False(o.Enabled);
        Assert.Equal(5, o.StartDelaySeconds);
        Assert.Equal(75f, o.OutOfBoundsYards);
        Assert.Equal(70f, o.ReturnInBoundsYards);
        Assert.Equal(12, o.OutOfBoundsGraceSeconds);
        Assert.True(o.RequireKnownArea);
        Assert.True(o.ExpiredRequestIsSilent);
        Assert.Same(o, feature.Service!.Options);
        Assert.Same(feature.Service, DuelService.Find(world));
        SpellSystem spells = sp.GetRequiredService<SpellFeature>().System;
        Assert.Same(feature.Service, DuelService.ForSpells(spells));
        Assert.True(spells.HasEffectHandler(SpellEffectName.Duel));
        Assert.Single(spells.CastChecks, c => c.GetType().Name == "DuelCastCheck");
    }

    [Fact]
    public async Task Defaults_AreTheRetailValues_WithoutAConfiguration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SpellFeature>();
        await using ServiceProvider sp = services.BuildServiceProvider();
        using WorldRuntime world = NewWorld(sp);
        var feature = new DuelFeature(sp, NullLogger<DuelFeature>.Instance);

        feature.Attach(world);

        Assert.True(feature.Options.Enabled);
        Assert.Equal(3, feature.Options.StartDelaySeconds);
        Assert.Equal(50f, feature.Options.OutOfBoundsYards);
        Assert.Equal(40f, feature.Options.ReturnInBoundsYards);
        Assert.Equal(10, feature.Options.OutOfBoundsGraceSeconds);
    }

    [Fact]
    public async Task WithoutTheSpellFeature_AttachDoesNotThrow_AndTheServiceStillExists()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using ServiceProvider sp = services.BuildServiceProvider();
        using WorldRuntime world = NewWorld(sp);
        var feature = new DuelFeature(sp, NullLogger<DuelFeature>.Instance);

        feature.Attach(world);

        Assert.NotNull(feature.Service);
        Assert.Null(feature.Service.Spells);
    }

    [Fact]
    public async Task AttachingTwice_Throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using ServiceProvider sp = services.BuildServiceProvider();
        using WorldRuntime world = NewWorld(sp);
        var feature = new DuelFeature(sp, NullLogger<DuelFeature>.Instance);
        feature.Attach(world);

        Assert.Throws<InvalidOperationException>(() => feature.Attach(world));
    }
}
