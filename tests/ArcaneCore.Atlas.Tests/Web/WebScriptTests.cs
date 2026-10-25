using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Textures;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Atlas.Tests.Web;

/// <summary>
/// Runs the JavaScript tests under node: the golden parity suite (the browser's ATG1 decoder, camera, tile, catalog and
/// view code held to the numbers the C# core writes into a temp directory) and the pure-module suites. Node's own exit
/// code and TAP summary decide; a run that finds no tests, or a golden suite that ran without its data, fails here.
/// </summary>
public sealed class WebScriptTests(ITestOutputHelper output)
{
    /// <summary>The golden suite's tests, by name; each must report <c>ok</c> (a skip or a missing name fails).</summary>
    private static readonly string[] GoldenTests =
    [
        "ATG1 decoder matches GeometryWire.Write",
        "ATG1 test writer is byte-identical to GeometryWire.Write",
        "camera maths matches CameraMath",
        "tile maths matches WorldGrid",
        "layer catalog matches LayerCatalog",
        "view helpers match ViewState",
    ];

    /// <summary>The lane A1 golden tests in materials.test.mjs (ATG1 v2, ATX1); each must report <c>ok</c> in the main run.</summary>
    private static readonly string[] MaterialsGoldenTests =
    [
        "ATG1 v2 decoder matches GeometryWire.Write",
        "ATG1 v2 test writer is byte-identical to GeometryWire.Write",
        "ATX1 decoder matches TextureWire.Write",
        "ATX1 test writer is byte-identical to TextureWire.Write",
        "corrupt ATG1 v2 and ATX1 files are refused by the browser decoders",
    ];

    /// <summary>The browser material tests (browser.materials.test.mjs); when ARCANECORE_TEST_BROWSER is set each must report <c>ok</c>, not skip.</summary>
    private static readonly string[] BrowserMaterialTests =
    [
        "a textured quad in a real browser, RGBA path",
        "a textured quad in a real browser, S3TC path",
        "an opaque DXT1 texture is uploaded as RGB: index 3 stays black under alpha-key, S3TC path",
        "a missing texture draws the placeholder once and is counted",
    ];

    private static readonly TimeSpan NodeTimeout = TimeSpan.FromMinutes(5);

    [NodeFact]
    public async Task JavaScriptSuites_PassAgainstTheCSharpGolden()
    {
        string golden = TempDirectory("atlas-web-golden-");
        try
        {
            JsonObject manifest = WebGolden.Export(golden);
            Assert.True(manifest["cameras"]!.AsArray().Count >= 9, "the golden export wrote fewer cameras than the three modes need");
            Assert.True(File.Exists(Path.Combine(golden, "golden.json")));

            IReadOnlyList<string> files = WebTestEnvironment.TestFiles(RealDataScript, "clientfs.test.mjs");
            Assert.Contains(files, f => f.EndsWith("golden.test.mjs", StringComparison.Ordinal));
            NodeRun run = await WebTestEnvironment.RunNodeTestsAsync(
                files,
                new Dictionary<string, string?>
                {
                    [WebGolden.Variable] = golden,
                    [AtlasWebRealTerrainFactAttribute.RealDataVariable] = null,
                },
                NodeTimeout);
            output.WriteLine($"node --test over {files.Count} files: tests {run.Tests}, pass {run.Pass}, fail {run.Fail}, skipped {run.Skipped}, exit {run.ExitCode}");

            Assert.True(run.ExitCode == 0, run.Digest());
            Assert.Equal(0, run.Fail);
            Assert.True(run.Pass >= 130, $"only {run.Pass} JS tests passed; the runner found fewer tests than the suites hold. {run.Digest()}");
            Assert.Equal(run.Tests, run.Pass + run.Skipped);
            foreach (string name in GoldenTests.Concat(MaterialsGoldenTests))
            {
                Assert.True(run.Passed(name), $"golden test '{name}' did not report ok. {run.Digest()}");
            }

            Assert.Contains(files, f => f.EndsWith("materials.test.mjs", StringComparison.Ordinal));

            if (Environment.GetEnvironmentVariable("ARCANECORE_TEST_BROWSER") is { Length: > 0 })
            {
                // A browser was asked for: a skip here (bad path, variable not reaching node) would read as a pass.
                foreach (string name in BrowserMaterialTests)
                {
                    Assert.True(run.Passed(name), $"browser material test '{name}' did not report ok although ARCANECORE_TEST_BROWSER is set. {run.Digest()}");
                }
            }
        }
        finally
        {
            Directory.Delete(golden, recursive: true);
        }
    }

    [NodeFact]
    public async Task GoldenSuite_FailsWithoutItsData()
    {
        // The other half of the parity proof: with no golden directory the suite must fail, not pass by comparing nothing.
        string goldenScript = Path.Combine(WebTestEnvironment.WebTests, "golden.test.mjs");
        NodeRun run = await WebTestEnvironment.RunNodeTestsAsync([goldenScript], new Dictionary<string, string?> { [WebGolden.Variable] = null }, NodeTimeout);
        output.WriteLine($"golden.test.mjs without {WebGolden.Variable}: tests {run.Tests}, pass {run.Pass}, fail {run.Fail}, exit {run.ExitCode}");

        Assert.NotEqual(0, run.ExitCode);
        Assert.True(run.Fail >= GoldenTests.Length, run.Digest());
    }

    [NodeFact]
    public async Task MaterialsGoldenSuite_FailsWithoutItsData()
    {
        string script = Path.Combine(WebTestEnvironment.WebTests, "materials.test.mjs");
        NodeRun run = await WebTestEnvironment.RunNodeTestsAsync([script], new Dictionary<string, string?> { [WebGolden.Variable] = null }, NodeTimeout);
        output.WriteLine($"materials.test.mjs without {WebGolden.Variable}: tests {run.Tests}, pass {run.Pass}, fail {run.Fail}, exit {run.ExitCode}");

        Assert.NotEqual(0, run.ExitCode);
        Assert.True(run.Fail >= MaterialsGoldenTests.Length, run.Digest());
    }

    [Fact]
    public void GoldenCorruptTextures_AndV2Chunks_AreRefusedByTheCSharpReaders()
    {
        string golden = TempDirectory("atlas-web-corrupt-a1-");
        try
        {
            JsonObject manifest = WebGolden.Export(golden);
            JsonArray v2 = manifest["v2"]![0]!["corrupt"]!.AsArray();
            Assert.Equal(WebGolden.CorruptV2Names, v2.Select(c => c!["name"]!.GetValue<string>()).ToArray());
            foreach (JsonNode? entry in v2)
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(golden, entry!["file"]!.GetValue<string>()));
                Assert.True(Record.Exception(() => GeometryWire.Read(bytes)) is not null, $"GeometryWire.Read accepted the v2 variant '{entry["name"]}'");
            }

            JsonArray textures = manifest["textures"]!["corrupt"]!.AsArray();
            Assert.Equal(WebGolden.CorruptTextureNames, textures.Select(c => c!["name"]!.GetValue<string>()).ToArray());
            foreach (JsonNode? entry in textures)
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(golden, entry!["file"]!.GetValue<string>()));
                Assert.True(Record.Exception(() => TextureWire.Read(bytes)) is not null, $"TextureWire.Read accepted the variant '{entry["name"]}'");
            }

            foreach (JsonNode? entry in manifest["textures"]!["entries"]!.AsArray())
            {
                TextureWire.Read(File.ReadAllBytes(Path.Combine(golden, entry!["file"]!.GetValue<string>())));
            }
        }
        finally
        {
            Directory.Delete(golden, recursive: true);
        }
    }

    [Fact]
    public void GoldenCorruptVariants_AreRefusedByGeometryWire()
    {
        string golden = TempDirectory("atlas-web-corrupt-");
        try
        {
            JsonObject manifest = WebGolden.Export(golden);
            byte[] terrain = File.ReadAllBytes(Path.Combine(golden, "terrain.bin"));
            GeometryChunk good = GeometryWire.Read(terrain);
            Assert.True(good.TriangleCount > 0);

            JsonArray corrupt = manifest["corrupt"]!.AsArray();
            Assert.Equal(WebGolden.CorruptNames, corrupt.Select(c => c!["name"]!.GetValue<string>()).ToArray());
            foreach (JsonNode? entry in corrupt)
            {
                string name = entry!["name"]!.GetValue<string>();
                byte[] bytes = File.ReadAllBytes(Path.Combine(golden, entry["file"]!.GetValue<string>()));
                Exception? refused = Record.Exception(() => GeometryWire.Read(bytes));
                Assert.True(refused is not null, $"GeometryWire.Read accepted the '{name}' corrupt variant; the JS decoder is tested to refuse it");
            }
        }
        finally
        {
            Directory.Delete(golden, recursive: true);
        }
    }

    [AtlasWebRealTerrainFact]
    public async Task RealTerrainTile_ReachesTheBrowserDecoderIntact()
    {
        string root = AtlasWebRealTerrainFactAttribute.RealTerrainRoot()!;
        string work = TempDirectory("atlas-web-real-");
        try
        {
            RealTerrainExport export = RealTerrainExport.Write(root, work);
            output.WriteLine(
                $"real tile {export.FileName}: {export.Samples} samples, {export.Triangles} triangles, height {export.MinZ:F2}..{export.MaxZ:F2}; " +
                $"{export.MapFiles} .map, {export.VMapTiles} .vmtile, {export.NavTiles} .mmtile files listed");

            // Facts only a real extraction gives: thousands of tiles and a tile that is not flat.
            Assert.True(export.MapFiles >= 1000, $"{export.MapFiles} .map files under {root}/maps");
            Assert.True(export.MaxZ - export.MinZ > 5, $"the sampled tile is flat ({export.MinZ}..{export.MaxZ}); it was not read from a real extraction");

            string script = Path.Combine(WebTestEnvironment.WebTests, RealDataScript);
            NodeRun run = await WebTestEnvironment.RunNodeTestsAsync(
                [script],
                new Dictionary<string, string?>
                {
                    [AtlasWebRealTerrainFactAttribute.RealDataVariable] = work,
                    [AtlasWebRealTerrainFactAttribute.Variable] = root,
                },
                NodeTimeout);
            output.WriteLine($"{RealDataScript}: tests {run.Tests}, pass {run.Pass}, fail {run.Fail}, skipped {run.Skipped}, exit {run.ExitCode}");

            Assert.True(run.ExitCode == 0, run.Digest());
            Assert.Equal(0, run.Skipped);
            Assert.Equal(RealDataTests.Length, run.Pass);
            foreach (string name in RealDataTests)
            {
                Assert.True(run.Passed(name), $"real-data test '{name}' did not report ok. {run.Digest()}");
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>The JS file the real-data fact runs on its own (the main run leaves it out).</summary>
    private const string RealDataScript = "realdata.test.mjs";

    private static readonly string[] RealDataTests =
    [
        "a real terrain tile decodes with the server heights",
        "cursor readout names the files the extraction holds",
        "every extracted file name round-trips through the tile maths",
    ];

    private static string TempDirectory(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
