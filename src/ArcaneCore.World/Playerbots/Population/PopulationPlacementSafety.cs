using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>Choose a graveyard for one actual bot, after its level and faction are known.</summary>
internal static class PopulationPlacementSafety
{
    // The zone's minimum sub-area level is deliberately loose for population coverage. A graveyard in a much higher
    // sub-area, or beside much higher hostile spawns, is not a suitable starting point for that particular bot.
    internal const int MaxLevelGap = 3;
    internal const float NearbySpawnYards = 90f;

    internal static WorldSafeLoc? Choose(WorldSafeLoc preferred, IReadOnlyList<WorldSafeLoc> candidates, byte botLevel,
        uint botFaction, Func<WorldSafeLoc, int> areaLevelAt, CreatureContent creatures, FactionTemplateCatalog factions)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(areaLevelAt);
        ArgumentNullException.ThrowIfNull(creatures);
        ArgumentNullException.ThrowIfNull(factions);

        if (Safe(preferred)) return preferred;
        foreach (WorldSafeLoc candidate in candidates)
            if (candidate.Id != preferred.Id && candidate.MapId == preferred.MapId && Safe(candidate)) return candidate;
        return null;

        bool Safe(WorldSafeLoc location)
        {
            int areaLevel = areaLevelAt(location);
            if (areaLevel > botLevel + MaxLevelGap) return false;
            FactionTemplateRecord? bot = factions.Find(botFaction);
            if (bot is null) return true; // no loaded faction templates means no data-backed hostility verdict
            foreach (CreatureSpawn spawn in creatures.GetSpawns(location.MapId))
            {
                float reach = NearbySpawnYards + MathF.Max(0, spawn.WanderDistance);
                float dx = spawn.X - location.X, dy = spawn.Y - location.Y;
                if ((dx * dx) + (dy * dy) > reach * reach) continue;
                CreatureTemplate? template = creatures.FindTemplate(spawn.Entry);
                if (template is null || template.MaxLevel <= botLevel + MaxLevelGap) continue;
                if (factions.Find(template.Faction) is { } npc && npc.IsHostileTo(bot)) return false;
            }

            return true;
        }
    }
}
