using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>One child process to run: an executable and its argument list (never a command line, never a shell), a working directory and the environment changes.</summary>
internal sealed record QaProcessRequest(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string?> Environment, TimeSpan Timeout);

/// <summary>How a child ended. <paramref name="TimedOut"/>: it was killed at the timeout (the exit code and output are then not meaningful).</summary>
internal sealed record QaProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>Runs a child process (the test seam of the <c>gh</c> adapter).</summary>
internal interface IQaProcessRunner
{
    Task<QaProcessResult> RunAsync(QaProcessRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The real runner: <see cref="Process"/> with <c>UseShellExecute = false</c> and <see cref="ProcessStartInfo.ArgumentList"/> (each argument is one argv entry; nothing is
/// parsed by a shell), stdin closed, stdout and stderr read up to 8 MiB each, an environment entry with a null value removed from the child, and the child (with its
/// tree) killed when the timeout ends. Thread-safe (stateless).
/// </summary>
internal sealed class QaProcessRunner : IQaProcessRunner
{
    private const int MaxOutputChars = 8 * 1024 * 1024;
    internal const string OutputCapMessage = "gh printed more than 8 MiB";

    public async Task<QaProcessResult> RunAsync(QaProcessRequest request, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = request.WorkingDirectory,
        };
        foreach (string argument in request.Arguments) info.ArgumentList.Add(argument);
        foreach ((string name, string? value) in request.Environment)
        {
            if (value is null) info.Environment.Remove(name);
            else info.Environment[name] = value;
        }

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new PlayerbotQaPublishException("gh could not be started", transient: false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new PlayerbotQaPublishException("gh could not be started: " + ex.GetType().Name, transient: false, ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        try
        {
            process.StandardInput.Close();
            stdout = ReadCappedAsync(process.StandardOutput, timeout.Token);
            stderr = ReadCappedAsync(process.StandardError, timeout.Token);
            Task exit = process.WaitForExitAsync(timeout.Token);

            // A reader that hits the cap stops draining its pipe, so the child would block and never exit: watch the readers too and fail the moment one faults.
            Task<string>? pendingOut = stdout;
            Task<string>? pendingErr = stderr;
            while (true)
            {
                Task finished = await Task.WhenAny(exit, pendingOut ?? exit, pendingErr ?? exit).ConfigureAwait(false);
                if (ReferenceEquals(finished, exit)) break;
                await finished.ConfigureAwait(false); // throws the cap error (or the cancellation); a reader that ended normally is simply done
                if (ReferenceEquals(finished, pendingOut)) pendingOut = null;
                else pendingErr = null;
            }

            await exit.ConfigureAwait(false);
            return new QaProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), TimedOut: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopAsync(process, timeout, stdout, stderr).ConfigureAwait(false);
            return new QaProcessResult(-1, string.Empty, string.Empty, TimedOut: true);
        }
        catch
        {
            await StopAsync(process, timeout, stdout, stderr).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Kills the child (before the readers are awaited, or the await could hang on a held pipe), cancels the readers and observes both so a faulted one is never left unobserved.</summary>
    private static async Task StopAsync(Process process, CancellationTokenSource timeout, Task<string>? stdout, Task<string>? stderr)
    {
        Kill(process);
        await timeout.CancelAsync().ConfigureAwait(false);
        foreach (Task? reader in new Task?[] { stdout, stderr })
        {
            if (reader is null) continue;
            try
            {
                await reader.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // already surfaced through the main path (or a cancellation); awaiting only marks the task observed
            }
        }
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        char[] buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (sb.Length + read > MaxOutputChars) throw new PlayerbotQaPublishException(OutputCapMessage, transient: false);
            sb.Append(buffer, 0, read);
        }

        return sb.ToString();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Record("playerbot.qa-gh-kill", ex); // a gh process that could not be killed is left running: counted
        }
    }
}

/// <summary>
/// The <c>gh</c> command-line transport of bot QA. Each call runs the resolved <c>gh</c> executable with an argument list (no shell), in a neutral working directory (the
/// temp folder), with a timeout (30 seconds by default), with <c>GITHUB_TOKEN</c> and <c>GH_TOKEN</c> removed from the child unless a token was handed in on purpose,
/// <c>GH_PROMPT_DISABLED</c> set so it never waits for input, and output parsed as JSON. Bodies go through a temp file (<c>--body-file</c>, deleted afterwards), never
/// the command line. The list is one <c>gh api</c> call per REST page of 100 (<see cref="ListIndexAsync"/>), each under its own timeout; an issue beyond
/// the listed pages or outside the label is found by <see cref="SearchByMarkerAsync"/> (<c>gh api search/issues</c>) or by number. A create is never resent: a timeout or failure throws a transient exception and the next pass's list settles it. One instance
/// serves one publish pass; calls are sequential. Failure text is redacted before it leaves.
/// </summary>
internal sealed partial class PlayerbotQaGhCliClient : IQaIssueTransport
{
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);
    private const int ItemsPerPage = 100;

    private readonly IQaProcessRunner _runner;
    private readonly string _executable;
    private readonly string _repository;
    private readonly PlayerbotQaGitHubOptions _options;
    private readonly string? _token;
    private readonly string _tempDirectory;
    private readonly TimeSpan _timeout;

    /// <param name="runner">Runs the child.</param>
    /// <param name="executable">The resolved <c>gh</c> path (see <see cref="ResolveExecutable"/>).</param>
    /// <param name="repository"><c>owner/repo</c>.</param>
    /// <param name="options">The reporter options (labels, list pages).</param>
    /// <param name="token">Handed to the child as <c>GH_TOKEN</c> when not null (only when the operator set <c>GhPassToken</c>); otherwise both token variables are removed.</param>
    /// <param name="tempDirectory">Where body files are made (default: the temp folder, which is also the child's working directory).</param>
    /// <param name="timeout">Per call (default 30 seconds).</param>
    public PlayerbotQaGhCliClient(IQaProcessRunner runner, string executable, string repository, PlayerbotQaGitHubOptions options, string? token = null,
        string? tempDirectory = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentNullException.ThrowIfNull(options);
        if (!PlayerbotQaGitHubOptions.IsValidRepository(repository)) throw new ArgumentException("not an owner/repo", nameof(repository));
        _runner = runner;
        _executable = executable;
        _repository = repository;
        _options = options;
        _token = string.IsNullOrEmpty(token) ? null : token;
        _tempDirectory = tempDirectory ?? Path.GetTempPath();
        _timeout = timeout ?? CallTimeout;
    }

    // Issues this instance created: the search index lags behind a create by seconds to minutes, so the instance answers for its own issues itself.
    private readonly List<(string Body, QaIssueRef Issue)> _created = [];
    private bool _searchRateLimited;

    public bool SupportsSearch => true;

    public void Dispose()
    {
    }

    /// <summary>
    /// The full path of the <c>gh</c> executable: <paramref name="configured"/> when it is a path (it must exist), otherwise the first match in the PATH folders (on Windows
    /// only <c>.exe</c> and <c>.com</c>: a batch file would need a shell). Null when not found. The current directory is never searched.
    /// </summary>
    internal static string? ResolveExecutable(string configured, Func<string, string?> getEnvironmentVariable, Func<string, bool> fileExists, bool windows)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        bool bare = configured.IndexOfAny(['/', '\\']) < 0 && !Path.IsPathRooted(configured);
        string[] names = windows && !configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !configured.EndsWith(".com", StringComparison.OrdinalIgnoreCase)
            ? [configured + ".exe", configured + ".com"]
            : [configured];
        if (!bare)
        {
            if (!Path.IsPathRooted(configured)) return null;
            foreach (string name in names)
            {
                if (fileExists(name)) return name;
            }

            return null;
        }

        string path = getEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Path.IsPathRooted(folder)) continue;
            foreach (string name in names)
            {
                string candidate = Path.Combine(folder, name);
                if (fileExists(candidate)) return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The labelled issues (state all), one REST page of <see cref="ItemsPerPage"/> per <c>gh api</c> call, each call with its own timeout, up to
    /// <see cref="PlayerbotQaGitHubOptions.MaxListPages"/> pages. The index is complete when a page came back short. A single <c>gh issue list --limit N</c>
    /// fetched every body in one child under one timeout: with 600+ issues (about 1.9 MB of bodies) it took 20-40 s and timed out on most passes (live log 2026-10-17).
    /// </summary>
    public async Task<QaIssueIndex> ListIndexAsync(CancellationToken cancellationToken)
    {
        string label = _options.Labels.Length > 0 ? _options.Labels[0] : "bot-qa";
        int pages = Math.Max(1, _options.MaxListPages);
        var items = new List<(QaIssueRef Issue, string Body)>();
        bool complete = false;
        for (int page = 1; page <= pages; page++)
        {
            string path = string.Create(CultureInfo.InvariantCulture,
                $"repos/{_repository}/issues?labels={Uri.EscapeDataString(label)}&state=all&per_page={ItemsPerPage}&page={page}");
            QaProcessResult result = await RunAsync(["api", "--method", "GET", path], "issue list", cancellationToken).ConfigureAwait(false);
            using JsonDocument doc = Parse(result.StdOut);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new PlayerbotQaPublishException("gh api did not print a JSON array for the issue list", transient: false);
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || item.TryGetProperty("pull_request", out _)) continue; // the REST list includes pull requests
                if (ToRestRef(item) is { } issue) items.Add((issue, BodyOf(item)));
            }

            if (doc.RootElement.GetArrayLength() < ItemsPerPage)
            {
                complete = true;
                break;
            }
        }

        var index = new QaIssueIndex(complete);
        foreach ((QaIssueRef issue, string body) in items) index.Add(issue, body);
        return index;
    }

    public async Task<(QaIssueRef Issue, string Body)?> GetIssueAsync(int number, CancellationToken cancellationToken)
    {
        QaProcessResult result = await RunAsync(["issue", "view", number.ToString(CultureInfo.InvariantCulture), "--repo", _repository, "--json", "number,state,stateReason,body,url"],
            "issue view", cancellationToken, allowNotFound: true).ConfigureAwait(false);
        if (result.ExitCode != 0) return null;
        using JsonDocument doc = Parse(result.StdOut);
        return ToRef(doc.RootElement) is { } issue ? (issue, BodyOf(doc.RootElement)) : null;
    }

    /// <summary>
    /// The issue whose body holds the marker of <paramref name="id"/>, by <c>gh api search/issues</c> (same query as the REST client: any label, so an issue whose label was
    /// removed by hand is found, and an issue beyond <see cref="PlayerbotQaGitHubOptions.MaxListPages"/> is found too). The query goes as one <c>-f q=...</c> argument (gh
    /// encodes it; no shell), the marker phrase quoted with its quotes and backslashes escaped. Null means "searched, none": an unknown outcome is never null, it throws a transient
    /// exception so the finding stays pending instead of being created twice: a rate-limited search (the first one is remembered, later ones in this instance do not call
    /// gh), a failed or timed-out call, and a reply GitHub flags <c>incomplete_results</c> (timed out on its side). An issue this instance created is answered from memory.
    /// </summary>
    public async Task<QaIssueRef?> SearchByMarkerAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        string marker = PlayerbotQaIssueComposer.MarkerOf(id);
        foreach ((string body, QaIssueRef issue) in _created)
        {
            if (body.Contains(marker, StringComparison.Ordinal)) return issue;
        }

        if (_searchRateLimited) throw new PlayerbotQaPublishException("gh search is rate limited; the finding stays pending", transient: true);
        string phrase = EscapePhrase("arcanecore-bot-qa:" + id);
        string query = $"repo:{_repository} is:issue in:body \"{phrase}\"";
        QaProcessResult result;
        try
        {
            result = await RunAsync(["api", "--method", "GET", "search/issues", "-f", "q=" + query, "-f", "per_page=30"], "issue search", cancellationToken).ConfigureAwait(false);
        }
        catch (PlayerbotQaPublishException ex) when (ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
        {
            _searchRateLimited = true; // the search quota (30 per minute) is gone: stop asking for the rest of this pass
            throw;
        }

        using JsonDocument doc = Parse(result.StdOut);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new PlayerbotQaPublishException("gh api did not print a search result", transient: true);
        }

        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.TryGetProperty("pull_request", out _)) continue;
            if (BodyOf(item).Contains(marker, StringComparison.Ordinal) && ToRestRef(item) is { } issue) return issue;
        }

        if (root.TryGetProperty("incomplete_results", out JsonElement incomplete) && incomplete.ValueKind == JsonValueKind.True)
        {
            throw new PlayerbotQaPublishException("the GitHub search result was incomplete; the finding stays pending", transient: true);
        }

        return null;
    }

    /// <summary>Escapes the characters that end or break a quoted search phrase (backslash, double quote, line breaks).</summary>
    internal static string EscapePhrase(string text)
        => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');

    public async Task<QaIssueRef> CreateIssueAsync(string title, string body, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        string file = await WriteBodyAsync(body, cancellationToken).ConfigureAwait(false);
        try
        {
            var args = new List<string> { "issue", "create", "--repo", _repository, "--title", title, "--body-file", file };
            foreach (string label in labels)
            {
                args.Add("--label");
                args.Add(label);
            }

            QaProcessResult result;
            try
            {
                result = await RunAsync(args, "issue create", cancellationToken).ConfigureAwait(false);
            }
            catch (PlayerbotQaPublishException ex) when (!ex.Transient && ex.Message == QaProcessRunner.OutputCapMessage)
            {
                // The child was killed mid-run, so the issue may already exist: transient, so the pass stops and the next list settles it (a permanent failure would be retried as a new create).
                throw new PlayerbotQaPublishException(ex.Message, transient: true, ex);
            }

            // gh prints the new issue's URL as the last line.
            string last = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;
            Match m = IssueUrlPattern().Match(last);
            if (!m.Success || !int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number <= 0)
            {
                // The issue may exist: transient, so the pass stops and the next list settles it.
                throw new PlayerbotQaPublishException("gh created an issue but printed no issue URL", transient: true);
            }

            var created = new QaIssueRef(number, true, last);
            _created.Add((body, created));
            return created;
        }
        finally
        {
            DeleteQuietly(file);
        }
    }

    public async Task CommentAsync(int number, string body, CancellationToken cancellationToken)
    {
        string file = await WriteBodyAsync(body, cancellationToken).ConfigureAwait(false);
        try
        {
            await RunAsync(["issue", "comment", number.ToString(CultureInfo.InvariantCulture), "--repo", _repository, "--body-file", file], "issue comment", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteQuietly(file);
        }
    }

    public async Task ReopenAsync(int number, CancellationToken cancellationToken)
        => await RunAsync(["issue", "reopen", number.ToString(CultureInfo.InvariantCulture), "--repo", _repository], "issue reopen", cancellationToken).ConfigureAwait(false);

    public async Task CloseAsDuplicateAsync(int number, CancellationToken cancellationToken)
        => await RunAsync(["issue", "close", number.ToString(CultureInfo.InvariantCulture), "--repo", _repository, "--reason", "not planned"], "issue close", cancellationToken).ConfigureAwait(false);

    public async Task CloseAsCompletedAsync(int number, CancellationToken cancellationToken)
        => await RunAsync(["issue", "close", number.ToString(CultureInfo.InvariantCulture), "--repo", _repository, "--reason", "completed"], "issue close", cancellationToken).ConfigureAwait(false);

    public async Task EditBodyAsync(int number, string body, CancellationToken cancellationToken)
    {
        string file = await WriteBodyAsync(body, cancellationToken).ConfigureAwait(false);
        try
        {
            await RunAsync(["issue", "edit", number.ToString(CultureInfo.InvariantCulture), "--repo", _repository, "--body-file", file], "issue edit", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteQuietly(file);
        }
    }

    private async Task<QaProcessResult> RunAsync(IReadOnlyList<string> arguments, string what, CancellationToken cancellationToken, bool allowNotFound = false)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GITHUB_TOKEN"] = null,
            ["GH_TOKEN"] = _token,
            ["GH_PROMPT_DISABLED"] = "1",
            ["GH_NO_UPDATE_NOTIFIER"] = "1",
            ["NO_COLOR"] = "1",
        };
        var request = new QaProcessRequest(_executable, arguments, _tempDirectory, environment, _timeout);
        QaProcessResult result;
        try
        {
            result = await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PlayerbotQaPublishException($"gh {what} timed out", transient: true);
        }

        if (result.TimedOut) throw new PlayerbotQaPublishException($"gh {what} timed out after {(int)_timeout.TotalSeconds} s", transient: true);
        if (result.ExitCode == 0) return result;
        if (allowNotFound && result.StdErr.Contains("Could not resolve", StringComparison.OrdinalIgnoreCase)) return result;
        string text = PlayerbotQaRedactor.RedactSecrets(result.StdErr, _token).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length > 300) text = text[..300] + "...";
        bool transient = text.Contains("rate limit", StringComparison.OrdinalIgnoreCase) || text.Contains("HTTP 5", StringComparison.Ordinal)
            || text.Contains("timeout", StringComparison.OrdinalIgnoreCase) || text.Contains("connection", StringComparison.OrdinalIgnoreCase);
        throw new PlayerbotQaPublishException($"gh {what} failed with exit code {result.ExitCode}: {text}", transient);
    }

    private async Task<string> WriteBodyAsync(string body, CancellationToken cancellationToken)
    {
        string path = Path.Combine(_tempDirectory, "arcanecore-qa-" + Guid.NewGuid().ToString("N") + ".md");
        try
        {
            await File.WriteAllTextAsync(path, body, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuietly(path);
            throw new PlayerbotQaPublishException("the body file for gh could not be written: " + ex.GetType().Name, transient: false, ex);
        }

        return path;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best-effort removal of a temporary body file; a leftover file is harmless
        }
    }

    private static JsonDocument Parse(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new PlayerbotQaPublishException("gh printed no JSON", transient: false, ex);
        }
    }

    private static string BodyOf(JsonElement item)
        => item.TryGetProperty("body", out JsonElement body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? string.Empty : string.Empty;

    private static QaIssueRef? ToRef(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("number", out JsonElement number) || !number.TryGetInt32(out int n) || n <= 0) return null;
        bool open = item.TryGetProperty("state", out JsonElement state) && state.ValueKind == JsonValueKind.String
            && string.Equals(state.GetString(), "open", StringComparison.OrdinalIgnoreCase);
        string url = item.TryGetProperty("url", out JsonElement u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? string.Empty : string.Empty;
        // gh prints the reason in capitals with an underscore (NOT_PLANNED, COMPLETED).
        string? reason = item.TryGetProperty("stateReason", out JsonElement why) && why.ValueKind == JsonValueKind.String ? why.GetString() : null;
        return new QaIssueRef(n, open, url, reason);
    }

    /// <summary>An issue of the REST list (<c>gh api</c>): lower-case <c>state</c>, <c>state_reason</c>, <c>html_url</c>.</summary>
    private static QaIssueRef? ToRestRef(JsonElement item)
    {
        if (!item.TryGetProperty("number", out JsonElement number) || !number.TryGetInt32(out int n) || n <= 0) return null;
        bool open = item.TryGetProperty("state", out JsonElement state) && state.ValueKind == JsonValueKind.String
            && string.Equals(state.GetString(), "open", StringComparison.OrdinalIgnoreCase);
        string url = item.TryGetProperty("html_url", out JsonElement u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? string.Empty : string.Empty;
        string? reason = item.TryGetProperty("state_reason", out JsonElement why) && why.ValueKind == JsonValueKind.String ? why.GetString() : null;
        return new QaIssueRef(n, open, url, reason);
    }

    [GeneratedRegex(@"^https://[A-Za-z0-9.\-]+/[^/\s]+/[^/\s]+/issues/(?<n>[0-9]{1,9})$", RegexOptions.CultureInvariant)]
    private static partial Regex IssueUrlPattern();
}
