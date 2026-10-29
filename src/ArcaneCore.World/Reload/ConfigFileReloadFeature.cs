using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.World.Features;
using ArcaneCore.World.HotCode;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace ArcaneCore.World.Reload;

/// <summary>
/// Applies an edited configuration file (world.conf, appsettings*.json) while the world runs when
/// <c>HotReload:ApplyConfigFileChanges</c> is on (default off, the retail behaviour): when a provider of the live configuration reloads (the .conf provider's watcher, ConfConfigurationProvider, or the
/// host's appsettings watcher), the change notifications are debounced and the <c>config</c> reload runs through the same
/// <see cref="ReloadCoordinator"/> and <see cref="ConfigContentReloadable"/> as <c>.reload config</c>: built and validated off the
/// world thread, swapped on it, restart-only keys kept and reported. There is no second apply path.
/// <para>
/// With <c>HotReload:Commands</c> on, the coordinator of <see cref="ReloadFeature"/> is used, so a file change and a <c>.reload</c>
/// command never run at once (a busy coordinator is retried after the debounce). With it off, this feature owns a coordinator that
/// holds only <c>config</c>; the <c>.reload</c> commands still do not exist. vmangos has no file watcher (ServerCommands.cpp:1016
/// is the only way in), so the option defaults to off.
/// </para>
/// <para>
/// A notification whose configuration is unchanged (an editor touching the file) runs nothing, so it does not end the
/// <c>.rates set</c> and <c>.server config set</c> overrides a real reload ends. Each applied change logs the changed keys that
/// took effect and those that need a restart.
/// </para>
/// </summary>
public sealed class ConfigFileReloadFeature : IWorldFeature
{
    /// <summary>How long the notifications must be quiet before the reload runs: an editor's save is several writes.</summary>
    internal static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);

    private readonly IServiceProvider _services;
    private readonly ILogger<ConfigFileReloadFeature> _logger;
    private readonly Lock _gate = new();
    private IConfiguration? _configuration;
    private ReloadCoordinator? _coordinator;
    private IDisposable? _subscription;
    private Timer? _timer;
    private Dictionary<string, string> _applied = new(StringComparer.OrdinalIgnoreCase);
    private bool _running;
    private bool _again;
    private bool _stopped;
    private Task _last = Task.CompletedTask;

    public ConfigFileReloadFeature(IServiceProvider services, ILogger<ConfigFileReloadFeature> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The quiet time before a reload runs (test seam).</summary>
    internal TimeSpan Debounce { get; init; } = DefaultDebounce;

    /// <summary>Whether a configuration change is applied (the option is on and the configuration can be re-read).</summary>
    public bool Enabled => _subscription is not null;

    /// <summary>The most recent file-triggered reload, or a completed task (test seam).</summary>
    internal Task LastApply
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        HotReloadOptions options = ReloadPolicy.Resolve(_services);
        if (!options.ApplyConfigFileChanges)
        {
            return; // retail: a configuration file edit takes effect through .reload config or a restart
        }

        // ConfigContentReloadable re-reads the source list; a configuration without one (a test host's in-memory view) cannot be re-read.
        if (_services.GetService<IConfiguration>() is not { } configuration || configuration is not IConfigurationBuilder)
        {
            return;
        }

        if (_services.GetService<ReloadFeature>() is { Enabled: true } reload)
        {
            _coordinator = reload.Coordinator;
        }
        else
        {
            var own = new ReloadCoordinator(_logger, options, _services.GetService<IHotSwapReporter>());
            own.Attach(world);
            own.Register(new ConfigContentReloadable(_services));
            _coordinator = own;
        }

        _configuration = configuration;
        _applied = Flatten(configuration);
        _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        _subscription = ChangeToken.OnChange(configuration.GetReloadToken, Schedule);
        _logger.LogInformation("Configuration file changes are applied while the world runs, as .reload config applies them (HotReload:ApplyConfigFileChanges)");
    }

    public async Task StopAsync()
    {
        Task last;
        lock (_gate)
        {
            _stopped = true;
            _subscription?.Dispose();
            _subscription = null;
            _timer?.Dispose();
            _timer = null;
            last = _last;
        }

        await last.ConfigureAwait(false);
    }

    /// <summary>A provider reloaded: (re)start the quiet period.</summary>
    private void Schedule()
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _timer?.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            if (_running)
            {
                _again = true; // a change during a run: one more pass when it ends
                return;
            }

            _running = true;
            _last = Task.Run(ApplyAsync);
        }
    }

    private async Task ApplyAsync()
    {
        bool retry = false;
        try
        {
            retry = await ApplyOnceAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("reload.config-file", ex, "Applying the changed configuration file failed");
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
                if ((retry || _again) && !_stopped)
                {
                    _again = false;
                    _timer?.Change(Debounce, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    /// <summary>One pass; true when it must run again (the coordinator was busy with another reload).</summary>
    private async Task<bool> ApplyOnceAsync()
    {
        if (_configuration is not { } configuration || _coordinator is not { } coordinator)
        {
            return false;
        }

        Dictionary<string, string> now = Flatten(configuration);
        List<string> changed = [.. now.Keys.Where(k => !_applied.TryGetValue(k, out string? before) || before != now[k])
            .Concat(_applied.Keys.Where(k => !now.ContainsKey(k)))
            .Order(StringComparer.OrdinalIgnoreCase)];
        if (changed.Count == 0)
        {
            return false; // a save that changed nothing: no reload, the overrides stay
        }

        ReloadResult result = await coordinator.ReloadAsync("config", origin: FileOrigin).ConfigureAwait(false);
        if (result.Status == ReloadStatus.Busy)
        {
            return true;
        }

        // Applied or not, this content was handled: an invalid file is reported once and waits for the next edit.
        _applied = now;
        if (result.Status != ReloadStatus.Applied)
        {
            _logger.LogWarning("The changed configuration file was not applied ({Status}: {Message}); fix it and save again. {Notes}",
                result.Status, result.Message, string.Join(" ", result.Notes));
            return false;
        }

        string[] live = [.. changed.Where(IsLive)];
        string[] restart = [.. changed.Where(k => !IsLive(k))];
        _logger.LogInformation(
            "Configuration file change applied: {LiveCount} live key(s) changed ({Live}); {RestartCount} changed key(s) need a restart to take effect ({Restart})",
            live.Length, live.Length == 0 ? "none" : string.Join(", ", live),
            restart.Length, restart.Length == 0 ? "none" : string.Join(", ", restart));
        return false;
    }

    private static readonly HotSwapOrigin FileOrigin = new("configuration file");

    /// <summary>
    /// A changed key that is in force now: one the config reload applies, or a <c>Logging</c> key (the logging providers follow
    /// their own options monitor, ArcaneLoggerProvider). Anything else waits for a restart.
    /// </summary>
    private static bool IsLive(string path)
        => ConfigContentReloadable.Classify(path) == true
            || path.StartsWith("Logging:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every leaf value of the live configuration, by path (case-insensitive like configuration itself).</summary>
    private static Dictionary<string, string> Flatten(IConfiguration configuration)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string?> pair in configuration.AsEnumerable())
        {
            if (pair.Value is not null)
            {
                values[pair.Key] = pair.Value;
            }
        }

        return values;
    }
}
