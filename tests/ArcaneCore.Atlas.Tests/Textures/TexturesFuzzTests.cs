using System.Buffers.Binary;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Textures;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Textures;

/// <summary>
/// Hostile and corrupt texture input (design section 10, "Hostile/corrupt files"): the BLP reader and the ATX1 reader may only
/// ever throw <see cref="InvalidDataException"/>, and a header that promises a huge image must not allocate it before the data
/// proves it exists. The mutations are deterministic (fixed seed), so a failure reproduces from the printed case.
/// </summary>
public sealed class TexturesFuzzTests
{
    private const int Cases = 12_000;

    private static readonly BlpTestWriter.Px[] Palette = [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(9, 9, 9)];

    private static List<(string Name, byte[] File)> Seeds()
    {
        byte[] block = [0x00, 0x00, 0x00, 0x40, 0xE4, 0x1B, 0x00, 0xFF];
        byte[] alpha = [0x50, 0xFA, 0xAF, 0x05, 0x88, 0x88, 0x21, 0x43];
        var levels = new List<BlpTestWriter.PaletteLevel> { new([1, 1, 1, 1], null), new([2], null) };
        return
        [
            ("pal-none", BlpTestWriter.Palettized(4, 4, Palette, [.. Enumerable.Repeat((byte)1, 16)], 0, null)),
            ("pal-a8", BlpTestWriter.Palettized(4, 4, Palette, [.. Enumerable.Repeat((byte)2, 16)], 8, [.. Enumerable.Repeat((byte)128, 16)])),
            ("pal-a1", BlpTestWriter.Palettized(8, 2, Palette, [.. Enumerable.Repeat((byte)0, 16)], 1, [0xFF, 0x0F])),
            ("pal-a4", BlpTestWriter.Palettized(4, 2, Palette, [.. Enumerable.Repeat((byte)0, 8)], 4, [1, 2, 3, 4])),
            ("pal-mips", BlpTestWriter.Palettized(4, 4, Palette, [.. Enumerable.Repeat((byte)0, 16)], 0, null, hasMips: 1, moreLevels: levels)),
            ("dxt1", BlpTestWriter.Dxt(4, 4, 0, 1, [block])),
            ("dxt3", BlpTestWriter.Dxt(4, 4, 1, 8, [[.. alpha, .. block]])),
            ("dxt5", BlpTestWriter.Dxt(4, 4, 7, 8, [[.. alpha, .. block]])),
            ("raw", BlpTestWriter.Raw(2, 2, [.. Enumerable.Range(0, 16).Select(i => (byte)(i * 15))])),
        ];
    }

    private static void MustOnlyThrowInvalidData(string what, Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            // The one allowed failure.
        }
        catch (Exception e)
        {
            Assert.Fail($"{what}: {e.GetType().Name} escaped instead of InvalidDataException: {e.Message}");
        }
    }

    [Fact]
    public void EveryTruncationOfEveryEncoding_OnlyThrowsInvalidData()
    {
        int cases = 0;
        foreach ((string name, byte[] file) in Seeds())
        {
            for (int length = 0; length < file.Length; length++)
            {
                byte[] cut = file[..length];
                MustOnlyThrowInvalidData($"{name} truncated to {length}", () => BlpDecoder.Decode(cut));
                cases++;
            }

            // The whole file is the control: it must still decode.
            Assert.NotEmpty(BlpDecoder.Decode(file).Levels);
        }

        Assert.True(cases > 1500, $"only {cases} truncations ran");
    }

    [Fact]
    public void RandomByteFlipsAndHeaderFields_OnlyThrowInvalidData_OrDecode()
    {
        var random = new Random(0x5EED1A);
        List<(string Name, byte[] File)> seeds = Seeds();
        int decoded = 0, refused = 0;
        for (int i = 0; i < Cases; i++)
        {
            (string name, byte[] file) = seeds[i % seeds.Count];
            byte[] mutant = (byte[])file.Clone();
            switch (random.Next(4))
            {
                case 0: // flip a few random bytes anywhere
                    for (int k = random.Next(1, 5); k > 0; k--)
                    {
                        mutant[random.Next(mutant.Length)] = (byte)random.Next(256);
                    }

                    break;
                case 1: // randomise one header u32 (type, dimensions, offsets or sizes)
                    BinaryPrimitives.WriteUInt32LittleEndian(mutant.AsSpan(4 * random.Next(0, 37)), (uint)random.NextInt64(0, uint.MaxValue));
                    break;
                case 2: // randomise the encoding bytes
                    mutant[8 + random.Next(4)] = (byte)random.Next(256);
                    break;
                default: // truncate and flip
                    mutant = mutant[..random.Next(0, mutant.Length)];
                    if (mutant.Length > 0)
                    {
                        mutant[random.Next(mutant.Length)] ^= 0xFF;
                    }

                    break;
            }

            try
            {
                BlpImage image = BlpDecoder.Decode(mutant);
                Assert.NotEmpty(image.Levels);
                decoded++;
            }
            catch (InvalidDataException)
            {
                refused++;
            }
            catch (Exception e)
            {
                Assert.Fail($"case {i} ({name}, seed 0x5EED1A): {e.GetType().Name} escaped: {e.Message}");
            }
        }

        // Both outcomes occurred, so the loop really exercised the decoder rather than failing on every input.
        Assert.True(decoded > 200, $"only {decoded} mutants decoded");
        Assert.True(refused > 200, $"only {refused} mutants were refused");
    }

    [Fact]
    public void AHeaderPromisingHugeDimensions_DoesNotAllocateThemBeforeTheDataProvesIt()
    {
        byte[] file = BlpTestWriter.Palettized(4, 4, Palette, [.. Enumerable.Repeat((byte)0, 16)], 8, [.. Enumerable.Repeat((byte)255, 16)]);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), 4096);

        long before = GC.GetAllocatedBytesForCurrentThread();
        MustOnlyThrowInvalidData("4096x4096 header on a 1.2 KB file", () => BlpDecoder.Decode(file));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 4 * 1024 * 1024, $"{allocated} bytes allocated for a file that cannot hold the image");
    }

    [Fact]
    public void Atx1Reader_OnlyThrowsInvalidData_ForTruncationsAndFlips()
    {
        var texture = new AtxTexture(
            AtxFormat.Rgba8, 2, 2, 1, AtxFlags.HasAlpha,
            [Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), [1, 2, 3, 4]],
            new JsonObject { ["key"] = "client:x.blp" });
        byte[] good = TextureWire.Write(texture);
        for (int length = 0; length < good.Length; length++)
        {
            byte[] cut = good[..length];
            MustOnlyThrowInvalidData($"ATX1 truncated to {length}", () => TextureWire.Read(cut));
        }

        var random = new Random(0xA7C1);
        int refused = 0;
        for (int i = 0; i < 3000; i++)
        {
            byte[] mutant = (byte[])good.Clone();
            for (int k = random.Next(1, 4); k > 0; k--)
            {
                mutant[random.Next(mutant.Length)] = (byte)random.Next(256);
            }

            try
            {
                _ = TextureWire.Read(mutant);
            }
            catch (InvalidDataException)
            {
                refused++;
            }
            catch (Exception e)
            {
                Assert.Fail($"ATX1 case {i}: {e.GetType().Name} escaped: {e.Message}");
            }
        }

        Assert.True(refused > 100, $"only {refused} ATX1 mutants were refused");
    }
}
