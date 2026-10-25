using System.Collections;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.Atlas.Host.Public;

/// <summary>An account security level, the same four values as the world's <c>AccountSecurity</c> (Atlas does not reference the world assembly).</summary>
public enum PublicSecurity
{
    Player = 0,
    Moderator = 1,
    GameMaster = 2,
    Administrator = 3,
}

/// <summary>How the public listener gets TLS (docs/areas/atlas-public.md).</summary>
public enum PublicTlsMode
{
    /// <summary>Kestrel terminates TLS with a certificate from a PFX file or the user's certificate store.</summary>
    Certificate,

    /// <summary>A reverse proxy terminates TLS; the listener is plain HTTP on a loopback address.</summary>
    Proxy,

    /// <summary>Plain HTTP without a proxy (development only).</summary>
    InsecureDev,
}

/// <summary>A map guests may view: the id and the name the operator wrote (never a client-derived string).</summary>
public sealed class PublicMapOption
{
    public uint Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>Certificate settings (<c>Atlas:Public:Tls</c>). The password is never in the file: <see cref="CertificatePasswordEnv"/> names the environment variable that holds it.</summary>
public sealed class PublicTlsOptions
{
    public string? CertificatePath { get; set; }

    public string? CertificatePasswordEnv { get; set; }

    public string? StoreThumbprint { get; set; }
}

/// <summary>Guest policy (<c>Atlas:Public:Guest</c>, design 0.2 and 0.6).</summary>
public sealed class PublicGuestOptions
{
    public int MaxSessions { get; set; } = 5000;

    public int MaxSessionsPerIp { get; set; } = 20;

    public List<PublicMapOption> Maps { get; set; } =
    [
        new() { Id = 0, Name = "Eastern Kingdoms" },
        new() { Id = 1, Name = "Kalimdor" },
    ];

    public double FeedHz { get; set; } = 2;

    public int MaxBytesPerSecond { get; set; } = 64 * 1024;

    public bool ShowPlayers { get; set; }

    public bool ShowPlayerNames { get; set; }

    public int PlayerCellYards { get; set; } = 250;

    public int PlayerDelaySeconds { get; set; } = 120;

    public int PlayerMinCellCount { get; set; } = 3;

    public int WorldCallsPerSecond { get; set; } = 50;
}

/// <summary>Account session lifetimes (<c>Atlas:Public:Session</c>).</summary>
public sealed class PublicSessionOptions
{
    public int IdleMinutes { get; set; } = 30;

    public int MaxHours { get; set; } = 12;
}

/// <summary>Web login settings (<c>Atlas:Public:Login</c>, design 0.4).</summary>
public sealed class PublicLoginOptions
{
    public bool AllowBelowMinActionSecurity { get; set; }

    public int PerIpPerMinute { get; set; } = 5;

    public int PerAccountPer15Min { get; set; } = 5;
}

/// <summary>Account-tier extras (<c>Atlas:Public:Account</c>).</summary>
public sealed class PublicAccountOptions
{
    public bool AllowInstances { get; set; }
}

/// <summary>How the public host reaches the world's map feed (<c>Atlas:Public:Feed</c>).</summary>
public sealed class PublicFeedOptions
{
    /// <summary>The world daemon's process id; 0 finds the one feed on this machine.</summary>
    public int Pid { get; set; }

    /// <summary>The world's <c>Web:MaxConnections</c> (separate process mode); default 2.</summary>
    public int WebMaxConnections { get; set; } = 2;

    /// <summary>The world's <c>MapFeed:MaxConnections</c> (in-process mode, one is kept for the Local connection); default 2.</summary>
    public int WorldMaxConnections { get; set; } = 2;

    /// <summary>
    /// What the operator states about the world: whether its action channel is enabled. In-process mode (design 0.10) is allowed only when the
    /// operator sets it to <c>false</c> (default <c>true</c>, which refuses in-process mode). This is a first check only: after the role ack the
    /// pool asks the world for <c>caps</c> and refuses the connection unless the world itself reports <c>actions:false</c> and
    /// <c>liveServer:false</c>, at start and on every reconnect.
    /// </summary>
    public bool WorldActionsEnabled { get; set; } = true;

    /// <summary>Whether the operator states the world is the live server (any in-process mode is refused then; the world's own <c>caps</c> answer is checked as well).</summary>
    public bool LiveServer { get; set; }
}

/// <summary>
/// The settings of the public listener, bound from <c>atlas.json</c> (section <c>Atlas:Public</c>) with <c>ARCANECORE_ATLAS_PUBLIC__*</c>
/// environment overrides (double underscore as the section separator) and the serve flags. Plain mutable data; the first thing the listener does is
/// validate it with <see cref="PublicConfigCheck"/>.
/// </summary>
public sealed class PublicOptions
{
    /// <summary>The configuration section the settings live in.</summary>
    public const string Section = "Atlas:Public";

    /// <summary>The environment prefix of the overrides.</summary>
    public const string EnvironmentPrefix = "ARCANECORE_ATLAS_PUBLIC__";

    public bool Enabled { get; set; }

    public string Bind { get; set; } = "[::]:8443";

    public string? Origin { get; set; }

    public List<string> AllowedHosts { get; set; } = [];

    public PublicSecurity MinActionSecurity { get; set; } = PublicSecurity.Moderator;

    public PublicLoginOptions Login { get; set; } = new();

    public PublicTlsOptions Tls { get; set; } = new();

    public bool BehindProxy { get; set; }

    public List<string> TrustedProxies { get; set; } = [];

    public bool AllowInsecureDev { get; set; }

    public int MaxConnections { get; set; } = 200;

    public int MaxConnectionsPerIp { get; set; } = 4;

    public PublicGuestOptions Guest { get; set; } = new();

    public PublicSessionOptions Session { get; set; } = new();

    public bool AccountActions { get; set; } = true;

    public PublicAccountOptions Account { get; set; } = new();

    public PublicFeedOptions Feed { get; set; } = new();

    /// <summary>
    /// Reads the settings: <paramref name="settingsFile"/> (optional, <c>Atlas:Public</c> section of a JSON file), then the environment variables
    /// <c>ARCANECORE_ATLAS_PUBLIC__&lt;Key&gt;[__&lt;SubKey&gt;]</c>, then <paramref name="overrides"/> (serve flags), each layer replacing the last.
    /// </summary>
    /// <param name="settingsFile">Path of <c>atlas.json</c>, or null for none; a missing file is not an error, an invalid one throws <see cref="InvalidDataException"/>.</param>
    /// <param name="environment">Environment variables (name to value); only names with the prefix are read.</param>
    /// <param name="overrides">Settings keyed like the section (<c>Guest:ShowPlayers</c>), for command-line flags.</param>
    public static PublicOptions Load(string? settingsFile, IDictionary? environment = null, IEnumerable<KeyValuePair<string, string?>>? overrides = null)
    {
        var builder = new ConfigurationBuilder();
        try
        {
            if (settingsFile is not null && File.Exists(settingsFile))
            {
                builder.AddJsonFile(settingsFile, optional: false, reloadOnChange: false);
            }

            var env = new List<KeyValuePair<string, string?>>();
            if (environment is not null)
            {
                foreach (DictionaryEntry entry in environment)
                {
                    if (entry.Key is string name && name.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        env.Add(new KeyValuePair<string, string?>($"{Section}:{name[EnvironmentPrefix.Length..].Replace("__", ":", StringComparison.Ordinal)}", entry.Value as string));
                    }
                }
            }

            builder.AddInMemoryCollection(env);
            if (overrides is not null)
            {
                builder.AddInMemoryCollection(overrides.Select(p => new KeyValuePair<string, string?>($"{Section}:{p.Key}", p.Value)));
            }

            IConfigurationRoot root = builder.Build();
            var options = new PublicOptions();
            if (root.GetSection($"{Section}:Guest:Maps").GetChildren().Any())
            {
                options.Guest.Maps.Clear(); // the binder appends to a list; a configured list replaces the defaults
            }

            // A misspelled security setting must not be silently ignored (the operator would believe a limit was set): an unknown key is an error.
            root.GetSection(Section).Bind(options, binder => binder.ErrorOnUnknownConfiguration = true);
            return options;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or IOException)
        {
            throw new InvalidDataException($"The Atlas public settings could not be read: {e.Message}", e);
        }
    }

    /// <summary>The settings as a human-readable guest policy word for the listener line.</summary>
    public string PlayersWord => Guest.ShowPlayers ? "shown" : "hidden";

    /// <summary>The guest name policy word (<c>on</c> only when players are shown and names too).</summary>
    public string NamesWord => Guest.ShowPlayers && Guest.ShowPlayerNames ? "on" : "off";

    /// <summary>Invariant text of a number for log lines.</summary>
    internal static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
