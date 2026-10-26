using System.Globalization;

namespace ArcaneCore.Atlas.Host.Public;

/// <summary>Where the public listener runs relative to the Local one (design 0.10).</summary>
public enum PublicProcessMode
{
    /// <summary><c>arcane-atlas public</c>: its own process with no Local listener and no Local feed connection.</summary>
    Separate,

    /// <summary><c>arcane-atlas serve --public</c>: beside the Local listener, in one process.</summary>
    InProcess,
}

/// <summary>What the check needs to know about its surroundings (so it stays pure and testable).</summary>
/// <param name="Mode">Separate process or in-process.</param>
/// <param name="LoopbackPort">The port the Local listener uses, or null when there is none (separate process, or it has not started).</param>
/// <param name="FileExists">Whether a file exists (the certificate path).</param>
/// <param name="GetEnvironmentVariable">Reads an environment variable (the certificate password's name).</param>
/// <param name="CertificateDnsName">The DNS name of the loaded certificate, when it is known, so the origin can be derived from it.</param>
internal sealed record PublicEnvironmentInfo(
    PublicProcessMode Mode,
    int? LoopbackPort,
    Func<string, bool> FileExists,
    Func<string, string?> GetEnvironmentVariable,
    string? CertificateDnsName = null);

/// <summary>The outcome of <see cref="PublicConfigCheck.Evaluate"/>: refusals (the listener does not start), warnings and the effective settings.</summary>
internal sealed class PublicConfigResult
{
    public List<string> Errors { get; } = [];

    public List<string> Warnings { get; } = [];

    public bool Ok => Errors.Count == 0;

    public PublicTlsMode TlsMode { get; set; }

    public PublicBind Bind { get; set; } = new(null, string.Empty, 0);

    /// <summary>The effective origin (<c>https://host:port</c>), explicit or derived; null when it could not be determined (an error says why).</summary>
    public Uri? Origin { get; set; }

    /// <summary>The <c>Host</c> header values accepted, lower-case, <c>host</c> and <c>host:port</c> forms.</summary>
    public HashSet<string> AllowedHosts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public TrustedProxies Proxies { get; set; } = TrustedProxies.None;

    /// <summary>Whether web login is offered: false in <see cref="PublicTlsMode.InsecureDev"/> unless the bind is loopback.</summary>
    public bool LoginAllowed { get; set; }

    /// <summary>The pooled web connections available to serve maps (design 0.11).</summary>
    public int FeedCapacity { get; set; }

    /// <summary>Whether the origin must be sent by every WebSocket handshake (BehindProxy or a non-literal bind).</summary>
    public bool OriginRequired { get; set; }
}

/// <summary>
/// The start-up validation of the public listener (design 0.2, 0.10, 0.11). Every refusal is an <see cref="PublicConfigResult.Errors"/> entry that names what
/// to change; the listener does not start while there is one. Pure: no file or network access except through <see cref="PublicEnvironmentInfo"/>.
/// </summary>
internal static class PublicConfigCheck
{
    /// <summary>The maps Atlas knows to be continents (not instanced): Eastern Kingdoms and Kalimdor. Anything else could be a dungeon, raid or battleground.</summary>
    public static readonly IReadOnlyList<uint> ContinentMaps = [0u, 1u];

    public static PublicConfigResult Evaluate(PublicOptions options, PublicEnvironmentInfo environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        var result = new PublicConfigResult();

        if (!PublicBind.TryParse(options.Bind, out PublicBind bind, out string bindProblem))
        {
            result.Errors.Add(bindProblem);
        }

        result.Bind = bind;
        if (result.Errors.Count == 0 && bind.Port == 0)
        {
            result.Errors.Add("Atlas:Public:Bind needs a fixed port (not 0): the origin and the Host policy are built from it.");
        }

        if (environment.LoopbackPort is { } loopback && loopback != 0 && bind.Port == loopback)
        {
            result.Errors.Add($"The public listener cannot use port {loopback}: it is the Local listener's port (the two listeners must differ in port, whatever the addresses). Change Atlas:Public:Bind.");
        }

        CheckTransport(options, environment, bind, result);
        CheckOrigin(options, environment, bind, result);
        CheckProxies(options, result);
        CheckGuest(options, environment, result);

        if (options.MaxConnections < 1 || options.MaxConnectionsPerIp < 1)
        {
            result.Errors.Add("Atlas:Public:MaxConnections and MaxConnectionsPerIp must be at least 1.");
        }

        if (options.Session.IdleMinutes < 1 || options.Session.MaxHours < 1)
        {
            result.Errors.Add("Atlas:Public:Session:IdleMinutes and MaxHours must be at least 1.");
        }

        if (options.Login.PerIpPerMinute < 1 || options.Login.PerAccountPer15Min < 1)
        {
            result.Errors.Add("Atlas:Public:Login:PerIpPerMinute and PerAccountPer15Min must be at least 1.");
        }

        if (options.Guest.ShowPlayers)
        {
            result.Warnings.Add($"Atlas public: guests can see real players' live positions (Atlas:Public:Guest:ShowPlayers=true, names {options.NamesWord})");
        }

        return result;
    }

    private static void CheckTransport(PublicOptions options, PublicEnvironmentInfo environment, PublicBind bind, PublicConfigResult result)
    {
        bool certificate = !string.IsNullOrWhiteSpace(options.Tls.CertificatePath) || !string.IsNullOrWhiteSpace(options.Tls.StoreThumbprint);
        if (options.BehindProxy)
        {
            result.TlsMode = PublicTlsMode.Proxy;
            if (bind.Address is not null && !bind.IsLoopback)
            {
                result.Errors.Add($"Atlas:Public:BehindProxy=true needs a loopback bind (127.0.0.1 or [::1]); '{options.Bind}' would let anyone reach the plain-HTTP listener directly.");
            }
            else if (bind.Address is null && bind.Host.Length > 0)
            {
                result.Errors.Add($"Atlas:Public:BehindProxy=true needs a literal loopback bind (127.0.0.1 or [::1]), not the name '{bind.Host}'.");
            }

            if (certificate)
            {
                result.Warnings.Add("Atlas:Public:Tls is set but BehindProxy=true: the listener serves plain HTTP and ignores the certificate.");
            }
        }
        else if (certificate)
        {
            result.TlsMode = PublicTlsMode.Certificate;
            if (!string.IsNullOrWhiteSpace(options.Tls.CertificatePath) && !environment.FileExists(options.Tls.CertificatePath))
            {
                result.Errors.Add($"The TLS certificate file '{options.Tls.CertificatePath}' does not exist.");
            }

            if (!string.IsNullOrWhiteSpace(options.Tls.CertificatePath) && !string.IsNullOrWhiteSpace(options.Tls.CertificatePasswordEnv)
                && string.IsNullOrEmpty(environment.GetEnvironmentVariable(options.Tls.CertificatePasswordEnv)))
            {
                result.Warnings.Add($"The environment variable '{options.Tls.CertificatePasswordEnv}' named by Atlas:Public:Tls:CertificatePasswordEnv is empty; the certificate must have no password.");
            }
        }
        else if (options.AllowInsecureDev)
        {
            result.TlsMode = PublicTlsMode.InsecureDev;
            if (!bind.IsLoopbackOrPrivate)
            {
                result.Errors.Add($"Atlas:Public:AllowInsecureDev=true is for development: it binds only a loopback or private (RFC 1918 / unique-local) address, not '{options.Bind}'.");
            }

            result.Warnings.Add("Atlas public: INSECURE-DEV mode serves plain HTTP; do not expose it to the Internet.");
        }
        else
        {
            result.TlsMode = PublicTlsMode.Certificate;
            result.Errors.Add(
                "The public listener refuses to start without TLS. Fix it one of three ways: set Atlas:Public:Tls:CertificatePath (a PFX file; the password goes in the environment variable named by Tls:CertificatePasswordEnv) or Tls:StoreThumbprint; "
                + "put it behind a TLS-terminating reverse proxy (Atlas:Public:BehindProxy=true with TrustedProxies); or, for development only, Atlas:Public:AllowInsecureDev=true.");
        }

        result.LoginAllowed = result.TlsMode != PublicTlsMode.InsecureDev || bind.IsLoopback;
    }

    private static void CheckOrigin(PublicOptions options, PublicEnvironmentInfo environment, PublicBind bind, PublicConfigResult result)
    {
        result.OriginRequired = options.BehindProxy || !bind.IsSingleLiteral;
        string expectedScheme = result.TlsMode == PublicTlsMode.InsecureDev ? Uri.UriSchemeHttp : Uri.UriSchemeHttps;
        if (!string.IsNullOrWhiteSpace(options.Origin))
        {
            if (!Uri.TryCreate(options.Origin.Trim(), UriKind.Absolute, out Uri? origin)
                || origin.Scheme != expectedScheme
                || origin.PathAndQuery != "/"
                || !string.IsNullOrEmpty(origin.UserInfo)
                || origin.OriginalString.EndsWith("/?", StringComparison.Ordinal))
            {
                result.Errors.Add($"Atlas:Public:Origin '{options.Origin}' must be {expectedScheme}://host[:port] with no path, query or credentials.");
                return;
            }

            result.Origin = origin;
        }
        else if (!result.OriginRequired || (result.TlsMode == PublicTlsMode.Certificate && environment.CertificateDnsName is not null))
        {
            string? host = bind.IsSingleLiteral ? bind.Host : environment.CertificateDnsName;
            if (host is not null && bind.Port > 0)
            {
                string bracketed = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
                // A wildcard certificate name (*.example.org) is not a host a browser visits: refuse it cleanly instead of throwing.
                if (Uri.TryCreate(string.Create(CultureInfo.InvariantCulture, $"{expectedScheme}://{bracketed}:{bind.Port}"), UriKind.Absolute, out Uri? derivedOrigin) && !host.Contains('*', StringComparison.Ordinal))
                {
                    result.Origin = derivedOrigin;
                }
                else
                {
                    result.Errors.Add($"Atlas:Public:Origin cannot be derived from '{host}' (a wildcard or malformed name): set Atlas:Public:Origin to the public address, for example https://atlas.example.org.");
                }
            }
        }
        else
        {
            result.Errors.Add("Atlas:Public:Origin is required when BehindProxy=true or the bind is not a single literal address (for example [::]:8443 or a host name): set it to the public address, for example https://atlas.example.org.");
        }

        var hosts = options.AllowedHosts.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()).ToList();
        if (hosts.Any(h => h.Contains('*', StringComparison.Ordinal)))
        {
            result.Errors.Add("Atlas:Public:AllowedHosts must not contain a wildcard.");
        }

        foreach (string host in hosts)
        {
            result.AllowedHosts.Add(host);
        }

        if (hosts.Count == 0 && result.Origin is { } derived)
        {
            result.AllowedHosts.Add(derived.Authority);
            if (derived.IsDefaultPort)
            {
                result.AllowedHosts.Add(derived.Host.Contains(':', StringComparison.Ordinal) ? $"[{derived.Host.Trim('[', ']')}]" : derived.Host);
            }
        }

        if (result.AllowedHosts.Count == 0 && result.Errors.Count == 0)
        {
            result.Errors.Add("Atlas:Public has no allowed Host: set Atlas:Public:Origin or Atlas:Public:AllowedHosts.");
        }
    }

    private static void CheckProxies(PublicOptions options, PublicConfigResult result)
    {
        if (!TrustedProxies.TryParse(options.TrustedProxies, out TrustedProxies proxies, out string problem))
        {
            result.Errors.Add(problem);
            return;
        }

        result.Proxies = proxies;
        if (options.BehindProxy && proxies.Count == 0)
        {
            result.Errors.Add("Atlas:Public:BehindProxy=true needs Atlas:Public:TrustedProxies (the proxy's address): without it every visitor would share the proxy's address, one login bucket and one connection cap.");
        }
    }

    private static void CheckGuest(PublicOptions options, PublicEnvironmentInfo environment, PublicConfigResult result)
    {
        PublicGuestOptions guest = options.Guest;
        var seen = new HashSet<uint>();
        foreach (PublicMapOption map in guest.Maps)
        {
            if (!seen.Add(map.Id))
            {
                result.Errors.Add($"Atlas:Public:Guest:Maps lists map {map.Id} twice.");
            }

            if (!ContinentMaps.Contains(map.Id))
            {
                result.Errors.Add($"Atlas:Public:Guest:Maps lists map {map.Id}, which is not a continent (0 or 1) and so may be an instance (dungeon, raid, battleground); guests see continents only.");
            }

            if (string.IsNullOrWhiteSpace(map.Name) || map.Name.Length > 64 || map.Name.Any(char.IsControl))
            {
                result.Errors.Add($"Atlas:Public:Guest:Maps map {map.Id} needs a Name of 1 to 64 printable characters.");
            }
        }

        if (guest.Maps.Count == 0)
        {
            result.Warnings.Add("Atlas:Public:Guest:Maps is empty: guests have no map to view.");
        }

        result.FeedCapacity = environment.Mode == PublicProcessMode.Separate
            ? options.Feed.WebMaxConnections
            : options.Feed.WorldMaxConnections - 1;
        if (environment.Mode == PublicProcessMode.Separate && options.Feed.WebMaxConnections is < 1 or > 8)
        {
            result.Errors.Add("Atlas:Public:Feed:WebMaxConnections must match the world's Web:MaxConnections, 1 to 8.");
        }

        if (guest.Maps.Count > result.FeedCapacity)
        {
            result.Errors.Add(
                $"Atlas:Public:Guest:Maps lists {guest.Maps.Count} maps but only {Math.Max(0, result.FeedCapacity)} pooled feed connection(s) can serve them "
                + (environment.Mode == PublicProcessMode.Separate
                    ? "(the world's Web:MaxConnections; raise it, up to 8, and Atlas:Public:Feed:WebMaxConnections to match)."
                    : "(the world's MapFeed:MaxConnections minus one kept for the Local connection)."));
        }

        if (environment.Mode == PublicProcessMode.InProcess)
        {
            if (options.Feed.WorldActionsEnabled)
            {
                result.Errors.Add("The in-process public listener is allowed only when the world's action channel is off (Atlas:Public:Feed:WorldActionsEnabled=false states that). Run 'arcane-atlas public' as a separate process instead (design 0.10).");
            }

            if (options.Feed.LiveServer)
            {
                result.Errors.Add("The in-process public listener is not allowed against the live server (Atlas:Public:Feed:LiveServer=true). Run 'arcane-atlas public' as a separate process.");
            }
        }

        if (guest.PlayerMinCellCount < 3)
        {
            result.Warnings.Add($"Atlas:Public:Guest:PlayerMinCellCount is {guest.PlayerMinCellCount}: a cell with fewer than 3 players can single a player out; 3 or more is recommended.");
        }

        if (guest.PlayerMinCellCount < 1 || guest.PlayerCellYards < 10 || guest.PlayerDelaySeconds < 0)
        {
            result.Errors.Add("Atlas:Public:Guest: PlayerMinCellCount must be at least 1, PlayerCellYards at least 10 and PlayerDelaySeconds 0 or more.");
        }

        if (!(guest.FeedHz is >= 0.2 and <= 10))
        {
            result.Errors.Add("Atlas:Public:Guest:FeedHz must be between 0.2 and 10.");
        }

        if (guest.MaxBytesPerSecond < 4096)
        {
            result.Errors.Add("Atlas:Public:Guest:MaxBytesPerSecond must be at least 4096.");
        }

        if (guest.MaxSessions < 1 || guest.MaxSessionsPerIp < 1 || guest.WorldCallsPerSecond < 1)
        {
            result.Errors.Add("Atlas:Public:Guest: MaxSessions, MaxSessionsPerIp and WorldCallsPerSecond must be at least 1.");
        }
    }
}
