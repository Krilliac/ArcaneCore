using System.Text.RegularExpressions;
using ArcaneCore.Data.Content.Names;
using ArcaneCore.World.Pets;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace ArcaneCore.World.Tests.Names;

public sealed class NameCatalogTests
{
    [Fact]
    public void ImmutableCatalog_ReturnsProfaneBeforeReservedAndKeepsProvenance()
    {
        var catalog = new NameCatalog(
            [new Regex("bad", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)],
            [new Regex("admin", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)],
            [new NameCatalogSource("synthetic", "fixture", "ABC", 2)]);
        Assert.Equal(NameCatalogResult.Profane, catalog.Check("BadName"));
        Assert.Equal(NameCatalogResult.Reserved, catalog.Check("Admin"));
        Assert.Equal(NameCatalogResult.Allowed, catalog.Check("Rex"));
        Assert.Equal("ABC", Assert.Single(catalog.Sources).Sha256);
    }

    [Fact]
    public async Task ConfiguredWorldHosts_UseIndependentCatalogs_AndKeepInjectedVeto()
    {
        string dir = Directory.CreateTempSubdirectory("arcane-name-catalog-").FullName;
        try
        {
            string badA = Path.Combine(dir, "bad-a.dbc");
            string reservedA = Path.Combine(dir, "reserved-a.dbc");
            string badB = Path.Combine(dir, "bad-b.dbc");
            string reservedB = Path.Combine(dir, "reserved-b.dbc");
            File.WriteAllBytes(badA, Image(["\\^bad\\$"]));
            File.WriteAllBytes(reservedA, Image(["\\^admin\\$"]));
            File.WriteAllBytes(badB, Image(["\\^zap\\$"]));
            File.WriteAllBytes(reservedB, Image(["\\^root\\$"]));
            IConfiguration configA = Config(badA, reservedA);
            IConfiguration configB = Config(badB, reservedB);
            var veto = new PetNameRules { ExternalVeto = name => name != "KeepOut" };
            await using WorldTestHost hostA = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton<IConfiguration>(configA);
                services.AddSingleton(veto);
            });
            await using WorldTestHost hostB = WorldTestHost.Start(configureServices: services => services.AddSingleton<IConfiguration>(configB));
            await hostA.OnWorldAsync(() =>
            {
                Func<string, string?> rules = hostA.WorldServices.GetRequiredService<PetsFeature>().Controller.PetNameNormalizer!;
                Assert.Null(rules("bad"));
                Assert.Null(rules("KeepOut"));
                Assert.Equal("zap", rules("zap"));
            });
            await hostB.OnWorldAsync(() =>
            {
                Func<string, string?> rules = hostB.WorldServices.GetRequiredService<PetsFeature>().Controller.PetNameNormalizer!;
                Assert.Equal("bad", rules("bad"));
                Assert.Null(rules("zap"));
            });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static IConfiguration Config(string profanity, string reserved)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Names:NamesProfanityDbcPath"] = profanity,
            ["Names:NamesReservedDbcPath"] = reserved,
        }).Build();

    private static byte[] Image(string[] patterns)
    {
        var strings = new List<byte> { 0 };
        var offsets = new List<uint>();
        foreach (string pattern in patterns)
        {
            offsets.Add((uint)strings.Count);
            strings.AddRange(Encoding.UTF8.GetBytes(pattern));
            strings.Add(0);
        }
        byte[] image = new byte[20 + patterns.Length * 8 + strings.Count];
        Encoding.ASCII.GetBytes("WDBC").CopyTo(image, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)patterns.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), (uint)strings.Count);
        for (int i = 0; i < patterns.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + (i * 8) + 4), offsets[i]);
        strings.ToArray().CopyTo(image, 20 + patterns.Length * 8);
        return image;
    }
}
