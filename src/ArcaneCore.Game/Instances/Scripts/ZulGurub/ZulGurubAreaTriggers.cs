using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// mangos-classic zulgurub.cpp AreaTrigger_at_zulgurub / instance_zulgurub::DoYellAtTriggerIfCan: a living non-GM player at 3958 (the
/// entrance) makes Hakkar zone-yell SAY_HAKKAR_PROTECT (broadcast_text 10546), at 3960 (the altar) SAY_MINION_DESTROY (10594), each once
/// per instance. Limit: cmangos simulates the yell when Hakkar is not loaded; this needs Hakkar in the map and skips the yell otherwise
/// (without spending it).
/// </summary>
public sealed partial class ZulGurubInstance
{
    public const uint AreaTriggerEnter = 3958, AreaTriggerAltar = 3960;
    public bool HasIntroYelled { get; private set; }
    public bool HasAltarYelled { get; private set; }

    public override void OnAreaTrigger(Player player, uint triggerId)
    {
        if (triggerId is not (AreaTriggerEnter or AreaTriggerAltar) || player.IsGameMaster || !player.IsAlive) return;
        if (GetSingleCreatureFromStorage(14834) is not { } hakkar || hakkar.System is not { } system) return;
        if (triggerId == AreaTriggerEnter && !HasIntroYelled)
        {
            system.ZoneYell(hakkar, 10546);
            HasIntroYelled = true;
        }
        else if (triggerId == AreaTriggerAltar && !HasAltarYelled)
        {
            system.ZoneYell(hakkar, 10594);
            HasAltarYelled = true;
        }
    }
}
