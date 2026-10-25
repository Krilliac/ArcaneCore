using System.Collections.Concurrent;
using System.Text;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Mpq;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using ArcaneCore.Atlas.Textures;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Atlas.Tests.Textures;

/// <summary>
/// The BLP decoder over the real 1.12.1 client (the MPQ chain, <c>ARCANECORE_TEST_CLIENT_DIR</c>; design sections 0.1 and 11, A1
/// acceptance check 2). The thresholds are the census of the whole chain (52,793 live BLPs, none empty), so a listing filter that
/// matches nothing, or a run over the partial extract, fails instead of passing. Nothing from the client is stored: the colour
/// oracle below holds a few mean and sample values measured with Pillow 12.3's independent BLP2 reader (RGB for the palettized
/// files, whose alpha Pillow does not decode, and RGBA for the DXT files).
/// </summary>
[Collection(RealTextureData.Collection)]
public sealed class TexturesRealDataTests(ITestOutputHelper output)
{
    [ClientArchiveFact]
    public void EveryListedBlp_DecodesOrIsRejectedWithAReason_AndTheCensusThresholdsHold()
    {
        using MpqFileSystem files = RealTextureData.Open();
        string[] blps = [.. files.List(string.Empty).Where(p => p.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))];

        var encodings = new ConcurrentDictionary<(int Compression, int AlphaDepth, int AlphaType), int>();
        var rejected = new ConcurrentBag<string>();
        var unexpected = new ConcurrentBag<string>();
        int decoded = 0, tileset = 0, character = 0, minimap = 0, empty = 0;
        Parallel.ForEach(blps, new ParallelOptions { MaxDegreeOfParallelism = 4 }, path =>
        {
            byte[]? bytes = files.Read(path);
            if (bytes is null || bytes.Length == 0)
            {
                Interlocked.Increment(ref empty);
                return;
            }

            try
            {
                BlpImage image = BlpDecoder.Decode(bytes);
                Assert.NotEmpty(image.Levels);
                Assert.Equal(image.Header.Width, image.Levels[0].Width);
                Interlocked.Increment(ref decoded);
                encodings.AddOrUpdate((image.Header.Compression, image.Header.AlphaDepth, image.Header.Compression == 2 ? image.Header.AlphaType : 0), 1, (_, n) => n + 1);
                if (path.StartsWith("Tileset\\", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref tileset);
                }

                if (path.StartsWith("Character\\", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref character);
                }

                if (path.Contains("minimap", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref minimap);
                }
            }
            catch (InvalidDataException e)
            {
                rejected.Add($"{path}: {e.Message}");
            }
            catch (Exception e)
            {
                unexpected.Add($"{path}: {e.GetType().Name}: {e.Message}");
            }
        });

        var table = new StringBuilder();
        foreach (((int c, int d, int t), int n) in encodings.OrderBy(e => e.Key))
        {
            table.AppendLine($"  compression {c} alphaDepth {d} alphaType {t}: {n}");
        }

        output.WriteLine($"listed {blps.Length}, decoded {decoded}, rejected {rejected.Count}, empty {empty}, tileset {tileset}, character {character}, minimap {minimap}");
        output.WriteLine(table.ToString());
        foreach (string r in rejected.Order())
        {
            output.WriteLine("rejected " + r);
        }

        Assert.Empty(unexpected);
        Assert.True(blps.Length >= 52_000, $"only {blps.Length} BLPs listed; the enumeration did not run over the whole chain");
        Assert.Equal(0, empty);
        Assert.True(decoded >= 52_780, $"only {decoded} BLPs decoded");
        Assert.True(rejected.Count <= 5, $"{rejected.Count} BLPs rejected (the census expects 0-1): {string.Join("; ", rejected.Order().Take(5))}");
        Assert.True(tileset >= 1000, $"{tileset} TILESET textures decoded (census 1,048)");
        Assert.True(character >= 4000, $"{character} Character textures decoded (census 4,301)");
        Assert.True(minimap * 5 < decoded, $"minimap tiles are {minimap} of {decoded} decoded (census 13.5 percent)");
        foreach ((int c, int d, int t) in new[] { (2, 0, 0), (2, 1, 0), (2, 8, 1) })
        {
            Assert.True(encodings.ContainsKey((c, d, t)), $"no decoded file with DXT encoding (compression {c}, alphaDepth {d}, alphaType {t})");
        }

        foreach (int d in new[] { 0, 1, 8 })
        {
            Assert.True(encodings.Keys.Any(k => k.Compression == 1 && k.AlphaDepth == d), $"no decoded palettized file with alphaDepth {d}");
        }
    }

    [ClientArchiveFact]
    public void PatchDeletedNames_AreNotListedAndNotReadable()
    {
        using MpqFileSystem files = RealTextureData.Open();
        HashSet<string> listed = new(files.List(string.Empty), StringComparer.OrdinalIgnoreCase);
        string[] deleted =
        [
            .. files.Archives
                .SelectMany(a => a.ListFile)
                .Where(p => p.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) && files.DeletedBy(p) is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

        output.WriteLine($"patch-deleted BLP names: {deleted.Length}; first: {deleted.FirstOrDefault()}");
        Assert.True(deleted.Length >= 5000, $"{deleted.Length} deleted BLP names found (census 5,454)");
        Assert.True(deleted.Count(p => p.Contains("minimap", StringComparison.OrdinalIgnoreCase)) >= 5000, "the deleted set should be mostly minimap tiles");
        Assert.DoesNotContain(deleted, listed.Contains);
        Assert.All(deleted.Take(50), p =>
        {
            Assert.False(files.Exists(p), p);
            Assert.Null(files.Read(p));
        });
    }

    // path, mean RGBA (Pillow; -1 = not compared), centre pixel (w/2, h/2) RGBA, pixel (0,0) RGBA (alpha -1 = not compared)
    private static readonly (string Path, double[] Mean, int[] Centre, int[] Corner)[] ColourOracle =
    [
        ("ITEM\\ObjectComponents\\WEAPON\\Bow_1H_PvPAlliance_A_01.blp", [108.771, 103.282, 116.87, 255.0], [70, 59, 104, -1], [20, 26, 105, -1]),
        ("XTextures\\splash\\splash.blp", [14.509, 14.509, 14.509, -1.0], [0, 0, 0, -1], [0, 0, 0, -1]),
        ("Interface\\Icons\\INV_Armor_Helm_Plate_Naxxramas_RaidWarrior_C_01.blp", [40.282, 36.808, 32.655, -1.0], [188, 160, 133, -1], [8, 6, 5, -1]),
        ("Interface\\Icons\\INV_Axe_05.blp", [63.704, 59.478, 50.553, 255.0], [40, 28, 16, 255], [8, 8, 8, 255]),
        ("Interface\\Icons\\INV_Ammo_Arrow_01.blp", [69.172, 64.116, 65.883, 252.51], [112, 90, 42, 255], [0, 0, 0, 0]),
        ("Interface\\Icons\\INV_AXE_25.BLP", [56.02, 46.522, 52.783, 251.331], [48, 28, 24, 255], [8, 8, 8, 0]),
    ];

    [ClientArchiveFact]
    public void RealTextures_MatchAnIndependentDecoderOnMeanColourAndSamplePixels()
    {
        using MpqFileSystem files = RealTextureData.Open();
        foreach ((string path, double[] mean, int[] centre, int[] corner) in ColourOracle)
        {
            byte[] bytes = files.Read(path) ?? throw new InvalidOperationException(path + " is not in the client");

            RgbaImage level0 = BlpDecoder.Decode(bytes).Levels[0];

            double[] actual = new double[4];
            for (int y = 0; y < level0.Height; y++)
            {
                for (int x = 0; x < level0.Width; x++)
                {
                    Rgba p = level0.Get(x, y);
                    actual[0] += p.R;
                    actual[1] += p.G;
                    actual[2] += p.B;
                    actual[3] += p.A;
                }
            }

            double pixels = level0.Width * (double)level0.Height;
            for (int c = 0; c < 4; c++)
            {
                if (mean[c] >= 0)
                {
                    // The oracle (Pillow) widens RGB565 endpoints by a plain shift, the hardware (and BlpDecoder) by bit replication, so
                    // a bright DXT1 image differs by up to 7/255 per red or blue texel: INV_Axe_05 differs by 1.8 on its mean red.
                    Assert.True(Math.Abs((actual[c] / pixels) - mean[c]) <= 3.0, $"{path}: mean of channel {c} is {actual[c] / pixels:F3}, an independent decoder says {mean[c]}");
                }
            }

            AssertPixel(level0.Get(level0.Width / 2, level0.Height / 2), centre, path + " centre");
            AssertPixel(level0.Get(0, 0), corner, path + " corner");
        }
    }

    [ClientArchiveFact]
    public void ACreatureSkin_ComesBackAsAtx1_InBothDeliveries()
    {
        using MpqFileSystem files = RealTextureData.Open();
        var cache = new TextureCache(files, new GeneratedTextureRegistry());
        const string key = "client:Creature\\Wolf\\WolfSkinTimber.blp";

        AtxTexture rgba = cache.Get(key, TextureDelivery.Rgba, 0, CancellationToken.None).Texture;
        AtxTexture dxt = cache.Get(key, TextureDelivery.Dxt, 0, CancellationToken.None).Texture;

        Assert.Equal(AtxFormat.Rgba8, rgba.Format);
        Assert.True(rgba.Width >= 64);
        Assert.Equal(rgba.Width * rgba.Height * 4, rgba.Levels[0].Length);
        Assert.Contains(dxt.Format, new[] { AtxFormat.Dxt1, AtxFormat.Dxt3, AtxFormat.Dxt5, AtxFormat.Rgba8 });
        Assert.Equal("Creature\\Wolf\\WolfSkinTimber.blp", rgba.Meta["path"]!.GetValue<string>(), ignoreCase: true);
        Assert.Equal(files.ArchiveOf("Creature\\Wolf\\WolfSkinTimber.blp"), rgba.Meta["archive"]!.GetValue<string>());
    }

    private static void AssertPixel(Rgba actual, int[] expected, string what)
    {
        int[] got = [actual.R, actual.G, actual.B, actual.A];
        for (int c = 0; c < 4; c++)
        {
            if (expected[c] >= 0)
            {
                // Colour: the oracle's RGB565 widening is a plain shift (up to 7 below the hardware's bit replication); alpha is exact.
                int tolerance = c < 3 ? 7 : 1;
                Assert.True(Math.Abs(got[c] - expected[c]) <= tolerance, $"{what} channel {c}: expected {expected[c]} +-{tolerance}, got {got[c]}");
            }
        }
    }
}

/// <summary>Opens the real client's archives for the texture tests, and puts them in one collection so the heavy ones run one at a time.</summary>
internal static class RealTextureData
{
    public const string Collection = "Atlas A1 real client textures";

    public static MpqFileSystem Open() => MpqFileSystem.Open(Path.Combine(ClientArchiveData.ClientRoot!, "Data"));
}

/// <summary>Definition of the collection above (xUnit requires one per collection name).</summary>
[CollectionDefinition(RealTextureData.Collection, DisableParallelization = true)]
public sealed class RealTextureCollection;
