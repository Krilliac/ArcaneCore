using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Configuration.Conf;

/// <summary>The .conf file could not be located or is not usable (a missing explicit file, a file where a directory is required). Start-up exit code 78.</summary>
public sealed class ConfLocationException(string message) : Exception(message);

/// <summary>
/// Collects reload failures of the .conf file until the host has a logger, then logs them at Error. Thread-safe: the file watcher
/// reports from a thread-pool thread.
/// </summary>
public sealed class ConfReloadReporter
{
    private readonly object _gate = new();
    private readonly List<string> _pending = [];
    private ILogger? _logger;

    /// <summary>Called by the provider; logs at once when a logger is attached, otherwise queues.</summary>
    public void Report(string message)
    {
        ILogger? logger;
        lock (_gate)
        {
            logger = _logger;
            if (logger is null)
            {
                _pending.Add(message);
                return;
            }
        }

        logger.LogError("configuration file reload failed: {Message}", message);
    }

    /// <summary>Attaches the logger and flushes the queued messages.</summary>
    public void Attach(ILogger logger)
    {
        string[] queued;
        lock (_gate)
        {
            _logger = logger;
            queued = [.. _pending];
            _pending.Clear();
        }

        foreach (string message in queued)
        {
            logger.LogError("configuration file reload failed: {Message}", message);
        }
    }
}

/// <summary>What <see cref="ConfFiles.Attach"/> did: the file it registered (null when there is none) and the reload reporter.</summary>
public sealed record ConfAttachment(string? Path, ConfReloadReporter Reporter);

/// <summary>Finds a daemon's .conf file and registers it as a configuration source between appsettings.json and the environment.</summary>
public static class ConfFiles
{
    /// <summary>The environment variable that names the directory holding world.conf and realm.conf.</summary>
    public const string DirectoryVariable = "ARCANECORE_CONFIG";

    /// <summary>
    /// Resolves the file (explicit path, else <c>$ARCANECORE_CONFIG/&lt;daemon&gt;.conf</c>, else <c>&lt;content root&gt;/&lt;daemon&gt;.conf</c>) and,
    /// when it exists, inserts a source right after the last <c>appsettings*.json</c> source, so the precedence is
    /// appsettings.json &lt; appsettings.{env}.json &lt; .conf &lt; user secrets &lt; environment variables &lt; command line.
    /// A missing explicit file is an error; a missing default file is skipped. A malformed file throws <see cref="ConfParseException"/>
    /// before anything is registered.
    /// </summary>
    public static ConfAttachment Attach(IHostApplicationBuilder builder, ConfigDaemon daemon, string? explicitPath)
    {
        var reporter = new ConfReloadReporter();
        string? path = Resolve(builder.Environment.ContentRootPath, daemon, explicitPath, Environment.GetEnvironmentVariable(DirectoryVariable), out bool required);
        if (path is null || (!required && !File.Exists(path)))
        {
            return new ConfAttachment(null, reporter);
        }

        if (!File.Exists(path))
        {
            throw new ConfLocationException($"the configuration file {path} does not exist (--config).");
        }

        _ = ConfDocument.Load(path); // fail closed on a malformed file before any source is registered

        IList<IConfigurationSource> sources = builder.Configuration.Sources;
        int after = -1;
        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i] is JsonConfigurationSource json
                && json.Path is { } jsonPath
                && System.IO.Path.GetFileName(jsonPath).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
            {
                after = i;
            }
        }

        if (after < 0)
        {
            throw new InvalidOperationException("the host configuration has no appsettings*.json source, so the position of the .conf file in the precedence order is undefined");
        }

        bool reloadOnChange = !bool.TryParse(builder.Configuration["hostBuilder:reloadConfigOnChange"], out bool configured) || configured;
        sources.Insert(after + 1, new ConfConfigurationSource
        {
            Path = path,
            Optional = false,
            ReloadOnChange = reloadOnChange,
            ReloadError = reporter.Report,
        });
        return new ConfAttachment(path, reporter);
    }

    /// <summary>The path of the daemon's file; <paramref name="required"/> is true when the operator named it (--config or the environment directory file exists check is left to the caller).</summary>
    internal static string? Resolve(string contentRoot, ConfigDaemon daemon, string? explicitPath, string? environmentDirectory, out bool required)
    {
        required = false;
        string fileName = ConfCatalog.FileName(daemon);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            required = true;
            return System.IO.Path.GetFullPath(explicitPath, contentRoot);
        }

        if (!string.IsNullOrWhiteSpace(environmentDirectory))
        {
            string directory = System.IO.Path.GetFullPath(environmentDirectory, contentRoot);
            if (File.Exists(directory))
            {
                throw new ConfLocationException(
                    $"{DirectoryVariable} must name a directory that holds world.conf and realm.conf, but {directory} is a file; use --config <file> to name one file.");
            }

            if (!Directory.Exists(directory))
            {
                throw new ConfLocationException($"{DirectoryVariable} names {directory}, which is not an existing directory.");
            }

            return System.IO.Path.Combine(directory, fileName);
        }

        return System.IO.Path.Combine(contentRoot, fileName);
    }
}
