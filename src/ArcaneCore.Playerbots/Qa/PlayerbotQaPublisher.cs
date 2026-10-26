using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArcaneCore.Kernel.Diagnostics;
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
    private readonly List<DateTime> _statusEdits = [];
    private readonly List<DateTime> _serverCreates = [];

    internal bool CanCreate(DateTime now, int max)
    {
        lock (_gate)
        {
            Prune(_creates, now);
            return _creates.Count < max;
        }
    }

    /// <summary>Whether another server-fault issue may be created within the rolling hour (its own cap, apart from <see cref="CanCreate"/>).</summary>
    internal bool CanCreateServer(DateTime now, int max)
    {
        lock (_gate)
        {
            Prune(_serverCreates, now);
            return _serverCreates.Count < max;
        }
    }

    internal void RecordServerCreate(DateTime now)
    {
        lock (_gate) _serverCreates.Add(now);
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

    /// <summary>Whether another live-status body edit may be made within the rolling hour.</summary>
    internal bool CanEditStatus(DateTime now, int max)
    {
        lock (_gate)
        {
            Prune(_statusEdits, now);
            return _statusEdits.Count < max;
        }
    }

    internal void RecordStatusEdit(DateTime now)
    {
        lock (_gate) _statusEdits.Add(now);
    }

    internal QaLedgerState Export()
    {
        lock (_gate) return new QaLedgerState([.. _creates], [.. _comments], [.. _spellCreates], [.. _statusEdits], [.. _serverCreates]);
    }

    internal void Restore(QaLedgerState? state)
    {
        lock (_gate)
        {
            _creates.Clear();
            _comments.Clear();
            _spellCreates.Clear();
            _statusEdits.Clear();
            _serverCreates.Clear();
            if (state is null) return;
            _creates.AddRange(state.CreatesUtc ?? []);
            _comments.AddRange(state.CommentsUtc ?? []);
            _spellCreates.AddRange(state.SpellCreatesUtc ?? []);
            _statusEdits.AddRange(state.StatusEditsUtc ?? []);
            _serverCreates.AddRange(state.ServerCreatesUtc ?? []);
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
    private readonly ConcurrentDictionary<string, byte> _cappedRegressions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _statusShown = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, byte> _quietCommented = new(); // issues whose quiet-close comment is posted: a failed close retries the close only
    private readonly DateTime _startedUtc;
    private int _quietCapLogged;
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
        _startedUtc = _time.GetUtcNow().UtcDateTime;
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
        QaFindingView[] eligible = [.. findings.Where(f => Eligible(f, findings))
            .OrderBy(f => QaKinds.Of(f.Kind).ServerSide ? 0 : 1).ThenBy(f => f.Publication.State == QaPublicationState.Failed ? 1 : 0).ThenByDescending(f => f.Severity).ThenBy(f => f.Id, StringComparer.Ordinal)];
        DateTime start = _time.GetUtcNow().UtcDateTime;
        if (eligible.Length == 0 && _state.PendingDuplicates().Count == 0 && !findings.Any(f => QuietFor(f, start) is not null)) return;
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

            // Closing duplicates and quiet issues comes last: the comments of findings that are still happening have first call on the hourly comment budget, which the closes share.
            if (!await CloseDuplicatesAsync(pass, cancellationToken).ConfigureAwait(false)) return;
            await CloseQuietAsync(findings, pass, cancellationToken).ConfigureAwait(false);
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

    private bool Eligible(QaFindingView f, IReadOnlyList<QaFindingView> all)
    {
        PlayerbotQaGitHubOptions git = _options.GitHub;
        // A spell gap is reported on its first real occurrence (a bot cast it); a gap only the start-up coverage lists (Count 0) stays in the
        // report's Spell coverage section and is never published on its own, so a first deploy cannot open one issue per uncast spell.
        if (f.Count < (QaKinds.Of(f.Kind).SingleOccurrence ? 1 : git.MinOccurrences)) return false;
        if (f.Kind == QaSignalKind.SpellUnsupported && QaSpellGaps.IsAdvisory(f.Reason)) return false; // a DUMMY aura nothing seems to read: a lead, listed only
        if (f.Kind == QaSignalKind.Anomaly && f.Goal == PlayerbotQaSpellMonitor.HeldTargetGoal
            && f.Reason.StartsWith(PlayerbotQaSpellMonitor.HeldTargetReason, StringComparison.Ordinal)) return false; // a creature held at its threshold: report-only
        if (f.Cause == QaCause.OutsideHome) return false; // expected for a homed bot (PlayerbotQaClassifier); the report lists it
        if (f.Kind == QaSignalKind.SharedSetAside && !f.Publication.HoldsIssue && CoveredByStall(f, all)) return false; // the stall finding carries the rich samples
        if (!f.BotIndependent && f.DistinctBots < git.MinDistinctBots) return false;
        return !(f.Publication.State == QaPublicationState.KnownClosed && !git.ReopenClosed && !IsRegression(f));
    }

    private static bool CoveredByStall(QaFindingView setAside, IReadOnlyList<QaFindingView> all)
        => setAside.Quest != 0
            ? all.Any(other => other.Kind == QaSignalKind.Stall && other.Quest == setAside.Quest && other.QuestIsGoal && other.Cause != QaCause.OutsideHome)
            : setAside.Target != 0 && all.Any(other => other.Kind == QaSignalKind.Stall && other.Target == setAside.Target && other.Cause != QaCause.OutsideHome);

    /// <summary>
    /// Whether <paramref name="f"/> recurs on the running build after its issue was closed on another one (<see cref="PlayerbotQaGitHubOptions.ReopenOnRegression"/>).
    /// A KnownClosed finding is compared with the build it was first observed closed on; a finding that was not KnownClosed yet (its issue was closed behind our back and is
    /// found closed just now) with the build its issue was last known open on (the last create, reopen or comment). With no such build recorded, or a recurrence on that same
    /// build (a person closed it while it was still happening), it is not a regression. The running build must have met the finding at least as often as a new finding is
    /// reported (<see cref="PlayerbotQaGitHubOptions.MinOccurrences"/>; one occurrence for a single-occurrence kind), and must be NEWER than the reference build: first run by this store
    /// after it (<see cref="IQaPublicationState.IsBuildNewer"/>). A rollback to an older build, or a build with no known first sighting, is not a regression; a finding whose issue was closed as not planned (<see cref="QaFindingView.NotPlanned"/>) never is.
    /// </summary>
    private bool IsRegression(QaFindingView f)
    {
        PlayerbotQaGitHubOptions git = _options.GitHub;
        if (f.NotPlanned || !git.ReopenOnRegression || f.CountOnBuild(_build) < (QaKinds.Of(f.Kind).SingleOccurrence ? 1 : git.MinOccurrences)) return false;
        bool closed = f.Publication.State == QaPublicationState.KnownClosed;
        string? reference = closed ? f.ClosedBuild : f.ReportedBuild;
        return reference is not null && !string.Equals(reference, _build, StringComparison.Ordinal) && _state.IsBuildNewer(_build, reference, closed ? f.ClosedBuildSeenUtc : f.ReportedBuildSeenUtc);
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
            bool statusDue = StatusDue(current, now);
            if (current.Publication.State == QaPublicationState.Published && current.OccurrencesReported >= current.Count && !statusDue) return true;
            if (current.Publication.State == QaPublicationState.KnownClosed && !git.ReopenClosed && !IsRegression(current)) return true;

            long freshCount = current.Count - current.OccurrencesReported;
            bool commentDue = freshCount > 0 && (current.LastCommentUtc is not { } lastComment || now - lastComment >= TimeSpan.FromHours(git.CommentIntervalHours))
                && Ledger.CanComment(now, git.MaxCommentsPerHour);
            if (current.Publication.Issue <= 0)
            {
                // No known issue: only a create can make progress, and the cap forbids it, so nothing is looked up.
                if (!CreateAllowed(current, now)) return true;
                if (!SpellCreateAllowed(current, pass, now)) return true;
            }
            else if (current.Publication.State == QaPublicationState.Published && !commentDue && !statusDue)
            {
                return true; // its issue is known and nothing is due: no call is needed
            }

            // The status is tried once per StatusUpdateMinutes of new sightings, whatever comes of it: an issue the list does not carry (unlabelled, beyond MaxListPages),
            // a lookup budget that runs out, a failed edit. Left due, every later pass would list every page again for it.
            if (statusDue) _statusShown[id] = current.LastSeenUtc;

            QaIssueIndex index = await IndexOfAsync(pass, cancellationToken).ConfigureAwait(false);
            QaIssueRef? found = index.ByMarker(id);
            // A finding that holds issue #N (any state: a failed attempt keeps the number) and finds #N in the list has its issue, whatever marker its body carries
            // (an issue handed over by a merged finding has that finding's). The same for Published and Failed; a marker a person edited away does not free the number.
            if (found is null && current.Publication.HoldsIssue) found = index.ByNumber(current.Publication.Issue);
            if (found is null && current.Publication.Issue > 0 && index.ByNumber(current.Publication.Issue) is null)
            {
                if (!await TakeLookupAsync(pass, cancellationToken).ConfigureAwait(false)) return true;
                var known = await client.GetIssueAsync(current.Publication.Issue, cancellationToken).ConfigureAwait(false);
                // Beyond a cut-short list the body is all there is to go on: any bot marker, this finding's or the one of a merged finding whose issue was handed over.
                if (known is { } k && QaIssueIndex.HasAnyMarker(k.Body)) found = k.Issue;
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
                if (!CreateAllowed(current, now)) return true;
                if (!SpellCreateAllowed(current, pass, now)) return true;
                QaIssueDraft draft = PlayerbotQaIssueComposer.Compose(current, LabelsFor(current), _build, redactor);
                QaIssueRef created = await client.CreateIssueAsync(draft.Title, draft.Body, draft.Labels, cancellationToken).ConfigureAwait(false);
                if (QaKinds.Of(current.Kind).ServerSide) Ledger.RecordServerCreate(now);
                else Ledger.RecordCreate(now);
                if (QaKinds.Of(current.Kind).SpellKind)
                {
                    pass.SpellCreates++;
                    Ledger.RecordSpellCreate(now);
                }

                index.AddCreated(created, id);
                _statusShown[id] = current.LastSeenUtc; // the new body carries the live status as of now
                _state.Apply(id, new QaPublicationUpdate(new QaPublication(QaPublicationState.Published, created.Number), now, current.Count));
                _logger.LogInformation("Bot QA: opened issue #{Number} for finding {Id}", created.Number, current.ShortId);
                return true;
            }

            bool reopened = false;
            bool regressionComment = false;
            if (!found.Open)
            {
                // An issue closed as not planned is a decision, not a fix: a newer build does not undo it (ReopenClosed, which reopens whatever its reason, still does).
                bool regression = IsRegression(current) && !found.NotPlanned;
                if (!git.ReopenClosed && !regression)
                {
                    _state.Apply(id, new QaPublicationUpdate(new QaPublication(QaPublicationState.KnownClosed, found.Number), null, current.Count, NotPlanned: found.NotPlanned));
                    _logger.LogInformation("Bot QA: finding {Id} has a closed issue (#{Number}); it is left closed", current.ShortId, found.Number);
                    return true;
                }

                // A regression reopens only together with its one comment, so the hourly comment cap decides first; without budget the finding stays as it is and the next pass tries again.
                regressionComment = regression && Ledger.CanComment(now, git.MaxCommentsPerHour);
                if (!git.ReopenClosed && !regressionComment)
                {
                    if (_cappedRegressions.TryAdd(id, 0)) // once per finding per process: the pass repeats every few minutes until the budget is back
                    {
                        _logger.LogInformation("Bot QA: the hourly comment cap ({Cap}) is reached; the regression of {Id} (issue #{Number}) is reported on a later pass", git.MaxCommentsPerHour, current.ShortId, found.Number);
                    }

                    return true;
                }

                await client.ReopenAsync(found.Number, cancellationToken).ConfigureAwait(false);
                reopened = true;
                _logger.LogInformation("Bot QA: reopened issue #{Number} for finding {Id}{Why}", found.Number, current.ShortId, regressionComment ? $" (regression on build {_build})" : string.Empty);
            }

            if (regressionComment)
            {
                await client.CommentAsync(found.Number, PlayerbotQaIssueComposer.RegressionComment(current, _build, current.ClosedBuild ?? current.ReportedBuild, redactor), cancellationToken).ConfigureAwait(false);
                Ledger.RecordComment(now);
                _state.Apply(id, new QaPublicationUpdate(new QaPublication(QaPublicationState.Published, found.Number), now, current.Count));
            }
            else if (commentDue)
            {
                await client.CommentAsync(found.Number, PlayerbotQaIssueComposer.Comment(current, freshCount, redactor), cancellationToken).ConfigureAwait(false);
                Ledger.RecordComment(now);
                _state.Apply(id, new QaPublicationUpdate(new QaPublication(QaPublicationState.Published, found.Number), now, current.Count));
            }
            else
            {
                _state.Apply(id, new QaPublicationUpdate(new QaPublication(QaPublicationState.Published, found.Number), Reported: reopened));
            }

            return !(statusDue || StatusDue(current with { Publication = new QaPublication(QaPublicationState.Published, found.Number) }, now))
                || await UpdateStatusAsync(current, found.Number, index, pass, now, cancellationToken).ConfigureAwait(false);
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
            // Type only, by policy (AnUnexpectedException_FailsTheFinding_WithItsTypeOnly): the exception text of a GitHub call may carry a
            // token or an issue body, and neither the log nor the fault ledger may hold it. The type is stored in the finding's publication state.
            FaultLedger.Shared.Record("playerbot.qa", ex);
            AnomalyLedger.Shared.Report("qa", "publish-fault", id, ex.GetType().Name, AnomalySeverity.Warning);
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
            if (survivor is null || !survivor.Publication.HoldsIssue) continue;
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
    /// Whether the live status block of <paramref name="f"/>'s issue may be behind (<see cref="PlayerbotQaGitHubOptions.StatusUpdateMinutes"/>): the finding is published, it was seen
    /// at least that long after the last-seen time this process knows the block shows (unknown after a start: the pass reads it from the list), and the hourly edit cap has room.
    /// </summary>
    private bool StatusDue(QaFindingView f, DateTime now)
    {
        PlayerbotQaGitHubOptions git = _options.GitHub;
        if (git.StatusUpdateMinutes <= 0 || f.Publication.State != QaPublicationState.Published || !f.Publication.HoldsIssue) return false;
        if (_statusShown.TryGetValue(f.Id, out DateTime shown) && f.LastSeenUtc - shown < TimeSpan.FromMinutes(git.StatusUpdateMinutes)) return false;
        return Ledger.CanEditStatus(now, git.MaxStatusEditsPerHour);
    }

    /// <summary>
    /// Rewrite the live status block of open issue <paramref name="number"/> when the one in its listed body is missing or at least <see cref="PlayerbotQaGitHubOptions.StatusUpdateMinutes"/>
    /// behind the finding. Only an issue whose body the pass listed is edited (the rest of the body is kept as it is). Every way out records the attempt, so the status is not due
    /// again until the finding is seen <see cref="PlayerbotQaGitHubOptions.StatusUpdateMinutes"/> later: an issue that is not listed, or not open, is not retried each pass. A failed
    /// edit is logged; false when the pass should stop (a transient failure).
    /// </summary>
    private async Task<bool> UpdateStatusAsync(QaFindingView f, int number, QaIssueIndex index, Pass pass, DateTime now, CancellationToken cancellationToken)
    {
        _statusShown[f.Id] = f.LastSeenUtc;
        if (index.ByNumber(number) is not { Open: true } || index.BodyOf(number) is not { } body) return true;
        TimeSpan interval = TimeSpan.FromMinutes(_options.GitHub.StatusUpdateMinutes);
        if (PlayerbotQaIssueComposer.StatusLastSeen(body) is { } shown && f.LastSeenUtc - shown < interval)
        {
            _statusShown[f.Id] = shown;
            return true;
        }

        string updated = PlayerbotQaIssueComposer.WithStatus(body, PlayerbotQaIssueComposer.StatusBlock(f, _build));
        if (updated.Length > PlayerbotQaRedactor.MaxBodyChars || string.Equals(updated, body, StringComparison.Ordinal)) return true;

        try
        {
            await pass.Transport.EditBodyAsync(number, updated, cancellationToken).ConfigureAwait(false);
        }
        catch (PlayerbotQaPublishException ex)
        {
            _logger.LogWarning("Bot QA could not update the live status of issue #{Number}: {Reason}", number, pass.Redactor.Redact(ex.Message));
            return !ex.Transient;
        }

        Ledger.RecordStatusEdit(now);
        index.SetBody(number, updated);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="f"/>'s open issue may be closed as completed now (<see cref="PlayerbotQaGitHubOptions.AutoCloseQuietMultiple"/>), with its usual gap and how long it has been
    /// quiet: it is published, not a won't-fix, met at least twice (so it has a gap), last met on a build other than the running one, which is newer (first run by this store after it)
    /// and has not met it; and the time since the later of its last sighting and this reporter's start is at least the multiple of its gap and at least
    /// <see cref="PlayerbotQaGitHubOptions.AutoCloseMinQuietHours"/>. Null otherwise.
    /// </summary>
    internal (TimeSpan Gap, TimeSpan Quiet)? QuietFor(QaFindingView f, DateTime now)
    {
        PlayerbotQaGitHubOptions git = _options.GitHub;
        if (git.AutoCloseQuietMultiple <= 0 || f.Publication.State != QaPublicationState.Published || !f.Publication.HoldsIssue || f.NotPlanned) return null;
        if (f.Count < 2 || f.LastBuild is null || string.Equals(f.LastBuild, _build, StringComparison.Ordinal) || f.CountOnBuild(_build) > 0) return null;
        if (!_state.IsBuildNewer(_build, f.LastBuild)) return null;
        TimeSpan gap = (f.LastSeenUtc - f.FirstSeenUtc) / (f.Count - 1);
        TimeSpan quiet = now - (f.LastSeenUtc > _startedUtc ? f.LastSeenUtc : _startedUtc);
        TimeSpan need = gap * git.AutoCloseQuietMultiple;
        TimeSpan floor = TimeSpan.FromHours(git.AutoCloseMinQuietHours);
        return quiet >= (need > floor ? need : floor) ? (gap, quiet) : null;
    }

    /// <summary>
    /// Close the open issues of findings that stopped (<see cref="QuietFor"/>): one comment saying why, then the close as completed; the finding becomes KnownClosed with the build it was
    /// last seen on as its closed build, so a recurrence on the running build (or any newer one) is a regression and reopens it (<see cref="PlayerbotQaGitHubOptions.ReopenOnRegression"/>).
    /// Shares the hourly comment budget; only an issue that is in the list and open is touched. False when the pass should stop (a transient failure).
    /// </summary>
    private async Task<bool> CloseQuietAsync(IReadOnlyList<QaFindingView> findings, Pass pass, CancellationToken cancellationToken)
    {
        PlayerbotQaGitHubOptions git = _options.GitHub;
        if (git.AutoCloseQuietMultiple <= 0) return true;
        foreach (QaFindingView candidate in findings.OrderBy(f => f.LastSeenUtc).ThenBy(f => f.Id, StringComparer.Ordinal))
        {
            DateTime now = _time.GetUtcNow().UtcDateTime;
            if (QuietFor(candidate, now) is null) continue;
            SemaphoreSlim gate = _gates.GetOrAdd(candidate.Id, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            _state.Pin(candidate.Id);
            try
            {
                // Read again under the finding's lock: it may have recurred, been merged or been published elsewhere since the snapshot.
                if (_state.Get(candidate.Id) is not { } f || QuietFor(f, now) is not { } quiet) continue;
                QaIssueIndex index = await IndexOfAsync(pass, cancellationToken).ConfigureAwait(false);
                if (index.ByNumber(f.Publication.Issue) is not { Open: true } issue) continue; // closed or not listed: left as it is
                if (!_quietCommented.ContainsKey(issue.Number))
                {
                    if (!Ledger.CanComment(now, git.MaxCommentsPerHour))
                    {
                        if (Interlocked.Exchange(ref _quietCapLogged, 1) == 0)
                        {
                            _logger.LogInformation("Bot QA: the hourly comment cap ({Cap}) is reached; quiet issues are closed on a later pass", git.MaxCommentsPerHour);
                        }

                        return true;
                    }

                    await pass.Transport.CommentAsync(issue.Number, PlayerbotQaIssueComposer.QuietCloseComment(f, _build, quiet.Gap, quiet.Quiet), cancellationToken).ConfigureAwait(false);
                    Ledger.RecordComment(now);
                    _quietCommented[issue.Number] = 0;
                }

                await pass.Transport.CloseAsCompletedAsync(issue.Number, cancellationToken).ConfigureAwait(false);
                _quietCommented.TryRemove(issue.Number, out _);
                _state.Apply(f.Id, new QaPublicationUpdate(new QaPublication(QaPublicationState.KnownClosed, issue.Number), NotPlanned: false, ClosedBuild: f.LastBuild));
                _logger.LogInformation("Bot QA: closed issue #{Number} of finding {Id} as completed: not seen for {Hours:F1} h on build {Build} (usual gap {Gap:F1} h)",
                    issue.Number, f.ShortId, quiet.Quiet.TotalHours, _build, quiet.Gap.TotalHours);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (PlayerbotQaPublishException ex)
            {
                _logger.LogWarning("Bot QA could not close the quiet issue of finding {Id}: {Reason}", candidate.ShortId, pass.Redactor.Redact(ex.Message));
                if (ex.Transient) return false;
            }
            finally
            {
                _state.Unpin(candidate.Id);
                gate.Release();
            }
        }

        return true;
    }

    /// <summary>
    /// The spell caps on top of the hourly one: at most <see cref="PlayerbotQaGitHubOptions.MaxSpellIssuesPerPass"/> spell issues per pass and
    /// <see cref="PlayerbotQaGitHubOptions.MaxSpellIssuesPerDay"/> per rolling day. Other kinds are not limited by them.
    /// </summary>
    /// <summary>Whether the hourly create cap of the finding's lane (server faults have their own) allows one more issue; logs the finding as pending when not.</summary>
    private bool CreateAllowed(QaFindingView finding, DateTime now)
    {
        PlayerbotQaGitHubOptions git = _options.GitHub;
        if (QaKinds.Of(finding.Kind).ServerSide)
        {
            if (Ledger.CanCreateServer(now, git.MaxServerFaultIssuesPerHour)) return true;
            _logger.LogInformation("Bot QA: the hourly server-fault issue cap ({Cap}) is reached; {Id} stays pending", git.MaxServerFaultIssuesPerHour, finding.ShortId);
            return false;
        }

        if (Ledger.CanCreate(now, git.MaxIssuesPerHour)) return true;
        _logger.LogInformation("Bot QA: the hourly issue cap ({Cap}) is reached; {Id} stays pending", git.MaxIssuesPerHour, finding.ShortId);
        return false;
    }

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
            if (f.Publication.HoldsIssue) continue;
            if (f.Publication.State == QaPublicationState.DryRun && f.OccurrencesReported == f.Count) continue;
            QaIssueDraft draft = PlayerbotQaIssueComposer.Compose(f, LabelsFor(f), _build, _redactor);
            try
            {
                Directory.CreateDirectory(folder);
                QaFiles.WriteAtomic(Path.Combine(folder, f.Id + ".md"), draft.Body);
                QaFiles.WriteAtomic(Path.Combine(folder, f.Id + ".json"), JsonSerializer.Serialize(
                    new QaPendingIssueFile(f.Id, git.Repository, draft.Title, draft.Marker, draft.Labels, f.Count, f.DistinctBots, now), QaPendingJsonContext.Default.QaPendingIssueFile));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Interlocked.Exchange(ref _warnedDirectory, 1) == 0)
                {
                    _logger.LogWarning(ex, "Bot QA could not write {Folder} ({Type}); dry-run issues are not saved", folder, ex.GetType().Name);
                }

                continue;
            }

            _state.Apply(f.Id, new QaPublicationUpdate(new QaPublication(QaPublicationState.DryRun), null, f.Count));
            _logger.LogInformation("bot-qa dry run: would open '{Title}' ({Id}) -> qa/pending-issues/{Id}.md", draft.Title, f.Id, f.Id);
        }
    }
}
