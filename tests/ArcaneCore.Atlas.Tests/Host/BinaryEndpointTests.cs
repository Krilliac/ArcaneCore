using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host;

/// <summary>
/// <c>GET /api/bin/{name}</c> (design sections 1 and 10): binary endpoints registered through <see cref="BinaryEndpoints"/>, served
/// with the same bearer token, Host guard and <c>no-store</c> as every other <c>/api</c> route, as <c>application/octet-stream</c>
/// with an <c>X-Atlas-Format</c> header. The bearer, not <c>Origin</c>, is the cross-origin guard: HTTP routes never check
/// <c>Origin</c>, and a cross-origin page cannot set <c>Authorization</c> without a preflight the server never grants.
/// </summary>
public sealed class BinaryEndpointTests
{
    private static Task<HostRig> Rig(params IBinaryEndpoint[] endpoints)
        => HostRig.StartAsync(register: context =>
        {
            BinaryEndpoints registry = context.Services.Require<BinaryEndpoints>();
            foreach (IBinaryEndpoint endpoint in endpoints)
            {
                registry.Add(endpoint);
            }
        });

    private static FakeBinary Echo() => new("echo", (query, _) => Task.FromResult(BinaryReply.Ok(
        Encoding.UTF8.GetBytes(string.Join(';', query.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"))),
        "TEST1")));

    [Fact]
    public async Task WithoutTheBearerToken_TheEndpointIs401_EvenForAnUnknownName()
    {
        await using HostRig rig = await Rig(Echo());

        foreach (string path in new[] { "api/bin/echo", "api/bin/echo?a=1", "api/bin/nothing", "api/bin/", "api/bin" })
        {
            using HttpResponseMessage response = await rig.Anonymous.GetAsync(path);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("Bearer", response.Headers.WwwAuthenticate.Select(h => h.Scheme));
            Assert.DoesNotContain(rig.Server!.Token, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WithTheToken_TheBodyIsOctetStream_WithTheFormatHeader_AndNoStore()
    {
        await using HostRig rig = await Rig(Echo());

        using HttpResponseMessage response = await rig.Http.GetAsync("api/bin/echo?key=a%3Ab%5Cc.blp&fmt=dxt");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("TEST1", Assert.Single(response.Headers.GetValues("X-Atlas-Format")));
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Equal("fmt=dxt;key=a:b\\c.blp", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ALargeBody_IsServedWholeWithItsLength()
    {
        byte[] big = [.. Enumerable.Range(0, 3 * 1024 * 1024).Select(i => (byte)(i * 31))];
        await using HostRig rig = await Rig(new FakeBinary("big", (_, _) => Task.FromResult(BinaryReply.Ok(big, "BIG"))));

        using HttpResponseMessage response = await rig.Http.GetAsync("api/bin/big");

        Assert.Equal(big.Length, response.Content.Headers.ContentLength);
        Assert.Equal(big, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task HandlerErrors_BecomeTheirStatusWithTheStandardJsonError()
    {
        await using HostRig rig = await Rig(
            new FakeBinary("bad", (_, _) => Task.FromResult(BinaryReply.Error(400, "bad-args", "no key"))),
            new FakeBinary("gone", (_, _) => Task.FromResult(BinaryReply.Error(404, "not-found", "no such file"))),
            new FakeBinary("corrupt", (_, _) => Task.FromResult(BinaryReply.Error(422, "rejected", "jpeg BLP not supported"))));

        foreach ((string name, HttpStatusCode status, string code) in new[]
        {
            ("bad", HttpStatusCode.BadRequest, "bad-args"),
            ("gone", HttpStatusCode.NotFound, "not-found"),
            ("corrupt", HttpStatusCode.UnprocessableEntity, "rejected"),
        })
        {
            using HttpResponseMessage response = await rig.Http.GetAsync("api/bin/" + name);

            Assert.Equal(status, response.StatusCode);
            JsonNode body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Assert.Equal(code, body.Str("error.code"));
            Assert.False(string.IsNullOrEmpty(body.Str("error.message")));
        }
    }

    [Fact]
    public async Task AHandlerThatThrows_Is500_WithoutAStackTrace()
    {
        await using HostRig rig = await Rig(new FakeBinary("boom", (_, _) => throw new InvalidOperationException("kaput")));

        using HttpResponseMessage response = await rig.Http.GetAsync("api/bin/boom");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal("failed", JsonNode.Parse(body)!.Str("error.code"));
        Assert.DoesNotContain(" at ArcaneCore", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownNamesExtraSegmentsAndOtherMethods_AreRefused()
    {
        await using HostRig rig = await Rig(Echo());

        foreach (string path in new[] { "api/bin/nothing", "api/bin/", "api/bin", "api/bin/echo/extra", "api/bin/..%2Fstatus", "api/bin/ECHO" })
        {
            using HttpResponseMessage response = await rig.Http.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("not-found", JsonNode.Parse(await response.Content.ReadAsStringAsync())!.Str("error.code"));
        }

        using HttpResponseMessage post = await rig.Http.PostAsync("api/bin/echo", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Contains("GET", post.Content.Headers.Allow);
    }

    [Fact]
    public async Task AForeignHostHeader_IsRefused_EvenWithTheToken()
    {
        await using HostRig rig = await Rig(Echo());
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/bin/echo");
        request.Headers.Host = "evil.example";
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rig.Server!.Token);

        using HttpResponseMessage response = await rig.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("bad-host", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AForeignOriginWithoutTheBearer_IsRefusedByTheBearer_NotByTheOrigin()
    {
        await using HostRig rig = await Rig(Echo());
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/bin/echo");
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");

        using HttpResponseMessage response = await rig.Anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Headers"));

        // A preflight is never granted either, which is why a cross-origin page cannot attach the Authorization header.
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "api/bin/echo");
        preflight.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "authorization");
        using HttpResponseMessage preflightResponse = await rig.Anonymous.SendAsync(preflight);
        Assert.False(preflightResponse.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(preflightResponse.IsSuccessStatusCode);
    }

    [Fact]
    public async Task TheQueryStringTokenIsNotAnAlternative()
    {
        await using HostRig rig = await Rig(Echo());

        using HttpResponseMessage response = await rig.Anonymous.GetAsync($"api/bin/echo?token={rig.Server!.Token}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Cancellation_ReachesTheHandler_WhenTheClientGoesAway()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HostRig rig = await Rig(new FakeBinary("slow", async (_, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }

            return BinaryReply.Ok([], "X");
        }));
        using var cts = new CancellationTokenSource();

        Task<HttpResponseMessage> call = rig.Http.GetAsync("api/bin/slow", cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TheRegistry_ValidatesNames_RefusesDuplicates_AndIsFrozenAfterCompose()
    {
        await using HostRig rig = HostRig.Create();
        BinaryEndpoints registry = rig.Context.Services.Require<BinaryEndpoints>();

        Assert.Throws<InvalidOperationException>(() => registry.Add(Echo()));

        var fresh = new BinaryEndpoints();
        fresh.Add(Echo());
        Assert.Throws<ArgumentException>(() => fresh.Add(Echo()));
        foreach (string bad in new[] { string.Empty, "A", "a b", "a/b", "..", "a.b", new string('a', 65) })
        {
            Assert.Throws<ArgumentException>(() => fresh.Add(new FakeBinary(bad, (_, _) => Task.FromResult(BinaryReply.Ok([], "X")))));
        }

        Assert.True(fresh.TryGet("echo", out _));
        Assert.False(fresh.TryGet("nothing", out _));
        Assert.Equal(["echo"], fresh.Names);
        fresh.Freeze();
        Assert.Throws<InvalidOperationException>(() => fresh.Add(new FakeBinary("late", (_, _) => Task.FromResult(BinaryReply.Ok([], "X")))));
        await Task.CompletedTask;
    }

    [Fact]
    public void BinaryReply_Factories_CarryTheirFields()
    {
        BinaryReply ok = BinaryReply.Ok([1, 2, 3], "ATX1");
        BinaryReply error = BinaryReply.Error(422, "rejected", "why");

        Assert.Equal(200, ok.Status);
        Assert.Equal([1, 2, 3], ok.Body);
        Assert.Equal("ATX1", ok.Format);
        Assert.Equal(422, error.Status);
        Assert.Equal("rejected", error.ErrorCode);
        Assert.Equal("why", error.ErrorMessage);
        Assert.Throws<ArgumentOutOfRangeException>(() => BinaryReply.Error(200, "x", "y"));
    }
}

/// <summary>A binary endpoint whose behaviour a test supplies.</summary>
internal sealed class FakeBinary(string name, Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<BinaryReply>> handler) : IBinaryEndpoint
{
    public string Name => name;

    public Task<BinaryReply> HandleAsync(IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken) => handler(query, cancellationToken);
}

/// <summary>A cache a test controls, for the participant list.</summary>
internal sealed class FakeCache(string name, long bytes = 0, int count = 0) : ICacheParticipant
{
    private int _clears;

    public string Name => name;

    public long Bytes { get; set; } = bytes;

    public int Count { get; set; } = count;

    public int Clears => Volatile.Read(ref _clears);

    public void Clear()
    {
        Interlocked.Increment(ref _clears);
        Bytes = 0;
        Count = 0;
    }
}
