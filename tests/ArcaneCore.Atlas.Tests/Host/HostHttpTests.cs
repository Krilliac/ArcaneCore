using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Host;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host;

/// <summary>The REST surface of the host over a real loopback listener: authentication, the Host guard, static files and routes.</summary>
public sealed class HostHttpTests
{
    private static readonly TileKey Goldshire = Geo.TileAt(-9462.66, 16.1915);

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response)
        => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task EveryApiRouteRefusesARequestWithoutTheBearerToken()
    {
        await using HostRig rig = await HostRig.StartAsync();
        (HttpMethod Method, string Path)[] routes =
        [
            (HttpMethod.Get, "api/status"),
            (HttpMethod.Get, "api/commands"),
            (HttpMethod.Post, "api/command"),
            (HttpMethod.Get, "api/tiles?map=0"),
            (HttpMethod.Get, "api/tile/terrain/0/49/31"),
            (HttpMethod.Post, "api/capture/abc"),
            (HttpMethod.Get, "api/nothing"),
        ];

        foreach ((HttpMethod method, string path) in routes)
        {
            using var request = new HttpRequestMessage(method, path);
            using HttpResponseMessage response = await rig.Anonymous.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("Bearer", response.Headers.WwwAuthenticate.Select(h => h.Scheme));
            JsonNode body = await ReadJsonAsync(response);
            Assert.Equal("unauthorized", body.Str("error.code"));
            Assert.DoesNotContain(rig.Server!.Token, body.ToJsonString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AWrongOrMisplacedToken_IsRefused_AndTheSchemeIsCaseInsensitive()
    {
        await using HostRig rig = await HostRig.StartAsync();
        string token = rig.Server!.Token;

        async Task<HttpStatusCode> StatusWith(Action<HttpRequestMessage> configure)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/status");
            configure(request);
            using HttpResponseMessage response = await rig.Anonymous.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await StatusWith(r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token + "x")));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusWith(r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token[..^1])));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusWith(r => r.Headers.Authorization = new AuthenticationHeaderValue("Basic", token)));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusWith(r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", string.Empty)));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusWith(r => r.RequestUri = new Uri($"api/status?token={token}", UriKind.Relative)));
        Assert.Equal(HttpStatusCode.OK, await StatusWith(r => r.Headers.Authorization = new AuthenticationHeaderValue("bearer", token)));
        Assert.Equal(HttpStatusCode.OK, await StatusWith(r => r.Headers.TryAddWithoutValidation("Authorization", $"Bearer   {token}  ")));
    }

    [Fact]
    public async Task TheTokenIs32RandomBytes_AndDiffersPerRun()
    {
        await using HostRig first = await HostRig.StartAsync();
        await using HostRig second = await HostRig.StartAsync();

        byte[] bytes = System.Buffers.Text.Base64Url.DecodeFromChars(first.Server!.Token);

        Assert.Equal(32, bytes.Length);
        Assert.NotEqual(first.Server.Token, second.Server!.Token);
        Assert.NotEqual(first.Server.Port, second.Server.Port);
    }

    [Fact]
    public async Task OnlyTheLoopbackEndpointIsListening()
    {
        await using HostRig rig = await HostRig.StartAsync();
        int port = rig.Server!.Port;

        IPEndPoint[] listeners = [.. System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(e => e.Port == port)];

        Assert.NotEmpty(listeners);
        Assert.All(listeners, e => Assert.Equal(IPAddress.Loopback, e.Address));
        Assert.Equal("127.0.0.1", rig.Server.BaseAddress.Host);
        Assert.Equal($"http://127.0.0.1:{port}/#t={rig.Server.Token}", rig.Server.BrowserAddress.AbsoluteUri);
    }

    [Fact]
    public async Task AHostHeaderThatIsNotTheLoopbackEndpoint_IsRefusedBeforeAnythingElse()
    {
        await using HostRig rig = await HostRig.StartAsync();
        int port = rig.Server!.Port;

        async Task<(HttpStatusCode Status, string Body)> Fetch(string path, string host)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Host = host;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rig.Server!.Token);
            using HttpResponseMessage response = await rig.Http.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        foreach (string host in new[] { "evil.example", $"evil.example:{port}", $"127.0.0.1:{port + 1}", "127.0.0.1", $"localhost.evil.example:{port}", $"127.0.0.1.evil.example:{port}", $"[::1]:{port}" })
        {
            foreach (string path in new[] { "api/status", "/", "app/js/main.js" })
            {
                (HttpStatusCode status, string body) = await Fetch(path, host);
                Assert.Equal(HttpStatusCode.BadRequest, status);
                Assert.Contains("bad-host", body, StringComparison.Ordinal);
            }
        }

        Assert.Equal(HttpStatusCode.OK, (await Fetch("api/status", $"localhost:{port}")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Fetch("api/status", $"LOCALHOST:{port}")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Fetch("api/status", $"127.0.0.1:{port}")).Status);
    }

    [Fact]
    public async Task AnUnknownApiRoute_Is404_AndAWrongMethodIs405WithAnAllowHeader()
    {
        await using HostRig rig = await HostRig.StartAsync();

        using HttpResponseMessage unknown = await rig.Http.GetAsync("api/nothing");
        using HttpResponseMessage bare = await rig.Http.GetAsync("api");
        using HttpResponseMessage extra = await rig.Http.GetAsync("api/status/more");
        using HttpResponseMessage postStatus = await rig.Http.PostAsync("api/status", Json("{}"));
        using HttpResponseMessage getCommand = await rig.Http.GetAsync("api/command");
        using HttpResponseMessage getCapture = await rig.Http.GetAsync("api/capture/abc");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, bare.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, extra.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, postStatus.StatusCode);
        Assert.Equal("GET", Assert.Single(postStatus.Content.Headers.Allow));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, getCommand.StatusCode);
        Assert.Equal("POST", Assert.Single(getCommand.Content.Headers.Allow));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, getCapture.StatusCode);
    }

    [Fact]
    public async Task Status_AndTheCommandList_AreServedWithoutWrapping()
    {
        await using HostRig rig = await HostRig.StartAsync();

        using HttpResponseMessage status = await rig.Http.GetAsync("api/status");
        using HttpResponseMessage commands = await rig.Http.GetAsync("api/commands");

        Assert.Equal("application/json; charset=utf-8", status.Content.Headers.ContentType!.ToString());
        Assert.Equal("no-store", status.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", Assert.Single(status.Headers.GetValues("X-Content-Type-Options")));
        JsonNode statusBody = await ReadJsonAsync(status);
        Assert.Equal("serve", statusBody.Str("mode"));
        Assert.Equal(0, statusBody.Num("ui.connections"));
        JsonArray list = (await ReadJsonAsync(commands)).AsArray();
        string[] names = [.. list.Select(c => c.Str("name"))];
        Assert.Contains("camera.goto", names);
        Assert.Contains("test.echo", names);
        JsonNode capture = list.Single(c => c.Str("name") == "capture")!;
        Assert.Contains("headless", capture.At("surfaces")!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Equal("object", capture.Str("schema.type"));
    }

    [Fact]
    public async Task StaticFiles_AreServedWithoutAToken_WithTheirContentType()
    {
        await using HostRig rig = await HostRig.StartAsync();

        foreach (string path in new[] { "/", "index.html" })
        {
            using HttpResponseMessage index = await rig.Anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
            Assert.Equal("text/html; charset=utf-8", index.Content.Headers.ContentType!.ToString());
            Assert.Equal(FakeAssets.IndexHtml, await index.Content.ReadAsStringAsync());
            Assert.Equal("no-cache", index.Headers.CacheControl!.ToString());
            Assert.Equal("no-referrer", Assert.Single(index.Headers.GetValues("Referrer-Policy")));
        }

        using HttpResponseMessage script = await rig.Anonymous.GetAsync("app/js/main.js");
        using HttpResponseMessage head = await rig.Anonymous.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/"));

        Assert.Equal("text/javascript; charset=utf-8", script.Content.Headers.ContentType!.ToString());
        Assert.Equal("console.log('atlas');", await script.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(FakeAssets.IndexHtml.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task StaticRoutes_RefuseMissingFiles_UnsafePaths_AndOtherMethods()
    {
        await using HostRig rig = await HostRig.StartAsync();

        foreach (string path in new[] { "app/missing.js", "app/", "app/js/", "app/js%5Cmain.js", "app/C%3A/x", "other", "index.html/extra", "app/js/main.js/", "app//js/main.js" })
        {
            using HttpResponseMessage response = await rig.Anonymous.GetAsync(path);
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"{path} answered {(int)response.StatusCode}");
        }

        using HttpResponseMessage post = await rig.Anonymous.PostAsync("/", Json("{}"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal(new[] { "GET", "HEAD" }, post.Content.Headers.Allow.Order());
    }

    [Fact]
    public async Task APathThatClimbsOutOfTheAppFolder_NeverReachesTheAssets()
    {
        await using HostRig rig = await HostRig.StartAsync();

        foreach (string target in new[] { "/app/../../../etc/passwd", "/app/js/../../secret", "/app/%2e%2e/%2e%2e/secret", "/app/..%5c..%5csecret" })
        {
            string reply = await RawGetAsync(rig.Server!.Port, target);
            string statusLine = reply.Split("\r\n")[0];

            Assert.DoesNotContain("200", statusLine, StringComparison.Ordinal);
            Assert.DoesNotContain("root:", reply, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WithoutAnEmbeddedUi_TheRootExplainsThatTheUiIsNotBuilt()
    {
        await using HostRig rig = await HostRig.StartAsync(assets: new NoAssets());

        using HttpResponseMessage root = await rig.Anonymous.GetAsync("/");
        using HttpResponseMessage app = await rig.Anonymous.GetAsync("app/js/main.js");
        using HttpResponseMessage head = await rig.Anonymous.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, root.StatusCode);
        Assert.Equal("missing", Assert.Single(root.Headers.GetValues("X-Atlas-UI")));
        Assert.Equal("text/html; charset=utf-8", root.Content.Headers.ContentType!.ToString());
        string page = await root.Content.ReadAsStringAsync();
        Assert.Contains("UI not built", page, StringComparison.Ordinal);
        Assert.Contains("arcane-atlas ctl status", page, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Server!.Token, page, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, app.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Command_RunsThroughTheRegistry_AndAFailedCommandIsStillHttp200()
    {
        await using HostRig rig = await HostRig.StartAsync();

        JsonObject ok = await rig.PostCommandAsync("test.echo", new JsonObject { ["text"] = "hello" });
        JsonObject failed = await rig.PostCommandAsync("camera.goto", new JsonObject());
        JsonObject unknown = await rig.PostCommandAsync("no.such");
        JsonObject extra = await rig.PostCommandAsync("test.echo", new JsonObject { ["nope"] = 1 });

        Assert.True(ok.Flag("ok"));
        Assert.Equal("test.echo", ok.Str("command"));
        Assert.Equal("hello", ok.Str("data.echo"));
        Assert.Equal("Http", ok.Str("data.origin"));
        Assert.False(ok.Flag("data.headless"));
        Assert.False(failed.Flag("ok"));
        Assert.Equal(CommandException.BadArguments, failed.Str("error.code"));
        Assert.Equal(CommandException.UnknownCommand, unknown.Str("error.code"));
        Assert.Equal(CommandException.BadArguments, extra.Str("error.code"));
    }

    [Fact]
    public async Task Command_TakesAnAdvisoryOriginLabel_AndPublishesItInTheCommandLog()
    {
        await using HostRig rig = await HostRig.StartAsync();

        JsonObject cli = await rig.PostCommandAsync("test.echo", origin: "cli");
        JsonObject script = await rig.PostCommandAsync("test.echo", origin: "script");
        JsonObject mcp = await rig.PostCommandAsync("test.echo", origin: "mcp");
        JsonObject spoof = await rig.PostCommandAsync("test.echo", origin: "ui:deadbeef");
        JsonObject none = await rig.PostCommandAsync("test.echo");

        Assert.Equal(new[] { "Cli", "Script", "Mcp", "Http", "Http" }, new[] { cli, script, mcp, spoof, none }.Select(r => r.Str("data.origin")));
        Assert.Equal(new[] { "cli", "script", "mcp", "http", "http" }, rig.EventsOf(EventHub.Topics.Command).Select(e => e.Str("origin")));
    }

    [Fact]
    public async Task Command_RefusesMalformedBodies_With400()
    {
        await using HostRig rig = await HostRig.StartAsync();
        string[] bodies =
        [
            string.Empty,
            "not json",
            "[1,2]",
            "42",
            "{}",
            """{"name":5}""",
            """{"name":""}""",
            """{"name":"status","args":[1]}""",
            """{"name":"status","args":"x"}""",
            """{"name":"status","name":"help"}""",
            """{"name":"status","args":{"a":1,"a":2}}""",
            $$"""{"name":"status","args":{{new string('[', 100)}}{{new string(']', 100)}}}""",
        ];

        foreach (string body in bodies)
        {
            using HttpResponseMessage response = await rig.Http.PostAsync("api/command", Json(body));

            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"body '{body[..Math.Min(body.Length, 40)]}' answered {(int)response.StatusCode}");
            Assert.Equal("bad-request", (await ReadJsonAsync(response)).Str("error.code"));
        }
    }

    [Fact]
    public async Task Command_RefusesABodyOverTheLimit_With413()
    {
        await using HostRig rig = await HostRig.StartAsync();
        string head = $"POST /api/command HTTP/1.1\r\nHost: 127.0.0.1:{rig.Server!.Port}\r\nAuthorization: Bearer {rig.Server.Token}\r\nContent-Type: application/json\r\nConnection: close\r\n";

        // The declared length alone decides: no body is sent, and the server must not wait for one.
        string over = await RawAsync(rig.Server.Port, head + $"Content-Length: {(4 * 1024 * 1024) + 1}\r\n\r\n");
        string within = await RawAsync(rig.Server.Port, head + "Content-Length: 22\r\n\r\n{\"name\":\"test.echo\"}  ");

        Assert.StartsWith("HTTP/1.1 413", over, StringComparison.Ordinal);
        Assert.Contains("too-large", over, StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 200", within, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tiles_ReportsStatusWithoutBuilding_AndRefusesBadQueries()
    {
        await using HostRig rig = await HostRig.StartAsync();
        rig.Terrain.AddChunks(LayerIds.Terrain, Goldshire, Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, 60));

        using HttpResponseMessage all = await rig.Http.GetAsync("api/tiles?map=0");
        using HttpResponseMessage around = await rig.Http.GetAsync($"api/tiles?map=0&layer=terrain&x={Goldshire.X}&y={Goldshire.Y}&radius=1");

        JsonArray tiles = (await ReadJsonAsync(all)).At("tiles")!.AsArray();
        Assert.Equal("Unknown", Assert.Single(tiles)!.Str("state"));
        Assert.Equal(9, (await ReadJsonAsync(around)).At("tiles")!.AsArray().Count);
        Assert.Equal(0, rig.Terrain.Builds);

        foreach ((string query, HttpStatusCode expected) in new[]
        {
            ("map=", HttpStatusCode.BadRequest),
            ("", HttpStatusCode.BadRequest),
            ("map=-1", HttpStatusCode.BadRequest),
            ("map=0&x=abc&y=1", HttpStatusCode.BadRequest),
            ("map=0&x=1", HttpStatusCode.BadRequest),
            ("map=0&layer=nope", HttpStatusCode.BadRequest),
            ("map=0&radius=99&x=1&y=1", HttpStatusCode.BadRequest),
            ("map=0&layer=nav.poly", HttpStatusCode.ServiceUnavailable),
        })
        {
            using HttpResponseMessage response = await rig.Http.GetAsync($"api/tiles?{query}");
            Assert.True(response.StatusCode == expected, $"?{query} answered {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task Tile_IsServedAsPartCountThenLengthPrefixedChunks_ThroughTheCache()
    {
        await using HostRig rig = await HostRig.StartAsync();
        GeometryChunk low = Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, 60, attribute: 1, part: 0);
        GeometryChunk high = Geo.Quad(LayerIds.Terrain, Goldshire, -9500, -9400, 10, 30, 70, attribute: 2, part: 1, kind: "ground");
        rig.Terrain.AddChunks(LayerIds.Terrain, Goldshire, high, low);

        using HttpResponseMessage response = await rig.Http.GetAsync($"api/tile/terrain/0/{Goldshire.X}/{Goldshire.Y}");
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        using HttpResponseMessage again = await rig.Http.GetAsync($"api/tile/terrain/0/{Goldshire.X}/{Goldshire.Y}");
        byte[] bodyAgain = await again.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("Ok", Assert.Single(response.Headers.GetValues("X-Atlas-Tile-State")));
        Assert.Equal(body.Length, response.Content.Headers.ContentLength);
        IReadOnlyList<byte[]> parts = SplitTile(body);
        Assert.Equal(2, parts.Count);
        GeometryChunk first = GeometryWire.Read(parts[0]);
        GeometryChunk second = GeometryWire.Read(parts[1]);
        Assert.Equal(0, first.Part);
        Assert.Equal(1, second.Part);
        Assert.Equal(Goldshire, first.Tile);
        Assert.Equal(LayerIds.Terrain, first.LayerId);
        Assert.Equal(2, first.TriangleCount);
        Assert.Equal(WorldGrid.Origin(Goldshire), first.Origin);
        Assert.Equal(60, first.Vertex(0).Z, 4);
        Assert.Equal(70, second.Vertex(0).Z, 4);
        Assert.Equal("ground", second.Attributes[2].Str("kind"));
        Assert.Equal(body, bodyAgain);
        Assert.Equal(1, rig.Terrain.BuildsOf(LayerIds.Terrain, Goldshire));
        IReadOnlyList<byte[]> cached = rig.Context.Cache.GetWire(LayerIds.Terrain, Goldshire, CancellationToken.None);
        Assert.Equal(cached[0], parts[0]);
        Assert.Equal(cached[1], parts[1]);

        await rig.DataAsync("cache.clear");
        using HttpResponseMessage rebuilt = await rig.Http.GetAsync($"api/tile/terrain/0/{Goldshire.X}/{Goldshire.Y}");
        Assert.Equal(body, await rebuilt.Content.ReadAsByteArrayAsync());
        Assert.Equal(2, rig.Terrain.BuildsOf(LayerIds.Terrain, Goldshire));
    }

    [Fact]
    public async Task Tile_WithoutGeometry_HasZeroParts_AndTheStateAndReasonInHeaders()
    {
        await using HostRig rig = await HostRig.StartAsync();
        var empty = new TileKey(0, Goldshire.X, Goldshire.Y + 1);
        var rejected = new TileKey(0, Goldshire.X, Goldshire.Y + 2);
        rig.Terrain.AddEmpty(LayerIds.Terrain, empty);
        rig.Terrain.AddRejected(LayerIds.Terrain, rejected, "bad magic: expected MAPS");

        using HttpResponseMessage emptyResponse = await rig.Http.GetAsync($"api/tile/terrain/0/{empty.X}/{empty.Y}");
        using HttpResponseMessage missing = await rig.Http.GetAsync("api/tile/terrain/0/1/1");
        using HttpResponseMessage refused = await rig.Http.GetAsync($"api/tile/terrain/0/{rejected.X}/{rejected.Y}");

        foreach ((HttpResponseMessage response, string state, string detail) in new[]
        {
            (emptyResponse, "Empty", "no features"),
            (missing, "Missing", "no such tile in the fixture"),
            (refused, "Rejected", "bad magic: expected MAPS"),
        })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(state, Assert.Single(response.Headers.GetValues("X-Atlas-Tile-State")));
            Assert.Equal(detail, Uri.UnescapeDataString(Assert.Single(response.Headers.GetValues("X-Atlas-Tile-Detail"))));
            Assert.Equal(new byte[4], await response.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task TileDetail_IsPercentEncoded_SoNoReasonCanInjectAHeaderOrBreakTheResponse()
    {
        await using HostRig rig = await HostRig.StartAsync();
        string hostile = "bad\r\nX-Injected: 1\r\n\r\n<script>alert(1)</script> Größe — ✓ 100%";
        rig.Terrain.AddRejected(LayerIds.Terrain, Goldshire, hostile);

        using HttpResponseMessage response = await rig.Http.GetAsync($"api/tile/terrain/0/{Goldshire.X}/{Goldshire.Y}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Injected"));
        string encoded = Assert.Single(response.Headers.GetValues("X-Atlas-Tile-Detail"));
        Assert.DoesNotContain('\r', encoded);
        Assert.DoesNotContain('\n', encoded);
        Assert.All(encoded, c => Assert.True(c is > ' ' and < (char)127, $"U+{(int)c:X4} in the header value"));
        Assert.Equal(hostile, Uri.UnescapeDataString(encoded));
        Assert.Equal(new byte[4], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Tile_FromASourceThatThrowsForBadData_IsServedAsRejected_NotAsEmptySpace()
    {
        await using HostRig rig = await HostRig.StartAsync();
        rig.Terrain.AddFailing(LayerIds.Terrain, Goldshire, new InvalidDataException("height block is truncated"));
        var buggy = new TileKey(0, Goldshire.X, Goldshire.Y + 1);
        rig.Terrain.AddFailing(LayerIds.Terrain, buggy, new ArgumentException("a source bug"));

        using HttpResponseMessage broken = await rig.Http.GetAsync($"api/tile/terrain/0/{Goldshire.X}/{Goldshire.Y}");
        using HttpResponseMessage bug = await rig.Http.GetAsync($"api/tile/terrain/0/{buggy.X}/{buggy.Y}");

        Assert.Equal(HttpStatusCode.OK, broken.StatusCode);
        Assert.Equal("Rejected", Assert.Single(broken.Headers.GetValues("X-Atlas-Tile-State")));
        Assert.Contains("height block is truncated", Uri.UnescapeDataString(Assert.Single(broken.Headers.GetValues("X-Atlas-Tile-Detail"))), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.InternalServerError, bug.StatusCode);
        Assert.Equal("failed", (await ReadJsonAsync(bug)).Str("error.code"));
    }

    [Fact]
    public async Task Tile_RefusesUnknownLayersAndBadCoordinates_AndServesTheGlobalTile()
    {
        await using HostRig rig = await HostRig.StartAsync();
        TileKey global = TileKey.Global(0);
        rig.Terrain.AddChunks(LayerIds.Terrain, global, Geo.Quad(LayerIds.Terrain, global, -9500, -9400, 10, 30, 80));

        foreach ((string path, HttpStatusCode expected, string code) in new[]
        {
            ("api/tile/nav.poly/0/49/31", HttpStatusCode.NotFound, "unknown-layer"),
            ("api/tile/no.such/0/49/31", HttpStatusCode.NotFound, "unknown-layer"),
            ("api/tile/terrain/x/49/31", HttpStatusCode.BadRequest, "bad-args"),
            ("api/tile/terrain/-1/49/31", HttpStatusCode.BadRequest, "bad-args"),
            ("api/tile/terrain/0/a/31", HttpStatusCode.BadRequest, "bad-args"),
            ("api/tile/terrain/0/64/0", HttpStatusCode.BadRequest, "bad-args"),
            ("api/tile/terrain/0/0/64", HttpStatusCode.BadRequest, "bad-args"),
            ("api/tile/terrain/0/-2/-2", HttpStatusCode.BadRequest, "bad-args"),
            ("api/tile/terrain/0/-1/3", HttpStatusCode.BadRequest, "bad-args"),
            ("api/tile/terrain/0/99999999999/0", HttpStatusCode.BadRequest, "bad-args"),
        })
        {
            using HttpResponseMessage response = await rig.Http.GetAsync(path);
            Assert.True(response.StatusCode == expected, $"{path} answered {(int)response.StatusCode}");
            Assert.Equal(code, (await ReadJsonAsync(response)).Str("error.code"));
        }

        using HttpResponseMessage globalResponse = await rig.Http.GetAsync("api/tile/terrain/0/-1/-1");
        IReadOnlyList<byte[]> parts = SplitTile(await globalResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, globalResponse.StatusCode);
        Assert.Equal(global, GeometryWire.Read(Assert.Single(parts)).Tile);
        using HttpResponseMessage post = await rig.Http.PostAsync("api/tile/terrain/0/49/31", Json("{}"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    [Fact]
    public async Task ACaptureAnswerForARequestNobodyIsWaitingFor_IsRefused()
    {
        await using HostRig rig = await HostRig.StartAsync();
        using var png = new ByteArrayContent(TestPng.Create(8, 8));
        using var notPng = new ByteArrayContent("<html>"u8.ToArray());

        using HttpResponseMessage unknown = await rig.Http.PostAsync("api/capture/not-waiting", png);
        using HttpResponseMessage invalid = await rig.Http.PostAsync("api/capture/not-waiting", notPng);
        using HttpResponseMessage empty = await rig.Http.PostAsync("api/capture/not-waiting", new ByteArrayContent([]));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("not-found", (await ReadJsonAsync(unknown)).Str("error.code"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task ABodyPostedByAStrangerWithTheTokenButNoPendingRequest_DoesNotCrashTheServer()
    {
        await using HostRig rig = await HostRig.StartAsync();

        for (int i = 0; i < 5; i++)
        {
            using HttpResponseMessage response = await rig.Http.PostAsync($"api/capture/{i}", new ByteArrayContent(TestPng.Create(8, 8)));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using HttpResponseMessage status = await rig.Http.GetAsync("api/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
    }

    /// <summary>Splits the body of <c>/api/tile</c>: u32 part count, then per part u32 length and that many bytes (little-endian).</summary>
    internal static IReadOnlyList<byte[]> SplitTile(byte[] body)
    {
        Assert.True(body.Length >= 4, "the body holds the part count");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(body);
        var parts = new List<byte[]>();
        int offset = 4;
        for (uint i = 0; i < count; i++)
        {
            Assert.True(offset + 4 <= body.Length, "a part length is missing");
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset)));
            offset += 4;
            Assert.True(offset + length <= body.Length, "a part is cut short");
            parts.Add(body[offset..(offset + length)]);
            offset += length;
        }

        Assert.Equal(body.Length, offset);
        return parts;
    }

    /// <summary>A GET with the request target exactly as written (an HTTP client would normalize dot segments away).</summary>
    private static Task<string> RawGetAsync(int port, string target)
        => RawAsync(port, $"GET {target} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");

    /// <summary>Sends the request text as written and returns everything the server answers until it closes the connection.</summary>
    private static async Task<string> RawAsync(int port, string requestText)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using NetworkStream stream = client.GetStream();
        byte[] request = Encoding.ASCII.GetBytes(requestText);
        await stream.WriteAsync(request);
        using var reply = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        byte[] chunk = new byte[16 * 1024];
        int read;

        // Stop at the end of the first complete response (by Content-Length) rather than at connection close: after a refused
        // body Kestrel may keep the connection open to drain the declared bytes and then reset it, which is not the answer.
        while (!IsCompleteResponse(reply) && (read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
        {
            reply.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(reply.ToArray());
    }

    private static bool IsCompleteResponse(MemoryStream reply)
    {
        string text = Encoding.ASCII.GetString(reply.GetBuffer(), 0, (int)reply.Length);
        int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (end < 0)
        {
            return false;
        }

        foreach (string line in text[..end].Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line["Content-Length:".Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int length))
            {
                return reply.Length >= end + 4 + length;
            }
        }

        return false;
    }
}
