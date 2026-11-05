using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>mangos-classic zulgurub/zulgurub.cpp OnObjectCreate/SetData(TYPE_ARLOKK)
/// and boss_arlokk.cpp GOUse_go_gong_of_bethekk.</summary>
public sealed partial class ZulGurubInstance
{
    public const uint GoSpiderEgg = 179985, GoGongOfBethekk = 180526;

    /// <summary>ScriptedInstance::DoRespawnGameObject's despawn-timer stand-in for <c>SetRespawnTime(2 * DAY)</c>.</summary>
    public const uint ManualRespawnSeconds = 2 * 24 * 60 * 60;

    /// <summary><c>m_lSpiderEggGUIDList</c>.</summary>
    private readonly List<ObjectGuid> _spiderEggs = [];

    /// <summary>The spider eggs created so far, in creation order.</summary>
    internal IReadOnlyList<ObjectGuid> SpiderEggs => _spiderEggs;

    public override void OnObjectCreate(GameObject go)
    {
        if (go.Entry == GoSpiderEgg)
        {
            _spiderEggs.Add(go.Guid);
        }

        if (go.Entry is GoGongOfBethekk or GoForcefield)
        {
            StoreGameObject(go);
            if (go.Entry == GoForcefield)
                OpenIf(go, GetData(4) is EncounterState.Done or EncounterState.Fail);
        }
    }

    /// <summary>
    /// zulgurub.cpp SetData(TYPE_MARLI, FAIL): every stored spider egg is respawned (its respawn time 2 days, "this type of Gameobject needs to be
    /// respawned manually"). Mar'li's AI also readies the eggs of a live map; this covers an egg that was despawned.
    /// </summary>
    private void RespawnSpiderEggs()
    {
        if (Instance.FindUpdater<CreatureMapSystem>() is not { } creatures || Instance.FindUpdater<GameObjectMapSystem>() is not { } objects)
        {
            return;
        }

        foreach (ObjectGuid guid in _spiderEggs)
        {
            if (objects.Find(guid) is { } egg)
            {
                creatures.RespawnGameObjectFor(egg, ManualRespawnSeconds);
                if (egg.IsSpawned)
                {
                    egg.State = GameObjectState.Ready;
                }
            }
        }
    }

    /// <summary>zulgurub.cpp SetData(TYPE_ARLOKK, FAIL): the stored gong of Bethekk is respawned (respawn time 2 days).</summary>
    private void RespawnGong()
    {
        if (GetSingleGameObjectFromStorage(GoGongOfBethekk) is { } gong && Instance.FindUpdater<CreatureMapSystem>() is { } creatures)
        {
            creatures.RespawnGameObjectFor(gong, ManualRespawnSeconds);
        }
    }

    public override void OnObjectUsed(Player player, GameObject go)
    {
        if (go.Entry == GoGongOfBethekk && GetData(4) is not (EncounterState.Done or EncounterState.InProgress))
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
