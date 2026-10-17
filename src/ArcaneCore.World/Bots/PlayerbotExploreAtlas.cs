using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.World.Bots;

/// <summary>
/// Startup index of the world's static creature spawns for optional grinding. The bot asks only for nearby cells of its own
/// level band; the live target and combat rules still decide whether an arrived creature can actually be attacked.
/// </summary>
internal sealed class PlayerbotExploreAtlas
{
    internal const float CellYards = 160f;
    internal const int MaxRing = 40;
    internal const int SamplesPerFactionCell = 4;
    internal const int MaxFactionsPerCell = 16;
    internal const int ThreatSamplesPerCell = 24;
    private const float ThreatCellYards = 80f;

    private readonly Dictionary<(uint Map, byte Level, int X, int Y), Cell> _cells = [];
    private readonly Dictionary<(uint Map, int X, int Y), ThreatCell> _threats = [];
    private readonly HashSet<uint> _mapsIndexed = [];
    private int _entryCount;

    internal int CellCount => _cells.Count;
    internal int EntryCount => _entryCount;
    internal int ThreatCellCount => _threats.Count;
    internal bool HasIndexedMap(uint mapId) => _mapsIndexed.Contains(mapId);

    internal readonly record struct Candidate(CreatureSpawn Spawn, int CellX, int CellY);
    private readonly record struct Threat(CreatureSpawn Spawn, byte MinLevel, uint Rank, uint Faction);

    private sealed class Cell
    {
        internal readonly Dictionary<uint, List<CreatureSpawn>> ByFaction = [];

        internal bool Add(uint faction, CreatureSpawn spawn)
        {
            if (!ByFaction.TryGetValue(faction, out List<CreatureSpawn>? samples))
            {
                if (ByFaction.Count >= MaxFactionsPerCell) return false;
                ByFaction.Add(faction, samples = []);
            }
            if (samples.Count < SamplesPerFactionCell)
            {
                samples.Add(spawn);
                return true;
            }
            return false;
        }
    }

    private sealed class ThreatCell
    {
        internal readonly Dictionary<uint, List<Threat>> ByFaction = [];

        internal void Add(Threat incoming)
        {
            if (!ByFaction.TryGetValue(incoming.Faction, out List<Threat>? samples))
            {
                if (ByFaction.Count >= MaxFactionsPerCell) return;
                ByFaction.Add(incoming.Faction, samples = []);
            }
            if (samples.Count < ThreatSamplesPerCell)
            {
                samples.Add(incoming);
                return;
            }
            int weakest = 0;
            for (int i = 1; i < samples.Count; i++)
                if (Strength(samples[i]) < Strength(samples[weakest])) weakest = i;
            if (Strength(incoming) > Strength(samples[weakest])) samples[weakest] = incoming;
        }
    }

    internal PlayerbotExploreAtlas(CreatureContent spawns, CreatureContent? definitions = null)
    {
        definitions ??= spawns;
        foreach (uint map in spawns.MapsWithSpawns)
        {
            if (map is not (0 or 1)) continue; // continent travel only
            foreach (CreatureSpawn spawn in spawns.GetSpawns(map))
            {
                CreatureTemplate? template = definitions.FindTemplate(spawn.Entry);
                if (template is null || !float.IsFinite(spawn.X) || !float.IsFinite(spawn.Y) || !float.IsFinite(spawn.Z)
                    || template.CreatureType == CreatureAiFactory.CritterType
                    || (template.Behaviour & (CreatureBehaviourFlags.Civilian | CreatureBehaviourFlags.Invisible
                        | CreatureBehaviourFlags.NoAggro)) != 0 || (template.UnitFlags & (uint)ArcaneCore.Game.UnitFlags.NotSelectable) != 0)
                    continue;
                int x = CellOf(spawn.X), y = CellOf(spawn.Y);
                var threatKey = (map, ThreatCellOf(spawn.X), ThreatCellOf(spawn.Y));
                if (!_threats.TryGetValue(threatKey, out ThreatCell? threats))
                    _threats.Add(threatKey, threats = new ThreatCell());
                threats.Add(new Threat(spawn, template.MinLevel, template.Rank, template.Faction));
                if (template.NpcFlags != 0 || template.Rank != 0) continue;

                // Template levels are a range: only index bots for which the entire possible range stays near their level.
                // The visible-target filter still applies the actual spawned level and the XP gray threshold.
                int first = Math.Max(1, template.MaxLevel - 2);
                int last = Math.Min(60, template.MinLevel + 3);
                for (int level = first; level <= last; level++)
                {
                    var key = (map, (byte)level, x, y);
                    if (!_cells.TryGetValue(key, out Cell? cell)) _cells.Add(key, cell = new Cell());
                    if (cell.Add(template.Faction, spawn))
                    {
                        _entryCount++;
                        _mapsIndexed.Add(map);
                    }
                }
            }
        }
    }

    internal static int CellOf(float coordinate) => (int)MathF.Floor(coordinate / CellYards);
    private static int ThreatCellOf(float coordinate) => (int)MathF.Floor(coordinate / ThreatCellYards);
    private static int Strength(Threat threat) => (threat.Rank == 0 ? 0 : 100) + threat.MinLevel;

    /// <summary>
    /// Whether any cell within <paramref name="rings"/> rings of <paramref name="from"/> lists a creature for a bot of
    /// <paramref name="level"/> (at most (2*rings+1)^2 dictionary probes). It ignores eligibility, hazards and blocked cells: it
    /// answers only whether the area holds anything of the bot's level band at all.
    /// </summary>
    internal bool HasCellNear(uint map, uint level, Vector3 from, int rings)
    {
        if (level is < 1 or > 60 || !float.IsFinite(from.X) || !float.IsFinite(from.Y)) return false;
        int cx = CellOf(from.X), cy = CellOf(from.Y);
        for (int x = cx - rings; x <= cx + rings; x++)
            for (int y = cy - rings; y <= cy + rings; y++)
                if (_cells.TryGetValue((map, (byte)level, x, y), out Cell? cell) && cell.ByFaction.Count > 0) return true;
        return false;
    }

    /// <summary>
    /// Whether a creature more than <paramref name="margin"/> levels above <paramref name="level"/> stands within
    /// <paramref name="rings"/> rings of <paramref name="from"/> (the threat index, which keeps the strongest samples of each
    /// 80-yard cell): the bot is under-levelled for the area, not merely in a place with nothing to fight (a city, a low zone).
    /// The square is the one <see cref="HasCellNear"/> searches. A radius of <c>rings</c> explore-cells measured from the single
    /// threat cell under the bot stops one threat cell short of that square's far side (the bot occupies only one half of its
    /// own explore cell), so the stronger creature in the missed strip was invisible and the bot stayed on Explore.
    /// </summary>
    internal bool HasStrongerNear(uint map, uint level, Vector3 from, int rings, int margin)
    {
        if (!float.IsFinite(from.X) || !float.IsFinite(from.Y) || rings < 0) return false;
        (int x0, int x1) = ThreatSpan(CellOf(from.X), rings);
        (int y0, int y1) = ThreatSpan(CellOf(from.Y), rings);
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                if (_threats.TryGetValue((map, x, y), out ThreatCell? cell))
                    foreach (List<Threat> samples in cell.ByFaction.Values)
                        foreach (Threat threat in samples)
                            if (threat.MinLevel > level + margin) return true;
        return false;
    }

    /// <summary>
    /// Threat cells overlapping explore cells <c>[cell - rings, cell + rings]</c>. Explore cell <c>e</c> is threat cells
    /// <c>scale*e</c> and <c>scale*e + scale - 1</c> (<see cref="CellYards"/> is <c>scale</c> times the threat cell), including
    /// when <c>e</c> is negative.
    /// </summary>
    private static (int First, int Last) ThreatSpan(int exploreCell, int rings)
    {
        int scale = (int)(CellYards / ThreatCellYards);
        int firstCell = exploreCell - rings, lastCell = exploreCell + rings;
        return (firstCell * scale, lastCell * scale + (scale - 1));
    }

    /// <summary>A candidate near an elite or creature at least five levels higher is a poor unattended grinding site.</summary>
    internal bool HasStrongHazard(Candidate candidate, uint level, Func<uint, bool> hostileFaction)
    {
        const float radius2 = 70f * 70f;
        int cx = ThreatCellOf(candidate.Spawn.X), cy = ThreatCellOf(candidate.Spawn.Y);
        for (int x = cx - 1; x <= cx + 1; x++)
            for (int y = cy - 1; y <= cy + 1; y++)
            {
                if (!_threats.TryGetValue((candidate.Spawn.MapId, x, y), out ThreatCell? threats)) continue;
                foreach ((uint faction, List<Threat> samples) in threats.ByFaction)
                {
                    if (!hostileFaction(faction)) continue;
                    foreach (Threat threat in samples)
                    {
                        if (threat.Rank == 0 && threat.MinLevel < level + 5) continue;
                        float dx = threat.Spawn.X - candidate.Spawn.X, dy = threat.Spawn.Y - candidate.Spawn.Y;
                        if (dx * dx + dy * dy <= radius2 && MathF.Abs(threat.Spawn.Z - candidate.Spawn.Z) <= 20f)
                            return true;
                    }
                }
            }
        return false;
    }

    /// <summary>
    /// One bounded spatial search. At most (2*MaxRing+1)^2 dictionary probes and 64 spawn checks per occupied cell;
    /// adding thousands of spawns to one cell does not add think-time work. The eligibility callback checks current faction,
    /// spawn gate, home and hazard state. The caller tests one route, then sets a refused or exhausted cell aside.
    /// </summary>
    internal Candidate? Find(uint map, uint level, Vector3 from, Func<Candidate, bool> eligible,
        Func<int, int, bool> blocked, out int examined, Func<uint, bool>? factionEligible = null)
    {
        examined = 0;
        if (!HasIndexedMap(map) || level is < 1 or > 60 || !float.IsFinite(from.X) || !float.IsFinite(from.Y)) return null;
        int cx = CellOf(from.X), cy = CellOf(from.Y);
        for (int ring = 0; ring <= MaxRing; ring++)
        {
            Candidate? best = null;
            float bestDistance = float.PositiveInfinity;
            for (int x = cx - ring; x <= cx + ring; x++)
            {
                for (int y = cy - ring; y <= cy + ring; y++)
                {
                    if (ring != 0 && x != cx - ring && x != cx + ring && y != cy - ring && y != cy + ring) continue;
                    if (blocked(x, y) || !_cells.TryGetValue((map, (byte)level, x, y), out Cell? cell)) continue;
                    foreach ((uint faction, List<CreatureSpawn> samples) in cell.ByFaction)
                    {
                        if (factionEligible is not null && !factionEligible(faction)) continue;
                        foreach (CreatureSpawn spawn in samples)
                        {
                            examined++;
                            var candidate = new Candidate(spawn, x, y);
                            if (!eligible(candidate)) continue;
                            float dx = spawn.X - from.X, dy = spawn.Y - from.Y;
                            float distance = dx * dx + dy * dy;
                            if (distance >= bestDistance) continue;
                            bestDistance = distance;
                            best = candidate;
                        }
                    }
                }
            }
            if (best is not null) return best;
        }
        return null;
    }
}
