using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Insight;

/// <summary>
/// Live debugging tools on top of the map feed: entity inspector, anomaly markers and heatmaps, record and replay, highlight
/// filters, watches and the perf report (docs/areas/atlas.md, "Live insight"). Discovered by reflection.
/// </summary>
public sealed class InsightModule : IAtlasModule
{
    private readonly Func<long>? _clock;
    private readonly SpellNames? _spells;

    /// <summary>The module with the wall clock and the spell names of the client files, when there are any.</summary>
    public InsightModule()
    {
    }

    /// <summary>The module with an injected millisecond clock and spell names (tests).</summary>
    internal InsightModule(Func<long>? clockMs, SpellNames? spells)
    {
        _clock = clockMs;
        _spells = spells;
    }

    /// <inheritdoc />
    public string Name => "insight";

    /// <inheritdoc />
    public int Order => ModuleOrder.Layers + 10;

    /// <inheritdoc />
    public void Register(AtlasContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ = (_clock, _spells);
    }
}

/// <summary>The module's shared state, registered as a service. Stub.</summary>
internal sealed class InsightHub
{
    /// <summary>The always-on short recording the heatmaps read.</summary>
    public InsightRecorder Tail => throw new NotImplementedException();

    /// <summary>The user's recording, null while <c>live.record</c> is off.</summary>
    public InsightRecorder? Record => throw new NotImplementedException();

    /// <summary>The running watches.</summary>
    public WatchService Watch => throw new NotImplementedException();
}
