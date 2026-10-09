using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>mangos-classic zulgurub/zulgurub.cpp OnObjectCreate/SetData(TYPE_ARLOKK)
/// and boss_arlokk.cpp GOUse_go_gong_of_bethekk.</summary>
public sealed partial class ZulGurubInstance
{
    public override void OnObjectCreate(GameObject go)
    {
        if (go.Entry is 180526 or 180497)
        {
            StoreGameObject(go);
            if (go.Entry == 180497)
                OpenIf(go, GetData(4) is EncounterState.Done or EncounterState.Fail);
        }
    }

    public override void OnObjectUsed(Player player, GameObject go)
    {
        if (go.Entry == 180526 && GetData(4) is not (EncounterState.Done or EncounterState.InProgress))
        {
            SetData(4, EncounterState.InProgress);
            // ClassicDB z2815 dbscripts_on_event 9066 is the gong's summon:
            // creature 14515, fifteen-minute timed spawn at these coordinates.
            // The GO host does not dispatch its data2 event script yet.
            if (Instance.FindUpdater<CreatureMapSystem>() is { } creatures &&
                !creatures.Creatures.Any(c => c.Entry == 14515 && c.IsAlive))
                creatures.SummonInstanceCreatureTimedOocOrDead(
                    14515, -11540.7f, -1627.71f, 41.27f, 0.1f, 900000);
        }
    }
}
