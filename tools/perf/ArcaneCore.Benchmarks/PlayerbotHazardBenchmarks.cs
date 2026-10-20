using System.Numerics;
using ArcaneCore.World.Playerbots;
using BenchmarkDotNet.Attributes;

namespace ArcaneCore.Benchmarks;

/// <summary>
/// The death-loop check a managed bot's target choice (PlayerbotRisk.ChooseTarget through PlayerbotBrain.CollectTargets) runs for
/// every creature in sight: <see cref="CreaturesInSight"/> positions, half inside one death-loop zone, against a hazard list that also
/// holds death and retreat marks (perf-tick 2026-10-20, docs/integration/perf-tick-20261020.md). Synthetic hazards, not world content.
/// </summary>
[MemoryDiagnoser]
public class PlayerbotHazardBenchmarks
{
    private const uint Now = 1_000;
    private static readonly Vector3 Zone = new(1900f, -450f, 35f);

    private readonly PlayerbotHazards _hazards = new();
    private Vector3[] _creatures = [];

    [Params(32)]
    public int CreaturesInSight { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _hazards.RememberDeathLoop(0, Zone, Now, 300, [6]);
        for (int i = 0; i < 6; i++)
        {
            _hazards.Add(0, Zone + new Vector3(150f + (i * 60f), 0f, 0f), PlayerbotHazards.DeathYards, Now, 300, i % 2 == 0 ? "death" : "retreat");
        }

        _creatures = new Vector3[CreaturesInSight];
        for (int i = 0; i < _creatures.Length; i++)
        {
            _creatures[i] = Zone + new Vector3(i % 2 == 0 ? i : 100f + i, 3f, 0f);
        }
    }

    [Benchmark]
    public int CoversDeathLoop()
    {
        int covered = 0;
        foreach (Vector3 creature in _creatures)
        {
            if (_hazards.CoversDeathLoop(0, creature, Now))
            {
                covered++;
            }
        }

        return covered;
    }
}
