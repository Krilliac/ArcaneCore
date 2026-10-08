using System.Globalization;
using ArcaneCore.Kernel.Configuration;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.Data.ClientData;

/// <summary>Where a consumer's DBC path came from.</summary>
public enum ClientDbcSource
{
    /// <summary>Its own key is set (it wins over the directory).</summary>
    Explicit,

    /// <summary>Filled from <c>ClientData:DbcDirectory</c>.</summary>
    Directory,

    /// <summary>Not set: the feature keeps its fallback.</summary>
    None,
}

/// <summary>How one consumer key resolved.</summary>
/// <param name="Consumer">The key.</param>
/// <param name="Source">Where the path came from.</param>
/// <param name="Path">The path handed to the feature (null for <see cref="ClientDbcSource.None"/>).</param>
/// <param name="Check">The header check of the file that was considered (null when nothing was).</param>
/// <param name="Note">Why a usable file under the directory was not handed over (a group member is unusable), or null.</param>
public sealed record ClientDbcResolution(ClientDbcConsumer Consumer, ClientDbcSource Source, string? Path, ClientDbcFileCheck? Check, string? Note);

/// <summary>How one directory key (<see cref="ClientDbcDirectoryConsumer"/>) resolved: its own value, the <c>ClientData:DbcDirectory</c> itself, or unset.</summary>
public sealed record ClientDirectoryResolution(ClientDbcDirectoryConsumer Consumer, ClientDbcSource Source, string? Path);

/// <summary>A client data problem, phrased for the log and for <c>check-config</c>.</summary>
public sealed record ClientDataProblem(string Key, string Problem, string Fix);

/// <summary>A log line of the start-up report and whether it is a warning.</summary>
public sealed record ClientDataLine(bool Warning, string Text);

/// <summary>
/// The resolution of every client DBC the world daemon reads (docs/areas/client-data.md): the configuration overlay that fills
/// unset per-file keys from <c>ClientData:DbcDirectory</c>, one line per DBC file for the start-up log, and the problems that
/// <c>ClientData:Strict</c> makes fatal. Built once from the configuration, before any feature binds its options.
/// </summary>
public sealed class ClientDataReport
{
    private ClientDataReport(
        ClientDataOptions options, bool directoryExists, IReadOnlyList<ClientDbcResolution> resolutions,
        IReadOnlyList<ClientDbcFileCheck> directoryFiles, IReadOnlyList<ClientDataProblem> problems,
        IReadOnlyList<ClientDirectoryResolution> directoryResolutions)
    {
        Options = options;
        DirectoryExists = directoryExists;
        Resolutions = resolutions;
        DirectoryFiles = directoryFiles;
        Problems = problems;
        DirectoryResolutions = directoryResolutions;
        var overlay = resolutions.Where(r => r.Source == ClientDbcSource.Directory)
            .ToDictionary(r => r.Consumer.Key, r => (string?)r.Path, StringComparer.OrdinalIgnoreCase);
        foreach (ClientDirectoryResolution resolution in directoryResolutions.Where(r => r.Source == ClientDbcSource.Directory))
        {
            overlay[resolution.Consumer.Key] = resolution.Path;
        }

        Overlay = overlay;
    }

    /// <summary>A report for a daemon without client data configuration (nothing set, nothing checked).</summary>
    public static ClientDataReport Empty { get; } = new(
        new ClientDataOptions(), false, [.. ClientDbcConsumers.All.Select(c => new ClientDbcResolution(c, ClientDbcSource.None, null, null, null))], [], [], [.. ClientDbcConsumers.Directories.Select(c => new ClientDirectoryResolution(c, ClientDbcSource.None, null))]);

    public ClientDataOptions Options { get; }

    public bool DirectoryConfigured => !string.IsNullOrWhiteSpace(Options.DbcDirectory);

    public bool DirectoryExists { get; }

    public IReadOnlyList<ClientDbcResolution> Resolutions { get; }

    /// <summary>Every *.dbc file of the directory and every reference-layout file it lacks (empty without a directory).</summary>
    public IReadOnlyList<ClientDbcFileCheck> DirectoryFiles { get; }

    public IReadOnlyList<ClientDataProblem> Problems { get; }

    /// <summary>How each directory key (<see cref="ClientDbcConsumers.Directories"/>) resolved.</summary>
    public IReadOnlyList<ClientDirectoryResolution> DirectoryResolutions { get; }

    /// <summary>The number of keys (per-file and directory) the directory can fill.</summary>
    public static int KeyCount => ClientDbcConsumers.All.Count + ClientDbcConsumers.Directories.Count;

    /// <summary>The keys the directory fills: add these to the configuration after every other source.</summary>
    public IReadOnlyDictionary<string, string?> Overlay { get; }

    /// <summary>Resolve every consumer of <paramref name="configuration"/>.</summary>
    public static ClientDataReport Build(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new ClientDataOptions();
        configuration.GetSection(ClientDataOptions.SectionName).Bind(options);
        string directory = options.DbcDirectory?.Trim() ?? string.Empty;
        bool useDirectory = directory.Length > 0;
        bool directoryExists = useDirectory && System.IO.Directory.Exists(directory);
        if (directoryExists)
        {
            directory = System.IO.Path.GetFullPath(directory); // one separator style in the paths handed out and logged
        }

        var problems = new List<ClientDataProblem>();
        if (useDirectory && !directoryExists)
        {
            problems.Add(new ClientDataProblem("ClientData:DbcDirectory", $"the directory '{directory}' does not exist, so no DBC is read from it.",
                "point it at the extracted build-5875 DBFilesClient directory, or leave it empty"));
        }

        var checks = new Dictionary<string, ClientDbcFileCheck>(StringComparer.OrdinalIgnoreCase);
        ClientDbcFileCheck CheckOnce(string path) => checks.TryGetValue(path, out ClientDbcFileCheck? known) ? known : checks[path] = ClientDbcInspector.Check(path);

        // First pass: what each key would get on its own.
        var candidates = new List<(ClientDbcConsumer Consumer, ClientDbcSource Source, string? Path, ClientDbcFileCheck? Check)>();
        foreach (ClientDbcConsumer consumer in ClientDbcConsumers.All)
        {
            string? own = configuration[consumer.Key];
            if (!string.IsNullOrWhiteSpace(own))
            {
                ClientDbcFileCheck check = CheckOnce(own.Trim());
                candidates.Add((consumer, ClientDbcSource.Explicit, own, check));
                if (!check.IsUsable)
                {
                    problems.Add(new ClientDataProblem(consumer.Key, $"{consumer.File} at '{own}' is {check.Describe()}; the {consumer.Feature} feature will refuse to start with it.",
                        "point the key at a build-5875 file, or remove it to use ClientData:DbcDirectory"));
                }
            }
            else if (directoryExists)
            {
                string path = System.IO.Path.Combine(directory, consumer.File);
                candidates.Add((consumer, ClientDbcSource.Directory, path, CheckOnce(path)));
            }
            else
            {
                candidates.Add((consumer, ClientDbcSource.None, null, null));
            }
        }

        // Second pass: a group is filled from the directory only when every member it would fill is usable.
        var resolutions = new List<ClientDbcResolution>();
        foreach ((ClientDbcConsumer consumer, ClientDbcSource source, string? path, ClientDbcFileCheck? check) in candidates)
        {
            if (source != ClientDbcSource.Directory)
            {
                resolutions.Add(new ClientDbcResolution(consumer, source, source == ClientDbcSource.Explicit ? path : null, check, null));
                continue;
            }

            if (!check!.IsUsable)
            {
                problems.Add(new ClientDataProblem(consumer.Key, $"{consumer.File} in ClientData:DbcDirectory is {check.Describe()}; {consumer.Feature}: {consumer.Fallback}.",
                    $"put the build-5875 {consumer.File} in '{directory}', or set {consumer.Key}"));
                resolutions.Add(new ClientDbcResolution(consumer, ClientDbcSource.None, null, check, null));
                continue;
            }

            ClientDbcConsumer? blocker = consumer.Group is null ? null : candidates
                .Where(c => c.Consumer.Group == consumer.Group && c.Source == ClientDbcSource.Directory && c.Check is { IsUsable: false })
                .Select(c => c.Consumer).FirstOrDefault();
            if (blocker is not null)
            {
                string note = $"not used: {blocker.File} is needed with it and is unusable";
                problems.Add(new ClientDataProblem(consumer.Key, $"{consumer.File} in ClientData:DbcDirectory is {note}; {consumer.Feature}: {consumer.Fallback}.",
                    $"put the build-5875 {blocker.File} in '{directory}', or set both keys"));
                resolutions.Add(new ClientDbcResolution(consumer, ClientDbcSource.None, null, check, note));
                continue;
            }

            resolutions.Add(new ClientDbcResolution(consumer, ClientDbcSource.Directory, path, check, null));
        }

        var directoryResolutions = new List<ClientDirectoryResolution>();
        foreach (ClientDbcDirectoryConsumer consumer in ClientDbcConsumers.Directories)
        {
            string? own = configuration[consumer.Key];
            if (!string.IsNullOrWhiteSpace(own))
            {
                directoryResolutions.Add(new ClientDirectoryResolution(consumer, ClientDbcSource.Explicit, own));
            }
            else
            {
                directoryResolutions.Add(directoryExists
                    ? new ClientDirectoryResolution(consumer, ClientDbcSource.Directory, directory)
                    : new ClientDirectoryResolution(consumer, ClientDbcSource.None, null));
            }
        }

        IReadOnlyList<ClientDbcFileCheck> files = directoryExists ? ClientDbcInspector.CheckDirectory(directory) : [];
        foreach (ClientDbcFileCheck file in files.Where(f => f.Status is ClientDbcStatus.Malformed or ClientDbcStatus.FormatMismatch))
        {
            if (!ClientDbcConsumers.All.Any(c => string.Equals(c.File, file.File, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add(new ClientDataProblem("ClientData:DbcDirectory", $"{file.File} in '{directory}' is {file.Describe()}.",
                    "replace the directory with a complete build-5875 extraction"));
            }
        }

        return new ClientDataReport(options, directoryExists, resolutions, files, problems, directoryResolutions);
    }

    /// <summary>
    /// The start-up log: a header line, then one line per DBC file the daemon reads (loaded N records, missing, or format mismatch,
    /// with the keys it feeds and what they fall back to), then a summary of the rest of the directory.
    /// </summary>
    public IReadOnlyList<ClientDataLine> Lines()
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        var lines = new List<ClientDataLine>();
        lines.Add(new ClientDataLine(DirectoryConfigured && !DirectoryExists, !DirectoryConfigured
            ? "ClientData:DbcDirectory is not set: only the per-file DBC keys are read"
            : DirectoryExists
                ? $"ClientData:DbcDirectory {Options.DbcDirectory}: {Overlay.Count} of {KeyCount} DBC keys filled from it{(Options.Strict ? " (ClientData:Strict)" : string.Empty)}"
                : $"ClientData:DbcDirectory {Options.DbcDirectory} does not exist: no DBC is read from it"));

        foreach (IGrouping<string, ClientDbcResolution> file in Resolutions.GroupBy(r => r.Consumer.File, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (IGrouping<string?, ClientDbcResolution> byPath in file.GroupBy(r => r.Check?.Path, StringComparer.OrdinalIgnoreCase))
            {
                ClientDbcResolution first = byPath.First();
                string keys = string.Join(", ", byPath.Select(r => r.Consumer.Key));
                if (first.Check is null)
                {
                    lines.Add(new ClientDataLine(false, $"ClientData: {file.Key}: not configured [{keys}] -> {first.Consumer.Fallback}"));
                    continue;
                }

                string source = first.Source switch
                {
                    ClientDbcSource.Explicit => "explicit key",
                    ClientDbcSource.Directory => "DbcDirectory",
                    _ => "DbcDirectory, not used",
                };
                bool used = first.Source != ClientDbcSource.None;
                string tail = used ? string.Empty : $" -> {first.Note ?? "fallback"}: {first.Consumer.Fallback}";
                lines.Add(new ClientDataLine(!first.Check.IsUsable || !used,
                    $"ClientData: {file.Key}: {first.Check.Describe()} from {first.Check.Path} ({source}) [{keys}]{tail}"));
            }
        }

        foreach (ClientDirectoryResolution resolution in DirectoryResolutions)
        {
            lines.Add(new ClientDataLine(false, resolution.Source switch
            {
                ClientDbcSource.Explicit => $"ClientData: {resolution.Consumer.Key}: directory {resolution.Path} (explicit key) [{resolution.Consumer.Feature}]",
                ClientDbcSource.Directory => $"ClientData: {resolution.Consumer.Key}: directory {resolution.Path} (DbcDirectory) [{resolution.Consumer.Feature}]",
                _ => $"ClientData: {resolution.Consumer.Key}: not configured -> {resolution.Consumer.Fallback}",
            }));
        }

        if (DirectoryFiles.Count > 0)
        {
            ClientDbcFileCheck[] present = [.. DirectoryFiles.Where(f => f.Status != ClientDbcStatus.Missing)];
            int withLayout = present.Count(f => f.Layout is not null);
            int bad = present.Count(f => !f.IsUsable);
            int missing = DirectoryFiles.Count(f => f.Status == ClientDbcStatus.Missing);
            lines.Add(new ClientDataLine(bad > 0 || missing > 0, string.Create(c,
                $"ClientData: directory holds {present.Length} DBC files; {withLayout} have a reference layout, {bad} do not match it or are malformed, {missing} reference files are missing")));
            foreach (ClientDbcFileCheck check in DirectoryFiles.Where(f => !f.IsUsable && !ClientDbcConsumers.Files.Contains(f.File, StringComparer.OrdinalIgnoreCase)))
            {
                lines.Add(new ClientDataLine(true, $"ClientData: {check.File}: {check.Describe()}"));
            }
        }

        return lines;
    }
}
