using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Ops.Control;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// The separate web pipe (docs/areas/atlas-actions.md, "The web pipe"; design 0.10): a second map-feed pipe whose connections are the
/// <see cref="FeedRole.Web"/> role by construction, for a public Atlas listener that runs as its own low-privilege Windows account. Its access
/// list admits only <see cref="WebFeedPipeOptions.ClientAccount"/> (a protected descriptor owned by the daemon: deny NETWORK, allow that one account;
/// not the daemon's account, not SYSTEM, not Administrators; the pipe is not created at all when the configured account is a group, a built-in or
/// service principal, a built-in account or the daemon's own account, see <see cref="WebFeedPipeOptions.RefuseClientSid"/>), and every instance carries <c>PIPE_REJECT_REMOTE_CLIENTS</c> (read back from the
/// kernel). So code running as the public listener's account opens only this pipe, whose connections the daemon serves with the web role: frames
/// and ticketed actions, never the packet tools, queries or the local actor.
/// <para>
/// The pipe serves through the map feed (<see cref="WorldMapFeed.ServeConnectionAsync"/>), which attaches itself when it starts
/// (<see cref="Attach"/>); this service does nothing while the feed is off or has not started. All <see cref="WebFeedPipeOptions.MaxConnections"/> instances
/// are created before any client is served, the first with <c>FILE_FLAG_FIRST_PIPE_INSTANCE</c>. Instances after the first can only be created
/// while the first one's descriptor grants the daemon the right to create instances, so they are created under a temporary descriptor that
/// also names the daemon, and every instance is switched to the final descriptor (<c>SetAccessControl</c>, on handles opened with
/// <c>WRITE_DAC</c>) before the first client can be served. Threads: accept loops on the thread pool; <see cref="Attach"/> and the lifecycle calls
/// once, from the host.
/// </para>
/// </summary>
public sealed class WebFeedPipe : IHostedService
{
    private readonly WebFeedPipeOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<NamedPipeServerStream> _servers = [];
    private readonly List<Task> _loops = [];
    private WorldMapFeed? _feed;

    /// <summary>Build the pipe from its options; nothing is created until <see cref="StartAsync"/>.</summary>
    public WebFeedPipe(IOptions<WebFeedPipeOptions> options, ILoggerFactory loggers)
    {
        _options = options.Value;
        _logger = loggers.CreateLogger(WorldMapFeed.LogCategory);
    }

    /// <summary>The pipe name template (tests pick a unique name; parallel test hosts share a process id).</summary>
    internal string PipeNameTemplate { get; set; } = WebFeedPipeOptions.PipeNameTemplate;

    /// <summary>
    /// Test hook, not reachable from configuration: skip the client-account rules (<see cref="WebFeedPipeOptions.RefuseClientSid"/>). The pipe client of a
    /// test is the daemon's own process, which the rules refuse by design. Production never sets it.
    /// </summary>
    internal bool SkipClientAccountRules { get; set; }

    /// <summary>The pipe name being served (without <c>\\.\pipe\</c>), or null when the pipe is off.</summary>
    internal string? PipeName { get; private set; }

    /// <summary>Whether an accept loop is running.</summary>
    internal bool IsListening => _loops.Count > 0 && _loops.Exists(static t => !t.IsCompleted);

    /// <summary>The server streams of the pipe instances (tests read their security descriptors); empty when the pipe is off.</summary>
    internal IReadOnlyList<NamedPipeServerStream> Servers => _servers;

    /// <summary>The feed that serves this pipe's connections; called by the feed when it starts.</summary>
    internal void Attach(WorldMapFeed feed) => _feed = feed;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogDebug("web feed pipe off: {Section}:Enabled is false", WebFeedPipeOptions.SectionName);
            return Task.CompletedTask;
        }

        IReadOnlyList<string> problems = _options.Problems();
        if (problems.Count > 0)
        {
            _logger.LogWarning("web feed pipe off: {Problems}", string.Join("; ", problems));
            return Task.CompletedTask;
        }

        if (_feed is not { IsEnabled: true })
        {
            _logger.LogWarning("web feed pipe off: the map feed is not running (World:Control:MapFeed:Enabled), and the web pipe is served by it");
            return Task.CompletedTask;
        }

        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning("web feed pipe off: it is Windows-only (a named pipe with an access list)");
            return Task.CompletedTask;
        }

        string? name = ControlPipeSecurity.ResolveName(PipeNameTemplate, Environment.ProcessId, out string? problem);
        if (name is null)
        {
            _logger.LogWarning("web feed pipe off: the pipe name: {Problem}", problem);
            return Task.CompletedTask;
        }

        Listen(name, _feed);
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private void Listen(string name, WorldMapFeed feed)
    {
        SecurityIdentifier? client = ResolveClient(_options.ClientAccount!);
        if (client is null)
        {
            return;
        }

        SecurityIdentifier daemon = ControlPipeSecurity.CurrentUser();
        if (!SkipClientAccountRules && WebFeedPipeOptions.RefuseClientSid(client, daemon) is { } reason)
        {
            _logger.LogWarning("web feed pipe off: ClientAccount '{Account}' is refused: {Reason}", _options.ClientAccount, reason);
            return;
        }

        var servers = new List<NamedPipeServerStream>(_options.MaxConnections);
        try
        {
            PipeSecurity temporary = Descriptor(daemon, client, includeDaemon: true);
            for (int i = 0; i < _options.MaxConnections; i++)
            {
                servers.Add(LocalPipeServer.Create(name, temporary, (uint)_options.MaxConnections, firstInstance: i == 0, writeDac: true));
            }

            PipeSecurity final = Descriptor(daemon, client, includeDaemon: false);
            foreach (NamedPipeServerStream server in servers)
            {
                server.SetAccessControl(final);
                if (!LocalPipeServer.RejectsRemoteClients(server.SafePipeHandle))
                {
                    // Never serve a pipe whose remote-client rejection did not take: the NETWORK deny entry would be the only barrier left.
                    throw new IOException("the pipe was created without remote-client rejection");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "web feed pipe off: could not create \\\\.\\pipe\\{Name} (does another process already hold the name?)", name);
            foreach (NamedPipeServerStream server in servers)
            {
                server.Dispose();
            }

            return;
        }

        _servers.AddRange(servers);
        PipeName = name;
        foreach (NamedPipeServerStream server in servers)
        {
            _loops.Add(Task.Run(() => AcceptLoopAsync(server, feed, _stopping.Token)));
        }

        _logger.LogWarning(
            "web feed pipe on: \\\\.\\pipe\\{Name} serves web-role map-feed connections (frames and ticketed actions only) to {Account} and nobody else, up to {Max} at once (World:Control:MapFeed:Web)",
            name, _options.ClientAccount, _options.MaxConnections);
    }

    /// <summary>The descriptor: protected, owned by the daemon, NETWORK denied, the one client allowed to read and write (and, temporarily, the daemon with full control).</summary>
    [SupportedOSPlatform("windows")]
    private static PipeSecurity Descriptor(SecurityIdentifier daemon, SecurityIdentifier client, bool includeDaemon)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(daemon);
        security.AddAccessRule(new PipeAccessRule(ControlPipeSecurity.Network, PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(client, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        if (includeDaemon)
        {
            security.AddAccessRule(new PipeAccessRule(daemon, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>The SID of <paramref name="account"/> (a SID string, or an account name the machine can translate); null, with a warning, when it is neither.</summary>
    [SupportedOSPlatform("windows")]
    private SecurityIdentifier? ResolveClient(string account)
    {
        string text = account.Trim();
        try
        {
            return text.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                ? new SecurityIdentifier(text)
                : (SecurityIdentifier)new NTAccount(text).Translate(typeof(SecurityIdentifier));
        }
        catch (Exception ex) when (ex is ArgumentException or IdentityNotMappedException or SystemException)
        {
            _logger.LogWarning("web feed pipe off: ClientAccount '{Account}' is not a SID or a known account name ({Reason})", text, ex.Message);
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task AcceptLoopAsync(NamedPipeServerStream server, WorldMapFeed feed, CancellationToken token)
    {
        int failures = 0;
        while (!token.IsCancellationRequested)
        {
            bool connected = false;
            try
            {
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                connected = true;
                failures = 0;
                await feed.ServeConnectionAsync(server, DescribeClient(server), token, FeedRole.Web).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("ops.map-feed", ex);
                _logger.LogWarning(ex, "web feed pipe: a connection ended with an error");
                if (++failures >= 5)
                {
                    _logger.LogWarning("web feed pipe: this pipe instance failed {Failures} times in a row and is closed", failures);
                    break;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * failures), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                if (connected)
                {
                    try
                    {
                        server.Disconnect();
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
                    {
                        // Already disconnected or disposed: nothing to release.
                    }
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static string DescribeClient(NamedPipeServerStream server)
    {
        if (!MapFeedNative.GetNamedPipeClientProcessId(server.SafePipeHandle, out uint pid))
        {
            return "web client pid unknown";
        }

        string name;
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            name = "exited";
        }

        return $"web client pid {pid} {name}";
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loops.Count > 0)
        {
            try
            {
                await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                _logger.LogWarning("web feed pipe: the accept loops did not end within 5 s");
            }
        }

        foreach (NamedPipeServerStream server in _servers)
        {
            server.Dispose();
        }

        _stopping.Dispose();
    }
}
