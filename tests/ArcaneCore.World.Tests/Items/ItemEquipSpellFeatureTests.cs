using System.Buffers.Binary;
using ArcaneCore.World.Items;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Items;

/// <summary>The "ItemSets" configuration section and the feature that loads ItemSet.dbc and binds the equip hook at login.</summary>
public sealed class ItemEquipSpellFeatureTests
{
    private static IConfiguration Config(string? path) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ItemSets:DbcPath"] = path,
    }).Build();

    private static byte[] OneSetImage()
    {
        const int fields = 45;
        byte[] image = new byte[20 + (fields * 4) + 1];
        "WDBC"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), fields * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20), 55);              // id
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + (27 * 4)), 7000); // first set spell
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + (35 * 4)), 2);    // its threshold
        return image;
    }

    [Fact]
    public void WithoutConfiguration_NoDbcIsConfigured()
        => Assert.Null(ItemEquipSpellFeature.Bind(null).DbcPath);

    [Fact]
    public void TheSectionSetsTheDbcPath()
        => Assert.Equal("/data/ItemSet.dbc", ItemEquipSpellFeature.Bind(Config("/data/ItemSet.dbc")).DbcPath);

    [Fact]
    public async Task WithoutAFile_TheFeatureRunsWithAnEmptyCatalog_AndTheEquipHookIsBound()
    {
        await using WorldTestHost host = WorldTestHost.Start();

        ItemEquipSpellFeature feature = host.WorldServices.GetRequiredService<ItemEquipSpellFeature>();

        Assert.Equal(0, feature.Catalog.Count);
        Assert.NotNull(feature.Spells);
    }

    [Fact]
    public async Task AConfiguredFile_IsLoadedIntoTheCatalog()
    {
        string path = Path.Combine(Path.GetTempPath(), $"itemset-{Guid.NewGuid():N}.dbc");
        File.WriteAllBytes(path, OneSetImage());
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton(Config(path)));

            ItemEquipSpellFeature feature = host.WorldServices.GetRequiredService<ItemEquipSpellFeature>();

            Assert.Equal(path, feature.Options.DbcPath);
            Assert.Equal(7000u, feature.Catalog.Find(55)!.SpellIds[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AConfiguredFileThatIsMissing_RefusesStartup()
    {
        string path = Path.Combine(Path.GetTempPath(), $"itemset-missing-{Guid.NewGuid():N}.dbc");

        Assert.ThrowsAny<Exception>(() => WorldTestHost.Start(configureServices: s => s.AddSingleton(Config(path))));
    }
}
