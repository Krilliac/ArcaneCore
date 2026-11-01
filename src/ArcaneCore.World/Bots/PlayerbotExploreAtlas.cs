using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.World.Bots;

/// <summary>
/// Startup index of the world's static creature spawns for optional grinding. The bot asks only for nearby cells of its own
/// level band; the live target and combat rules still decide whether an arrived creature can actually be attacked.
/// </summary>
public sealed class PlayerbotExploreAtlas
{
    public const float CellYards = 160f;
    public const int MaxRing = 40;
    public const int SamplesPerFactionCell = 4;
    public const int MaxFactionsPerCell = 16;
    public const int ThreatSamplesPerCell = 24;
    private const float ThreatCellYards = 80f;

    private readonly Dictionary<(uint Map, byte Level, int X, int Y), Cell> _cells = [];
    private readonly Dictionary<(uint Map, int X, int Y), ThreatCell> _threats = [];
    private readonly HashSet<uint> _mapsIndexed = [];
    private int _entryCount;

    public int CellCount => _cells.Count;
    public int EntryCount => _entryCount;
    public int ThreatCellCount => _threats.Count;
    public bool HasIndexedMap(uint mapId) => _mapsIndexed.Contains(mapId);

    public readonly record struct Candidate(CreatureSpawn Spawn, int CellX, int CellY);
    private readonly record struct Threat(CreatureSpawn Spawn, byte MinLevel, uint Rank, uint Faction);

    private sealed class Cell
    {
        public readonly Dictionary<uint, List<CreatureSpawn>> ByFaction = [];

        public bool Add(uint faction, CreatureSpawn spawn)
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
        public readonly Dictionary<uint, List<Threat>> ByFaction = [];

        public void Add(Threat incoming)
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

    public PlayerbotExploreAtlas(CreatureContent spawns, CreatureContent? definitions = null)
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

    public static int CellOf(float coordinate) => (int)MathF.Floor(coordinate / CellYards);
    private static int ThreatCellOf(float coordinate) => (int)MathF.Floor(coordinate / ThreatCellYards);
    private static int Strength(Threat threat) => (threat.Rank == 0 ? 0 : 100) + threat.MinLevel;

    /// <summary>
    /// Whether any cell within <paramref name="rings"/> rings of <paramref name="from"/> lists a creature for a bot of
    /// <paramref name="level"/> (at most (2*rings+1)^2 dictionary probes). It ignores eligibility, hazards and blocked cells: it
    /// answers only whether the area holds anything of the bot's level band at all.
    /// </summary>
    public bool HasCellNear(uint map, uint level, Vector3 from, int rings)
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
    public bool HasStrongerNear(uint map, uint level, Vector3 from, int rings, int margin)
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
    public bool HasStrongHazard(Candidate candidate, uint level, Func<uint, bool> hostileFaction)
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
    /// The state of one resumable <see cref="Find"/> search (<see cref="FindStep"/>): where the ring walk stands and the best candidate of
    /// the ring under way. One cursor per search; owned by the caller and used on one thread (the world thread). The search measures
    /// distances from the point it began at and is dropped by the caller when the bot, map or level it was begun for changes.
    /// </summary>
    public sealed class FindCursor
    {
        internal bool Started;
        internal uint Map;
        internal uint Level;
        internal Vector3 From;
        internal int CentreX, CentreY, Ring, X, Y;
        internal Candidate? Best;
        internal float BestDistance;

        /// <summary>The point the search was begun at.</summary>
        public Vector3 SearchFrom => From;

        /// <summary>The map the search was begun on.</summary>
        public uint SearchMap => Map;

        /// <summary>The level the search was begun for.</summary>
        public uint SearchLevel => Level;

        /// <summary>The ring (0 = the bot's own cell) the walk has reached.</summary>
        public int CurrentRing => Ring;
    }

    /// <summary>How <see cref="FindStep"/> ended.</summary>
    public enum FindStatus : byte
    {
        /// <summary>A candidate was found (the nearest of the first ring that held an eligible one); the cursor is spent.</summary>
        Found,

        /// <summary>Every ring up to <see cref="MaxRing"/> was searched and none held an eligible candidate; the cursor is spent.</summary>
        Exhausted,

        /// <summary>The budget ran out first: call <see cref="FindStep"/> again with the same cursor to resume where it stopped.</summary>
        Pending,
    }

    /// <summary>
    /// One bounded spatial search. At most (2*MaxRing+1)^2 dictionary probes and 64 spawn checks per occupied cell;
    /// adding thousands of spawns to one cell does not add think-time work. The eligibility callback checks current faction,
    /// spawn gate, home and hazard state. The caller tests one route, then sets a refused or exhausted cell aside.
    /// This runs the whole search in one call; <see cref="FindStep"/> is the same search in budgeted slices.
    /// </summary>
    public Candidate? Find(uint map, uint level, Vector3 from, Func<Candidate, bool> eligible,
        Func<int, int, bool> blocked, out int examined, Func<uint, bool>? factionEligible = null)
    {
        var cursor = new FindCursor();
        FindStatus status = FindStep(cursor, map, level, from, eligible, blocked, int.MaxValue, null, out Candidate? found, out examined, factionEligible);
        return status == FindStatus.Found ? found : null;
    }

    /// <summary>
    /// <see cref="Find"/> in slices: it works until <paramref name="maxExamined"/> spawns have been checked in this call or
    /// <paramref name="outOfBudget"/> (read after each occupied cell; null: never) says stop, then returns <see cref="FindStatus.Pending"/>
    /// with <paramref name="cursor"/> holding the walk, and the next call with the same cursor carries on from the next cell. At least one
    /// occupied cell is searched per call, so repeated calls always finish. A slice ends only between cells, so it overruns
    /// <paramref name="maxExamined"/> by at most one cell (64 spawns per faction sample list, <see cref="MaxFactionsPerCell"/> factions).
    /// The answer is the one <see cref="Find"/> gives for the same state: the cell order, the ring rule and the nearest-of-ring rule are
    /// unchanged. <paramref name="examined"/> is this call's count. A fresh cursor begins a search at <paramref name="from"/>; on a resumed
    /// cursor <paramref name="map"/>, <paramref name="level"/> and <paramref name="from"/> are ignored (the search keeps those it began with).
    /// </summary>
    public FindStatus FindStep(FindCursor cursor, uint map, uint level, Vector3 from, Func<Candidate, bool> eligible,
        Func<int, int, bool> blocked, int maxExamined, Func<bool>? outOfBudget, out Candidate? found, out int examined,
        Func<uint, bool>? factionEligible = null)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        found = null;
        examined = 0;
        if (!cursor.Started)
        {
            if (!HasIndexedMap(map) || level is < 1 or > 60 || !float.IsFinite(from.X) || !float.IsFinite(from.Y)) return FindStatus.Exhausted;
            cursor.Started = true;
            cursor.Map = map;
            cursor.Level = level;
            cursor.From = from;
            cursor.CentreX = CellOf(from.X);
            cursor.CentreY = CellOf(from.Y);
            cursor.Ring = 0;
            cursor.X = cursor.CentreX;
            cursor.Y = cursor.CentreY;
            cursor.Best = null;
            cursor.BestDistance = float.PositiveInfinity;
        }

        map = cursor.Map;
        level = cursor.Level;
        from = cursor.From;
        int cx = cursor.CentreX, cy = cursor.CentreY;
        while (cursor.Ring <= MaxRing)
        {
            int ring = cursor.Ring;
            while (cursor.X <= cx + ring)
            {
                while (cursor.Y <= cy + ring)
                {
                    int x = cursor.X, y = cursor.Y;
                    cursor.Y++;
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
                            if (distance >= cursor.BestDistance) continue;
                            cursor.BestDistance = distance;
                            cursor.Best = candidate;
                        }
                    }

                    if (examined >= maxExamined || outOfBudget?.Invoke() == true) return FindStatus.Pending;
                }

                cursor.X++;
                cursor.Y = cy - ring;
            }

            if (cursor.Best is { } best)
            {
                found = best;
                cursor.Best = null;
                return FindStatus.Found;
            }

            cursor.Ring++;
            cursor.X = cx - cursor.Ring;
            cursor.Y = cy - cursor.Ring;
        }

        return FindStatus.Exhausted;
    }
}
