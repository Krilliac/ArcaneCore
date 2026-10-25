using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Textures;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Textures;

/// <summary>
/// <see cref="TextureCache"/> turns a texture key (design section 2.4) into ATX1 bytes: <c>client:&lt;path&gt;</c> reads a BLP from the
/// <see cref="IClientFileSource"/>, <c>gen:&lt;scheme&gt;/&lt;args&gt;</c> asks an <see cref="IGeneratedTextureSource"/>, and
/// <c>missing</c> is the visible placeholder. Failures are <see cref="TextureException"/> with a stable code.
/// </summary>
public sealed class TextureCacheTests
{
    private const string FourColoursPath = "Textures\\Test\\FourColours.blp";

    private static readonly BlpTestWriter.Px[] Palette4 = [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(200, 100, 50)];

    private static byte[] FourColours(byte hasMips = 0)
        => BlpTestWriter.Palettized(2, 2, Palette4, [0, 1, 2, 3], alphaDepth: 8, alphaPlane: [255, 255, 255, 255], hasMips: hasMips);

    private static byte[] MippedEight()
    {
        // 8x8 -> 4x4 -> 2x2 -> 1x1, a distinct index per level.
        var levels = new List<BlpTestWriter.PaletteLevel>
        {
            new([.. Enumerable.Repeat((byte)1, 16)], null),
            new([.. Enumerable.Repeat((byte)2, 4)], null),
            new([3], null),
        };
        return BlpTestWriter.Palettized(8, 8, Palette4, [.. Enumerable.Repeat((byte)0, 64)], alphaDepth: 0, alphaPlane: null, hasMips: 1, moreLevels: levels);
    }

    private static TextureCache Cache(MemoryClientFiles files, GeneratedTextureRegistry? generated = null, long decodedBudget = 256L * 1024 * 1024, long wireBudget = 128L * 1024 * 1024)
        => new(files, generated ?? new GeneratedTextureRegistry(), decodedBudget, wireBudget);

    private static TextureException Fails(Func<object?> act)
        => Assert.Throws<TextureException>(() => act());

    [Fact]
    public void ClientKey_ProducesRgbaAtx1_WithMetadata()
    {
        var files = new MemoryClientFiles();
        files.Add(FourColoursPath, FourColours());
        TextureCache cache = Cache(files);

        TextureReply reply = cache.Get("client:Textures/Test/FOURCOLOURS.blp", TextureDelivery.Rgba, maxSize: 0, CancellationToken.None);

        AtxTexture texture = TextureWire.Read(reply.Wire);
        Assert.Equal(AtxFormat.Rgba8, texture.Format);
        Assert.Equal((2, 2, 1), (texture.Width, texture.Height, texture.Layers));
        Assert.True(texture.Flags.HasFlag(AtxFlags.HasAlpha));
        Assert.False(texture.Flags.HasFlag(AtxFlags.Alpha1Bit));
        Assert.False(texture.Flags.HasFlag(AtxFlags.Generated));
        Assert.Equal(2, texture.Levels.Count); // 2x2 then 1x1
    }

    [Fact]
    public void ClientKey_MetadataNamesTheCanonicalKeyPathArchiveAndSource()
    {
        var files = new MemoryClientFiles();
        files.Add(FourColoursPath, FourColours());

        AtxTexture texture = Cache(files).Get("client:textures/test/fourcolours.blp", TextureDelivery.Rgba, 0, CancellationToken.None).Texture;

        JsonObject meta = texture.Meta;
        Assert.Equal("client:textures\\test\\fourcolours.blp", meta["key"]!.GetValue<string>());
        Assert.Equal("Textures\\Test\\FourColours.blp", meta["path"]!.GetValue<string>());
        Assert.Equal("memory", meta["archive"]!.GetValue<string>());
        Assert.Equal(1, meta["source"]!["compression"]!.GetValue<int>());
        Assert.Equal(8, meta["source"]!["alphaDepth"]!.GetValue<int>());
        Assert.Equal(8, meta["source"]!["alphaType"]!.GetValue<int>());
        JsonArray mean = meta["mean"]!.AsArray();
        Assert.Equal(4, mean.Count);

        // Mean of (255,0,0) (0,255,0) (0,0,255) (200,100,50) is (113.75, 88.75, 76.25); alpha is 255. Rounding is the implementer's choice.
        Assert.InRange(mean[0]!.GetValue<double>(), 113, 114.5);
        Assert.InRange(mean[1]!.GetValue<double>(), 88, 89.5);
        Assert.InRange(mean[2]!.GetValue<double>(), 76, 77.5);
        Assert.InRange(mean[3]!.GetValue<double>(), 254.5, 255);
    }

    [Fact]
    public void RgbaDelivery_IncludesTheGeneratedChain_WhenTheFileHasNone()
    {
        var files = new MemoryClientFiles();
        files.Add(FourColoursPath, FourColours(hasMips: 0));

        AtxTexture texture = Cache(files).Get("client:" + FourColoursPath, TextureDelivery.Rgba, 0, CancellationToken.None).Texture;

        // 2x2 (16 bytes) then 1x1 (4 bytes).
        Assert.Equal([16, 4], texture.Levels.Select(l => l.Length).ToArray());
    }

    [Fact]
    public void DxtDelivery_PassesTheFilesBlocksThroughUnchanged_AndFlagsOneBitAlpha()
    {
        byte[] block = [0x00, 0x00, 0x00, 0x40, 0xE4, 0x1B, 0x00, 0xFF];
        var files = new MemoryClientFiles();
        files.Add("Textures\\Test\\Key.blp", BlpTestWriter.Dxt(4, 4, alphaType: 0, alphaDepth: 1, [block]));
        files.Add("Textures\\Test\\Opaque.blp", BlpTestWriter.Dxt(4, 4, alphaType: 0, alphaDepth: 0, [block]));
        TextureCache cache = Cache(files);

        AtxTexture keyed = cache.Get("client:textures\\test\\key.blp", TextureDelivery.Dxt, 0, CancellationToken.None).Texture;
        AtxTexture opaque = cache.Get("client:textures\\test\\opaque.blp", TextureDelivery.Dxt, 0, CancellationToken.None).Texture;

        Assert.Equal(AtxFormat.Dxt1, keyed.Format);
        Assert.Equal(block, keyed.Levels[0]);
        Assert.True(keyed.Flags.HasFlag(AtxFlags.HasAlpha));
        Assert.True(keyed.Flags.HasFlag(AtxFlags.Alpha1Bit));
        Assert.Equal(AtxFormat.Dxt1, opaque.Format);
        Assert.False(opaque.Flags.HasFlag(AtxFlags.HasAlpha));
        Assert.Single(opaque.Levels);
    }

    [Theory]
    [InlineData((byte)1, AtxFormat.Dxt3)]
    [InlineData((byte)7, AtxFormat.Dxt5)]
    public void DxtDelivery_MapsAlphaTypeToTheFormat(byte alphaType, AtxFormat expected)
    {
        byte[] blocks = [.. Enumerable.Range(0, 16).Select(i => (byte)(i * 7))];
        var files = new MemoryClientFiles();
        files.Add("a.blp", BlpTestWriter.Dxt(4, 4, alphaType, alphaDepth: 8, [blocks]));

        AtxTexture texture = Cache(files).Get("client:a.blp", TextureDelivery.Dxt, 0, CancellationToken.None).Texture;

        Assert.Equal(expected, texture.Format);
        Assert.Equal(blocks, texture.Levels[0]);
    }

    [Fact]
    public void DxtDelivery_OfAPalettizedFile_FallsBackToRgba8()
    {
        var files = new MemoryClientFiles();
        files.Add(FourColoursPath, FourColours());

        AtxTexture texture = Cache(files).Get("client:" + FourColoursPath, TextureDelivery.Dxt, 0, CancellationToken.None).Texture;

        Assert.Equal(AtxFormat.Rgba8, texture.Format);
    }

    [Theory]
    [InlineData(8, 8, 4)]
    [InlineData(4, 4, 3)]
    [InlineData(2, 2, 2)]
    [InlineData(1, 1, 1)]
    [InlineData(64, 8, 4)]
    public void MaxSize_DropsTopLevels(int maxSize, int expectedWidth, int expectedLevels)
    {
        var files = new MemoryClientFiles();
        files.Add("m.blp", MippedEight());

        AtxTexture texture = Cache(files).Get("client:m.blp", TextureDelivery.Rgba, maxSize, CancellationToken.None).Texture;

        Assert.Equal(expectedWidth, texture.Width);
        Assert.Equal(expectedWidth, texture.Height);
        Assert.Equal(expectedLevels, texture.Levels.Count);
        Assert.Equal(expectedWidth * expectedWidth * 4, texture.Levels[0].Length);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(100)]
    public void MaxSize_MustBeZeroOrAPowerOfTwo(int maxSize)
    {
        var files = new MemoryClientFiles();
        files.Add("m.blp", MippedEight());

        TextureException error = Fails(() => Cache(files).Get("client:m.blp", TextureDelivery.Rgba, maxSize, CancellationToken.None));

        Assert.Equal(TextureException.BadArguments, error.Code);
    }

    [Fact]
    public void MaxSize_BelowASingleLevelDxtTexture_FallsBackToADownsizedRgba8()
    {
        byte[] block = [0x00, 0xF8, 0x1F, 0x00, 0xE4, 0x1B, 0x00, 0xFF];
        var files = new MemoryClientFiles();
        files.Add("d.blp", BlpTestWriter.Dxt(4, 4, 0, 0, [block]));

        AtxTexture texture = Cache(files).Get("client:d.blp", TextureDelivery.Dxt, 2, CancellationToken.None).Texture;

        Assert.Equal(AtxFormat.Rgba8, texture.Format);
        Assert.Equal(2, texture.Width);
    }

    [Fact]
    public void RepeatedRequests_AreServedFromTheCache_AndKeyCaseAndSlashesDoNotMatter()
    {
        var files = new MemoryClientFiles();
        files.Add(FourColoursPath, FourColours());
        TextureCache cache = Cache(files);

        TextureReply first = cache.Get("client:Textures\\Test\\FourColours.blp", TextureDelivery.Rgba, 0, CancellationToken.None);
        TextureReply second = cache.Get("client:textures/test/fourcolours.BLP", TextureDelivery.Rgba, 0, CancellationToken.None);

        Assert.Equal(first.Wire, second.Wire);
        TextureCacheStats stats = cache.Stats;
        Assert.Equal(1, stats.Decodes);
        Assert.Equal(1, stats.Encodes);
        Assert.Equal(1, stats.Hits);
        Assert.Equal(1, stats.Misses);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.Bytes > 0);
    }

    [Fact]
    public void AnRgbaClientWireEntry_IsChargedItsLevelsToo_BecauseItPinsThemAfterTheDecodedEntryGoes()
    {
        var files = new MemoryClientFiles();
        files.Add(FourColoursPath, FourColours());
        TextureCache cache = Cache(files);

        TextureReply reply = cache.Get("client:Textures\\Test\\FourColours.blp", TextureDelivery.Rgba, 0, CancellationToken.None);

        long levels = reply.Texture.Levels.Sum(l => (long)l.Length);
        Assert.True(cache.Bytes >= (2 * levels) + reply.Wire.Length, $"{cache.Bytes} bytes counted for {levels} level bytes and {reply.Wire.Length} wire bytes");
    }

    [Fact]
    public void ADifferentFormatOrMaxSize_IsADifferentWireEntry_ButDecodesOnce()
    {
        var files = new MemoryClientFiles();
        files.Add("m.blp", MippedEight());
        TextureCache cache = Cache(files);

        _ = cache.Get("client:m.blp", TextureDelivery.Rgba, 0, CancellationToken.None);
        _ = cache.Get("client:m.blp", TextureDelivery.Rgba, 4, CancellationToken.None);
        _ = cache.Get("client:m.blp", TextureDelivery.Rgba, 4, CancellationToken.None);

        TextureCacheStats stats = cache.Stats;
        Assert.Equal(1, stats.Decodes);
        Assert.Equal(2, stats.Encodes);
        Assert.Equal(1, stats.Hits);
    }

    [Fact]
    public async Task ConcurrentRequestsForOneKey_DecodeOnce()
    {
        var files = new MemoryClientFiles();
        files.Add("m.blp", MippedEight());
        TextureCache cache = Cache(files);

        TextureReply[] replies = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() => cache.Get("client:m.blp", TextureDelivery.Rgba, 0, CancellationToken.None))));

        Assert.All(replies, r => Assert.Equal(replies[0].Wire, r.Wire));
        Assert.Equal(1, cache.Stats.Decodes);
        Assert.Equal(1, cache.Stats.Encodes);
    }

    [Fact]
    public void Clear_EmptiesTheCache_AndTheNextRequestIsAMiss()
    {
        var files = new MemoryClientFiles();
        files.Add(FourColoursPath, FourColours());
        TextureCache cache = Cache(files);
        _ = cache.Get("client:" + FourColoursPath, TextureDelivery.Rgba, 0, CancellationToken.None);

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.Bytes);
        _ = cache.Get("client:" + FourColoursPath, TextureDelivery.Rgba, 0, CancellationToken.None);
        Assert.Equal(2, cache.Stats.Decodes);
        Assert.Equal("textures", cache.Name);
    }

    [Fact]
    public void TheWireBudgetBoundsTheCache_OldestEntriesAreEvicted()
    {
        var files = new MemoryClientFiles();
        for (int i = 0; i < 40; i++)
        {
            files.Add($"t{i}.blp", FourColours());
        }

        // Each wire entry is a few hundred bytes; a 4 KiB budget cannot hold 40 of them.
        TextureCache cache = Cache(files, decodedBudget: 4096, wireBudget: 4096);
        for (int i = 0; i < 40; i++)
        {
            _ = cache.Get($"client:t{i}.blp", TextureDelivery.Rgba, 0, CancellationToken.None);
        }

        Assert.True(cache.Count < 40, $"{cache.Count} entries kept under a 4 KiB budget");
        Assert.True(cache.Bytes <= 8192, $"{cache.Bytes} bytes kept under a 4 KiB + 4 KiB budget");
    }

    [Fact]
    public void MissingKey_IsAVisibleMagentaAndBlackChecker()
    {
        TextureCache cache = Cache(new MemoryClientFiles());

        AtxTexture texture = cache.Get("missing", TextureDelivery.Rgba, 0, CancellationToken.None).Texture;

        Assert.Equal(AtxFormat.Rgba8, texture.Format);
        Assert.Equal((8, 8), (texture.Width, texture.Height));
        Assert.True(texture.Flags.HasFlag(AtxFlags.Generated));
        byte[] level0 = texture.Levels[0];
        int magenta = 0, black = 0;
        for (int i = 0; i < level0.Length; i += 4)
        {
            if (level0[i] == 255 && level0[i + 1] == 0 && level0[i + 2] == 255 && level0[i + 3] == 255)
            {
                magenta++;
            }
            else if (level0[i] == 0 && level0[i + 1] == 0 && level0[i + 2] == 0 && level0[i + 3] == 255)
            {
                black++;
            }
        }

        Assert.Equal(64, magenta + black);
        Assert.InRange(magenta, 16, 48);
        Assert.InRange(black, 16, 48);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("client:")]
    [InlineData("client:textures\\x.png")]
    [InlineData("client:textures\\x")]
    [InlineData("gen:")]
    [InlineData("MISSING!")]
    public void MalformedKeys_AreBadKeys(string key)
    {
        TextureException error = Fails(() => Cache(new MemoryClientFiles()).Get(key, TextureDelivery.Rgba, 0, CancellationToken.None));

        Assert.Equal(TextureException.BadKey, error.Code);
    }

    [Fact]
    public void AnOverlongKey_IsABadKey()
    {
        TextureException error = Fails(() => Cache(new MemoryClientFiles()).Get("client:" + new string('a', 2000) + ".blp", TextureDelivery.Rgba, 0, CancellationToken.None));

        Assert.Equal(TextureException.BadKey, error.Code);
    }

    [Fact]
    public void AClientPathThatIsNotInTheIndex_IsNotFound()
    {
        TextureException error = Fails(() => Cache(new MemoryClientFiles()).Get("client:textures\\nothing.blp", TextureDelivery.Rgba, 0, CancellationToken.None));

        Assert.Equal(TextureException.NotFound, error.Code);
    }

    [Fact]
    public void PathTraversal_CannotLeaveTheClientIndex()
    {
        using var directory = new TestDirectory();
        string root = Path.Combine(directory.Path, "client");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "inside.blp"), FourColours());
        File.WriteAllBytes(Path.Combine(directory.Path, "outside.blp"), FourColours());
        var cache = new TextureCache(new DirectoryClientFiles(root), new GeneratedTextureRegistry());

        Assert.Equal(AtxFormat.Rgba8, cache.Get("client:inside.blp", TextureDelivery.Rgba, 0, CancellationToken.None).Texture.Format);
        foreach (string key in new[] { "client:..\\outside.blp", "client:../outside.blp", "client:" + Path.Combine(directory.Path, "outside.blp"), "client:\\\\?\\" + Path.Combine(directory.Path, "outside.blp") })
        {
            TextureException error = Fails(() => cache.Get(key, TextureDelivery.Rgba, 0, CancellationToken.None));
            Assert.True(error.Code is TextureException.NotFound or TextureException.BadKey, $"{key}: {error.Code}");
        }
    }

    [Fact]
    public void ACorruptOrJpegBlp_IsRejectedWithItsReason()
    {
        var files = new MemoryClientFiles();
        files.Add("bad.blp", FourColours()[..60]);
        files.Add("jpeg.blp", BlpTestWriter.WithType(FourColours(), 0));
        TextureCache cache = Cache(files);

        TextureException truncated = Fails(() => cache.Get("client:bad.blp", TextureDelivery.Rgba, 0, CancellationToken.None));
        TextureException jpeg = Fails(() => cache.Get("client:jpeg.blp", TextureDelivery.Rgba, 0, CancellationToken.None));

        Assert.Equal(TextureException.Rejected, truncated.Code);
        Assert.Equal(TextureException.Rejected, jpeg.Code);
        Assert.Contains("jpeg", jpeg.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedKeys_GoToTheRegisteredScheme_WithTheRestOfTheKeyAsArguments()
    {
        var registry = new GeneratedTextureRegistry();
        var source = new RecordingSource("test");
        registry.Add(source);
        registry.Freeze();
        TextureCache cache = Cache(new MemoryClientFiles(), registry);

        AtxTexture texture = cache.Get("gen:test/map0/12/34", TextureDelivery.Rgba, 0, CancellationToken.None).Texture;

        Assert.Equal(["map0/12/34"], source.Calls);
        Assert.Equal((2, 2), (texture.Width, texture.Height));
        Assert.True(texture.Flags.HasFlag(AtxFlags.Generated));
        Assert.Equal("gen:test/map0/12/34", texture.Meta["key"]!.GetValue<string>());
    }

    [Fact]
    public void GeneratedKeys_AreCachedLikeAnyOther()
    {
        var registry = new GeneratedTextureRegistry();
        var source = new RecordingSource("test");
        registry.Add(source);
        TextureCache cache = Cache(new MemoryClientFiles(), registry);

        _ = cache.Get("gen:test/a", TextureDelivery.Rgba, 0, CancellationToken.None);
        _ = cache.Get("gen:test/a", TextureDelivery.Rgba, 0, CancellationToken.None);

        Assert.Single(source.Calls);
    }

    [Fact]
    public void AnUnknownScheme_IsAnUnknownTextureScheme_NotABadKey()
    {
        TextureException error = Fails(() => Cache(new MemoryClientFiles()).Get("gen:nosuch/1", TextureDelivery.Rgba, 0, CancellationToken.None));

        Assert.Equal(TextureException.UnknownScheme, error.Code);
        Assert.Equal("unknown-texture-scheme", TextureException.UnknownScheme);
    }

    [Fact]
    public void TheRegistry_RefusesDuplicateSchemes_AndAdditionsAfterFreeze()
    {
        var registry = new GeneratedTextureRegistry();
        registry.Add(new RecordingSource("a"));

        Assert.Throws<ArgumentException>(() => registry.Add(new RecordingSource("a")));
        registry.Freeze();
        Assert.Throws<InvalidOperationException>(() => registry.Add(new RecordingSource("b")));
        Assert.True(registry.TryGet("a", out _));
        Assert.False(registry.TryGet("b", out _));
        Assert.Equal(["a"], registry.Schemes);
    }

    [Fact]
    public void ACancelledToken_StopsTheRequest()
    {
        var files = new MemoryClientFiles();
        files.Add(FourColoursPath, FourColours());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Cache(files).Get("client:" + FourColoursPath, TextureDelivery.Rgba, 0, cts.Token));
    }

    private sealed class RecordingSource(string scheme) : IGeneratedTextureSource
    {
        private readonly List<string> _calls = [];

        public string Scheme => scheme;

        public IReadOnlyList<string> Calls => _calls;

        public AtxTexture Generate(string arguments, CancellationToken cancellationToken)
        {
            _calls.Add(arguments);
            return new AtxTexture(AtxFormat.Rgba8, 2, 2, 1, AtxFlags.Generated, [new byte[16]], new JsonObject());
        }
    }
}
