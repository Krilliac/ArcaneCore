using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary><c>qa/pending-issues/&lt;id&gt;.json</c>: what a dry run would send.</summary>
internal sealed record QaPendingIssueFile(string Fingerprint, string Repository, string Title, string Marker, IReadOnlyList<string> Labels, long Count,
    int DistinctBots, DateTime GeneratedUtc);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(QaPendingIssueFile))]
internal sealed partial class QaPendingJsonContext : JsonSerializerContext;

/// <summary>The creates and comments of the last hour, which the hourly caps count; persisted so a restart loop cannot flood GitHub. Thread-safe.</summary>
internal sealed class QaLedger
{
    private readonly object _gate = new();
    private readonly List<DateTime> _creates = [];
    private readonly List<DateTime> _comments = [];
    private readonly List<DateTime> _spellCreates = [];

    internal bool CanCreate(DateTime now, int max)
    {
        lock (_gate)
        {
            Prune(_creates, now);
            return _creates.Count < max;
        }
    }

    internal bool CanComment(DateTime now, int max)
    {
        lock (_gate)
        {
            Prune(_comments, now);
            return _comments.Count < max;
        }
    }

    internal void RecordCreate(DateTime now)
    {
        lock (_gate) _creates.Add(now);
    }

    /// <summary>Whether another spell issue may be created within the rolling 24 hours.</summary>
    internal bool CanCreateSpell(DateTime now, int max)
    {
        lock (_gate)
        {
            _spellCreates.RemoveAll(at => now - at >= TimeSpan.FromHours(24));
            return _spellCreates.Count < max;
        }
    }

    internal void RecordSpellCreate(DateTime now)
    {
        lock (_gate) _spellCreates.Add(now);
    }

    internal void RecordComment(DateTime now)
    {
        lock (_gate) _comments.Add(now);
    }

    internal QaLedgerState Export()
    {
        lock (_gate) return new QaLedgerState([.. _creates], [.. _comments], [.. _spellCreates]);
    }

    internal void Restore(QaLedgerState? state)
    {
        lock (_gate)
        {
            _creates.Clear();
            _comments.Clear();
            _spellCreates.Clear();
            if (state is null) return;
            _creates.AddRange(state.CreatesUtc ?? []);
            _comments.AddRange(state.CommentsUtc ?? []);
            _spellCreates.AddRange(state.SpellCreatesUtc ?? []);
        }
    }

    private static void Prune(List<DateTime> times, DateTime now) => times.RemoveAll(at => now - at >= TimeSpan.FromHours(1));
}

/// <summary>
/// Reports eligible findings: a dry run (reporter enabled, <c>Publish</c> off) writes the would-be issues to <c>qa/pending-issues/</c> and makes no
/// network call (no client is even created); publishing finds the finding's issue by its exact marker (open or closed), comments on an open one
/// (at most once per interval and under the hourly comment cap), leaves a closed one alone (or reopens it with <c>ReopenClosed</c>), and creates an
/// issue when none exists (under the hourly create cap). A per-fingerprint lock makes find-then-create atomic within the process. A pass lists the labelled
/// issues once (<see cref="IQaIssueTransport.ListIndexAsync"/>) and finds every marker in that index; a finding with no known issue is skipped before any
/// lookup while the create cap is exhausted; the lookups outside the index (an issue by number, the search) are capped at <see cref="MaxLookupsPerPass"/> per
/// pass and <see cref="LookupSpacing"/> apart, a finding that would need one more stays pending. The transport is the REST client or the <c>gh</c> adapter
/// (<see cref="PlayerbotQaGitHubOptions.Transport"/>). Every failure leaves
/// the finding pending (<see cref="QaPublicationState.Failed"/>, retried next pass); nothing is thrown out of <see cref="RunAsync"/> except cancellation.
/// The token is read from the environment for each pass and lives in a local variable and the pass's client only.
/// Thread-safety: <see cref="RunAsync"/> may run concurrently; shared state is the lock dictionary, the ledger and the state sink.
/// </summary>
internal sealed class PlayerbotQaPublisher
{
    private readonly PlayerbotQaOptions _options;
    private readonly string _directory;
    private readonly IQaPublicationState _state;
    private readonly PlayerbotQaRedactor _redactor;
    private readonly ILogger _logger;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<string, IQaIssueTransport> _createClient;
    private readonly Func<string?, IQaIssueTransport?> _createGhClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _time;
    private readonly string _build;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private int _warnedToken;
    private int _warnedGh;
    private int _warnedDirectory;

    public PlayerbotQaPublisher(PlayerbotQaOptions options, string directory, IQaPublicationState state, PlayerbotQaRedactor redactor, ILogger logger,
        Func<string, string?>? getEnvironmentVariable = null, Func<string, IQaIssueTransport>? createClient = null, TimeProvider? time = null, string? build = null,
        Func<string?, IQaIssueTransport?>? createGhClient = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _options = options;
        _directory = directory;
        _state = state;
        _redactor = redactor;
        _logger = logger;
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        _createClient = createClient ?? (token => new PlayerbotQaGitHubClient(null, options.GitHub.Repository, token, options.GitHub));
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
        _createGhClient = createGhClient ?? (token =>
        {
            string? executable = PlayerbotQaGhCliClient.ResolveExecutable(options.GitHub.GhPath, _getEnvironmentVariable, File.Exists, OperatingSystem.IsWindows());
            return executable is null ? null : new PlayerbotQaGhCliClient(new QaProcessRunner(), executable, options.GitHub.Repository, options.GitHub, token);
        });
        _time = time ?? TimeProvider.System;
        _build = build ?? PlayerbotQaIssueComposer.BuildVersion();
    }

    /// <summary>The most lookups outside the issue list (an issue by number, the REST search) one pass makes.</summary>
    internal const int MaxLookupsPerPass = 5;

    /// <summary>The wait between two such lookups of one pass (the search API allows 30 a minute).</summary>
    internal static readonly TimeSpan LookupSpacing = TimeSpan.FromSeconds(2);

    /// <summary>The hourly caps' memory.</summary>
    internal QaLedger Ledger { get; } = new();

    internal async Task RunAsync(IReadOnlyList<QaFindingView> findings, CancellationToken cancellationToken)
    {
        PlayerbotQaGitHubOptions git = _options.GitHub;
        if (!git.Enabled) return;
        QaFindingView[] eligible = [.. findings.Where(Eligible)
            .OrderBy(f => f.Publication.State == QaPublicationState.Failed ? 1 : 0).ThenByDescending(f => f.Severity).ThenBy(f => f.Id, StringComparer.Ordinal)];
        if (eligible.Length == 0 && _state.PendingDuplicates().Count == 0) return;
        if (!git.Publish)
        {
            DryRun(eligible);
            return;
        }

        bool gh = git.Transport == PlayerbotQaGitHubTransport.GhCli;
        // REST needs the token; gh uses its own login and only gets the token when GhPassToken is on.
        bool needsToken = git.RequiresToken();
        string? token = needsToken ? _getEnvironmentVariable(git.TokenEnvironmentVariable) : null;
        if ((needsToken && string.IsNullOrEmpty(token)) || !PlayerbotQaGitHubOptions.IsValidRepository(git.Repository))
        {
            if (Interlocked.Exchange(ref _warnedToken, 1) == 0)
            {
                _logger.LogWarning("Bot QA publishing is on but the environment variable {Variable} is empty or the repository is invalid; findings stay pending", git.TokenEnvironmentVariable);
            }

            return;
        }

        PlayerbotQaRedactor redactor = string.IsNullOrEmpty(token) ? _redactor : _redactor.WithToken(token);
        IQaIssueTransport? transport = gh ? _createGhClient(token) : _createClient(token!);
        if (transport is null)
        {
            if (Interlocked.Exchange(ref _warnedGh, 1) == 0)
            {
                _logger.LogWarning("Bot QA publishing through gh is on but the executable {Path} was not found; findings stay pending", git.GhPath);
            }

            return;
        }

        var pass = new Pass(transport, redactor);
        try
        {
            foreach (QaFindingView finding in eligible)
            {
                if (!await PublishOneAsync(finding.Id, pass, cancellationToken).ConfigureAwait(false)) return;
            }

            // Closing duplicates comes last: the comments of findings that are still happening have first call on the hourly comment budget, which the closes share.
            await CloseDuplicatesAsync(pass, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            transport.Dispose();
        }
    }

    /// <summary>What one publish pass shares: the transport, the redactor, the issue list (read at most once) and the lookup budget.</summary>
    private sealed class Pass(IQaIssueTransport transport, PlayerbotQaRedactor redactor)
    {
        public IQaIssueTransport Transport { get; } = transport;

        public PlayerbotQaRedactor Redactor { get; } = redactor;

        public QaIssueIndex? Index;

        public PlayerbotQaPublishException? IndexFailure;

        public int Lookups;

        /// <summary>Spell issues created in this pass (<see cref="PlayerbotQaGitHubOptions.MaxSpellIssuesPerPass"/>).</summary>
        public int SpellCreates;

        public bool IncompleteLogged;
    }

    private static async Task<QaIssueIndex> IndexOfAsync(Pass pass, CancellationToken cancellationToken)
    {
        if (pass.Index is not null) return pass.Index;
        if (pass.IndexFailure is not null) throw new PlayerbotQaPublishException(pass.IndexFailure.Message, pass.IndexFailure.Transient, pass.IndexFailure);
        try
        {
            pass.Index = await pass.Transport.ListIndexAsync(cancellationToken).ConfigureAwait(false);
            return pass.Index;
        }
        catch (PlayerbotQaPublishException ex)
        {
            pass.IndexFailure = ex;
            throw;
        }
    }

    /// <summary>Whether one more lookup outside the list is allowed; if so, spaces it from the previous one.</summary>
    private async Task<bool> TakeLookupAsync(Pass pass, CancellationToken cancellationToken)
    {
        if (pass.Lookups >= MaxLookupsPerPass) return false;
        if (pass.Lookups > 0) await _delay(LookupSpacing, cancellationToken).ConfigureAwait(false);
        pass.Lookups++;
        return true;
    }

    private bool Eligible(QaFindingView f)
    {
        PlayerbotQaGitHubOptions git = _options.GitHub;
        // A spell gap is reported on its first real occurrence (a bot cast it); a gap only the start-up coverage lists (Count 0) stays in the
        // report's Spell coverage section and is never published on its own, so a first deploy cannot open one issue per uncast spell.
        if (f.Count < (QaKinds.Of(f.Kind).SingleOccurrence ? 1 : git.MinOccurrences)) return false;
        if (f.Kind == QaSignalKind.SpellUnsupported && QaSpellGaps.IsAdvisory(f.Reason)) return false; // a DUMMY aura nothing seems to read: a lead, listed only
        if (f.Cause == QaCause.OutsideHome) return false; // expected for a homed bot (PlayerbotQaClassifier); the report lists it
        if (!f.BotIndependent && f.DistinctBots < git.MinDistinctBots) return false;
        return !(f.Publication.State == QaPublicationState.KnownClosed && !git.ReopenClosed);
    }

    /// <summary>Publish one finding; false when the pass should stop (a transient failure: the rest would fail the same way).</summary>
    private async Task<bool> PublishOneAsync(string id, Pass pass, CancellationToken cancellationToken)
    {
        PlayerbotQaGitHubOptions git = _options.GitHub;
        IQaIssueTransport client = pass.Transport;
        PlayerbotQaRedactor redactor = pass.Redactor;
        SemaphoreSlim gate = _gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        QaFindingView? current = null;
        _state.Pin(id); // not aged out, evicted or lost to a merge while this call awaits GitHub; a merge still leaves the outcome a place to go
        try
        {
            current = _state.Get(id);
            if (current is null) return true;
            DateTime now = _time.GetUtcNow().UtcDateTime;
            if (current.Publication.State == QaPublicationState.Published && current.OccurrencesReported >= current.Count) return true;
            if (current.Publication.State == QaPublicationState.KnownClosed && !git.ReopenClosed) return true;

            long freshCount = current.Count - current.OccurrencesReported;
            bool commentDue = freshCount > 0 && (current.LastCommentUtc is not { } lastComment || now - lastComment >= TimeSpan.FromHours(git.CommentIntervalHours))
                && Ledger.CanComment(now, git.MaxCommentsPerHour);
            if (current.Publication.Issue <= 0)
            {
                // No known issue: only a create can make progress, and the cap forbids it, so nothing is looked up.
                if (!Ledger.CanCreate(now, git.MaxIssuesPerHour))
                {
                    _logger.LogInformation("Bot QA: the hourly issue cap ({Cap}) is reached; {Id} stays pending", git.MaxIssuesPerHour, current.ShortId);
                    return true;
                }

                if (!SpellCreateAllowed(current, pass, now)) return true;
            }
            else if (current.Publication.State == QaPublicationState.Published && !commentDue)
            {
                return true; // its issue is known and nothing is due: no call is needed
            }

            QaIssueIndex index = await IndexOfAsync(pass, cancellationToken).ConfigureAwait(false);
            QaIssueRef? found = index.ByMarker(id);
            // A finding that holds issue #N and finds #N in the list has its issue, whatever marker its body carries (an issue handed over by a merged finding has that finding's).
            // A failed attempt keeps the number too: it holds the issue as long as the body still carries some finding's marker (a person editing the marker away frees it).
            if (found is null && current.Publication.Issue > 0 && (current.Publication.Pinned || index.HasMarker(current.Publication.Issue))) found = index.ByNumber(current.Publication.Issue);
            string marker = PlayerbotQaIssueComposer.MarkerOf(id);
            if (found is null && current.Publication.Issue > 0 && index.ByNumber(current.Publication.Issue) is null)
            {
                if (!await TakeLookupAsync(pass, cancellationToken).ConfigureAwait(false)) return true;
                var known = await client.GetIssueAsync(current.Publication.Issue, cancellationToken).ConfigureAwait(false);
                if (known is { } k && k.Body.Contains(marker, StringComparison.Ordinal)) found = k.Issue;
            }

            if (found is null && client.SupportsSearch)
            {
                if (!await TakeLookupAsync(pass, cancellationToken).ConfigureAwait(false)) return true;
                found = await client.SearchByMarkerAsync(id, cancellationToken).ConfigureAwait(false);
            }

            if (found is null && !client.SupportsSearch && !index.Complete)
            {
                // The list was cut short and nothing else can look: creating now could duplicate an issue that is beyond the list.
                if (!pass.IncompleteLogged)
                {
                    pass.IncompleteLogged = true;
                    _logger.LogWarning("Bot QA: the issue list was cut at {Pages} pages; findings with no known issue stay pending (raise GitHub:MaxListPages)", git.MaxListPages);
                }

                return true;
            }

            if (found is null)
            {
                if (!Ledger.CanCreate(now, git.MaxIssuesPerHour))
                {
                    _logger.LogInformation("Bot QA: the hourly issue cap ({Cap}) is reached; {Id} stays pending", git.MaxIssuesPerHour, current.ShortId);
                    return true;
                }

                if (!SpellCreateAllowed(current, pass, now)) return true;
                QaIssueDraft draft = PlayerbotQaIssueComposer.Compose(current, LabelsFor(current), _build, redactor);
                QaIssueRef created = await client.CreateIssueAsync(draft.Title, draft.Body, draft.Labels, cancellationToken).ConfigureAwait(false);
                Ledger.RecordCreate(now);
                if (QaKinds.Of(current.Kind).SpellKind)
                {
                    pass.SpellCreates++;
                    Ledger.RecordSpellCreate(now);
                }

                index.AddCreated(created, id);
                _state.Apply(id, new QaPublicationUpdate(new QaPublication(QaPublicationState.Published, created.Number), now, current.Count));
                _logger.LogInformation("Bot QA: opened issue #{Number} for finding {Id}", created.Number, current.ShortId);
                return true;
            }

            if (!found.Open)
            {
                if (!git.ReopenClosed)
                {
                    _state.Apply(id, new QaPublicationUpdate(new QaPublication(QaPublicationState.KnownClosed, found.Number), null, current.Count));
                    _logger.LogInformation("Bot QA: finding {Id} has a closed issue (#{Number}); it is left closed", current.ShortId, found.Number);
                    return true;
                }

                await client.ReopenAsync(found.Number, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Bot QA: reopened issue #{Number} for finding {Id}", found.Number, current.ShortId);
            }

            if (commentDue)
            {
                await client.CommentAsync(found.Number, PlayerbotQaIssueComposer.Comment(current, freshCount, redactor), cancellationToken).ConfigureAwait(false);
                Ledger.RecordComment(now);
                _state.Apply(id, new QaPublicationUpdate(new QaPublication(QaPublicationState.Published, found.Number), now, current.Count));
            }
            else
            {
                _state.Apply(id, new QaPublicationUpdate(new QaPublication(QaPublicationState.Published, found.Number)));
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PlayerbotQaPublishException ex)
        {
            Fail(id, current, redactor.Redact(ex.Message));
            return !ex.Transient;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail(id, current, ex.GetType().Name);
            return true;
        }
        finally
        {
            _state.Unpin(id);
            gate.Release();
        }
    }

    /// <summary>
    /// Close the issues of findings that were merged into another one: comment with a link to the surviving issue, then close as not planned. One
    /// failure is logged and retried next pass; false when the pass should stop (a transient failure). Does nothing with <c>CloseAbsorbedDuplicates</c> off.
    /// </summary>
    private async Task<bool> CloseDuplicatesAsync(Pass pass, CancellationToken cancellationToken)
    {
        PlayerbotQaGitHubOptions git = _options.GitHub;
        if (!git.CloseAbsorbedDuplicates) return true;
        foreach (QaAbsorbed duplicate in _state.PendingDuplicates())
        {
            QaFindingView? survivor = _state.Get(duplicate.Survivor);
            if (survivor is null || !survivor.Publication.Pinned || survivor.Publication.Issue <= 0) continue;
            if (survivor.Publication.Issue == duplicate.Issue)
            {
                _state.MarkDuplicateHandled(duplicate.Id);
                continue;
            }

            try
            {
                QaIssueIndex index = await IndexOfAsync(pass, cancellationToken).ConfigureAwait(false);
                QaIssueRef? issue = index.ByNumber(duplicate.Issue);
                if (issue is null)
                {
                    // Not in the list (deleted, unlabelled, or beyond a cut-short list): unknown, so left alone and kept pending.
                    _logger.LogInformation("Bot QA: duplicate issue #{Number} is not in the issue list; left alone", duplicate.Issue);
                    continue;
                }

                if (!issue.Open)
                {
                    _state.MarkDuplicateHandled(duplicate.Id); // already closed by a person: nothing to say
                    continue;
                }

                // Never close an open issue in favour of one that is closed or unknown.
                if (index.ByNumber(survivor.Publication.Issue) is not { Open: true })
                {
                    _logger.LogInformation("Bot QA: issue #{Number} is a duplicate of #{Survivor}, which is not open; left open", duplicate.Issue, survivor.Publication.Issue);
                    continue;
                }

                DateTime now = _time.GetUtcNow().UtcDateTime;
                if (!duplicate.Commented)
                {
                    if (!Ledger.CanComment(now, git.MaxCommentsPerHour))
                    {
                        _logger.LogInformation("Bot QA: the hourly comment cap ({Cap}) is reached; the duplicate issue #{Number} is closed on a later pass", git.MaxCommentsPerHour, duplicate.Issue);
                        return true;
                    }

                    await pass.Transport.CommentAsync(duplicate.Issue, PlayerbotQaIssueComposer.DuplicateComment(survivor.Publication.Issue), cancellationToken).ConfigureAwait(false);
                    Ledger.RecordComment(now);
                    _state.MarkDuplicateCommented(duplicate.Id); // a failed close below retries the close only, never the comment
                }

                await pass.Transport.CloseAsDuplicateAsync(duplicate.Issue, cancellationToken).ConfigureAwait(false);
                _state.MarkDuplicateHandled(duplicate.Id);
                _logger.LogInformation("Bot QA: closed issue #{Number} as a duplicate of #{Survivor}", duplicate.Issue, survivor.Publication.Issue);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (PlayerbotQaPublishException ex)
            {
                _logger.LogWarning("Bot QA could not close duplicate issue #{Number}: {Reason}", duplicate.Issue, pass.Redactor.Redact(ex.Message));
                if (ex.Transient) return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The spell caps on top of the hourly one: at most <see cref="PlayerbotQaGitHubOptions.MaxSpellIssuesPerPass"/> spell issues per pass and
    /// <see cref="PlayerbotQaGitHubOptions.MaxSpellIssuesPerDay"/> per rolling day. Other kinds are not limited by them.
    /// </summary>
    private bool SpellCreateAllowed(QaFindingView finding, Pass pass, DateTime now)
    {
        if (!QaKinds.Of(finding.Kind).SpellKind) return true;
        PlayerbotQaGitHubOptions git = _options.GitHub;
        if (pass.SpellCreates >= git.MaxSpellIssuesPerPass)
        {
            _logger.LogInformation("Bot QA: the per-pass spell issue cap ({Cap}) is reached; {Id} stays pending", git.MaxSpellIssuesPerPass, finding.ShortId);
            return false;
        }

        if (!Ledger.CanCreateSpell(now, git.MaxSpellIssuesPerDay))
        {
            _logger.LogInformation("Bot QA: the daily spell issue cap ({Cap}) is reached; {Id} stays pending", git.MaxSpellIssuesPerDay, finding.ShortId);
            return false;
        }

        return true;
    }

    /// <summary>The configured labels, plus the kind's own label when <see cref="PlayerbotQaGitHubOptions.KindLabels"/> is on (the first label stays the listing one).</summary>
    internal IReadOnlyList<string> LabelsFor(QaFindingView finding)
    {
        PlayerbotQaGitHubOptions git = _options.GitHub;
        if (!git.KindLabels) return git.Labels;
        string kind = QaKinds.Of(finding.Kind).GitHubLabel;
        return git.Labels.Contains(kind, StringComparer.OrdinalIgnoreCase) ? git.Labels : [.. git.Labels, kind];
    }

    private void Fail(string id, QaFindingView? current, string reason)
    {
        string text = reason.Length > 300 ? reason[..300] : reason;
        _state.Apply(id, new QaPublicationUpdate(new QaPublication(QaPublicationState.Failed, current?.Publication.Issue ?? 0, text)));
        _logger.LogWarning("Bot QA could not publish finding {Id}: {Reason}", id.Length > 8 ? id[..8] : id, text);
    }

    private void DryRun(IReadOnlyList<QaFindingView> eligible)
    {
        PlayerbotQaGitHubOptions git = _options.GitHub;
        string folder = Path.Combine(_directory, "pending-issues");
        DateTime now = _time.GetUtcNow().UtcDateTime;
        foreach (QaFindingView f in eligible)
        {
            if (f.Publication.Pinned) continue;
            if (f.Publication.State == QaPublicationState.DryRun && f.OccurrencesReported == f.Count) continue;
            QaIssueDraft draft = PlayerbotQaIssueComposer.Compose(f, LabelsFor(f), _build, _redactor);
            try
            {
                Directory.CreateDirectory(folder);
                WriteAtomic(Path.Combine(folder, f.Id + ".md"), draft.Body);
                WriteAtomic(Path.Combine(folder, f.Id + ".json"), JsonSerializer.Serialize(
                    new QaPendingIssueFile(f.Id, git.Repository, draft.Title, draft.Marker, draft.Labels, f.Count, f.DistinctBots, now), QaPendingJsonContext.Default.QaPendingIssueFile));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Interlocked.Exchange(ref _warnedDirectory, 1) == 0)
                {
                    _logger.LogWarning("Bot QA could not write {Folder} ({Type}); dry-run issues are not saved", folder, ex.GetType().Name);
                }

                continue;
            }

            _state.Apply(f.Id, new QaPublicationUpdate(new QaPublication(QaPublicationState.DryRun), null, f.Count));
            _logger.LogInformation("bot-qa dry run: would open '{Title}' ({Id}) -> qa/pending-issues/{Id}.md", draft.Title, f.Id, f.Id);
        }
    }

    private static void WriteAtomic(string path, string text)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}
