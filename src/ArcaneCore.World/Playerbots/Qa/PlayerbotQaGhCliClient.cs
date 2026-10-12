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
        try
        {
            process.StandardInput.Close();
            Task<string> stdout = ReadCappedAsync(process.StandardOutput, timeout.Token);
            Task<string> stderr = ReadCappedAsync(process.StandardError, timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return new QaProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), TimedOut: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            return new QaProcessResult(-1, string.Empty, string.Empty, TimedOut: true);
        }
        catch
        {
            Kill(process);
            throw;
        }
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        char[] buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (sb.Length + read > MaxOutputChars) throw new PlayerbotQaPublishException("gh printed more than 8 MiB", transient: false);
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
/// the command line. The list is one call (<c>gh issue list --label L --state all --json number,state,body,url --limit N</c>); there is no search, so an issue outside
/// the label is only found by number. A create is never resent: a timeout or failure throws a transient exception and the next pass's list settles it. One instance
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

    public bool SupportsSearch => false;

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

    public async Task<QaIssueIndex> ListIndexAsync(CancellationToken cancellationToken)
    {
        string label = _options.Labels.Length > 0 ? _options.Labels[0] : "bot-qa";
        int limit = Math.Max(1, _options.MaxListPages) * ItemsPerPage;
        QaProcessResult result = await RunAsync(["issue", "list", "--repo", _repository, "--label", label, "--state", "all", "--json", "number,state,body,url", "--limit",
            limit.ToString(CultureInfo.InvariantCulture)], "issue list", cancellationToken).ConfigureAwait(false);
        using JsonDocument doc = Parse(result.StdOut);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new PlayerbotQaPublishException("gh issue list did not print a JSON array", transient: false);
        var index = new QaIssueIndex(complete: doc.RootElement.GetArrayLength() < limit);
        foreach (JsonElement item in doc.RootElement.EnumerateArray())
        {
            if (ToRef(item) is { } issue) index.Add(issue, BodyOf(item));
        }

        return index;
    }

    public async Task<(QaIssueRef Issue, string Body)?> GetIssueAsync(int number, CancellationToken cancellationToken)
    {
        QaProcessResult result = await RunAsync(["issue", "view", number.ToString(CultureInfo.InvariantCulture), "--repo", _repository, "--json", "number,state,body,url"],
            "issue view", cancellationToken, allowNotFound: true).ConfigureAwait(false);
        if (result.ExitCode != 0) return null;
        using JsonDocument doc = Parse(result.StdOut);
        return ToRef(doc.RootElement) is { } issue ? (issue, BodyOf(doc.RootElement)) : null;
    }

    public Task<QaIssueRef?> SearchByMarkerAsync(string id, CancellationToken cancellationToken) => Task.FromResult<QaIssueRef?>(null);

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

            QaProcessResult result = await RunAsync(args, "issue create", cancellationToken).ConfigureAwait(false);
            // gh prints the new issue's URL as the last line.
            string last = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;
            Match m = IssueUrlPattern().Match(last);
            if (!m.Success || !int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number <= 0)
            {
                // The issue may exist: transient, so the pass stops and the next list settles it.
                throw new PlayerbotQaPublishException("gh created an issue but printed no issue URL", transient: true);
            }

            return new QaIssueRef(number, true, last);
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
        return new QaIssueRef(n, open, url);
    }

    [GeneratedRegex(@"^https://[A-Za-z0-9.\-]+/[^/\s]+/[^/\s]+/issues/(?<n>[0-9]{1,9})$", RegexOptions.CultureInvariant)]
    private static partial Regex IssueUrlPattern();
}
