using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Ops.Announce;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>What <c>.playerbot qa</c> shows about the reporter itself. No secret: the token variable is reported as set or not set only.</summary>
internal sealed record QaStatus(bool Running, string GitHubMode, string TokenVariable, bool TokenSet, long Received, long Dropped, long Rejected,
    long IngestFailures, int Findings, string? PersistenceDisabled);

/// <summary>
/// Bot QA (docs/areas/playbots-qa.md): one long-running background task drains the signals the log tap queued, folds them into findings, and every
/// <see cref="PlayerbotQaOptions.ExportIntervalSeconds"/> saves them, writes <c>qa/report.md</c> and <c>report.json</c>, and schedules the GitHub publisher on a
/// task of its own (at most one at a time; a pass that finds the previous one still running skips publishing), so a rate-limit wait or a slow GitHub never stops
/// the draining of the signal queue. It never works on the world thread and never throws into it; a failed pass is logged and leaves the state as it was. Inert (no task, no files) when
/// QA is disabled or its services are not registered. Commands read the aggregator's immutable snapshots. Thread-safe by the aggregator's lock.
/// </summary>
public sealed class PlayerbotQaFeature(IServiceProvider services) : IDetachableWorldFeature
{
    private CancellationTokenSource _cts = new();
    private ILogger _log = NullLogger.Instance;
    private PlayerbotQaOptions _options = new();
    private PlayerbotQaSignalBus? _bus;
    private PlayerbotQaStore? _store;
    private PlayerbotQaPublisher? _publisher;
    private IQaContentFacts _facts = NoQaContentFacts.Instance;
    private Task _loop = Task.CompletedTask;
    private Task _publishing = Task.CompletedTask;
    private readonly object _persistGate = new();
    private string _directory = string.Empty;
    private long _ingestFailures;
    private bool _exportFailureLogged;
    private bool _coverageFailureLogged;
    private QaSpellCoverageReport? _coverage;
    private WorldRuntime? _world;
    private PlayerbotQaFaultFeed? _faultFeed;
    private PlayerbotQaAnomalyFeed? _anomalyFeed;
    private PlayerbotQaFirstChanceWatch? _firstChance;
    private System.Diagnostics.Metrics.Meter? _findingsMeter;

    /// <summary>Test seam: reads the token variable (default: the process environment).</summary>
    internal Func<string, string?>? EnvironmentReader { get; init; }

    /// <summary>Test seam: makes the REST transport of a publish pass (default: the GitHub REST client).</summary>
    internal Func<string, IQaIssueTransport>? TransportFactory { get; init; }

    /// <summary>Test seam: the fault ledger the server-fault signals are read from (default: the container's, else the process-wide one).</summary>
    internal FaultLedger? FaultSource { get; init; }

    /// <summary>Test seam: the anomaly ledger the bot-anomaly signals are read from (default: the container's, else <see cref="AnomalyLedger.Shared"/>).</summary>
    internal AnomalyLedger? AnomalySource { get; init; }

    /// <summary>Test seam: the invariant failure source the server-fault feed polls (default: <see cref="Invariant.Failures"/> when using the host's fault ledger).</summary>
    internal Func<IReadOnlyList<InvariantFailure>>? InvariantSource { get; init; }

    /// <summary>Test seam: the clock of the passes, the age-out and the publisher (default: the system clock).</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    private DateTime Now => Time.GetUtcNow().UtcDateTime;

    /// <summary>The clock of the views (age of a finding), the same as the passes use.</summary>
    internal DateTime UtcNow => Now;

    /// <summary>When this process attached QA: a finding last seen before it was not met by the current build (the views sink it as stale).</summary>
    internal DateTime StartedUtc { get; private set; } = DateTime.MaxValue;

    /// <summary>The build this process runs, which stamps every occurrence and every report (test seam: a test may give another).</summary>
    internal string Build { get; init; } = PlayerbotQaIssueComposer.BuildVersion();

    internal PlayerbotQaAggregator? Aggregator { get; private set; }

    /// <summary>The spell watcher of the bots' casts (registered on the world's spell system while QA runs; null otherwise).</summary>
    internal PlayerbotQaSpellMonitor? SpellMonitor { get; private set; }

    /// <summary>The static spell coverage of the bots (computed by the first pass that finds the spell table loaded; null before).</summary>
    internal QaSpellCoverageReport? SpellCoverage => Volatile.Read(ref _coverage);

    internal PlayerbotQaRedactor Redactor { get; } = new();

    /// <summary>The folder of the findings file, exports and dry-run issues (empty while inert).</summary>
    internal string Directory => _directory;

    // --- lifecycle (docs/design/hot-reload-tiers.md, section 4.2) -------------------------------------------------------

    /// <summary>
    /// Stateless: the findings live in the store file, which the quiesce saves and the next attach loads, so a detach loses nothing
    /// but the signals that arrive while it is detached (the log tap's bus keeps queueing them; the next attach drains them).
    /// </summary>
    public DetachKind DetachKind => DetachKind.Stateless;

    public void Attach(WorldRuntime world) => AttachFrom(world, null, new FeatureSubscriptions(nameof(PlayerbotQaFeature)));

    /// <summary>
    /// Stop the background pass and save the findings, like <see cref="StopAsync"/>, except that a pass still running is waited for
    /// as long as the host allows (its token), not abandoned after 10 s: a quiesce that gives up fails, and the host restores the feature.
    /// </summary>
    public async Task QuiesceAsync(CancellationToken cancellationToken)
    {
        if (Aggregator is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_loop, _publishing).WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_loop.IsCompleted || !_publishing.IsCompleted) throw new TimeoutException("bot QA's background pass did not stop");
        _findingsMeter?.Dispose();
        _findingsMeter = null;
        SaveFinal();
    }

    /// <summary>Forget everything the attach built; the next attach starts from the store file again.</summary>
    public void Detach(FeatureHandover handover)
    {
        Aggregator = null;
        SpellMonitor = null;
        _store = null;
        _publisher = null;
        _bus = null;
        _faultFeed = null;
        _anomalyFeed = null;
        _firstChance = null;
        _findingsMeter = null;
        _facts = NoQaContentFacts.Instance;
        _options = new();
        _directory = string.Empty;
        // _loop and _publishing are kept: after a quiesce that gave up they may still run (cancelled), and the next attach starts its
        // pass only once the old one has ended, and a publish only once the old publish has.
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        _world = null;
    }

    public void AttachFrom(WorldRuntime world, FeatureHandover? handover, FeatureSubscriptions subscriptions)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        _world = world;
        _log = (ILogger?)services.GetService<ILogger<PlayerbotQaFeature>>() ?? NullLogger.Instance;
        ManagedBotRegistry? registry = services.GetService<ManagedBotRegistry>();
        // The tick watchdog reports a hang to bot QA through the registry's seam: whenever the sink is on, as before (it does not need
        // the feature itself to be enabled, only the sink).
        if (registry is not null && services.GetService<IPlayerbotQaSink>() is { Enabled: true } hangSink)
        {
            int warmupMinutes = services.GetService<IOptions<PlayerbotQaOptions>>()?.Value.TickHangWarmupMinutes ?? 5;
            bool worldHang = services.GetService<IOptions<PlayerbotQaOptions>>()?.Value.Faults.WorldHangEnabled ?? true;
            subscriptions.Add(registry.UseHangSink(new QaHangSink(hangSink, TimeSpan.FromMinutes(warmupMinutes), worldHang)));
        }

        _bus = services.GetService<PlayerbotQaSignalBus>();
        if (services.GetService<IOptions<PlayerbotQaOptions>>()?.Value is not { } options || _bus is null) return;
        IReadOnlyList<string> problems = options.Validate();
        if (problems.Count > 0)
        {
            _log.LogError("Bot QA is off: {Problems}", string.Join("; ", problems));
            return;
        }

        if (!options.Enabled) return;
        _options = options;
        if (StartedUtc == DateTime.MaxValue) StartedUtc = Now; // an attach after a detach keeps the process's first
        _directory = Path.GetFullPath(Path.Combine(services.GetService<IHostEnvironment>()?.ContentRootPath ?? AppContext.BaseDirectory, options.Directory));
        _store = new PlayerbotQaStore(_directory, _log);
        QaStoreFile? saved = _store.Load();
        byte[]? salt = null;
        if (saved is not null && saved.SaltBase64.Length > 0)
        {
            try
            {
                salt = Convert.FromBase64String(saved.SaltBase64);
            }
            catch (FormatException)
            {
                salt = null;
            }
        }

        var aggregator = new PlayerbotQaAggregator(options.MaxFindings, options.MaxSamples, salt, options.StaleHours > 0 ? TimeSpan.FromHours(options.StaleHours) : null, _log, Build);
        _facts = new PlayerbotQaContentFacts(services);
        _publisher = new PlayerbotQaPublisher(options, _directory, aggregator, Redactor, _log, EnvironmentReader, TransportFactory, Time, Build);
        if (saved is not null)
        {
            int dropped = aggregator.Restore(new QaAggregatorState(saved.Findings, saved.Bots, saved.Absorbed, saved.Tombstones, saved.Builds, saved.EvictedBuilds), Now);
            _publisher.Ledger.Restore(saved.Ledger);
            _log.LogInformation("Bot QA restored {Count} findings from {Path} ({Dropped} invalid or over the cap, {Aged} aged out, {Merged} merged into another, {Issues} issues of merged findings to close as duplicates)",
                aggregator.Count, _store.FilePath, dropped, aggregator.AgedOut, aggregator.Merged, aggregator.PendingDuplicates().Count);
            foreach (QaAbsorbed duplicate in aggregator.PendingDuplicates())
            {
                if (aggregator.Get(duplicate.Survivor) is { } survivor && survivor.Publication.HoldsIssue)
                {
                    _log.LogInformation("Bot QA: issue #{Issue} -> duplicate of #{Survivor} ({Action})", duplicate.Issue, survivor.Publication.Issue,
                        options.GitHub.CloseAbsorbedDuplicates
                            ? (survivor.Publication.State == QaPublicationState.Failed ? "will be commented and closed once the survivor's issue is confirmed open (its last publish failed)" : "will be commented and closed")
                            : "left alone: World:Playerbots:Qa:GitHub:CloseAbsorbedDuplicates is off");
                }
            }
        }

        Aggregator = aggregator;
        if (services.GetService<MetricsOptions>() is { Enabled: true }) _findingsMeter = PlayerbotQaMeters.RegisterFindings(aggregator.Snapshot, this);
        if (services.GetService<IOpsAnnouncer>() is { } announcer)
        {
            aggregator.FindingCreated = view => PlayerbotQaAnnouncements.Announce(announcer, view, Redactor);
        }

        // Only a host that registered the ledger (UseArcaneDiagnostics) or an explicit test source feeds server faults.
        FaultLedger? faultLedger = FaultSource ?? services.GetService<FaultLedger>();
        AnomalyLedger? anomalyLedger = AnomalySource ?? services.GetService<AnomalyLedger>();
        if (faultLedger is not null || InvariantSource is not null)
        {
            Func<IReadOnlyList<InvariantFailure>>? invariantSource = InvariantSource ?? (FaultSource is null ? Invariant.Failures : null);
            _faultFeed = new PlayerbotQaFaultFeed(
                faultLedger ?? new FaultLedger(),
                invariantSource,
                FaultSource is null ? anomalyLedger : null,
                InvariantSource is null && FaultSource is null ? (Func<long>)(static () => Invariant.Epoch) : null);
        }

        if (anomalyLedger is not null) _anomalyFeed = new PlayerbotQaAnomalyFeed(anomalyLedger);
        if (_bus is not null)
        {
            // The crash hooks' exception stream, as objects: repeated first-chance exceptions, and unobserved/unhandled ones the ledger did not record.
            _firstChance = new PlayerbotQaFirstChanceWatch(_bus, options.Faults, Time);
            subscriptions.Add(FaultObservers.Add(_firstChance));
        }
        if (services.GetService<SpellFeature>() is { } spells)
        {
            // Spell signals come straight from the spell system's observer seam, never from a log line (docs/areas/playbots-qa.md).
            IPlayerbotQaSink sink = services.GetService<IPlayerbotQaSink>() ?? new PlayerbotQaSink(_bus);
            SpellMonitor = new PlayerbotQaSpellMonitor(sink, spells.System,
                knows: (player, spell) => spells.Spellbook.GetSpells(player).Contains(spell));
            subscriptions.Observe(spells.System, SpellMonitor);
            if (registry is not null) subscriptions.Add(registry.UseCastRefusals(SpellMonitor));
        }

        // A pass of an earlier attach still winding down (a quiesce the host gave up on) ends first: never two passes at once.
        Task previous = _loop;
        CancellationToken token = _cts.Token;
        _loop = Task.Run(async () =>
        {
            await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await RunAsync(token).ConfigureAwait(false);
        });
    }

    public async Task StopAsync()
    {
        if (Aggregator is null) return;
        _findingsMeter?.Dispose();
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_loop, _publishing).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _log.LogWarning("Bot QA did not stop within 10 seconds; its last pass is abandoned");
        }

        SaveFinal();
    }

    /// <summary>Fold what is queued and save the findings (the end of a stop or a quiesce).</summary>
    private void SaveFinal()
    {
        try
        {
            _firstChance?.Flush();
            Drain();
            PollServerFaults();
            PollAnomalies();
            Persist();
            Export();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.qa", ex);
            AnomalyLedger.Shared.Report("qa", "pass-fault", "save-final", ex.GetType().Name, AnomalySeverity.Error);
            _log.LogError(ex, "Bot QA could not save its findings at a stop or detach");
        }
    }

    /// <summary>The reporter's state for <c>.playerbot qa</c>.</summary>
    internal QaStatus Status()
    {
        PlayerbotQaGitHubOptions git = _options.GitHub;
        bool gh = git.Transport == PlayerbotQaGitHubTransport.GhCli;
        string mode = !git.Enabled ? "off" : git.Publish ? (gh ? "publish via gh" : "publish") : "dry-run";
        bool tokenSet = git.Enabled && PlayerbotQaGitHubOptions.IsValidVariableName(git.TokenEnvironmentVariable)
            && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(git.TokenEnvironmentVariable));
        return new QaStatus(Aggregator is not null, mode, gh && !git.GhPassToken ? string.Empty : git.TokenEnvironmentVariable, tokenSet, _bus?.Received ?? 0, _bus?.Dropped ?? 0, Aggregator?.Rejected ?? 0,
            Interlocked.Read(ref _ingestFailures), Aggregator?.Count ?? 0, _store?.DisabledReason);
    }

    /// <summary>
    /// Fold the queued signals into the findings (at most <paramref name="max"/>, the rest stays queued for the background pass); how many were taken.
    /// Safe to call from any thread.
    /// </summary>
    internal int Drain(int max = int.MaxValue)
    {
        if (Aggregator is null || _bus is null) return 0;
        var batch = new List<QaSignal>(256);
        int total = 0;
        int moved;
        do
        {
            batch.Clear();
            moved = _bus.Drain(batch, Math.Min(4096, max - total));
            DateTime now = Now;
            foreach (QaSignal signal in batch)
            {
                try
                {
                    if (Aggregator.Ingest(signal, _facts, now) is not null && !signal.FromCoverage) PlayerbotQaMeters.CountOccurrences(signal.Kind, signal.Occurrences);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("playerbot.qa", ex);
                    AnomalyLedger.Shared.Report("qa", "pass-fault", "drain", ex.GetType().Name, AnomalySeverity.Error);
                    if (Interlocked.Increment(ref _ingestFailures) == 1) _log.LogError(ex, "Bot QA could not fold a signal into its findings");
                }
            }

            total += moved;
        }
        while (moved > 0);
        return total;
    }

    /// <summary>
    /// Fold what the server's fault ledger recorded since the previous pass into the findings as <see cref="QaSignalKind.ServerFault"/> signals
    /// (<see cref="PlayerbotQaFaultFeed"/>); how many signals. Runs on the QA task (or the test's caller), never on the world thread.
    /// </summary>
    internal int PollServerFaults()
    {
        if (Aggregator is null || _faultFeed is null) return 0;
        IReadOnlyList<QaSignal> signals = _faultFeed.Pull();
        DateTime now = Now;
        foreach (QaSignal signal in signals)
        {
            try
            {
                if (Aggregator.Ingest(signal, _facts, now) is not null) PlayerbotQaMeters.CountOccurrences(signal.Kind, signal.Occurrences);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("playerbot.qa", ex);
                AnomalyLedger.Shared.Report("qa", "pass-fault", "poll-server-faults", ex.GetType().Name, AnomalySeverity.Error);
                if (Interlocked.Increment(ref _ingestFailures) == 1) _log.LogError(ex, "Bot QA could not fold a server fault into its findings");
            }
        }

        return signals.Count;
    }

    /// <summary>
    /// Fold qualifying <c>bot-*</c> outcome anomalies from the anomaly ledger into the findings as <see cref="QaSignalKind.Anomaly"/> signals
    /// (<see cref="PlayerbotQaAnomalyFeed"/>); how many signals. Runs on the QA task (or the test's caller), never on the world thread.
    /// </summary>
    internal int PollAnomalies()
    {
        if (Aggregator is null || _anomalyFeed is null) return 0;
        IReadOnlyList<QaSignal> signals = _anomalyFeed.Pull();
        DateTime now = Now;
        foreach (QaSignal signal in signals)
        {
            try
            {
                if (Aggregator.Ingest(signal, _facts, now) is not null) PlayerbotQaMeters.CountOccurrences(signal.Kind, signal.Occurrences);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("playerbot.qa", ex);
                AnomalyLedger.Shared.Report("qa", "pass-fault", "poll-anomalies", ex.GetType().Name, AnomalySeverity.Error);
                if (Interlocked.Increment(ref _ingestFailures) == 1) _log.LogError(ex, "Bot QA could not fold a bot anomaly into its findings");
            }
        }

        return signals.Count;
    }

    /// <summary>The publish task of the latest pass (completed when none runs); tests await it.</summary>
    internal Task Publishing => _publishing;

    /// <summary>
    /// One save-export-publish pass (the loop's timer calls it; tests call it directly). It drains, saves and exports on the caller's task and only schedules the
    /// publisher: when the previous publish task is still running (it may be waiting out a Retry-After) nothing is scheduled, and the loop is never blocked by it.
    /// </summary>
    internal void RunPass(CancellationToken cancellationToken)
    {
        if (Aggregator is null || _publisher is null) return;
        _firstChance?.Flush();
        Drain();
        PollServerFaults();
        PollAnomalies();
        TryComputeSpellCoverage();
        if (services.GetService<IPlayerbotService>() is { } bots)
        {
            Redactor.SetNames(bots.Snapshot().Select(bot => bot.Name));
        }

        Aggregator.AgeOut(Now);
        if (_options.AutoPruneHours > 0)
        {
            // Measured from the later of the finding's last sighting and this process's start: a restart after a long gap prunes nothing until the age has passed in this process.
            QaPruneResult pruned = Aggregator.Prune(Now, TimeSpan.FromHours(_options.AutoPruneHours), closed: false, dry: false, floorUtc: StartedUtc);
            if (pruned.Count > 0)
            {
                string ids = string.Join(", ", pruned.Listed.Select(f => f.ShortId)) + (pruned.Count > pruned.Listed.Count ? ", ..." : string.Empty);
                _log.LogInformation("Bot QA: forgot {Count} findings not seen for {Hours} hours (World:Playerbots:Qa:AutoPruneHours; {Ids}); {Kept} with an issue kept, {Left} remain",
                    pruned.Count, _options.AutoPruneHours, ids, pruned.Kept, pruned.Remaining);
            }
        }

        Persist();
        Export();
        // The pass runs every ExportIntervalSeconds (5 minutes by default): the retreat counters ride on it as the periodic summary line.
        _log.LogInformation("Bot QA: {Retreats}", PlayerbotRetreatStats.Shared.Describe());
        if (!_options.GitHub.Enabled || !_publishing.IsCompleted) return;
        IReadOnlyList<QaFindingView> findings = Aggregator.Snapshot();
        _publishing = Task.Run(() => PublishAsync(findings, cancellationToken), CancellationToken.None);
    }

    private async Task PublishAsync(IReadOnlyList<QaFindingView> findings, CancellationToken passToken)
    {
        if (_publisher is not { } publisher) return;
        // The publisher stops when the caller's token or the feature's own (StopAsync) is cancelled, so a long rate-limit wait never outlives shutdown.
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(passToken, _cts.Token);
        CancellationToken cancellationToken = linked.Token;
        try
        {
            await publisher.RunAsync(findings, cancellationToken).ConfigureAwait(false);
            Persist();
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _log.LogDebug(ex, "Bot QA publishing canceled during shutdown");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.qa", ex);
            _log.LogError(ex, "Bot QA publishing failed; the findings stay pending and the next pass tries again");
        }
    }

    /// <summary>
    /// Compute the static spell coverage (<see cref="PlayerbotQaSpellCoverage"/>) once the spell table is loaded, and fold each gap in as a coverage finding.
    /// The coverage reads the spell system's registries, which belong to the world thread (features install handlers from posted work, and a hot module
    /// could register one later), so it runs there (<see cref="WorldRuntime.InvokeAsync{T}"/>, one call a server lifetime; the QA task waits at most 30 s)
    /// and only the folding happens on the QA task. Without a world (unit tests) it runs on the caller. It is computed again only when the first run had no class trainer rows and they have loaded since. True when it ran now.
    /// A failure is logged once and retried on the next pass.
    /// </summary>
    internal bool TryComputeSpellCoverage()
    {
        if (Aggregator is null || services.GetService<SpellFeature>() is not { } spells || spells.System.Store.Count == 0) return false;
        IReadOnlyList<TrainerSpell> rows = services.GetService<QuestNpcFeature>()?.Services?.Npcs.Content.TrainerSpells ?? [];
        if (_coverage is { } done && (done.TrainerRows > 0 || rows.Count == 0)) return false;
        try
        {
            IEnumerable<uint> talents = services.GetService<Talents.TalentFeature>()?.Catalog?.Talents.SelectMany(t => t.RankSpells) ?? [];
            Func<QaSpellCoverageReport> build = () => PlayerbotQaSpellCoverage.Build(spells.System, rows, talents, Now);
            QaSpellCoverageReport report = _world is { } world && !world.IsWorldThread
                ? world.InvokeAsync(build).WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult()
                : build();
            foreach (QaSignal gap in PlayerbotQaSpellCoverage.Signals(report)) Aggregator.Ingest(gap, _facts, report.ComputedUtc);
            Volatile.Write(ref _coverage, report);
            _log.LogInformation("Bot QA: {Coverage}", report.Describe());
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.qa", ex);
            AnomalyLedger.Shared.Report("qa", "pass-fault", "spell-coverage", ex.GetType().Name, AnomalySeverity.Warning);
            if (!_coverageFailureLogged)
            {
                _coverageFailureLogged = true;
                _log.LogError(ex, "Bot QA could not compute the spell coverage; the next pass tries again");
            }

            return false;
        }
    }

    /// <summary>Forget one finding (exact id) or all; how many went. Takes effect on the next save.</summary>
    internal int Reset(string? id) => Aggregator?.Reset(id) ?? 0;

    /// <summary>
    /// <c>.playerbot qa prune</c>: forget (or with <paramref name="dry"/> only list) the findings not seen for <paramref name="hours"/> and, with
    /// <paramref name="closed"/>, those whose issue is known closed. Like <see cref="Reset"/> it takes effect on the next save (the world thread never waits on the disk) and never touches GitHub.
    /// </summary>
    internal QaPruneResult Prune(int hours, bool closed, bool dry)
        => Aggregator?.Prune(Now, TimeSpan.FromHours(hours), closed, dry) ?? new QaPruneResult(0, [], [], 0, dry, 0);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        DateTime next = Now.AddSeconds(_options.ExportIntervalSeconds);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                Drain();
                if (Now < next) continue;
                next = Now.AddSeconds(_options.ExportIntervalSeconds);
                RunPass(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("playerbot.qa", ex);
                _log.LogError(ex, "Bot QA pass failed; its findings are unchanged and the next pass tries again");
            }
        }
    }

    private void Persist()
    {
        if (Aggregator is null || _store is null || _publisher is null) return;
        lock (_persistGate)
        {
            QaAggregatorState state = Aggregator.Export();
            _store.Save(new QaStoreFile
            {
                SaltBase64 = Convert.ToBase64String(Aggregator.Salt), Findings = state.Findings, Bots = state.Bots, Absorbed = state.Absorbed ?? [], Tombstones = state.Tombstones ?? [], Builds = state.Builds ?? [], EvictedBuilds = state.EvictedBuilds ?? [], Ledger = _publisher.Ledger.Export(),
            });
        }
    }

    private void Export()
    {
        if (Aggregator is null) return;
        IReadOnlyList<QaFindingView> findings = Aggregator.Snapshot();
        DateTime now = Now;
        string build = Build;
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            QaSpellCoverageReport? coverage = SpellCoverage;
            QaFiles.WriteAtomic(Path.Combine(_directory, "report.md"), PlayerbotQaReport.RenderMarkdown(findings, now, build, Redactor, coverage, StartedUtc));
            QaFiles.WriteAtomic(Path.Combine(_directory, "report.json"), PlayerbotQaReport.RenderJson(findings, now, build, Redactor, coverage, StartedUtc));
            _exportFailureLogged = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_exportFailureLogged)
            {
                _exportFailureLogged = true;
                _log.LogError(ex, "Bot QA could not write its report to {Folder} ({Type}); it retries on the next pass", _directory, ex.GetType().Name);
            }
        }
    }

    /// <summary>The goal and exception type of the world-hang fault (subject <c>server: world-hang</c>).</summary>
    internal const string WorldHangGoal = "server";

    internal const string WorldHangType = "world-hang";

    /// <summary>
    /// The fault of a hung world tick: identified by the world thread's top <c>ArcaneCore</c> frames and nothing else (a longer hang at the same place is the same finding),
    /// the tick's length in the diagnostic text only.
    /// </summary>
    internal static QaSignal WorldHangSignal(long tickMs, IReadOnlyList<string> frames, DateTime atUtc) => new()
    {
        Kind = QaSignalKind.ServerFault,
        AtUtc = atUtc,
        FaultId = FaultLedger.HashSignature("world-hang|" + string.Join("|", frames)),
        Goal = WorldHangGoal,
        ExceptionType = WorldHangType,
        Reason = WorldHangType,
        Detail = string.Join(" > ", frames),
        Diag = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"world tick running {tickMs} ms; the world thread was in {string.Join(" <- ", frames)}"),
        Message = string.Empty,
        Occurrences = 1,
    };

    /// <summary>The tick watchdog's hang seam over the QA sink (watchdog thread; never throws past the sink's own contract).</summary>
    private sealed class QaHangSink(IPlayerbotQaSink sink, TimeSpan warmup, bool worldHang = true) : IBotTickHangSink
    {
        public TimeSpan Warmup => warmup;

        public void ReportHangStack(long tickMs, IReadOnlyList<string> worldFrames)
        {
            if (worldHang && sink.Enabled && worldFrames.Count > 0) sink.Report(WorldHangSignal(tickMs, worldFrames, DateTime.UtcNow));
        }

        public void ReportHang(string phase, string work, string message, long occurrences)
        {
            if (!sink.Enabled) return;
            sink.Report(new QaSignal
            {
                Kind = QaSignalKind.TickHang, AtUtc = DateTime.UtcNow, Stage = phase, Reason = work, Message = message, Occurrences = occurrences,
            });
        }
    }
}
