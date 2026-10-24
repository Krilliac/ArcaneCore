using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using ArcaneCore.Atlas.Reference.Mpq;
using ArcaneCore.Atlas.Tests.Reference.Mpq;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Atlas.Tests.Web;

/// <summary>
/// Lane D4 (devui-assets): the tests of the browser-side client-file reader (<c>Web/js/clientfs</c>). The synthetic client
/// folder is written here with the existing test writers and the C# reference implementations, the JavaScript suite
/// (<c>clientfs.test.mjs</c>) is run over it under node and headless Chromium, and node's own TAP result decides. Nothing
/// is committed and no client byte is involved; the real-data half runs only with <c>ARCANECORE_TEST_CLIENT_DIR</c>.
/// </summary>
public sealed class ClientFsFixtureTests(ITestOutputHelper output)
{
    private const string Script = "clientfs.test.mjs";
    private static readonly TimeSpan NodeTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Every JS test (or subtest) that must report ok; a missing name or a skip fails the run.</summary>
    private static readonly string[] RequiredTests =
    [
        "limits.js states the parser limits of design 0.5",
        "mpq.js reads every member of the synthetic archives byte for byte",
        "mpq.js orders archives like MpqFileSystem.LoadOrder",
        "mpq.js mounts a patch chain in MpqFileSystem priority order",
        "mpq.js refuses every malformed archive with a typed error inside the worker timeout",
        "mpq.js refuses bzip2 with unsupported-compression until the census has run",
        "mpq.js census counts the compression masks of an archive",
        "explode.js expands every StormLib-made PKWARE vector",
        "explode.js refuses wrong sizes, truncation and bad headers",
        "dbc.js parses the synthetic tables and refuses damaged ones",
        "adt.js terrain and liquid ATG1 equal ClientTerrainGeometry byte for byte on synthetic tiles",
        "adt.js area names reach the chunk attributes and a view with an odd byteOffset gives the same bytes",
        "adt.js refuses malformed ADTs with bad-file inside the tile timeout",
        "adt.js builds a tile from the archive chain with Api.tile states",
        "validate.js accepts the 1.12.1 folder and reads the version resource",
        "validate.js treats archive names case-insensitively, ignores extra archives and never needs optional ones",
        "validate.js refuses a folder without Data",
        "validate.js refuses a folder missing a required archive and names it",
        "validate.js refuses a 2.x+ client by its archives",
        "validate.js only warns for a Data/<locale> folder alone",
        "validate.js warns for another 1.12 build and refuses a different layout with the build number",
        "validate.js names a damaged archive header",
        "validate.js reads PE version resources defensively",
        "scope.js opens only Data/*.MPQ and WoW.exe from a directory handle",
        "scope.js never opens or lists the WTF canary folder through scan and validation",
        "scope.js finds Data and WoW.exe whatever their letter case",
        "scope.js filters a webkitdirectory FileList by relative path before handing any File on",
        "scope.js reports no Data when the picked folder is Data itself or empty",
        "store.js keys cache entries by path, archive name, size and lastModified",
        "store.js evicts the least recently used entry beyond its byte budget",
        "store.js default cache budget is 2 GiB capped by the storage estimate",
        "pool.js sizes the worker pool min(4, hardwareConcurrency - 1)",
        "pool.js runs jobs and reports typed worker errors",
        "pool.js terminates a timed-out worker, respawns it and marks the file bad for the session",
        "pool.js times files out after 5 s and terrain tiles after 30 s",
        "the canary detector finds raw, hex, base64 and url-encoded canaries at any alignment",
        "real client: census of compression masks per archive; bzip2 stays refused when unset",
        "a module Worker starts under the public CSP header",
        "a module Worker starts under the local meta CSP (script-src fallback)",
        "dialog flows in headless Chromium (showDirectoryPicker mocked)",
        "first entry shows the text and the three buttons",
        "choosing a folder validates it, stores the handle and calls the picker as specified",
        "a later visit asks for one click to re-allow access and calls requestPermission from that click",
        "a denied re-allow keeps the banner and says the browser blocked access",
        "validation errors reach the dialog and nothing is stored or cached",
        "a locale folder alone is a warning, not a refusal, in the dialog",
        "Skip keeps the untextured view and the dialog stays closed on the next visit",
        "localTileProvider returns Api.tile envelopes whose geometry equals the C# ATG1 (through the Worker pool)",
        "the second load is served from the OPFS cache without reading an archive, and a changed archive misses it",
        "the webkitdirectory fallback caches parsed results and asks again only when the cache misses",
        "Skip on a cache-miss dialog is remembered for the session",
        "localTileProvider rejects at once when its signal aborts while the request waits for files",
        "the Worker mounts a tile chain in MpqFileSystem load order whatever order the archives arrive in",
        "localTileProvider resolves a patched tile from the patch when the FileList arrives in non-load order",
        "Forget folder while a tile is in flight leaves no cache entry behind",
        "settings show folder, build and cache size; Forget folder deletes the handle and the cache; the budget slider is capped",
        "never uploaded: no request, header, body or WebSocket frame carries a canary, and no body passes 4 KiB",
        "the recording catches a deliberate leak in every encoding (positive control)",
    ];

    private const string RealParityTest = "real client: the JS chain reads the sampled files and the patch order exactly as the C# MpqFileSystem";

    /// <summary>
    /// Grounds every expectation of the JS suite in the C# readers: the archives read back byte for byte through
    /// <see cref="MpqArchive"/>, the expected ATG1 chunks decode and name the areas, each malformed ADT is refused by
    /// <see cref="AdtFile"/>, the malformed archives that exercise the reader's decoders are refused by it too, the
    /// canaries are really in the sources, and the PE files carry the version the manifest says.
    /// </summary>
    [Fact]
    public void Fixtures_AreGroundedInTheCSharpReaders()
    {
        string dir = TempDirectory();
        try
        {
            JsonObject manifest = ClientFsFixtures.Write(dir);

            int members = 0;
            foreach ((string name, JsonNode? node) in manifest["archives"]!.AsObject())
            {
                using MpqArchive archive = MpqArchive.Open(Path.Combine(dir, node!["file"]!.GetValue<string>()));
                foreach (JsonNode? member in node["members"]!.AsArray())
                {
                    if (member!["deleted"]!.GetValue<bool>())
                    {
                        Assert.True(archive.TryFind(member["path"]!.GetValue<string>(), out MpqEntry entry), name);
                        Assert.True((entry.Block.Flags & MpqFileFlags.DeleteMarker) != 0);
                        continue;
                    }

                    byte[] bytes = archive.Read(member["path"]!.GetValue<string>());
                    Assert.Equal(member["size"]!.GetValue<int>(), bytes.Length);
                    Assert.Equal(member["sha256"]!.GetValue<string>(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
                    members++;
                }
            }

            Assert.True(members >= 20, $"{members} members");

            // Tiles: expected chunks decode, three of them carry liquid, areas are named in the tiles that have them.
            JsonArray tiles = manifest["tiles"]!.AsArray();
            Assert.Equal(4, tiles.Count);
            Assert.Equal(3, tiles.Count(t => t!["liquid"] is not null));
            int named = 0;
            foreach (JsonNode? tile in tiles)
            {
                byte[] terrain = File.ReadAllBytes(Path.Combine(dir, tile!["terrain"]!.GetValue<string>()));
                GeometryChunk chunk = GeometryWire.Read(terrain);
                Assert.Equal(tile["terrainTriangles"]!.GetValue<int>(), chunk.TriangleCount);
                Assert.True(tile["holeChunks"]!.GetValue<int>() > 0);
                named += Encoding.UTF8.GetString(terrain).Contains(ClientFsFixtures.NamedAreaName, StringComparison.Ordinal) ? 1 : 0;
                AdtFile adt = AdtFile.Parse(File.ReadAllBytes(Path.Combine(dir, tile["adt"]!.GetValue<string>())));
                Assert.Equal(256, adt.Chunks.Count());
                if (tile["liquid"] is { } liquidPath)
                {
                    Assert.True(GeometryWire.Read(File.ReadAllBytes(Path.Combine(dir, liquidPath.GetValue<string>()))).TriangleCount > 0);
                }
            }

            Assert.Equal(2, named); // tiles 1 and 2 have a chunk in area 12

            foreach (JsonNode? bad in manifest["malformedAdts"]!.AsArray())
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(dir, bad!["file"]!.GetValue<string>()));
                Assert.Throws<InvalidDataException>(() => AdtFile.Parse(bytes));
            }

            // The decoder-level malformed archives are refused by the C# reader as well (header-level limits are the browser's own, stricter, rules).
            using (MpqArchive control = MpqArchive.Open(Path.Combine(dir, manifest["malformedControl"]!["file"]!.GetValue<string>())))
            {
                Assert.Equal(manifest["malformedControl"]!["size"]!.GetValue<int>(), control.Read(manifest["malformedControl"]!["path"]!.GetValue<string>()).Length);
            }

            int refused = 0;
            foreach (JsonNode? c in manifest["malformedArchives"]!.AsArray())
            {
                string name = c!["name"]!.GetValue<string>();
                if (name is not ("zlib-bomb" or "zlib-short" or "explode-bomb" or "bzip2-refused"))
                {
                    continue;
                }

                using MpqArchive archive = MpqArchive.Open(Path.Combine(dir, c["file"]!.GetValue<string>()));
                Assert.ThrowsAny<Exception>(() => archive.Read(c["read"]!.GetValue<string>()));
                refused++;
            }

            Assert.Equal(4, refused);

            // Canaries really sit in the sources the browser reads.
            Assert.Contains(ClientFsFixtures.CanaryAdtString, Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(dir, "adt", "0-33-31.adt"))), StringComparison.Ordinal);
            Assert.Contains(ClientFsFixtures.CanaryAdtString, Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(dir, "adt", "0-33-32.adt"))), StringComparison.Ordinal);
            using (MpqArchive dbc = MpqArchive.Open(Path.Combine(dir, "archives", "dbc.MPQ")))
            {
                Assert.Contains(ClientFsFixtures.CanaryDbcString, Encoding.ASCII.GetString(dbc.Read("DBFilesClient\\CreatureDisplayInfo.dbc")), StringComparison.Ordinal);
            }

            using (MpqArchive texture = MpqArchive.Open(Path.Combine(dir, "archives", "texture.MPQ")))
            {
                Assert.Contains(Convert.ToHexString(ClientFsFixtures.CanaryBlpBytes), Convert.ToHexString(texture.Read("Textures\\Canary\\CanaryTexture.blp")), StringComparison.Ordinal);
            }

            Assert.Contains(ClientFsFixtures.CanaryConfig, File.ReadAllText(Path.Combine(dir, "wtf", "Config.wtf")), StringComparison.Ordinal);
            Assert.Equal(6, manifest["canaries"]!.AsArray().Count);

            // The version resources, read back by the operating system's own parser rather than by the writer.
            foreach ((string text, int major, int minor, int build, int priv) in new[] { ("1.12.1.5875", 1, 12, 1, 5875), ("1.12.2.6005", 1, 12, 2, 6005), ("2.4.3.8606", 2, 4, 3, 8606) })
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(Path.Combine(dir, "exe", $"WoW-{text}.exe"));
                Assert.Equal((major, minor, build, priv), (info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart));
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [NodeFact]
    public async Task ClientFsSuite_PassesAgainstTheCSharpFixtures()
    {
        string dir = TempDirectory();
        try
        {
            ClientFsFixtures.Write(dir);
            string browser = FindBrowser()
                ?? throw new Xunit.Sdk.XunitException("No Chrome or Edge was found. Set ARCANECORE_TEST_BROWSER to a Chromium executable; the dialog and Worker tests cannot be skipped.");
            string script = Path.Combine(WebTestEnvironment.WebTests, Script);
            Assert.True(File.Exists(script), $"{Script} is missing");
            NodeRun run = await WebTestEnvironment.RunNodeTestsAsync(
                [script],
                new Dictionary<string, string?>
                {
                    [ClientFsFixtures.Variable] = dir,
                    [ClientFsFixtures.RealVariable] = null,
                    ["ARCANECORE_TEST_BROWSER"] = browser,
                },
                NodeTimeout);
            output.WriteLine($"{Script}: tests {run.Tests}, pass {run.Pass}, fail {run.Fail}, skipped {run.Skipped}, exit {run.ExitCode}");

            Assert.True(run.ExitCode == 0, run.Digest());
            Assert.Equal(0, run.Fail);
            Assert.True(run.Pass >= RequiredTests.Length, $"only {run.Pass} JS tests passed; {RequiredTests.Length} are required. {run.Digest()}");
            Assert.Equal(run.Tests, run.Pass + run.Skipped);
            Assert.Equal(1, run.Skipped); // only the real-client parity test, which needs ARCANECORE_TEST_CLIENT_DIR
            Assert.False(run.Passed(RealParityTest), "the real-data test cannot have run without its manifest");
            foreach (string name in RequiredTests)
            {
                Assert.True(run.Passed(name), $"JS test '{name}' did not report ok. {run.Digest()}");
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [ClientArchiveFact]
    public async Task RealClient_SampledFilesAndCensus_MatchTheCSharpReader()
    {
        string root = ClientArchiveData.ClientRoot!;
        string dir = TempDirectory();
        try
        {
            ClientFsFixtures.Write(dir);
            JsonObject real = ClientFsFixtures.WriteRealSample(root, dir);
            Assert.True(real["samples"]!.AsArray().Count >= 40, "fewer than 40 sampled files");
            string browser = FindBrowser() ?? "";
            string script = Path.Combine(WebTestEnvironment.WebTests, Script);
            NodeRun run = await WebTestEnvironment.RunNodeTestsAsync(
                [script],
                new Dictionary<string, string?>
                {
                    [ClientFsFixtures.Variable] = dir,
                    [ClientFsFixtures.RealVariable] = dir,
                    ["ARCANECORE_TEST_CLIENT_DIR"] = root,
                    ["ARCANECORE_TEST_BROWSER"] = string.IsNullOrEmpty(browser) ? null : browser,
                },
                NodeTimeout);
            output.WriteLine($"{Script} over the real client: tests {run.Tests}, pass {run.Pass}, fail {run.Fail}, skipped {run.Skipped}");
            foreach (string line in run.Output.Split('\n').Where(l => l.StartsWith("# census", StringComparison.Ordinal)))
            {
                output.WriteLine(line.TrimEnd('\r'));
            }

            Assert.True(run.Passed(RealParityTest), run.Digest());
            Assert.True(run.Passed("real client: census of compression masks per archive; bzip2 stays refused when unset"), run.Digest());
        }
        finally
        {
            Cleanup(dir);
        }
    }

    /// <summary>
    /// A fresh directory under the temp folder, or (for running <c>node --test clientfs.test.mjs</c> by hand against the same
    /// fixtures) under the folder named by <c>ARCANECORE_ATLAS_CLIENTFS_KEEP</c>, in which case it is not deleted afterwards.
    /// </summary>
    private static string TempDirectory()
    {
        string? keep = Environment.GetEnvironmentVariable("ARCANECORE_ATLAS_CLIENTFS_KEEP");
        string dir = Path.Combine(string.IsNullOrWhiteSpace(keep) ? Path.GetTempPath() : keep, "atlas-clientfs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ARCANECORE_ATLAS_CLIENTFS_KEEP")))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>ARCANECORE_TEST_BROWSER, else a Chrome or Edge in the usual places or on PATH.</summary>
    private static string? FindBrowser()
    {
        string? configured = Environment.GetEnvironmentVariable("ARCANECORE_TEST_BROWSER");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        string[] fixedPaths =
        [
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            "/usr/bin/google-chrome",
            "/usr/bin/google-chrome-stable",
            "/usr/bin/chromium",
            "/usr/bin/chromium-browser",
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
        ];
        return fixedPaths.FirstOrDefault(File.Exists);
    }
}
