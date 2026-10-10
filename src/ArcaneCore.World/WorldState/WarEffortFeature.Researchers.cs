using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.WorldState;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// vmangos events 62-64 ("AQ War - Secrets of the Colossus Ashi / Regal / Zora"): each Colossus researcher appears only once its
/// Colossus has been killed (npc_colossus::JustDied sets VAR_WE_HIVE_REWARD; UpdateHiveColossusEvents starts the event that spawns
/// him). ClassicDB puts the three researchers in the ten-hour-war event 123 from the start; here their database spawns are kept off
/// the map (script-only spawns) and brought in from the saved Colossus death flags, so his quest giver shows with the event 125-127
/// quest it offers. The flags survive a restart (vmangos deliberately forgets them on a crash).
/// </summary>
public sealed partial class WarEffortFeature
{
    /// <summary>Researcher entry by Colossus index (Ashi, Regal, Zora): vmangos events 62, 63 and 64.</summary>
    internal static readonly uint[] ColossusResearchers = [15798, 15799, 15797];

    private readonly Dictionary<CreatureMapSystem, uint[][]> _researcherSpawns = [];

    internal static bool ResearcherShown(WarEffortSnapshot state, int bossIndex)
        => state.Phase == WarEffortPhase.TenHourWar && (state.KilledBossMask & (1 << bossIndex)) != 0;

    private void SyncResearchers(CreatureMapSystem silithus)
    {
        if (!_researcherSpawns.TryGetValue(silithus, out uint[][]? guids))
        {
            guids = ColossusResearchers
                .Select(entry => silithus.Content.GetSpawns(1, entry).Where(s => s.X < -6000 && s.Y > 300).Select(s => s.Guid).ToArray())
                .ToArray();
            _researcherSpawns[silithus] = guids;
            silithus.RegisterScriptOnlySpawns(guids.SelectMany(g => g)); // also removes any already loaded
        }

        WarEffortSnapshot state = Snapshot;
        for (int boss = 0; boss < guids.Length; boss++)
        {
            bool shown = ResearcherShown(state, boss);
            foreach (uint guid in guids[boss])
            {
                Creature? loaded = silithus.Creatures.FirstOrDefault(c => c.Spawn?.Guid == guid);
                // SpawnScripted keeps the spawn's own gating, so he still needs event 123 running.
                if (shown && loaded is null) silithus.SpawnScripted(guid);
                else if (!shown && loaded is not null) silithus.Despawn(loaded);
            }
        }
    }
}
