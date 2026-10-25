using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Host;
using ArcaneCore.Atlas.Host.Public;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host.Public;

/// <summary>The UI files the public listener serves in tests.</summary>
internal sealed class PublicAssets : IWebAssets
{
    public const string PublicHtml = "<!doctype html><title>fake public ui</title><script type=\"module\" src=\"/app/js/public/boot.js\"></script>";

    private readonly Dictionary<string, WebAsset> _files = new(StringComparer.Ordinal)
    {
        ["public.html"] = new WebAsset(Encoding.UTF8.GetBytes(PublicHtml), "text/html; charset=utf-8"),
        ["js/public/boot.js"] = new WebAsset(Encoding.UTF8.GetBytes("console.log('public');"), "text/javascript; charset=utf-8"),
        ["index.html"] = new WebAsset(Encoding.UTF8.GetBytes("<!doctype html><title>LOCAL UI</title>"), "text/html; charset=utf-8"),
        ["js/main.js"] = new WebAsset(Encoding.UTF8.GetBytes("console.log('local');"), "text/javascript; charset=utf-8"),
    };

    public bool TryGet(string path, out WebAsset asset) => _files.TryGetValue(path, out asset!);
}

/// <summary>A cookie session as a browser would hold it.</summary>
internal sealed record Visitor(string Cookie, string Csrf);

/// <summary>An HTTP answer with the parts the tests read.</summary>
internal sealed class Reply
{
    public int Status { get; init; }

    public string Text { get; init; } = string.Empty;

    public JsonNode? Json { get; init; }

    public Dictionary<string, string[]> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Header(string name) => Headers.TryGetValue(name, out string[]? values) ? string.Join(", ", values) : null;

    public string? Code => Json?["error"]?["code"]?.GetValue<string>();

    public IEnumerable<string> SetCookies => Headers.TryGetValue("Set-Cookie", out string[]? values) ? values : [];
}

/// <summary>A running public listener on a free loopback port with a fake world, a client and the captured log.</summary>
internal sealed class PublicRig : IAsyncDisposable
{
    private readonly string? _pfx;

    private PublicRig(PublicServer server, FakeWorld world, CapturingLoggers logs, HttpClient http, string scheme, int port, bool tls, string? pfx)
    {
        Server = server;
        World = world;
        Logs = logs;
        Http = http;
        Scheme = scheme;
        Port = port;
        Tls = tls;
        _pfx = pfx;
    }

    public PublicServer Server { get; }

    public FakeWorld World { get; }

    public CapturingLoggers Logs { get; }

    public HttpClient Http { get; }

    public string Scheme { get; }

    public int Port { get; }

    public bool Tls { get; }

    public string CookieName => Tls ? "__Host-atlas" : "atlas-dev";

    public string Origin => $"{Scheme}://127.0.0.1:{Port}";

    public PublicRuntime Runtime => Server.Runtime;

    /// <summary>Builds the settings of a test listener: a free loopback port and either a self-signed certificate or the insecure development mode.</summary>
    public static PublicOptions Options(bool tls, out string? pfx)
    {
        var options = new PublicOptions { Enabled = true, Bind = $"127.0.0.1:{FreePort.Next()}" };
        pfx = null;
        if (tls)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            pfx = Path.Combine(Path.GetTempPath(), $"atlas-public-test-{Guid.NewGuid():N}.pfx");
            File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, "pw"));
            options.Tls = new PublicTlsOptions { CertificatePath = pfx, CertificatePasswordEnv = "ATLAS_TEST_PFX" };
        }
        else
        {
            options.AllowInsecureDev = true;
        }

        return options;
    }

    public static async Task<PublicRig> StartAsync(Action<PublicOptions>? edit = null, bool tls = false, PublicState? state = null, FakeWorld? world = null)
    {
        PublicOptions options = Options(tls, out string? pfx);
        edit?.Invoke(options);
        world ??= new FakeWorld();
        var logs = new CapturingLoggers();
        PublicServer server;
        try
        {
            server = await PublicServer.StartAsync(
                new PublicStartup
                {
                    Options = options,
                    Loggers = logs,
                    World = world,
                    Assets = new PublicAssets(),
                    State = state,
                    GetEnvironmentVariable = name => name == "ATLAS_TEST_PFX" ? "pw" : null,
                },
                CancellationToken.None);
        }
        catch
        {
            if (pfx is not null)
            {
                File.Delete(pfx);
            }

            throw;
        }

        int port = server.Port;
        string scheme = tls ? "https" : "http";
        var handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false, ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        var http = new HttpClient(handler) { BaseAddress = new Uri($"{scheme}://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(20) };
        return new PublicRig(server, world, logs, http, scheme, port, tls, pfx);
    }

    public async Task<Reply> SendAsync(HttpMethod method, string path, Visitor? visitor = null, object? body = null, bool csrf = true, Action<HttpRequestMessage>? tweak = null, string? rawBody = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (visitor is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"{CookieName}={visitor.Cookie}");
            if (csrf && method == HttpMethod.Post)
            {
                request.Headers.TryAddWithoutValidation("X-Atlas-CSRF", visitor.Csrf);
            }
        }

        if (rawBody is not null)
        {
            request.Content = new StringContent(rawBody, Encoding.UTF8, "application/json");
        }
        else if (body is not null)
        {
            request.Content = new StringContent(body is JsonNode node ? node.ToJsonString() : System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        tweak?.Invoke(request);
        using HttpResponseMessage response = await Http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        JsonNode? json = null;
        if (response.Content.Headers.ContentType?.MediaType == "application/json" && text.Length > 0)
        {
            json = JsonNode.Parse(text);
        }

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IEnumerable<string>> pair in response.Headers.Concat(response.Content.Headers))
        {
            headers[pair.Key] = [.. pair.Value];
        }

        return new Reply { Status = (int)response.StatusCode, Text = text, Json = json, Headers = headers };
    }

    public Task<Reply> GetAsync(string path, Visitor? visitor = null, Action<HttpRequestMessage>? tweak = null)
        => SendAsync(HttpMethod.Get, path, visitor, tweak: tweak);

    public Task<Reply> PostAsync(string path, Visitor? visitor, object? body, bool csrf = true, Action<HttpRequestMessage>? tweak = null)
        => SendAsync(HttpMethod.Post, path, visitor, body, csrf, tweak);

    /// <summary>A new guest session, the way a browser gets one: a page load, then <c>/api/public/me</c> for the CSRF token.</summary>
    public async Task<Visitor> VisitAsync(string? forwardedFor = null, string? host = null)
    {
        void Tweak(HttpRequestMessage r)
        {
            if (forwardedFor is not null)
            {
                r.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
            }

            if (host is not null)
            {
                r.Headers.Host = host;
            }
        }

        Reply page = await GetAsync("/api/public/status", tweak: Tweak);
        string cookie = CookieOf(page) ?? throw new InvalidOperationException("no session cookie: " + page.Text);
        var visitor = new Visitor(cookie, string.Empty);
        Reply me = await GetAsync("/api/public/me", visitor, Tweak);
        return visitor with { Csrf = me.Json!["csrf"]!.GetValue<string>() };
    }

    public string? CookieOf(Reply reply)
    {
        foreach (string setCookie in reply.SetCookies)
        {
            string pair = setCookie.Split(';')[0];
            if (pair.StartsWith(CookieName + "=", StringComparison.Ordinal))
            {
                return pair[(CookieName.Length + 1)..];
            }
        }

        return null;
    }

    /// <summary>Logs in as an account the fake world knows and returns the new session.</summary>
    public async Task<Visitor> LoginAsync(Visitor guest, string user, string password)
    {
        Reply reply = await PostAsync("/api/public/login", guest, new { user, password });
        if (reply.Status != 200)
        {
            throw new InvalidOperationException("login failed: " + reply.Text);
        }

        return new Visitor(CookieOf(reply)!, reply.Json!["csrf"]!.GetValue<string>());
    }

    public async Task<JsonNode> CommandAsync(Visitor visitor, string name, JsonObject? args = null)
    {
        Reply reply = await PostAsync("/api/public/command", visitor, new JsonObject { ["name"] = name, ["args"] = args ?? [] });
        Assert.Equal(200, reply.Status);
        return reply.Json!;
    }

    public async Task<ClientWebSocket> ConnectAsync(Visitor? visitor, string? origin = "default", CancellationToken cancellationToken = default)
    {
        var socket = new ClientWebSocket();
        if (visitor is not null)
        {
            socket.Options.SetRequestHeader("Cookie", $"{CookieName}={visitor.Cookie}");
        }

        if (origin is not null)
        {
            socket.Options.SetRequestHeader("Origin", origin == "default" ? Origin : origin);
        }

        try
        {
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/api/public/ws"), cancellationToken);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return socket;
    }

    public ValueTask DisposeAsync()
    {
        Http.Dispose();
        return DisposeServerAsync();
    }

    private async ValueTask DisposeServerAsync()
    {
        await Server.DisposeAsync();
        if (_pfx is not null)
        {
            File.Delete(_pfx);
        }
    }
}

/// <summary>Reading a client WebSocket in tests.</summary>
internal static class SocketExtensions
{
    public static async Task SendJsonAsync(this ClientWebSocket socket, JsonNode message)
        => await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>The next text message, or null when the peer closed (the close status is then in <c>CloseStatus</c>).</summary>
    public static async Task<string?> ReceiveTextAsync(this ClientWebSocket socket, int timeoutMs = 10_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[16 * 1024];
        var text = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            text.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(text.ToArray());
            }
        }
    }

    /// <summary>Reads messages until one has this <c>op</c> (and <c>topic</c> when given); null when the socket closed first.</summary>
    public static async Task<JsonNode?> ReceiveUntilAsync(this ClientWebSocket socket, string op, string? topic = null, int timeoutMs = 10_000)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            string? text = await socket.ReceiveTextAsync(Math.Max(100, (int)(end - DateTime.UtcNow).TotalMilliseconds));
            if (text is null)
            {
                return null;
            }

            JsonNode node = JsonNode.Parse(text)!;
            if (node["op"]?.GetValue<string>() == op && (topic is null || node["topic"]?.GetValue<string>() == topic))
            {
                return node;
            }
        }

        throw new TimeoutException($"no {op} {topic} message arrived");
    }
}
