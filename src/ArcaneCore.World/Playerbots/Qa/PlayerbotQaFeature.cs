using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
public sealed class PlayerbotQaFeature(IServiceProvider services) : IWorldFeature
{
    private readonly CancellationTokenSource _cts = new();
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

    /// <summary>Test seam: reads the token variable (default: the process environment).</summary>
    internal Func<string, string?>? EnvironmentReader { get; init; }

    /// <summary>Test seam: makes the REST transport of a publish pass (default: the GitHub REST client).</summary>
    internal Func<string, IQaIssueTransport>? TransportFactory { get; init; }

    internal PlayerbotQaAggregator? Aggregator { get; private set; }

    /// <summary>The spell watcher of the bots' casts (registered on the world's spell system while QA runs; null otherwise).</summary>
    internal PlayerbotQaSpellMonitor? SpellMonitor { get; private set; }

    /// <summary>The static spell coverage of the bots (computed by the first pass that finds the spell table loaded; null before).</summary>
    internal QaSpellCoverageReport? SpellCoverage => Volatile.Read(ref _coverage);

    internal PlayerbotQaRedactor Redactor { get; } = new();

    /// <summary>The folder of the findings file, exports and dry-run issues (empty while inert).</summary>
    internal string Directory => _directory;

    public void Attach(WorldRuntime world)
    {
        _world = world;
        _log = (ILogger?)services.GetService<ILogger<PlayerbotQaFeature>>() ?? NullLogger.Instance;
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

        var aggregator = new PlayerbotQaAggregator(options.MaxFindings, options.MaxSamples, salt);
        _facts = new PlayerbotQaContentFacts(services);
        _publisher = new PlayerbotQaPublisher(options, _directory, aggregator, Redactor, _log, EnvironmentReader, TransportFactory);
        if (saved is not null)
        {
            int dropped = aggregator.Restore(new QaAggregatorState(saved.Findings, saved.Bots));
            _publisher.Ledger.Restore(saved.Ledger);
            _log.LogInformation("Bot QA restored {Count} findings from {Path}{Dropped}", aggregator.Count, _store.FilePath, dropped > 0 ? $" ({dropped} invalid entries dropped)" : string.Empty);
        }

        Aggregator = aggregator;
        if (services.GetService<SpellFeature>() is { } spells)
        {
            // Spell signals come straight from the spell system's observer seam, never from a log line (docs/areas/playbots-qa.md).
            IPlayerbotQaSink sink = services.GetService<IPlayerbotQaSink>() ?? new PlayerbotQaSink(_bus);
            SpellMonitor = new PlayerbotQaSpellMonitor(sink, spells.System,
                knows: (player, spell) => spells.Spellbook.GetSpells(player).Contains(spell));
            spells.System.RegisterObserver(SpellMonitor);
        }

        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (Aggregator is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_loop, _publishing).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _log.LogWarning("Bot QA did not stop within 10 seconds; its last pass is abandoned");
        }

        try
        {
            Drain();
            Persist();
            Export();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Bot QA could not save its findings at shutdown");
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

    /// <summary>Fold every queued signal into the findings; how many were taken. Safe to call from any thread.</summary>
    internal int Drain()
    {
        if (Aggregator is null || _bus is null) return 0;
        var batch = new List<QaSignal>(256);
        int total = 0;
        int moved;
        do
        {
            batch.Clear();
            moved = _bus.Drain(batch, 4096);
            DateTime now = DateTime.UtcNow;
            foreach (QaSignal signal in batch)
            {
                try
                {
                    Aggregator.Ingest(signal, _facts, now);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    if (Interlocked.Increment(ref _ingestFailures) == 1) _log.LogError(ex, "Bot QA could not fold a signal into its findings");
                }
            }

            total += moved;
        }
        while (moved > 0);
        return total;
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
        Drain();
        TryComputeSpellCoverage();
        if (services.GetService<IPlayerbotService>() is { } bots)
        {
            Redactor.SetNames(bots.Snapshot().Select(bot => bot.Name));
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
        // The publisher stops when the caller's token or the feature's own (StopAsync) is cancelled, so a long rate-limit wait never outlives shutdown.
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(passToken, _cts.Token);
        CancellationToken cancellationToken = linked.Token;
        try
        {
            await _publisher!.RunAsync(findings, cancellationToken).ConfigureAwait(false);
            Persist();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
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
            Func<QaSpellCoverageReport> build = () => PlayerbotQaSpellCoverage.Build(spells.System, rows, talents, DateTime.UtcNow);
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

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        DateTime next = DateTime.UtcNow.AddSeconds(_options.ExportIntervalSeconds);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                Drain();
                if (DateTime.UtcNow < next) continue;
                next = DateTime.UtcNow.AddSeconds(_options.ExportIntervalSeconds);
                RunPass(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
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
                SaltBase64 = Convert.ToBase64String(Aggregator.Salt), Findings = state.Findings, Bots = state.Bots, Ledger = _publisher.Ledger.Export(),
            });
        }
    }

    private void Export()
    {
        if (Aggregator is null) return;
        IReadOnlyList<QaFindingView> findings = Aggregator.Snapshot();
        DateTime now = DateTime.UtcNow;
        string build = PlayerbotQaIssueComposer.BuildVersion();
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            QaSpellCoverageReport? coverage = SpellCoverage;
            WriteAtomic(Path.Combine(_directory, "report.md"), PlayerbotQaReport.RenderMarkdown(findings, now, build, Redactor, coverage));
            WriteAtomic(Path.Combine(_directory, "report.json"), PlayerbotQaReport.RenderJson(findings, now, build, Redactor, coverage));
            _exportFailureLogged = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_exportFailureLogged)
            {
                _exportFailureLogged = true;
                _log.LogError("Bot QA could not write its report to {Folder} ({Type}); it retries on the next pass", _directory, ex.GetType().Name);
            }
        }
    }

    private static void WriteAtomic(string path, string text)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}
