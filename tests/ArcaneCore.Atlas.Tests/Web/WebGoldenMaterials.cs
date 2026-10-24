using System.Buffers.Binary;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Materials;
using ArcaneCore.Atlas.Tests.Core;
using ArcaneCore.Atlas.Textures;

namespace ArcaneCore.Atlas.Tests.Web;

/// <summary>The ATG1 v2 and ATX1 halves of the golden export (lane A1): what the C# writers produce, for the JS decoders to match.</summary>
internal static partial class WebGolden
{
    /// <summary>Names of the corrupt ATX1 variants; each must be refused by TextureWire.Read and by the JS decoder.</summary>
    public static readonly string[] CorruptTextureNames = ["truncated", "magic", "version", "level-length", "json-length", "format", "level-count"];

    /// <summary>Names of the corrupt ATG1 v2 variants; each must be refused by GeometryWire.Read and by the JS decoder.</summary>
    public static readonly string[] CorruptV2Names = ["truncated", "truncated-header", "stream-mask", "batch-past-triangles", "batch-material", "batch-count", "nan-normal"];

    private static IEnumerable<(string Name, GeometryChunk Chunk)> V2Chunks()
    {
        yield return ("v2-quad", GeometryV2Fixtures.Quad());
        yield return ("v2-all", GeometryV2Fixtures.AllStreams());
        yield return ("v2-model", GeometryV2Fixtures.QuadWithModel());
    }

    private static JsonArray ExportV2(string directory)
    {
        var entries = new JsonArray();
        foreach ((string name, GeometryChunk chunk) in V2Chunks())
        {
            byte[] bytes = GeometryWire.Write(chunk);
            File.WriteAllBytes(Path.Combine(directory, name + ".bin"), bytes);
            entries.Add(new JsonObject
            {
                ["name"] = name,
                ["file"] = name + ".bin",
                ["expect"] = new JsonObject
                {
                    ["version"] = 2,
                    ["layer"] = chunk.LayerId,
                    ["byteLength"] = bytes.Length,
                    ["vertexCount"] = chunk.VertexCount,
                    ["triangleCount"] = chunk.TriangleCount,
                    ["positions"] = Doubles(chunk.Positions),
                    ["triangles"] = Uints(chunk.Triangles),
                    ["triangleAttributes"] = Uints(chunk.TriangleAttributes),
                    ["normals"] = chunk.Normals is null ? null : Doubles([.. chunk.Normals.SelectMany(n => new[] { n.X, n.Y, n.Z })]),
                    ["uv0"] = chunk.Uv0 is null ? null : Doubles([.. chunk.Uv0.SelectMany(n => new[] { n.X, n.Y })]),
                    ["uv1"] = chunk.Uv1 is null ? null : Doubles([.. chunk.Uv1.SelectMany(n => new[] { n.X, n.Y })]),
                    ["color0"] = chunk.Color0 is null ? null : Uints(chunk.Color0),
                    ["color1"] = chunk.Color1 is null ? null : Uints(chunk.Color1),
                    ["boneIndices"] = chunk.BoneIndices is null ? null : Uints(chunk.BoneIndices),
                    ["boneWeights"] = chunk.BoneWeights is null ? null : Uints(chunk.BoneWeights),
                    ["submesh"] = chunk.Submesh is null ? null : Uints([.. chunk.Submesh.Select(s => (uint)s)]),
                    ["batches"] = new JsonArray([.. chunk.Batches!.Select(b => (JsonNode)new JsonArray((int)b.FirstTriangle, (int)b.TriangleCount, (int)b.Material, (long)b.Tag))]),
                    ["materials"] = new JsonArray([.. chunk.Materials!.Select(m => (JsonNode)m.ToJson())]),
                    ["model"] = chunk.Model?.DeepClone(),
                },
            });
        }

        var corrupt = new JsonArray();
        byte[] good = File.ReadAllBytes(Path.Combine(directory, "v2-quad.bin"));
        foreach ((string name, byte[] bytes) in CorruptV2(good))
        {
            File.WriteAllBytes(Path.Combine(directory, "corrupt-v2-" + name + ".bin"), bytes);
            corrupt.Add(new JsonObject { ["name"] = name, ["file"] = "corrupt-v2-" + name + ".bin" });
        }

        return new JsonArray { new JsonObject { ["chunks"] = entries, ["corrupt"] = corrupt } };
    }

    /// <summary>Corrupt variants of the v2 quad (header 64 bytes, Quad stream mask 0x8B, batches at offset 256).</summary>
    public static IReadOnlyList<(string Name, byte[] Bytes)> CorruptV2(byte[] good)
    {
        var list = new List<(string, byte[])> { ("truncated", good[..^5]), ("truncated-header", good[..56]) };

        byte[] mask = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(mask.AsSpan(48), 0x1_00u);
        list.Add(("stream-mask", mask));

        byte[] past = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(past.AsSpan(256 + 4), 99);
        list.Add(("batch-past-triangles", past));

        byte[] material = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(256 + 8), 9);
        list.Add(("batch-material", material));

        byte[] count = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(count.AsSpan(52), 1_000_000);
        list.Add(("batch-count", count));

        byte[] nan = (byte[])good.Clone();
        BinaryPrimitives.WriteSingleLittleEndian(nan.AsSpan(112), float.NaN);
        list.Add(("nan-normal", nan));
        return list;
    }

    private static IEnumerable<(string Name, AtxTexture Texture)> Textures()
    {
        yield return ("tex-rgba", new AtxTexture(
            AtxFormat.Rgba8, 2, 2, 1, AtxFlags.HasAlpha,
            [[255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 0, 10, 20, 30, 255], [90, 80, 70, 60]],
            new JsonObject { ["key"] = "client:textures\\gold\\one.blp", ["mean"] = new JsonArray(1, 2, 3, 4) }));
        yield return ("tex-dxt1", new AtxTexture(
            AtxFormat.Dxt1, 4, 4, 1, AtxFlags.Alpha1Bit | AtxFlags.HasAlpha,
            [[0x00, 0xF8, 0x1F, 0x00, 0xE4, 0xE4, 0xE4, 0xE4]],
            new JsonObject { ["key"] = "client:textures\\gold\\dxt.blp" }));
        yield return ("tex-dxt5", new AtxTexture(
            AtxFormat.Dxt5, 8, 4, 1, AtxFlags.HasAlpha | AtxFlags.Generated,
            [[.. Enumerable.Range(1, 32).Select(i => (byte)i)], [.. Enumerable.Range(40, 16).Select(i => (byte)i)]],
            new JsonObject { ["key"] = "gen:gold/dxt5", ["note"] = "ü \"quoted\"" }));
        yield return ("tex-r8", new AtxTexture(AtxFormat.R8, 3, 1, 1, AtxFlags.None, [[7, 8, 9]], new JsonObject()));
    }

    private static JsonObject ExportTextures(string directory)
    {
        var entries = new JsonArray();
        byte[] first = [];
        foreach ((string name, AtxTexture texture) in Textures())
        {
            byte[] bytes = TextureWire.Write(texture);
            if (first.Length == 0)
            {
                first = bytes;
            }

            File.WriteAllBytes(Path.Combine(directory, name + ".atx"), bytes);
            entries.Add(new JsonObject
            {
                ["name"] = name,
                ["file"] = name + ".atx",
                ["expect"] = new JsonObject
                {
                    ["format"] = (int)texture.Format,
                    ["width"] = texture.Width,
                    ["height"] = texture.Height,
                    ["layers"] = texture.Layers,
                    ["flags"] = (int)texture.Flags,
                    ["byteLength"] = bytes.Length,
                    ["levels"] = new JsonArray([.. texture.Levels.Select(l => (JsonNode)new JsonArray([.. l.Select(b => (JsonNode)(int)b)]))]),
                    ["meta"] = texture.Meta.DeepClone(),
                },
            });
        }

        var corrupt = new JsonArray();
        foreach ((string name, byte[] bytes) in CorruptTextures(first))
        {
            File.WriteAllBytes(Path.Combine(directory, "corrupt-atx-" + name + ".atx"), bytes);
            corrupt.Add(new JsonObject { ["name"] = name, ["file"] = "corrupt-atx-" + name + ".atx" });
        }

        return new JsonObject { ["entries"] = entries, ["corrupt"] = corrupt };
    }

    /// <summary>Corrupt variants of an ATX1 file (header offsets: magic 0, version 4, format 6, levels 14, json bytes 16, data 20).</summary>
    public static IReadOnlyList<(string Name, byte[] Bytes)> CorruptTextures(byte[] good)
    {
        var list = new List<(string, byte[])> { ("truncated", good[..^3]) };

        byte[] magic = (byte[])good.Clone();
        magic[0] = (byte)'Y';
        list.Add(("magic", magic));

        byte[] version = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(version.AsSpan(4), 9);
        list.Add(("version", version));

        byte[] level = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(level.AsSpan(20), 17);
        list.Add(("level-length", level));

        byte[] json = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(json.AsSpan(16), (uint)good.Length);
        list.Add(("json-length", json));

        byte[] format = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(format.AsSpan(6), 99);
        list.Add(("format", format));

        byte[] count = (byte[])good.Clone();
        count[14] = 9;
        list.Add(("level-count", count));
        return list;
    }
}
