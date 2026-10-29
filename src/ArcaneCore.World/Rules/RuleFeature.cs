using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Rules;
using ArcaneCore.Kernel.Rules;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Rules;

/// <summary>
/// The shared rule engine's content in the world daemon (docs/superpowers/specs/2026-10-28-rule-engine-design.md): reads the rule
/// tables through the optional <see cref="IRuleContentStore"/> when the world starts, validates them against
/// <see cref="RuleVocabulary.Default"/> and the loaded <c>conditions</c> table, logs the first 20 rejected rows and a summary, and holds
/// the <see cref="RuleCatalog"/> hosts bind their subjects to. <c>.reload rules</c> replaces it (<see cref="RuleContentReloadable"/>);
/// <c>.rules check</c>, <c>.rules vocab</c> and <c>.rules trace</c> read it (<see cref="RuleCommands"/>).
/// <para>
/// Phase P0: no host is wired yet, so nothing evaluates these rules in the world; the engine itself is proven by its unit tests. Spell and
/// creature references are checked only for their sign until a host phase wires existence checks into <see cref="RuleBuildOptions"/> (and
/// re-validates the rules when those stores reload).
/// </para>
/// </summary>
public sealed class RuleFeature(IServiceProvider services, IServiceScopeFactory scopes, ILogger<RuleFeature> logger) : IWorldFeature
{
    /// <summary>How many rejected rows the startup log and a reload's notes name; the rest are counted.</summary>
    public const int LoggedRejections = 20;

    /// <summary>How many traced evaluations <c>.rules trace</c> keeps to show.</summary>
    public const int TraceHistory = 64;

    private readonly ILogger<RuleFeature> _logger = logger;
    private readonly Lock _traceLock = new();
    private readonly Queue<string> _trace = new();
    private RuleCatalog _catalog = RuleCatalog.Empty;

    /// <summary>The loaded rules (immutable; safe to read from any thread).</summary>
    public RuleCatalog Catalog => Volatile.Read(ref _catalog);

    /// <summary>The subjects <c>.rules trace</c> traces; hosts hand it to their evaluators.</summary>
    public RuleTracer Tracer { get; } = new();

    /// <summary>Where hosts' evaluators write traced evaluations: the log and the recent history <c>.rules trace</c> shows.</summary>
    public IRuleTraceSink TraceSink => _sink ??= new Sink(this);

    private Sink? _sink;

    /// <summary>The most recent traced evaluations, oldest first.</summary>
    public IReadOnlyList<string> RecentTrace
    {
        get
        {
            lock (_traceLock)
            {
                return [.. _trace];
            }
        }
    }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        RuleContent content = LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        RuleCatalog catalog = Build(content);
        Volatile.Write(ref _catalog, catalog);
        foreach (RuleRejection rejection in catalog.Rejected.Take(LoggedRejections))
        {
            _logger.LogWarning("Rule row skipped: {Table} {Id}: {Reason}", rejection.Table, rejection.Id, rejection.Reason);
        }

        _logger.LogInformation("Rules: {Summary}{More}", catalog.Summary,
            catalog.Rejected.Count > LoggedRejections ? $" ({catalog.Rejected.Count - LoggedRejections} more rejections: .rules check lists them all)" : "");
    }

    /// <summary>Read the rule tables (no store registered: no rows). Off the world thread.</summary>
    public async Task<RuleContent> LoadAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopes.CreateScope();
        return scope.ServiceProvider.GetService<IRuleContentStore>() is { } store
            ? await store.LoadAsync(cancellationToken).ConfigureAwait(false)
            : RuleContent.Empty;
    }

    /// <summary>Validate and compile <paramref name="content"/> against the vocabulary and the conditions table loaded now.</summary>
    public RuleCatalog Build(RuleContent content) => RuleCatalog.Build(content, RuleVocabulary.Default, new RuleBuildOptions
    {
        Conditions = services.GetService<ConditionFeature>()?.Table ?? Game.Conditions.ConditionTable.Empty,
    });

    /// <summary>Swap in <paramref name="catalog"/> (world thread, from a reload); returns the catalog it replaced.</summary>
    public RuleCatalog Replace(RuleCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Interlocked.Exchange(ref _catalog, catalog);
    }

    private void Record(string line)
    {
        lock (_traceLock)
        {
            _trace.Enqueue(line);
            while (_trace.Count > TraceHistory)
            {
                _trace.Dequeue();
            }
        }
    }

    private sealed class Sink(RuleFeature owner) : IRuleTraceSink
    {
        public void Write(in RuleTraceEntry entry)
        {
            string line = $"t={entry.NowMs} guid=0x{entry.SubjectGuid:X} rule={entry.RuleId} event={entry.EventType} outcome={entry.Outcome} roll={entry.Roll} "
                + $"condition={(entry.ConditionResult is { } c ? c.ToString() : "-")} actions={entry.ActionsRun} failed={entry.ActionsFailed}";
            owner.Record(line);
            owner._logger.LogInformation("Rule trace: {Line}", line);
        }
    }
}
