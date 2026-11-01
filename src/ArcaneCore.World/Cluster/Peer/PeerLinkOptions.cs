using System.Globalization;
using System.Net;

namespace ArcaneCore.World.Cluster.Peer;

/// <summary>
/// The worker-to-worker peer link (<c>Cluster:Peer</c>): the node that holds the Social or Economy realm authority lease listens on
/// <see cref="BindAddress"/>:<see cref="Port"/>, and every other node finds that holder through the lease store plus the <see cref="Nodes"/> endpoint map
/// and keeps one link to it. The link proves a shared secret (HMAC of a per-link challenge) and compares node manifests like the gateway link does.
/// The secret is never a configuration key: it is read from the environment variable <see cref="SecretEnvironmentVariable"/> only.
/// </summary>
public sealed class PeerLinkOptions
{
    public const string SectionName = "Cluster:Peer";

    /// <summary>The environment variable that carries the peer link secret (at least <see cref="MinSecretLength"/> characters). Not a configuration key, so it cannot end up in a committed file.</summary>
    public const string SecretEnvironmentVariable = "ARCANECORE_PEER_LINK_SECRET";

    /// <summary>Shortest secret the link accepts.</summary>
    public const int MinSecretLength = 16;

    /// <summary>Address the lease holder listens on. Keep it on loopback or a private network: the link is not encrypted.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>Port the lease holder listens on (0 lets the operating system pick one, for tests).</summary>
    public int Port { get; set; } = 8097;

    /// <summary>Where each node's peer listener is, by node id (<c>Cluster:Worker:NodeId</c>) to <c>host:port</c>. A node that finds the lease holder in the lease store looks its endpoint up here; a holder without an entry is not dialled.</summary>
    public Dictionary<string, string> Nodes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>First wait, in milliseconds, before a client redials a lost or refused link; it doubles per failed attempt up to <see cref="ReconnectMaxMs"/> and resets once a link is admitted.</summary>
    public int ReconnectInitialMs { get; set; } = 250;

    /// <summary>Longest wait, in milliseconds, between redials.</summary>
    public int ReconnectMaxMs { get; set; } = 10_000;

    /// <summary>How often, in milliseconds, a client re-reads the lease store to notice that the holder changed (a link to a former holder is dropped and the new holder is dialled).</summary>
    public int HolderPollMs { get; set; } = 1000;

    /// <summary>How long, in milliseconds, a new link may take to answer the challenge and say hello before it is closed.</summary>
    public int HandshakeTimeoutMs { get; set; } = 5000;

    /// <summary>How long, in milliseconds, a request waits for its answer before it fails with a timeout.</summary>
    public int RequestTimeoutMs { get; set; } = 5000;

    /// <summary>Most requests a client may have in flight; one more is refused at once instead of queued.</summary>
    public int MaxPendingRequests { get; set; } = 256;

    /// <summary><see cref="ReconnectInitialMs"/> as a time span.</summary>
    public TimeSpan ReconnectInitial => TimeSpan.FromMilliseconds(ReconnectInitialMs);

    /// <summary><see cref="ReconnectMaxMs"/> as a time span.</summary>
    public TimeSpan ReconnectMax => TimeSpan.FromMilliseconds(ReconnectMaxMs);

    /// <summary><see cref="HolderPollMs"/> as a time span.</summary>
    public TimeSpan HolderPoll => TimeSpan.FromMilliseconds(HolderPollMs);

    /// <summary><see cref="HandshakeTimeoutMs"/> as a time span.</summary>
    public TimeSpan HandshakeTimeout => TimeSpan.FromMilliseconds(HandshakeTimeoutMs);

    /// <summary><see cref="RequestTimeoutMs"/> as a time span.</summary>
    public TimeSpan RequestTimeout => TimeSpan.FromMilliseconds(RequestTimeoutMs);

    /// <summary>What is wrong with these values; empty when valid.</summary>
    public IEnumerable<string> Validate()
    {
        if (!IPAddress.TryParse(BindAddress, out _))
        {
            yield return $"BindAddress '{BindAddress}' is not an IP address";
        }

        if (Port is < 0 or > 65535)
        {
            yield return $"Port {Port} must be between 0 and 65535";
        }

        if (ReconnectInitialMs < 1)
        {
            yield return $"ReconnectInitialMs {ReconnectInitialMs} must be at least 1";
        }

        if (ReconnectMaxMs < ReconnectInitialMs)
        {
            yield return $"ReconnectMaxMs {ReconnectMaxMs} must not be below ReconnectInitialMs ({ReconnectInitialMs})";
        }

        if (HolderPollMs < 1)
        {
            yield return $"HolderPollMs {HolderPollMs} must be at least 1";
        }

        if (HandshakeTimeoutMs < 1)
        {
            yield return $"HandshakeTimeoutMs {HandshakeTimeoutMs} must be at least 1";
        }

        if (RequestTimeoutMs < 1)
        {
            yield return $"RequestTimeoutMs {RequestTimeoutMs} must be at least 1";
        }

        if (MaxPendingRequests < 1)
        {
            yield return $"MaxPendingRequests {MaxPendingRequests} must be at least 1";
        }

        foreach ((string node, string endpoint) in Nodes)
        {
            if (string.IsNullOrWhiteSpace(node))
            {
                yield return "Nodes has an entry with an empty node id";
            }
            else if (!TryParseEndpoint(endpoint, out _, out _))
            {
                yield return $"Nodes:{node} '{endpoint}' is not host:port";
            }
        }
    }

    /// <summary>The secret from <see cref="SecretEnvironmentVariable"/>, or null when it is unset or shorter than <see cref="MinSecretLength"/>.</summary>
    public static string? ReadSecretFromEnvironment()
    {
        string? secret = Environment.GetEnvironmentVariable(SecretEnvironmentVariable);
        return secret is not null && secret.Length >= MinSecretLength ? secret : null;
    }

    /// <summary>Splits <c>host:port</c> (the last colon separates the port, so a bracketed IPv6 literal works); false when the text is not a host and a port in 1..65535.</summary>
    public static bool TryParseEndpoint(string? text, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        int colon = trimmed.LastIndexOf(':');
        if (colon <= 0 || colon == trimmed.Length - 1)
        {
            return false;
        }

        string hostText = trimmed[..colon].Trim('[', ']');
        if (hostText.Length == 0
            || !int.TryParse(trimmed[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            || parsed is < 1 or > 65535)
        {
            return false;
        }

        host = hostText;
        port = parsed;
        return true;
    }
}
