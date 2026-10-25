using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using ArcaneCore.Atlas.Core;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Host.Public;

/// <summary>Counters the operator reads with <c>public.status</c>. Thread-safe.</summary>
internal sealed class PublicStats(TimeProvider time)
{
    private static readonly TimeSpan LoginWindow = TimeSpan.FromMinutes(15);

    private readonly ConcurrentQueue<long> _loginFailures = new();
    private readonly ConcurrentDictionary<string, long> _bytesByIp = new(StringComparer.Ordinal);
    private long _bytesOut;
    private long _framesDropped;
    private long _framesSent;
    private long _floodCloses;
    private int _sockets;

    /// <summary>Open WebSockets.</summary>
    public int Sockets => Volatile.Read(ref _sockets);

    public long BytesOut => Interlocked.Read(ref _bytesOut);

    public long FramesSent => Interlocked.Read(ref _framesSent);

    public long FramesDropped => Interlocked.Read(ref _framesDropped);

    /// <summary>Sockets the server closed with 1008 "flood" (a client that kept sending refused messages).</summary>
    public long FloodCloses => Interlocked.Read(ref _floodCloses);

    /// <summary>Counts a socket closed for flooding.</summary>
    public void FloodClosed() => Interlocked.Increment(ref _floodCloses);

    public int SocketOpened() => Interlocked.Increment(ref _sockets);

    public int SocketClosed() => Interlocked.Decrement(ref _sockets);

    /// <summary>Counts a frame that was written to a socket.</summary>
    public void Sent(string ipKey, int bytes)
    {
        Interlocked.Add(ref _bytesOut, bytes);
        Interlocked.Increment(ref _framesSent);
        if (_bytesByIp.Count < 2000 || _bytesByIp.ContainsKey(ipKey))
        {
            _bytesByIp.AddOrUpdate(ipKey, bytes, (_, total) => total + bytes);
        }
    }

    /// <summary>Counts a frame that was not sent (byte budget, slow socket).</summary>
    public void Dropped() => Interlocked.Increment(ref _framesDropped);

    public void LoginFailed() => _loginFailures.Enqueue(time.GetTimestamp());

    /// <summary>Login failures in the last 15 minutes.</summary>
    public int LoginFailuresRecent()
    {
        long cutoff = time.GetTimestamp() - (long)(LoginWindow.TotalSeconds * time.TimestampFrequency);
        while (_loginFailures.TryPeek(out long oldest) && oldest < cutoff)
        {
            _loginFailures.TryDequeue(out _);
        }

        return _loginFailures.Count;
    }

    /// <summary>The addresses that received the most bytes.</summary>
    public IReadOnlyList<(string Ip, long Bytes)> TopIps(int count)
        => [.. _bytesByIp.OrderByDescending(p => p.Value).Take(count).Select(p => (p.Key, p.Value))];
}

/// <summary>
/// Everything one running public listener shares: the validated settings, sessions, limits, the frame store, the world link and the counters. Built by
/// <see cref="PublicServer"/>; the commands (<c>guest.*</c>, <c>public.*</c>) and the hub read it. Immutable references; the members are thread-safe.
/// </summary>
internal sealed class PublicRuntime
{
    public required PublicOptions Options { get; init; }

    public required PublicConfigResult Config { get; init; }

    public required PublicProcessMode Mode { get; init; }

    public required PublicSessions Sessions { get; init; }

    public required PublicRateLimits Limits { get; init; }

    public required PublicFrameStore Frames { get; init; }

    public required IPublicWorld World { get; init; }

    public required PublicStats Stats { get; init; }

    public required ILogger Log { get; init; }

    public required TimeProvider Time { get; init; }

    public required DateTimeOffset Started { get; init; }

    /// <summary>The bookmarks guests may see (the built-in ones; saved ones belong to the Local user).</summary>
    public IReadOnlyList<Bookmark> Bookmarks { get; init; } = BuiltInBookmarks.All;

    /// <summary>Runs action commands for account sessions; null when this host has no action channel for the public listener.</summary>
    public IPublicActionRunner? Actions { get; init; }

    /// <summary>The certificate's expiry in <see cref="PublicTlsMode.Certificate"/> mode.</summary>
    public DateTimeOffset? CertificateNotAfter { get; init; }

    /// <summary>The ids of the layers guests may see (live entity layers only; terrain layers are derived from client files and are never served).</summary>
    public static IReadOnlyList<string> PublicLayerIds { get; } = [LayerIds.LivePlayers, LayerIds.LiveCreatures, LayerIds.LiveGameObjects];

    /// <summary>The effective listener URL scheme.</summary>
    public string Scheme => Config.TlsMode == PublicTlsMode.Certificate ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;

    /// <summary>The map ids guests may view.</summary>
    public IReadOnlyList<uint> AllowedMaps => [.. Options.Guest.Maps.Select(m => m.Id)];

    /// <summary>Whether any map's guest data is reduced (the world's <c>links</c> is missing or stale).</summary>
    public bool AnyReduced => Options.Guest.ShowPlayers ? false : AllowedMaps.Any(m => Frames.Status(m).Reduced);

    /// <summary>The key-value state of the account levels: the lowest level that may log in.</summary>
    public int LoginFloor => Options.Login.AllowBelowMinActionSecurity ? 0 : (int)Options.MinActionSecurity;

    public static string? LevelName(int level) => Enum.IsDefined((PublicSecurity)level) ? ((PublicSecurity)level).ToString() : null;
}

/// <summary>
/// The service the Atlas commands reach the running public listener through: set by <see cref="PublicServer"/> while it runs. The commands exist in
/// every Atlas process (so <c>ctl help</c> lists them); without a running listener they answer <c>unavailable</c>.
/// </summary>
public sealed class PublicState
{
    private volatile PublicRuntime? _runtime;

    internal PublicRuntime? Runtime => _runtime;

    internal void Attach(PublicRuntime runtime) => _runtime = runtime;

    internal void Detach(PublicRuntime runtime) => Interlocked.CompareExchange(ref _runtime, null, runtime);
}
