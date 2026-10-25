using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Live;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Host.Public;

/// <summary>The public listener refused to start; <see cref="Errors"/> says why (each entry names what to change).</summary>
internal sealed class PublicStartException(IReadOnlyList<string> errors) : Exception(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>What <see cref="PublicServer.StartAsync"/> needs. Everything but <see cref="Options"/> and <see cref="Loggers"/> has a default.</summary>
internal sealed class PublicStartup
{
    public required PublicOptions Options { get; init; }

    public required ILoggerFactory Loggers { get; init; }

    public PublicProcessMode Mode { get; init; } = PublicProcessMode.Separate;

    /// <summary>The Local listener's port when it runs in this process (the public port must differ), else null.</summary>
    public int? LoopbackPort { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The UI files; default the files embedded in this assembly.</summary>
    public IWebAssets? Assets { get; init; }

    /// <summary>Opens the world feed connections; default the named pipes.</summary>
    public IPublicFeedConnector? Connector { get; init; }

    /// <summary>The live options of the pipe connector (timeouts, discovery); default <see cref="LiveOptions.Default"/>.</summary>
    public LiveOptions? Live { get; init; }

    /// <summary>Replaces the world link entirely (tests); null builds a <see cref="PublicFeedPool"/>.</summary>
    public IPublicWorld? World { get; init; }

    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>Where the commands find the running listener; null for none.</summary>
    public PublicState? State { get; init; }

    public IPublicActionRunner? Actions { get; init; }

    /// <summary>The bookmarks guests may see; default the built-in ones.</summary>
    public IReadOnlyList<Bookmark>? Bookmarks { get; init; }
}

/// <summary>
/// The public web listener (design 0.1 to 0.4, 0.8): its own Kestrel <see cref="WebApplication"/> with its own route table, never a path into
/// <see cref="AtlasServer"/>. A request's tier is a property of this listener (Guest or Account by session); no address, header or proxy can make it Local.
/// <c>X-Forwarded-For</c> is read only to pick the client address for rate limits, and only from a trusted proxy.
/// <para>
/// Routes: <c>GET /</c> and the embedded UI files under <c>/app/</c>, <c>GET /api/public/status|me</c>, <c>POST /api/public/login|logout|command</c> and
/// <c>GET /api/public/ws</c>. Nothing else. Every request passes the Host policy (421), every error is <c>{"ok":false,"error":{"code","ref"}}</c> with no
/// exception text, and every response carries the security headers. Sessions are cookie plus CSRF token (<c>__Host-atlas</c> over TLS, <c>atlas-dev</c> in
/// the insecure development mode).
/// </para>
/// <para>
/// Threading: Kestrel pool threads run the handlers; they use the thread-safe sessions, limits, store and pool. A hub timer and a maintenance timer run
/// beside them. Disposal stops the listener first, then closes sockets and the world link.
/// </para>
/// </summary>
internal sealed class PublicServer : IAsyncDisposable
{
    private const int MaxLoginBodyBytes = 2048;
    private const int MaxCommandBodyBytes = 16 * 1024;
    private const string CsrfHeader = "X-Atlas-CSRF";

    private static readonly string[] HiddenAssets = ["index.html", "js/main.js"];

    private readonly PublicRuntime _rt;
    private readonly PublicStartup _startup;
    private readonly PublicHub _hub;
    private readonly PublicCommandRunner _runner;
    private readonly IWebAssets _assets;
    private readonly WebApplication _app;
    private readonly PublicFeedPool? _pool;
    private readonly X509Certificate2? _certificate;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, int> _failureRuns = new(StringComparer.Ordinal);
    private readonly string _cookieName;
    private Task? _hubLoop;
    private Task? _maintenanceLoop;
    private volatile bool _halted;
    private int _disposed;
    private int _port;

    private PublicServer(PublicStartup startup, PublicRuntime runtime, PublicFeedPool? pool, X509Certificate2? certificate, PublicBind bind, IPAddress address)
    {
        _startup = startup;
        _rt = runtime;
        _pool = pool;
        _certificate = certificate;
        _assets = startup.Assets ?? new EmbeddedWebAssets(typeof(PublicServer).Assembly);
        _cookieName = runtime.Config.TlsMode == PublicTlsMode.InsecureDev ? "atlas-dev" : "__Host-atlas";
        _hub = new PublicHub(runtime);
        _runner = new PublicCommandRunner(runtime);
        _port = bind.Port;

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(startup.Loggers);
        builder.Services.AddSingleton<IHostLifetime, LoopbackLifetime>();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = MaxCommandBodyBytes;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
            kestrel.Limits.MinRequestBodyDataRate = new MinDataRate(240, TimeSpan.FromSeconds(10));
            kestrel.Limits.MaxConcurrentConnections = (startup.Options.MaxConnections * 2) + 100;
            kestrel.Listen(address, bind.Port, listen =>
            {
                if (certificate is not null)
                {
                    // HTTP/2 serves ordinary requests; the socket handler refuses anything but HTTP/1.1 (one code path for the handshake checks).
                    listen.Protocols = HttpProtocols.Http1AndHttp2;
                    listen.UseHttps(new HttpsConnectionAdapterOptions { ServerCertificate = certificate, SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 });
                }
                else
                {
                    listen.Protocols = HttpProtocols.Http1;
                }
            });
        });
        _app = builder.Build();
        _app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        _app.Run(HandleAsync);
    }

    /// <summary>The port the listener is bound to (after start).</summary>
    public int Port => _port;

    /// <summary>The shared state (tests and the commands).</summary>
    internal PublicRuntime Runtime => _rt;

    internal PublicHub Hub => _hub;

    /// <summary>The origin of the listener (<c>https://host:port</c>), as configured or derived.</summary>
    public Uri? Origin => _rt.Config.Origin;

    /// <summary>
    /// Validates the settings, connects the world link, binds the listener and logs the listener lines. Throws <see cref="PublicStartException"/> when the
    /// listener must not start: no TLS, a bad bind, a missing certificate, a wrong feed. Nothing is left running after a failure.
    /// </summary>
    public static async Task<PublicServer> StartAsync(PublicStartup startup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startup);
        PublicOptions options = startup.Options;
        ILogger log = startup.Loggers.CreateLogger("ArcaneCore.Atlas.Public");

        X509Certificate2? certificate = null;
        var certProblems = new List<string>();
        DateTimeOffset? notAfter = null;
        try
        {
            if (!options.BehindProxy)
            {
                certificate = PublicCertificate.TryLoad(options.Tls, startup.GetEnvironmentVariable, certProblems);
            }

            if (certificate is not null)
            {
                notAfter = certificate.NotAfter.ToUniversalTime();
                DateTimeOffset now = startup.Time.GetUtcNow();
                if (notAfter <= now)
                {
                    certProblems.Add($"The TLS certificate expired on {notAfter:yyyy-MM-dd}; renew it.");
                }
                else if (notAfter - now < TimeSpan.FromDays(14))
                {
                    log.LogWarning("The TLS certificate expires on {NotAfter:yyyy-MM-dd}; renew it soon", notAfter);
                }
            }

            var environment = new PublicEnvironmentInfo(startup.Mode, startup.LoopbackPort, File.Exists, startup.GetEnvironmentVariable, certificate is null ? null : PublicCertificate.DnsName(certificate));
            PublicConfigResult config = PublicConfigCheck.Evaluate(options, environment);
            config.Errors.AddRange(certProblems);
            foreach (string warning in config.Warnings)
            {
                log.LogWarning("{Warning}", warning);
            }

            if (!config.Ok)
            {
                foreach (string error in config.Errors)
                {
                    log.LogError("Atlas public listener not started: {Error}", error);
                }

                throw new PublicStartException(config.Errors);
            }

            IPAddress address = await ResolveAsync(config.Bind, cancellationToken).ConfigureAwait(false);
            var time = startup.Time;
            var store = new PublicFrameStore(options, time);
            PublicFeedPool? pool = null;
            IPublicWorld world;
            if (startup.World is { } supplied)
            {
                world = supplied;
            }
            else
            {
                IPublicFeedConnector connector = startup.Connector ?? new PipeFeedConnector(startup.Mode, options.Feed.Pid, startup.Live ?? LiveOptions.Default, startup.Loggers);
                pool = new PublicFeedPool(connector, options, config.FeedCapacity, store, time, startup.Loggers.CreateLogger("ArcaneCore.Atlas.Public.Feed"));
                world = pool;
                try
                {
                    await pool.StartAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (PublicFeedException e)
                {
                    log.LogError("Atlas public listener not started: {Error}", e.Message);
                    await pool.DisposeAsync().ConfigureAwait(false);
                    throw new PublicStartException([e.Message]);
                }
            }

            var runtime = new PublicRuntime
            {
                Options = options,
                Config = config,
                Mode = startup.Mode,
                Sessions = new PublicSessions(options, time),
                Limits = new PublicRateLimits(options, time),
                Frames = store,
                World = world,
                Stats = new PublicStats(time),
                Log = log,
                Time = time,
                Started = time.GetUtcNow(),
                Bookmarks = startup.Bookmarks ?? BuiltInBookmarks.All,
                Actions = startup.Actions,
                CertificateNotAfter = notAfter,
            };
            var server = new PublicServer(startup, runtime, pool, certificate, config.Bind, address);
            try
            {
                await server.BeginAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await server.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            certificate = null; // owned by the server now
            return server;
        }
        finally
        {
            certificate?.Dispose();
        }
    }

    private static async Task<IPAddress> ResolveAsync(PublicBind bind, CancellationToken cancellationToken)
    {
        if (bind.Address is { } literal)
        {
            return literal;
        }

        IPAddress[] found = await Dns.GetHostAddressesAsync(bind.Host, cancellationToken).ConfigureAwait(false);
        return found.FirstOrDefault() ?? throw new PublicStartException([$"The host name '{bind.Host}' in Atlas:Public:Bind resolves to no address."]);
    }

    private async Task BeginAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _app.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            throw new PublicStartException([$"The public listener could not bind {_rt.Options.Bind}: {e.Message}"]);
        }

        IServerAddressesFeature addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("The web server reports no listening address.");
        _port = new Uri(addresses.Addresses.First()).Port;
        if (_pool is not null)
        {
            _pool.Fatal += reason => _ = HaltAsync(reason);
        }

        _startup.State?.Attach(_rt);
        _hubLoop = Task.Run(() => _hub.RunAsync(_stop.Token));
        _maintenanceLoop = Task.Run(() => MaintainAsync(_stop.Token));
        LogListenerLines();
    }

    private void LogListenerLines()
    {
        PublicOptions o = _rt.Options;
        string origin = _rt.Config.Origin?.GetLeftPart(UriPartial.Authority) ?? "(none)";
        string bind = _rt.Options.Bind;
        _rt.Log.LogInformation(
            "Atlas public listener: {Scheme}://{Bind} origin={Origin} (tiers Guest+Account, process={Process}, tls={Tls}, guest players={Players}, names={Names}, account actions={Actions}, login min={Login})",
            _rt.Scheme,
            bind,
            origin,
            _rt.Mode == PublicProcessMode.Separate ? "separate" : "in-process",
            PublicOpsCommands.TlsWord(_rt.Config.TlsMode),
            o.PlayersWord,
            o.NamesWord,
            o.AccountActions ? "on" : "off",
            _rt.Config.LoginAllowed ? PublicRuntime.LevelName(_rt.LoginFloor) : "off");
        if (o.Guest.ShowPlayers)
        {
            _rt.Log.LogWarning("Atlas public: guests can see real players' live positions (Atlas:Public:Guest:ShowPlayers=true, names {Names})", o.NamesWord);
        }
    }

    private async Task HaltAsync(string reason)
    {
        if (_halted)
        {
            return;
        }

        _halted = true;
        _rt.Log.LogError("Atlas public listener stopped serving: {Reason}", reason);
        await _hub.CloseAllAsync("halted").ConfigureAwait(false);
        foreach (EndedSession ended in _rt.Sessions.End(_ => true, "halted"))
        {
            _ = ended;
        }
    }

    private async Task MaintainAsync(CancellationToken cancellationToken)
    {
        long lastInsecureWarning = _rt.Time.GetTimestamp();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await PublicOpsCommands.EndTicketsAsync(_rt, _rt.Sessions.Sweep(), kick: false, cancellationToken).ConfigureAwait(false);
                    if (_pool is not null && !_halted)
                    {
                        await _pool.MaintainAsync(cancellationToken).ConfigureAwait(false);
                    }

                    if (_rt.Config.TlsMode == PublicTlsMode.InsecureDev && _rt.Time.GetTimestamp() - lastInsecureWarning >= 600 * _rt.Time.TimestampFrequency)
                    {
                        lastInsecureWarning = _rt.Time.GetTimestamp();
                        _rt.Log.LogWarning("Atlas public: INSECURE-DEV mode is serving plain HTTP on {Bind}; do not expose it to the Internet", _rt.Options.Bind);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _rt.Log.LogWarning(e, "Atlas public maintenance pass failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _startup.State?.Detach(_rt);
        await _stop.CancelAsync().ConfigureAwait(false);
        using (var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            try
            {
                await _hub.CloseAllAsync("stopping").ConfigureAwait(false);
                await _app.StopAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _rt.Log.LogWarning("The public web server did not stop within 5 s; closing it anyway.");
            }
        }

        foreach (Task? loop in new[] { _hubLoop, _maintenanceLoop })
        {
            if (loop is not null)
            {
                await loop.ConfigureAwait(false);
            }
        }

        await _app.DisposeAsync().ConfigureAwait(false);
        if (_pool is not null)
        {
            await _pool.DisposeAsync().ConfigureAwait(false);
        }

        _certificate?.Dispose();
        _stop.Dispose();
    }

    // ---- requests ----------------------------------------------------------------------------------------------------------------

    private sealed record RequestContext(IPAddress Client, string IpKey);

    private async Task HandleAsync(HttpContext http)
    {
        SecurityHeaders(http.Response);
        try
        {
            if (_halted)
            {
                await ErrorAsync(http, StatusCodes.Status503ServiceUnavailable, "halted", null).ConfigureAwait(false);
                return;
            }

            if (!http.Request.Host.HasValue || !_rt.Config.AllowedHosts.Contains(http.Request.Host.Value))
            {
                await ErrorAsync(http, StatusCodes.Status421MisdirectedRequest, "bad-host", null).ConfigureAwait(false);
                return;
            }

            IPAddress peer = http.Connection.RemoteIpAddress ?? IPAddress.Loopback;
            IPAddress client = PublicAddresses.ClientAddress(peer, http.Request.Headers["X-Forwarded-For"].ToArray()!, _rt.Config.Proxies);
            var context = new RequestContext(client, PublicAddresses.Key(client));
            if (!_rt.Limits.AllowHttp(context.IpKey))
            {
                http.Response.Headers.RetryAfter = "1";
                await ErrorAsync(http, StatusCodes.Status429TooManyRequests, "rate-limited", context).ConfigureAwait(false);
                return;
            }

            string path = http.Request.Path.Value ?? "/";
            if (path.StartsWith("/api/public/", StringComparison.Ordinal))
            {
                http.Response.Headers.CacheControl = "no-store";
                await ApiAsync(http, path["/api/public/".Length..], context).ConfigureAwait(false);
            }
            else
            {
                await StaticAsync(http, path, context).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested || _stop.IsCancellationRequested)
        {
            // The client went away or the listener is stopping.
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException e)
        {
            if (!http.Response.HasStarted)
            {
                await ErrorAsync(http, e.StatusCode, "bad-request", null).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            string reference = NewRef();
            _rt.Log.LogWarning(e, "public request {Method} {Path} failed (ref {Ref})", http.Request.Method, http.Request.Path, reference);
            if (!http.Response.HasStarted)
            {
                await ErrorAsync(http, StatusCodes.Status500InternalServerError, "failed", null, reference).ConfigureAwait(false);
            }
        }
    }

    private void SecurityHeaders(HttpResponse response)
    {
        IHeaderDictionary h = response.Headers;
        h.XContentTypeOptions = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        h.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; connect-src 'self'; base-uri 'none'; form-action 'none'; worker-src 'self'; frame-ancestors 'none'";
        if (_rt.Config.TlsMode != PublicTlsMode.InsecureDev)
        {
            h.StrictTransportSecurity = "max-age=31536000";
        }
    }

    private async Task StaticAsync(HttpContext http, string path, RequestContext context)
    {
        bool head = HttpMethods.IsHead(http.Request.Method);
        if (!head && !HttpMethods.IsGet(http.Request.Method))
        {
            http.Response.Headers.Allow = "GET, HEAD";
            await ErrorAsync(http, StatusCodes.Status405MethodNotAllowed, "method-not-allowed", context).ConfigureAwait(false);
            return;
        }

        string? relative = path switch
        {
            "/" => "public.html",
            _ when path.StartsWith("/app/", StringComparison.Ordinal) => path["/app/".Length..],
            _ => null,
        };
        if (relative is null || !IsSafeRelativePath(relative) || HiddenAssets.Contains(relative, StringComparer.Ordinal) || !_assets.TryGet(relative, out WebAsset asset))
        {
            await ErrorAsync(http, StatusCodes.Status404NotFound, "not-found", context).ConfigureAwait(false);
            return;
        }

        if (relative == "public.html" && EnsureSession(http, context, create: true) is null)
        {
            await ErrorAsync(http, StatusCodes.Status503ServiceUnavailable, "busy", context).ConfigureAwait(false);
            return;
        }

        http.Response.Headers.CacheControl = "no-cache";
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = asset.ContentType;
        http.Response.ContentLength = asset.Content.Length;
        if (!head)
        {
            await http.Response.Body.WriteAsync(asset.Content, http.RequestAborted).ConfigureAwait(false);
        }
    }

    private static bool IsSafeRelativePath(string relative)
    {
        if (relative.Length == 0)
        {
            return false;
        }

        foreach (string segment in relative.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }
        }

        return !relative.Any(c => c is '\\' or ':' || char.IsControl(c));
    }

    private async Task ApiAsync(HttpContext http, string route, RequestContext context)
    {
        switch (route)
        {
            case "status":
                if (await RequireMethodAsync(http, HttpMethods.Get, context).ConfigureAwait(false))
                {
                    await StatusAsync(http, context).ConfigureAwait(false);
                }

                break;
            case "me":
                if (await RequireMethodAsync(http, HttpMethods.Get, context).ConfigureAwait(false))
                {
                    await MeAsync(http, context).ConfigureAwait(false);
                }

                break;
            case "login":
                if (await RequireMethodAsync(http, HttpMethods.Post, context).ConfigureAwait(false))
                {
                    await LoginAsync(http, context).ConfigureAwait(false);
                }

                break;
            case "logout":
                if (await RequireMethodAsync(http, HttpMethods.Post, context).ConfigureAwait(false))
                {
                    await LogoutAsync(http, context).ConfigureAwait(false);
                }

                break;
            case "command":
                if (await RequireMethodAsync(http, HttpMethods.Post, context).ConfigureAwait(false))
                {
                    await CommandAsync(http, context).ConfigureAwait(false);
                }

                break;
            case "ws":
                await WebSocketAsync(http, context).ConfigureAwait(false);
                break;
            default:
                await ErrorAsync(http, StatusCodes.Status404NotFound, "not-found", context).ConfigureAwait(false);
                break;
        }
    }

    private async Task<bool> RequireMethodAsync(HttpContext http, string method, RequestContext context)
    {
        if (string.Equals(http.Request.Method, method, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        http.Response.Headers.Allow = method;
        await ErrorAsync(http, StatusCodes.Status405MethodNotAllowed, "method-not-allowed", context).ConfigureAwait(false);
        return false;
    }

    private async Task StatusAsync(HttpContext http, RequestContext context)
    {
        if (EnsureSession(http, context, create: true) is null)
        {
            await ErrorAsync(http, StatusCodes.Status503ServiceUnavailable, "busy", context).ConfigureAwait(false);
            return;
        }

        JsonObject status = GuestCommands.Status(_rt);
        status["ok"] = true;
        await WriteJsonAsync(http, StatusCodes.Status200OK, status).ConfigureAwait(false);
    }

    private async Task MeAsync(HttpContext http, RequestContext context)
    {
        PublicSession? session = EnsureSession(http, context, create: false);
        if (session is null)
        {
            await ErrorAsync(http, StatusCodes.Status401Unauthorized, "no-session", context).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(http, StatusCodes.Status200OK, SessionJson(session)).ConfigureAwait(false);
    }

    private JsonObject SessionJson(PublicSession session)
        => new()
        {
            ["ok"] = true,
            ["tier"] = session.Tier == PublicTier.Account ? "account" : "guest",
            ["account"] = session.Account,
            ["security"] = session.Tier == PublicTier.Account ? session.Security : null,
            ["csrf"] = session.Csrf,
            ["idleMinutes"] = _rt.Options.Session.IdleMinutes,
            ["maxHours"] = _rt.Options.Session.MaxHours,
        };

    /// <summary>The session of the request's cookie; when there is none and <paramref name="create"/> is set, a new guest session (cookie set), or null when the caps refuse.</summary>
    private PublicSession? EnsureSession(HttpContext http, RequestContext context, bool create)
    {
        PublicSession? session = _rt.Sessions.Find(http.Request.Cookies[_cookieName]);
        if (session is not null || !create)
        {
            return session;
        }

        IssuedSession? issued = _rt.Sessions.CreateGuest(context.IpKey, context.Client.ToString());
        if (issued is null)
        {
            return null;
        }

        SetCookie(http, issued.Cookie);
        return issued.Session;
    }

    private void SetCookie(HttpContext http, string value)
        => http.Response.Cookies.Append(_cookieName, value, new CookieOptions
        {
            HttpOnly = true,
            Secure = _rt.Config.TlsMode != PublicTlsMode.InsecureDev,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
        });

    /// <summary>A POST needs a session, its CSRF token, and (when present, or required by the mode) the right <c>Origin</c>. Writes the refusal and returns null when it fails.</summary>
    private async Task<PublicSession?> RequirePostSessionAsync(HttpContext http, RequestContext context)
    {
        PublicSession? session = EnsureSession(http, context, create: false);
        if (session is null)
        {
            await ErrorAsync(http, StatusCodes.Status401Unauthorized, "no-session", context).ConfigureAwait(false);
            return null;
        }

        if (!OriginAllowed(http.Request))
        {
            await ErrorAsync(http, StatusCodes.Status403Forbidden, "bad-origin", context).ConfigureAwait(false);
            return null;
        }

        if (!session.CsrfMatches(http.Request.Headers[CsrfHeader].ToString()))
        {
            await ErrorAsync(http, StatusCodes.Status403Forbidden, "bad-csrf", context).ConfigureAwait(false);
            return null;
        }

        return session;
    }

    private bool OriginAllowed(HttpRequest request)
    {
        string origin = request.Headers.Origin.ToString();
        if (origin.Length == 0)
        {
            return !_rt.Config.OriginRequired;
        }

        return _rt.Config.Origin is { } expected
            && string.Equals(origin.TrimEnd('/'), expected.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    }

    private async Task LoginAsync(HttpContext http, RequestContext context)
    {
        PublicSession? session = await RequirePostSessionAsync(http, context).ConfigureAwait(false);
        if (session is null)
        {
            return;
        }

        byte[]? body = await ReadBodyAsync(http, MaxLoginBodyBytes, context).ConfigureAwait(false);
        if (body is null)
        {
            return;
        }

        string? user = null;
        string? password = null;
        string? code = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                user = Text(document.RootElement, "user");
                password = Text(document.RootElement, "password");
                code = Text(document.RootElement, "code");
            }
        }
        catch (JsonException)
        {
            // Treated as a malformed body below.
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }

        if (string.IsNullOrWhiteSpace(user) || user.Length > 32 || string.IsNullOrEmpty(password) || password.Length > 128 || (code is not null && code.Length > 16))
        {
            await ErrorAsync(http, StatusCodes.Status400BadRequest, "bad-request", context).ConfigureAwait(false);
            return;
        }

        if (!_rt.Config.LoginAllowed)
        {
            await ErrorAsync(http, StatusCodes.Status403Forbidden, "login-disabled", context).ConfigureAwait(false);
            return;
        }

        if (!_rt.Limits.AllowLogin(context.IpKey, user, out TimeSpan wait))
        {
            http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            await FailedLoginAsync(http, context, user, StatusCodes.Status429TooManyRequests).ConfigureAwait(false);
            return;
        }

        WorldLoginResult result = await _rt.World.LoginAsync(user, password, string.IsNullOrEmpty(code) ? null : code, context.Client.ToString(), _rt.LoginFloor, http.RequestAborted).ConfigureAwait(false);
        switch (result.Kind)
        {
            case WorldLoginKind.Ok:
                _failureRuns.TryRemove(context.IpKey, out _);
                (IssuedSession issued, EndedSession? ended) = _rt.Sessions.Login(session, context.IpKey, context.Client.ToString(), result.Account ?? user.ToUpperInvariant(), result.Security, result.Ticket!);
                if (ended?.Ticket is { } oldTicket)
                {
                    _ = _rt.World.EndTicketAsync(oldTicket, kick: false, CancellationToken.None);
                }

                SetCookie(http, issued.Cookie);
                _rt.Log.LogInformation("login ok account={Account} ip={Ip} ref={Ref}", issued.Session.Account, context.Client, issued.Session.Ref);
                await WriteJsonAsync(http, StatusCodes.Status200OK, SessionJson(issued.Session)).ConfigureAwait(false);
                break;
            case WorldLoginKind.Off:
                await ErrorAsync(http, StatusCodes.Status403Forbidden, "login-disabled", context).ConfigureAwait(false);
                break;
            case WorldLoginKind.Unavailable:
                await ErrorAsync(http, StatusCodes.Status503ServiceUnavailable, "feed-unavailable", context).ConfigureAwait(false);
                break;
            default:
                await FailedLoginAsync(http, context, user, StatusCodes.Status401Unauthorized).ConfigureAwait(false);
                break;
        }
    }

    private async Task FailedLoginAsync(HttpContext http, RequestContext context, string user, int status)
    {
        _rt.Stats.LoginFailed();
        int run = _failureRuns.AddOrUpdate(context.IpKey, 1, (_, n) => n + 1);
        string reference = NewRef();
        if (run >= 3)
        {
            _rt.Log.LogWarning("login failed account={Account} ip={Ip} ref={Ref} ({Run} in a row)", user, context.Client, reference, run);
        }
        else
        {
            _rt.Log.LogInformation("login failed account={Account} ip={Ip} ref={Ref}", user, context.Client, reference);
        }

        await ErrorAsync(http, status, "login-failed", context, reference).ConfigureAwait(false);
    }

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private async Task LogoutAsync(HttpContext http, RequestContext context)
    {
        PublicSession? session = await RequirePostSessionAsync(http, context).ConfigureAwait(false);
        if (session is null)
        {
            return;
        }

        (IssuedSession Issued, EndedSession Ended)? outcome = _rt.Sessions.Logout(session);
        if (outcome is not { } result)
        {
            await ErrorAsync(http, StatusCodes.Status401Unauthorized, "no-session", context).ConfigureAwait(false);
            return;
        }

        if (result.Ended.Ticket is { } ticket)
        {
            _rt.Log.LogInformation("logout account={Account} ip={Ip} ref={Ref}", result.Ended.Session.Account, context.Client, result.Ended.Session.Ref);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await _rt.World.EndTicketAsync(ticket, kick: false, timeout.Token).ConfigureAwait(false);
        }

        SetCookie(http, result.Issued.Cookie);
        await WriteJsonAsync(http, StatusCodes.Status200OK, SessionJson(result.Issued.Session)).ConfigureAwait(false);
    }

    private async Task CommandAsync(HttpContext http, RequestContext context)
    {
        PublicSession? session = await RequirePostSessionAsync(http, context).ConfigureAwait(false);
        if (session is null)
        {
            return;
        }

        byte[]? body = await ReadBodyAsync(http, MaxCommandBodyBytes, context).ConfigureAwait(false);
        if (body is null)
        {
            return;
        }

        JsonObject? request;
        try
        {
            request = JsonNode.Parse(body, nodeOptions: null, new JsonDocumentOptions { MaxDepth = PublicHub.MaxJsonDepth }) as JsonObject;
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request?["name"] is not JsonValue nameValue || !nameValue.TryGetValue(out string? name) || string.IsNullOrWhiteSpace(name) || name.Length > 64
            || (request["args"] is not null && request["args"] is not JsonObject))
        {
            await ErrorAsync(http, StatusCodes.Status400BadRequest, "bad-request", context).ConfigureAwait(false);
            return;
        }

        JsonObject result = await _runner.RunAsync(session, name, request["args"] as JsonObject ?? [], http.RequestAborted).ConfigureAwait(false);
        await WriteJsonAsync(http, StatusCodes.Status200OK, result).ConfigureAwait(false);
    }

    private async Task WebSocketAsync(HttpContext http, RequestContext context)
    {
        if (!http.WebSockets.IsWebSocketRequest || !HttpMethods.IsGet(http.Request.Method))
        {
            await ErrorAsync(http, StatusCodes.Status400BadRequest, "bad-request", context).ConfigureAwait(false);
            return;
        }

        // WebSockets are served over HTTP/1.1 only (RFC 8441 is not offered), so the handshake checks have one code path.
        if (!HttpProtocol.IsHttp11(http.Request.Protocol))
        {
            await ErrorAsync(http, StatusCodes.Status400BadRequest, "http11-only", context).ConfigureAwait(false);
            return;
        }

        PublicSession? session = EnsureSession(http, context, create: false);
        if (session is null)
        {
            await ErrorAsync(http, StatusCodes.Status401Unauthorized, "no-session", context).ConfigureAwait(false);
            return;
        }

        // A browser always sends Origin on a WebSocket handshake; a page from another origin must not open this socket.
        string origin = http.Request.Headers.Origin.ToString();
        if (origin.Length == 0 || !OriginAllowed(http.Request))
        {
            await ErrorAsync(http, StatusCodes.Status403Forbidden, "bad-origin", context).ConfigureAwait(false);
            return;
        }

        if (!_hub.TryReserve(session.IpKey))
        {
            await ErrorAsync(http, StatusCodes.Status503ServiceUnavailable, "busy", context).ConfigureAwait(false);
            return;
        }

        WebSocket socket;
        try
        {
            socket = await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        }
        catch
        {
            _hub.ReleaseReservation(session.IpKey);
            throw;
        }

        using (socket)
        {
            await _hub.RunSocketAsync(session, socket, _stop.Token).ConfigureAwait(false);
        }
    }

    private async Task<byte[]?> ReadBodyAsync(HttpContext http, int limit, RequestContext context)
    {
        if (http.Request.ContentType is null || !http.Request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await ErrorAsync(http, StatusCodes.Status415UnsupportedMediaType, "bad-request", context).ConfigureAwait(false);
            return null;
        }

        if (http.Request.ContentLength is { } declared && declared > limit)
        {
            http.Response.Headers.Connection = "close";
            await ErrorAsync(http, StatusCodes.Status413PayloadTooLarge, "too-large", context).ConfigureAwait(false);
            return null;
        }

        var buffer = new byte[limit + 1];
        int length = 0;
        int read;
        while ((read = await http.Request.Body.ReadAsync(buffer.AsMemory(length, buffer.Length - length), http.RequestAborted).ConfigureAwait(false)) > 0)
        {
            length += read;
            if (length > limit)
            {
                CryptographicOperations.ZeroMemory(buffer);
                await ErrorAsync(http, StatusCodes.Status413PayloadTooLarge, "too-large", context).ConfigureAwait(false);
                return null;
            }
        }

        byte[] body = buffer.AsSpan(0, length).ToArray();
        CryptographicOperations.ZeroMemory(buffer);
        return body;
    }

    private static Task WriteJsonAsync(HttpContext http, int status, JsonNode body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json; charset=utf-8";
        http.Response.ContentLength = bytes.Length;
        return http.Response.Body.WriteAsync(bytes, http.RequestAborted).AsTask();
    }

    private static string NewRef() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    /// <summary>The public error body: <c>{"ok":false,"error":{"code","ref"}}</c> and nothing else (no message, no exception type). Details are in the log under the reference.</summary>
    private async Task ErrorAsync(HttpContext http, int status, string code, RequestContext? context, string? reference = null)
    {
        reference ??= NewRef();
        if (status >= 500)
        {
            _rt.Log.LogWarning("public {Status} {Code} {Method} {Path} ref={Ref}", status, code, http.Request.Method, http.Request.Path, reference);
        }
        else
        {
            _rt.Log.LogDebug("public {Status} {Code} {Method} {Path} ip={Ip} ref={Ref}", status, code, http.Request.Method, http.Request.Path, context?.Client, reference);
        }

        if (http.Response.HasStarted)
        {
            return;
        }

        var body = new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["ref"] = reference } };
        await WriteJsonAsync(http, status, body).ConfigureAwait(false);
    }
}

/// <summary>Loads the listener's certificate (design 0.2): a PFX file whose password comes from a named environment variable, or a store thumbprint.</summary>
internal static class PublicCertificate
{
    /// <summary>The certificate, or null with a problem added to <paramref name="problems"/> (or nothing configured).</summary>
    public static X509Certificate2? TryLoad(PublicTlsOptions tls, Func<string, string?> getEnvironmentVariable, List<string> problems)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(tls.CertificatePath))
            {
                if (!File.Exists(tls.CertificatePath))
                {
                    return null; // PublicConfigCheck reports the missing file
                }

                string? password = string.IsNullOrWhiteSpace(tls.CertificatePasswordEnv) ? null : getEnvironmentVariable(tls.CertificatePasswordEnv);
                return X509CertificateLoader.LoadPkcs12FromFile(tls.CertificatePath, password);
            }

            if (!string.IsNullOrWhiteSpace(tls.StoreThumbprint))
            {
                using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadOnly);
                X509Certificate2Collection found = store.Certificates.Find(X509FindType.FindByThumbprint, tls.StoreThumbprint.Trim(), validOnly: false);
                if (found.Count == 0)
                {
                    problems.Add("No certificate with the thumbprint in Atlas:Public:Tls:StoreThumbprint was found in CurrentUser\\My.");
                    return null;
                }

                return new X509Certificate2(found[0]);
            }
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            problems.Add($"The TLS certificate could not be loaded: {e.Message}");
        }

        return null;
    }

    /// <summary>The certificate's DNS name (subject alternative name, else the common name), or null.</summary>
    public static string? DnsName(X509Certificate2 certificate)
    {
        string name = certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}

/// <summary>Protocol checks of a request.</summary>
internal static class HttpProtocol
{
    /// <summary>Whether <paramref name="protocol"/> (<c>Request.Protocol</c>) is HTTP/1.1.</summary>
    public static bool IsHttp11(string protocol) => string.Equals(protocol, "HTTP/1.1", StringComparison.OrdinalIgnoreCase);
}
