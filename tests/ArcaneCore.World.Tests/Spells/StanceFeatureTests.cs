using System.Buffers.Binary;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>S06: the world daemon installs the warrior stance handler and gate, with the DBC or the three-stance table.</summary>
public sealed class StanceFeatureTests
{
    private static WorldRuntime NewWorld(ServiceProvider empty)
    {
        var saves = new CharacterSaveQueue(empty.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        return new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
    }

    private static ServiceProvider Services(params KeyValuePair<string, string?>[] settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddLogging();
        services.AddSingleton<SpellFeature>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void IsDiscoveredAsAWorldFeature()
        => Assert.Contains(typeof(StanceFeature), WorldFeatures.FeatureTypes);

    [Fact]
    public async Task Attach_WithoutADbcPath_InstallsTheHandlerAndGate_WithTheRetailTable()
    {
        await using ServiceProvider sp = Services();
        using WorldRuntime world = NewWorld(sp);
        var feature = new StanceFeature(sp, NullLogger<StanceFeature>.Instance);

        feature.Attach(world);

        SpellSystem spells = sp.GetRequiredService<SpellFeature>().System;
        Assert.True(spells.HasAuraHandler(AuraType.ModShapeshift));
        Assert.Single(spells.CastChecks.OfType<StanceCastCheck>());
        Assert.Equal(7u, feature.Service!.GetFormFlags(ShapeshiftForm.BattleStance));   // client row: Stance | NotToggleable | PersistOnDeath
        Assert.Equal(0u, feature.Service.GetFormFlags(ShapeshiftForm.Cat));              // a client row, not "unknown"
        Assert.Equal(1u, feature.Service.GetFormFlags(ShapeshiftForm.Stealth));
        Assert.Same(ShapeshiftFormCatalog.Retail, CombatEnvironment.For(world).ShapeshiftForms);
    }

    [Fact]
    public async Task Attach_WithRequireShapeshiftFormDbcAndNoPath_StopsTheDaemonStart()
    {
        await using ServiceProvider sp = Services(new KeyValuePair<string, string?>("Combat:RequireShapeshiftFormDbc", "true"));
        using WorldRuntime world = NewWorld(sp);

        Assert.Throws<InvalidOperationException>(() => new StanceFeature(sp, NullLogger<StanceFeature>.Instance).Attach(world));
    }

    [Fact]
    public async Task Attach_WithADbcPath_UsesTheClientTable()
    {
        string path = Path.Combine(Path.GetTempPath(), $"SpellShapeshiftForm-{Guid.NewGuid():N}.dbc");
        try
        {
            File.WriteAllBytes(path, Dbc([1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], [17, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0]));
            await using ServiceProvider sp = Services(new("Combat:ShapeshiftFormDbcPath", path), new("Combat:StanceShiftKeepsSelfBuffs", "true"));
            using WorldRuntime world = NewWorld(sp);
            var feature = new StanceFeature(sp, NullLogger<StanceFeature>.Instance);

            feature.Attach(world);

            Assert.True(feature.Options.StanceShiftKeepsSelfBuffs);
            Assert.Equal(0u, feature.Service!.GetFormFlags(ShapeshiftForm.Cat));   // a non-stance form from the DBC
            Assert.Equal(1u, feature.Service.GetFormFlags(ShapeshiftForm.BattleStance));
            Assert.Null(feature.Service.GetFormFlags(ShapeshiftForm.DefensiveStance));   // not in this file
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Attach_WithAMissingDbc_StopsTheDaemonStart()
    {
        await using ServiceProvider sp = Services(new KeyValuePair<string, string?>("Combat:ShapeshiftFormDbcPath", Path.Combine(Path.GetTempPath(), "no-such-shapeshift-form.dbc")));
        using WorldRuntime world = NewWorld(sp);

        Assert.ThrowsAny<IOException>(() => new StanceFeature(sp, NullLogger<StanceFeature>.Instance).Attach(world));
    }

    private static byte[] Dbc(params uint[][] records)
    {
        const int fields = 14;
        byte[] image = new byte[20 + (records.Length * fields * 4) + 1];
        "WDBC"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)records.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), fields * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), 1);
        for (int record = 0; record < records.Length; record++)
        {
            for (int field = 0; field < fields; field++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + (((record * fields) + field) * 4)), records[record][field]);
            }
        }

        return image;
    }
}
