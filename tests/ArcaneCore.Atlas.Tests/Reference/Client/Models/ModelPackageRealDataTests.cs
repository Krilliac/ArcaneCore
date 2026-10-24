using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Materials;
using ArcaneCore.Atlas.Reference.Client.Models;
using ArcaneCore.Atlas.Tests.Host;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using ArcaneCore.Atlas.Tests.Textures;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Models;

/// <summary>
/// The package builder against the real Wolf model (the offsets the synthetic fixtures use are the wowdev vanilla layout; this is
/// where a real file proves them). Only structural invariants are asserted: no value is invented. Skipped visibly without
/// ARCANECORE_TEST_CLIENT_DIR.
/// </summary>
[Collection("Atlas A1 real client textures")]
public sealed class ModelPackageRealDataTests
{
    private const string Wolf = "Creature\\Wolf\\Wolf.m2";

    [ClientArchiveFact]
    public async Task Wolf_PackagesWithValidBatches_AndEveryNamedTextureExists()
    {
        using var files = RealTextureData.Open();
        await using HostRig rig = HostRig.Create(register: context =>
        {
            context.Services.Add<IClientFileSource>(files);
            new ModelPackagesModule().Register(context);
        });

        JsonNode data = await rig.DataAsync("model.package", new JsonObject { ["path"] = Wolf });

        int triangles = (int)data.Num("triangles");
        Assert.True(data.Num("vertices") > 100);
        Assert.True(triangles > 100);
        JsonArray batches = data["batches"]!.AsArray();
        Assert.NotEmpty(batches);
        foreach (JsonNode? batch in batches)
        {
            long first = (long)batch.Num("firstTriangle");
            long count = (long)batch.Num("triangleCount");
            Assert.True(count > 0 && first >= 0 && first + count <= triangles, $"batch {first}+{count} of {triangles}");
            Material material = Material.FromJson(batch!["material"]!.AsObject());
            Assert.Equal("generic", material.Kind);
            foreach (TextureRef texture in material.Textures)
            {
                if (texture.Replace is null && texture.Key.StartsWith("client:", StringComparison.Ordinal))
                {
                    Assert.True(files.Exists(texture.Key["client:".Length..]), texture.Key);
                }
            }
        }

        Assert.Empty(data["problems"]!.AsArray());
    }

    [ClientArchiveFact]
    public async Task Wolf_Uv0IsFinite_AndTheWireRoundTrips()
    {
        using var files = RealTextureData.Open();
        await using HostRig rig = await HostRig.StartAsync(register: context =>
        {
            context.Services.Add<IClientFileSource>(files);
            new ModelPackagesModule().Register(context);
        });

        using HttpResponseMessage response = await rig.Http.GetAsync("api/bin/model?path=Creature%5CWolf%5CWolf.m2");
        GeometryChunk chunk = GeometryWire.Read(await response.Content.ReadAsByteArrayAsync());

        Assert.True(response.IsSuccessStatusCode);
        Assert.NotNull(chunk.Uv0);
        Assert.All(chunk.Uv0!, uv => Assert.True(float.IsFinite(uv.X) && float.IsFinite(uv.Y)));
        Assert.Equal(chunk.VertexCount, chunk.Uv0!.Length);
    }
}
