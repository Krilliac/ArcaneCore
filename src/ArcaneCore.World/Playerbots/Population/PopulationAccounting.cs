using System.Globalization;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>
/// What a pass plans against: the population's own share of the target and how it is split over the bands, and the population's own
/// bots running in each band.
/// <para>
/// The design choice (2026-10-11): an operator's bot (one without a <c>playerbot_population_member</c> row) counts towards the total,
/// because <c>TargetOnline</c> and <c>MaxBots</c> bound the whole server's load, but never towards a band. The operator bots running
/// are taken off the top (<see cref="OwnedTarget"/> = effective target - operator bots running, at least 0) and only that share is
/// split over the bands by weight; a band's <see cref="Have"/> is the population's own bots in it. So the control loop's totals are
/// unchanged (running against the effective target), while a band full of operator bots still gets its weighted share of population
/// bots. Before, every running managed bot counted in its band: live, 201 operator bots at levels 11-20 held that band over its
/// share, so no 11-20 bot was ever created, and the band 1-10 bots (78 operator bots there) looked closest to full and were parked
/// first and restarted last.
/// </para>
/// </summary>
internal sealed record PopulationCounts(int[] Desired, int[] Have, int OperatorRunning, int OwnedTarget)
{
    /// <param name="bands">The bands in force.</param>
    /// <param name="effectiveTarget">The lower of TargetOnline and MaxBots.</param>
    /// <param name="runningAll">Every managed bot running, whoever created it.</param>
    /// <param name="ownedRunningAll">The running managed bots the population owns (whether or not their player is in the world yet).</param>
    /// <param name="ownedLevels">The level of each of the population's own running bots whose player was captured.</param>
    public static PopulationCounts Of(IReadOnlyList<PopulationBand> bands, int effectiveTarget, int runningAll, int ownedRunningAll,
        IEnumerable<byte> ownedLevels)
    {
        ArgumentNullException.ThrowIfNull(bands);
        ArgumentNullException.ThrowIfNull(ownedLevels);
        int operatorRunning = Math.Max(0, runningAll - ownedRunningAll);
        int ownedTarget = Math.Max(0, effectiveTarget - operatorRunning);
        int[] have = new int[bands.Count];
        foreach (byte level in ownedLevels)
            if (PopulationBandPlanner.BandOf(bands, level) is var band and >= 0) have[band]++;
        return new PopulationCounts(PopulationBandPlanner.DesiredPerBand(ownedTarget, bands), have, operatorRunning, ownedTarget);
    }
}

/// <summary>What one pass changed: the reason of every bot parked, and the bots started, created and moved.</summary>
internal sealed class PopulationPassActions
{
    public List<string> Parked { get; } = [];

    public int Started { get; set; }

    public int Created { get; set; }

    public int Moved { get; set; }

    public bool Any => Parked.Count > 0 || Started > 0 || Created > 0 || Moved > 0;

    /// <summary>The reasons as <c>dead 2, sick 1</c>, in a fixed order (dead, sick, stalled, surplus, rotation), counts above 0 only.</summary>
    public static string Tally(IEnumerable<string> reasons)
    {
        Dictionary<string, int> counts = reasons.GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
        string[] known = ["dead", "sick", "stalled", "surplus", "rotation"];
        return string.Join(", ", known.Where(counts.ContainsKey).Concat(counts.Keys.Except(known).Order(StringComparer.Ordinal))
            .Select(r => string.Create(CultureInfo.InvariantCulture, $"{r} {counts[r]}")));
    }
}

/// <summary>
/// Rate-limits the population's information line: the changes of every pass are added up, and <see cref="Due"/> hands them out at
/// most once per <c>ActionLogSeconds</c> of world time (the first change at once). A pass that changes nothing adds nothing, but may
/// hand out what earlier passes added. Thread affinity: the population pass (one at a time).
/// </summary>
internal sealed class PopulationActionLog
{
    private readonly List<string> _parked = [];
    private int _started;
    private int _created;
    private int _moved;
    private int _passes;
    private long? _lastMs;

    public void Add(PopulationPassActions pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        if (!pass.Any) return;
        _parked.AddRange(pass.Parked);
        _started += pass.Started;
        _created += pass.Created;
        _moved += pass.Moved;
        _passes++;
    }

    /// <summary>The line's action part (<c>parked 3 (dead 1, sick 2), started 0, created 1, moved 0 in 2 passes</c>) when one is due, else null.</summary>
    public string? Due(long nowMs, int intervalSeconds)
    {
        if (_passes == 0) return null;
        if (_lastMs is { } last && nowMs - last < intervalSeconds * 1000L) return null;
        string parked = _parked.Count == 0 ? "parked 0" : $"parked {_parked.Count} ({PopulationPassActions.Tally(_parked)})";
        string line = string.Create(CultureInfo.InvariantCulture,
            $"{parked}, started {_started}, created {_created}, moved {_moved} in {_passes} pass{(_passes == 1 ? string.Empty : "es")}");
        _parked.Clear();
        _started = _created = _moved = _passes = 0;
        _lastMs = nowMs;
        return line;
    }
}
