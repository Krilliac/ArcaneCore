using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>Data-backed graveyard safety for one bot level and race faction, evaluated before population creates or moves it.</summary>
internal static class PopulationPlacementSafety
{
    // ArcaneCore placement buffers, not retail aggro distances. Zone-level slack permits level one in a level-five
    // starting area; the local-area veto must not contradict it when no hostile spawn is near the graveyard.
    internal const int MaxLevelGap = 3;
    internal const int StarterAreaGap = 4;
    internal const float NearbySpawnYards = 90f;

    internal static bool IsSafe(WorldSafeLoc location, byte botLevel, uint botFaction, int areaLevel,
        PopulationSpawnIndex spawns, FactionTemplateCatalog factions)
    {
        ArgumentNullException.ThrowIfNull(spawns);
        ArgumentNullException.ThrowIfNull(factions);
        if (areaLevel > botLevel + (botLevel == 1 ? StarterAreaGap : MaxLevelGap)) return false;

        float search = NearbySpawnYards + spawns.MaxWander(location.MapId);
        int firstX = PopulationSpawnIndex.Cell(location.X - search), lastX = PopulationSpawnIndex.Cell(location.X + search);
        int firstY = PopulationSpawnIndex.Cell(location.Y - search), lastY = PopulationSpawnIndex.Cell(location.Y + search);
        for (int x = firstX; x <= lastX; x++)
            for (int y = firstY; y <= lastY; y++)
                foreach (CreatureSpawn spawn in spawns.InCell(location.MapId, x, y))
                {
                    float reach = NearbySpawnYards + MathF.Max(0f, spawn.WanderDistance);
                    float dx = spawn.X - location.X, dy = spawn.Y - location.Y;
                    if (dx * dx + dy * dy > reach * reach) continue;

                    IReadOnlyList<uint> alternatives = spawns.Content.GetSpawnEntries(spawn.Guid);
                    if (spawn.Entry == 0 && alternatives.Count == 0) return false; // unresolved pooled spawn
                    if (spawn.Entry != 0 && Dangerous(spawn.Entry)) return false;
                    foreach (uint entry in alternatives)
                        if (entry != spawn.Entry && Dangerous(entry)) return false;
                }

        return true;

        bool Dangerous(uint entry)
        {
            CreatureTemplate? template = spawns.Content.FindTemplate(entry);
            if (template is null) return true;
            if (template.MaxLevel <= botLevel + MaxLevelGap) return false;
            // Static gates of CreatureMapSystem.CanInitiateAttack and CreatureAggro.InitialReactState.
            if (CreatureAggro.InitialReactState(template) != CreatureReactState.Aggressive
                || ((UnitFlags)template.UnitFlags & (UnitFlags.Stunned | UnitFlags.Spawning | UnitFlags.NotSelectable)) != 0)
                return false;
            if (factions.TryNpcHostility(template.Faction, botFaction, out bool hostile)) return hostile;

            // TryNpcHostility cannot settle reputation-capable factions without player state. A known friendly template
            // reaction is safe for a newly provisioned character; missing templates and neutral reactions are not.
            FactionTemplateRecord? npc = factions.Find(template.Faction), bot = factions.Find(botFaction);
            return npc is null || bot is null || npc.IsHostileTo(bot)
                || !(npc.IsFriendlyTo(bot) || bot.IsFriendlyTo(npc));
        }
    }

    /// <summary>A small pure seam for unit tests; the population feature uses the indexed <see cref="IsSafe"/> directly.</summary>
    internal static WorldSafeLoc? Choose(WorldSafeLoc preferred, IReadOnlyList<WorldSafeLoc> candidates, byte botLevel,
        uint botFaction, Func<WorldSafeLoc, int> areaLevelAt, CreatureContent creatures, FactionTemplateCatalog factions)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(areaLevelAt);
        var index = new PopulationSpawnIndex(creatures);
        if (IsSafe(preferred, botLevel, botFaction, areaLevelAt(preferred), index, factions)) return preferred;
        foreach (WorldSafeLoc candidate in candidates)
            if (candidate.Id != preferred.Id && candidate.MapId == preferred.MapId
                && IsSafe(candidate, botLevel, botFaction, areaLevelAt(candidate), index, factions)) return candidate;
        return null;
    }
}
