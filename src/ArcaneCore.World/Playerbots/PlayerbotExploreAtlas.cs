using System.Numerics;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.World.Playerbots;

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
    private readonly Dictionary<(uint Map, int X, int Y), List<Threat>> _threats = [];

    internal readonly record struct Candidate(CreatureSpawn Spawn, int CellX, int CellY);
    private readonly record struct Threat(CreatureSpawn Spawn, byte MinLevel, uint Rank, uint Faction);

    private sealed class Cell
    {
        internal readonly Dictionary<uint, List<CreatureSpawn>> ByFaction = [];

        internal void Add(uint faction, CreatureSpawn spawn)
        {
            if (!ByFaction.TryGetValue(faction, out List<CreatureSpawn>? samples))
            {
                if (ByFaction.Count >= MaxFactionsPerCell) return;
                ByFaction.Add(faction, samples = []);
            }
            if (samples.Count < SamplesPerFactionCell)
                samples.Add(spawn);
        }
    }

    internal PlayerbotExploreAtlas(CreatureContent content)
    {
        foreach (uint map in content.MapsWithSpawns)
        {
            if (map is not (0 or 1)) continue; // continent travel only
            foreach (CreatureSpawn spawn in content.GetSpawns(map))
            {
                CreatureTemplate? template = content.FindTemplate(spawn.Entry);
                if (template is null || !float.IsFinite(spawn.X) || !float.IsFinite(spawn.Y) || !float.IsFinite(spawn.Z)
                    || template.CreatureType == 8
                    || (template.Behaviour & (CreatureBehaviourFlags.Civilian | CreatureBehaviourFlags.Invisible
                        | CreatureBehaviourFlags.NoAggro)) != 0 || (template.UnitFlags & (uint)ArcaneCore.Game.UnitFlags.NotSelectable) != 0)
                    continue;
                int x = CellOf(spawn.X), y = CellOf(spawn.Y);
                var threatKey = (map, ThreatCellOf(spawn.X), ThreatCellOf(spawn.Y));
                if (!_threats.TryGetValue(threatKey, out List<Threat>? threats))
                    _threats.Add(threatKey, threats = []);
                if (threats.Count < ThreatSamplesPerCell)
                    threats.Add(new Threat(spawn, template.MinLevel, template.Rank, template.Faction));
                else
                {
                    // Keep the strongest threats when a site has many spawns. Startup work is bounded per spawn,
                    // while the bot's later hazard check remains bounded by the sample size.
                    int weakest = 0;
                    for (int i = 1; i < threats.Count; i++)
                        if (Strength(threats[i]) < Strength(threats[weakest])) weakest = i;
                    var incoming = new Threat(spawn, template.MinLevel, template.Rank, template.Faction);
                    if (Strength(incoming) > Strength(threats[weakest])) threats[weakest] = incoming;
                }
                if (template.NpcFlags != 0 || template.Rank != 0) continue;

                // Template levels are a range: only index bots for which the entire possible range stays near their level.
                // The visible-target filter still applies the actual spawned level and the XP gray threshold.
                int first = Math.Max(1, template.MaxLevel - 2);
                int last = Math.Min(60, template.MinLevel + 3);
                for (int level = first; level <= last; level++)
                {
                    var key = (map, (byte)level, x, y);
                    if (!_cells.TryGetValue(key, out Cell? cell)) _cells.Add(key, cell = new Cell());
                    cell.Add(template.Faction, spawn);
                }
            }
        }
    }

    internal static int CellOf(float coordinate) => (int)MathF.Floor(coordinate / CellYards);
    private static int ThreatCellOf(float coordinate) => (int)MathF.Floor(coordinate / ThreatCellYards);
    private static int Strength(Threat threat) => (threat.Rank == 0 ? 0 : 100) + threat.MinLevel;

    /// <summary>A candidate near an elite or creature at least five levels higher is a poor unattended grinding site.</summary>
    internal bool HasStrongHazard(Candidate candidate, uint level, Func<uint, bool> attackableFaction)
    {
        const float radius2 = 70f * 70f;
        int cx = ThreatCellOf(candidate.Spawn.X), cy = ThreatCellOf(candidate.Spawn.Y);
        for (int x = cx - 1; x <= cx + 1; x++)
            for (int y = cy - 1; y <= cy + 1; y++)
            {
                if (!_threats.TryGetValue((candidate.Spawn.MapId, x, y), out List<Threat>? threats)) continue;
                foreach (Threat threat in threats)
                {
                    if (threat.Rank == 0 && threat.MinLevel < level + 5 || !attackableFaction(threat.Faction)) continue;
                    float dx = threat.Spawn.X - candidate.Spawn.X, dy = threat.Spawn.Y - candidate.Spawn.Y;
                    if (dx * dx + dy * dy <= radius2 && MathF.Abs(threat.Spawn.Z - candidate.Spawn.Z) <= 20f)
                        return true;
                }
            }
        return false;
    }

    /// <summary>
    /// One bounded spatial search. At most (2*MaxRing+1)^2 dictionary probes and 64 spawn checks per occupied cell;
    /// adding thousands of spawns to one cell does not add think-time work. The eligibility callback checks current faction,
    /// spawn gate, home and hazard state. The caller tests one route, then sets a refused or exhausted cell aside.
    /// </summary>
    internal Candidate? Find(uint map, uint level, Vector3 from, Func<CreatureSpawn, bool> eligible,
        Func<int, int, bool> blocked, out int examined, Func<uint, bool>? factionEligible = null)
    {
        examined = 0;
        if (map is not (0 or 1) || level is < 1 or > 60 || !float.IsFinite(from.X) || !float.IsFinite(from.Y)) return null;
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
                            if (!eligible(spawn)) continue;
                            float dx = spawn.X - from.X, dy = spawn.Y - from.Y;
                            float distance = dx * dx + dy * dy;
                            if (distance >= bestDistance) continue;
                            bestDistance = distance;
                            best = new Candidate(spawn, x, y);
                        }
                    }
                }
            }
            if (best is not null) return best;
        }
        return null;
    }
}
