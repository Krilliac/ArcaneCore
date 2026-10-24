using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Materials;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Core;

/// <summary>
/// The material model of ATG1 v2 (design section 2.3). The numeric values are wire/API: blend 0 opaque, 1 alpha key, 2 alpha blend,
/// 3 additive, 4 additive alpha, 5 modulate, 6 modulate 2x (M2 and WMO share it); flags 0x1 unlit, 0x2 unfogged, 0x4 two-sided,
/// 0x8 no depth write, 0x10 no depth test, 0x20 clamp S, 0x40 clamp T, 0x80 use color0, 0x100 billboard (reserved), 0x200 env map.
/// </summary>
public sealed class MaterialTests
{
    [Fact]
    public void BlendModes_HaveTheDocumentedNumbers()
    {
        Assert.Equal(0, (int)BlendMode.Opaque);
        Assert.Equal(1, (int)BlendMode.AlphaKey);
        Assert.Equal(2, (int)BlendMode.AlphaBlend);
        Assert.Equal(3, (int)BlendMode.Additive);
        Assert.Equal(4, (int)BlendMode.AdditiveAlpha);
        Assert.Equal(5, (int)BlendMode.Modulate);
        Assert.Equal(6, (int)BlendMode.Modulate2x);
        Assert.Equal(7, Enum.GetValues<BlendMode>().Length);
    }

    [Fact]
    public void Flags_HaveTheDocumentedBits()
    {
        Assert.Equal(0u, (uint)MaterialFlags.None);
        Assert.Equal(0x1u, (uint)MaterialFlags.Unlit);
        Assert.Equal(0x2u, (uint)MaterialFlags.Unfogged);
        Assert.Equal(0x4u, (uint)MaterialFlags.TwoSided);
        Assert.Equal(0x8u, (uint)MaterialFlags.NoDepthWrite);
        Assert.Equal(0x10u, (uint)MaterialFlags.NoDepthTest);
        Assert.Equal(0x20u, (uint)MaterialFlags.ClampS);
        Assert.Equal(0x40u, (uint)MaterialFlags.ClampT);
        Assert.Equal(0x80u, (uint)MaterialFlags.UseColor0);
        Assert.Equal(0x100u, (uint)MaterialFlags.Billboard);
        Assert.Equal(0x200u, (uint)MaterialFlags.EnvMap);
    }

    [Fact]
    public void ToJson_UsesTheDocumentedNames_AndOmitsWhatIsUnset()
    {
        var plain = new Material("generic", BlendMode.Opaque, MaterialFlags.None, [new TextureRef("client:a.blp")]);

        JsonObject json = plain.ToJson();

        Assert.Equal(["kind", "blend", "flags", "textures", "alphaRef"], json.Select(p => p.Key).ToArray());
        Assert.Equal(0, json["alphaRef"]!.GetValue<double>());
        JsonObject texture = json["textures"]![0]!.AsObject();
        Assert.Equal(["key", "slot", "wrap"], texture.Select(p => p.Key).ToArray());
        Assert.Equal([0, 0], texture["wrap"]!.AsArray().Select(n => n!.GetValue<int>()).ToArray());
    }

    [Fact]
    public void JsonRoundTrip_KeepsFlagBitsAtOrAboveBit31()
    {
        var material = new Material("generic", BlendMode.Opaque, (MaterialFlags)0x8000_0201u, []);

        JsonObject json = material.ToJson();
        Material back = Material.FromJson(json);

        Assert.Equal(0x8000_0201L, json["flags"]!.GetValue<long>());
        Assert.Equal(material.Flags, back.Flags);
    }

    [Fact]
    public void JsonRoundTrip_KeepsEveryField()
    {
        Material original = GeometryV2Fixtures.Alpha();

        Material back = Material.FromJson(original.ToJson());

        Assert.Equal(original.ToJson().ToJsonString(), back.ToJson().ToJsonString());
        Assert.Equal(BlendMode.AlphaKey, back.Blend);
        Assert.Equal(MaterialFlags.Unlit | MaterialFlags.TwoSided, back.Flags);
        Assert.Equal(1, back.Textures[0].WrapT);
        Assert.Null(back.Textures[0].Replace);
        Assert.Equal(11, Material.FromJson(GeometryV2Fixtures.Replaceable().ToJson()).Textures[0].Replace);
    }

    [Fact]
    public void FromJson_RefusesWhatTheWireDoesNotAllow()
    {
        JsonObject Good() => GeometryV2Fixtures.Alpha().ToJson();
        Assert.Throws<InvalidDataException>(() => Material.FromJson(Edit(Good(), j => j["kind"] = "nonsense")));
        Assert.Throws<InvalidDataException>(() => Material.FromJson(Edit(Good(), j => j["blend"] = 7)));
        Assert.Throws<InvalidDataException>(() => Material.FromJson(Edit(Good(), j => j["blend"] = -1)));
        Assert.Throws<InvalidDataException>(() => Material.FromJson(Edit(Good(), j => j["alphaRef"] = 1.5)));
        Assert.Throws<InvalidDataException>(() => Material.FromJson(Edit(Good(), j => j["alphaRef"] = -0.1)));
        Assert.Throws<InvalidDataException>(() => Material.FromJson(Edit(Good(), j => j["flags"] = "x")));
        Assert.Throws<InvalidDataException>(() => Material.FromJson(Edit(Good(), j => j["textures"] = "x")));
        Assert.Throws<InvalidDataException>(() => Material.FromJson(Edit(Good(), j => j["textures"]![0]!["key"] = string.Empty)));
        Assert.Throws<InvalidDataException>(() => Material.FromJson(Edit(Good(), j => j["textures"]![0]!["wrap"] = new JsonArray(0))));
        Assert.Throws<InvalidDataException>(() => Material.FromJson(Edit(Good(), j => j["textures"]![0]!["wrap"] = new JsonArray(0, 2))));
        Assert.Throws<InvalidDataException>(() => Material.FromJson(Edit(Good(), j => j["color"] = new JsonArray(1, 1, 1))));
    }

    [Theory]
    [InlineData("generic")]
    [InlineData("terrain")]
    [InlineData("wmo")]
    [InlineData("m2")]
    [InlineData("skinned")]
    [InlineData("character")]
    public void EveryDocumentedKind_IsAccepted(string kind)
    {
        var material = new Material(kind, BlendMode.Opaque, MaterialFlags.None, []);

        Assert.Equal(kind, Material.FromJson(material.ToJson()).Kind);
    }

    [Fact]
    public void Batch_IsFourWords()
    {
        var batch = new GeometryBatch(3, 4, 5, 0x0002_0007);

        Assert.Equal(3u, batch.FirstTriangle);
        Assert.Equal(4u, batch.TriangleCount);
        Assert.Equal(5u, batch.Material);
        Assert.Equal(7, batch.Geoset);
        Assert.Equal(2, batch.Priority);
    }

    [Fact]
    public void Batch_PriorityIsSigned_SoANegativeM2PriorityReadsBackNegative()
    {
        var batch = new GeometryBatch(0, 1, 0, 0xFFFE_0009u);

        Assert.Equal(-2, batch.Priority);
        Assert.Equal(9, batch.Geoset);
    }

    private static JsonObject Edit(JsonObject json, Action<JsonObject> edit)
    {
        edit(json);
        return json;
    }
}
