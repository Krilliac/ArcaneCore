using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Scholomance;

/// <summary>
/// ScriptDev2 instance_scholomance (mangos-classic
/// src/game/AI/ScriptDevAI/scripts/eastern_kingdoms/scholomance/instance_scholomance.cpp:
/// OnObjectCreate, SetData, DoSpawnGandlingIfCan, HandlePortalEvent, OnCreatureEnterCombat,
/// OnCreatureEvade, OnCreatureDeath). The Kirtonos brazier summon is a DB script; his gate
/// and combat state belong to this instance script.
/// </summary>
[InstanceScript(MapId)]
public sealed class ScholomanceInstance(Map instance) : ScriptedInstance(instance, 10)
{
    public const uint MapId = 289;
    public const uint TypeKirtonos = 0, TypeRattlegore = 1, TypeRas = 2,
        TypeMalicia = 3, TypeTheolen = 4, TypePolkelt = 5, TypeRavenian = 6,
        TypeAlexeiBarov = 7, TypeIlluciaBarov = 8, TypeGandling = 9;

    public const uint GoKirtonosGate = 175570, GoViewingRoomDoor = 175167,
        GoRasGate = 177370, GoGandlingGate = 177374;
    public const uint GoBrazierOfTheHerald = 175564;
    public const uint NpcKirtonos = 10506, NpcGandling = 1853, NpcGuardian = 11598;

    private static readonly uint[] RoomDoors = [177375, 177377, 177376, 177372, 177373, 177371];
    private static readonly uint[] BossEntries = [10506, 11622, 10508, 10505, 11261, 10901, 10507, 10504, 10502, NpcGandling];
    private static readonly uint[] PortalEvents = [5620, 5619, 5618, 5623, 5622, 5621];
    // ClassicDB_1_12_1_z2815 dbscripts_on_event, command 10 for events 5618-5623:
    // spawn the 3-4 Risen Guardians two seconds after Gandling's SPELL_EFFECT_SEND_EVENT.
    private static readonly Dictionary<uint, (float X, float Y, float Z, float O)[]> PortalGuardians = new()
    {
        [5618] = [(256.289f, 0.652f, 84.924f, 4.765f), (241.345f, 4.231f, 84.924f, 5.062f),
            (249.715f, -5.978f, 85.106f, 3.177f), (230.05f, -9.946f, 85.317f, 5.847f)],
        [5619] = [(180.707f, -75.818f, 84.925f, 1.396f), (185.689f, -64.627f, 84.925f, 5.55f),
            (175.7f, -55.238f, 85.229f, 4.772f)],
        [5620] = [(123.306f, 3.933f, 85.312f, 6.056f), (110.892f, -6.463f, 85.312f, 0.436f),
            (102.454f, 4.374f, 85.312f, 2.182f)],
        [5621] = [(239.556f, -4.945f, 72.674f, 1.4525f), (226.854f, 0.262f, 72.673f, 3.161f),
            (248.115f, 2.809f, 72.669f, 4.668f)],
        [5622] = [(185.616f, -42.912f, 75.4812f, 4.45059f), (177.746f, -42.7475f, 75.4812f, 4.88692f),
            (181.825f, -42.5812f, 75.4812f, 4.66003f)],
        [5623] = [(128.806f, -7.874f, 75.482f, 5.62f), (130.415f, -1.113f, 75.482f, 2.688f),
            (124.162f, 5.816f, 75.482f, 5.061f)],
    };
    private readonly Dictionary<uint, HashSet<ObjectGuid>> _guardiansByEvent = [];
    private readonly HashSet<uint> _activePortals = [];
    private readonly List<(uint EventId, uint RemainingMs)> _pendingGuardians = [];
    private uint _currentPortal;
    private uint _kirtonosTimer;

    public override void Initialize()
    {
        base.Initialize();
        _guardiansByEvent.Clear();
        _activePortals.Clear();
        _pendingGuardians.Clear();
        _currentPortal = 0;
        _kirtonosTimer = 0;
    }

    public override void OnObjectCreate(GameObject go)
    {
        if (go.Entry == GoViewingRoomDoor)
        {
            OpenIf(go, Encounters[TypeRattlegore] == EncounterState.Done);
        }

        if (go.Entry is GoKirtonosGate or GoViewingRoomDoor or GoRasGate or GoGandlingGate
            || RoomDoors.Contains(go.Entry))
        {
            StoreGameObject(go);
        }
    }

    public override void OnCreatureCreate(Creature creature)
    {
        if (creature.Template.Entry == NpcGandling)
        {
            StoreCreature(creature);
        }
        else if (creature.Template.Entry == NpcGuardian && _currentPortal != 0)
        {
            _guardiansByEvent.GetValueOrDefault(_currentPortal)?.Add(creature.Guid);
        }
    }

    // instance_scholomance::OnPlayerEnter calls DoSpawnGandlingIfCan(true): a reload spawn is silent.
    public override void OnPlayerEnter(Player player) => SpawnGandlingIfReady(byPlayerEnter: true);

    public override void OnGameObjectUse(Player player, GameObject go)
    {
        // ClassicDB z2815 dbscripts_on_go_use id 2890009 (Brazier of the Herald):
        // close gate spawn 2890010, then summon Kirtonos five seconds later.
        if (go.Entry == GoBrazierOfTheHerald && Encounters[TypeKirtonos] != EncounterState.Done && _kirtonosTimer == 0)
        {
            if (GetSingleGameObjectFromStorage(GoKirtonosGate) is { State: GameObjectState.Active })
            {
                DoUseDoorOrButton(GoKirtonosGate);
            }

            _kirtonosTimer = 5_000;
        }
    }

    public override void OnSpellEvent(Unit caster, uint eventId)
    {
        // instance_scholomance::ProcessEventId_event_spell_gandling_shadow_portal.
        if (caster is Creature && PortalGuardians.ContainsKey(eventId))
        {
            HandlePortalEvent(eventId, EncounterState.Special);
            _pendingGuardians.Add((eventId, 2_000));
        }
    }

    public override uint GetData(uint type) => type < Encounters.Length ? Encounters[type] : 0;

    public override void SetData(uint type, uint data)
    {
        if (type >= Encounters.Length)
        {
            return;
        }

        switch (type)
        {
            case TypeKirtonos:
                // The DB script shuts the door on first aggro. SD2 only toggles it on a
                // later aggro after FAIL, or on FAIL / DONE (SetData:164-170).
                if (Encounters[type] != EncounterState.Fail && data == EncounterState.InProgress)
                {
                    return;
                }

                DoUseDoorOrButton(GoKirtonosGate);
                break;
            case TypeRas:
                DoUseDoorOrButton(GoRasGate);
                break;
            case >= TypeMalicia and <= TypeIlluciaBarov:
                DoUseDoorOrButton(RoomDoors[type - TypeMalicia]);
                break;
            case TypeGandling:
                DoUseDoorOrButton(GoGandlingGate);
                break;
        }

        Encounters[type] = data;
        if (data == EncounterState.Done)
        {
            SpawnGandlingIfReady(byPlayerEnter: false);
            SaveToDB();
        }
    }

    /// <summary>instance_scholomance::HandlePortalEvent: a shadow portal closes its room until its guardians die.</summary>
    public void HandlePortalEvent(uint eventId, uint data)
    {
        if (!PortalEvents.Contains(eventId))
        {
            return;
        }

        if (data == EncounterState.Special)
        {
            _currentPortal = eventId;
            _guardiansByEvent.TryAdd(eventId, []);
            if (_activePortals.Add(eventId))
            {
                DoUseDoorOrButton(RoomDoors[Array.IndexOf(PortalEvents, eventId)]);
            }
        }
        else if (data == EncounterState.InProgress ? _activePortals.Add(eventId) : _activePortals.Remove(eventId))
        {
            DoUseDoorOrButton(RoomDoors[Array.IndexOf(PortalEvents, eventId)]);
        }
    }

    public override void OnCreatureEnterCombat(Creature creature)
    {
        int index = Array.IndexOf(BossEntries, creature.Template.Entry);
        if (index >= 0)
        {
            SetData((uint)index, EncounterState.InProgress);
        }
        else if (creature.Template.Entry == NpcGuardian)
        {
            foreach ((uint eventId, HashSet<ObjectGuid> guardians) in _guardiansByEvent)
            {
                if (guardians.Contains(creature.Guid))
                {
                    HandlePortalEvent(eventId, EncounterState.InProgress);
                    break;
                }
            }
        }
    }

    public override void OnCreatureEvade(Creature creature)
    {
        int index = Array.IndexOf(BossEntries, creature.Template.Entry);
        if (index >= 0)
        {
            SetData((uint)index, EncounterState.Fail);
        }
        else if (creature.Template.Entry == NpcGuardian)
        {
            foreach ((uint eventId, HashSet<ObjectGuid> guardians) in _guardiansByEvent)
            {
                if (guardians.Contains(creature.Guid))
                {
                    HandlePortalEvent(eventId, EncounterState.Fail);
                    break;
                }
            }
        }
    }

    public override void OnCreatureDeath(Creature creature)
    {
        int index = Array.IndexOf(BossEntries, creature.Template.Entry);
        if (index >= 0)
        {
            SetData((uint)index, EncounterState.Done);
        }
        else if (creature.Template.Entry == NpcGuardian)
        {
            foreach ((uint eventId, HashSet<ObjectGuid> guardians) in _guardiansByEvent)
            {
                if (guardians.Remove(creature.Guid) && guardians.Count == 0)
                {
                    HandlePortalEvent(eventId, EncounterState.Done);
                    break;
                }
            }
        }
    }

    public override void Update(uint diffMs)
    {
        if (_kirtonosTimer > 0)
        {
            if (diffMs >= _kirtonosTimer)
            {
                _kirtonosTimer = 0;
                if (Instance.FindUpdater<CreatureMapSystem>() is { } creatures
                    && creatures.SummonInstanceCreature(NpcKirtonos, 309.65f, 93.47f, 101.66f, 0.03f) is { } kirtonos)
                {
                    creatures.ForcedDespawn(kirtonos, 900_000);
                }
            }
            else
            {
                _kirtonosTimer -= diffMs;
            }
        }

        for (int i = _pendingGuardians.Count - 1; i >= 0; i--)
        {
            (uint eventId, uint remaining) = _pendingGuardians[i];
            if (diffMs < remaining)
            {
                _pendingGuardians[i] = (eventId, remaining - diffMs);
                continue;
            }

            _pendingGuardians.RemoveAt(i);
            _currentPortal = eventId;
            foreach ((float x, float y, float z, float orientation) in PortalGuardians[eventId])
            {
                if (Instance.FindUpdater<CreatureMapSystem>() is { } creatures
                    && creatures.SummonInstanceCreature(NpcGuardian, x, y, z, orientation) is { } guardian)
                {
                    creatures.ForcedDespawn(guardian, 300_000);
                }
            }

            _currentPortal = 0;
        }

        CreatureMapSystem? system = Instance.FindUpdater<CreatureMapSystem>();
        foreach (uint eventId in _activePortals.ToArray())
        {
            if (_guardiansByEvent.TryGetValue(eventId, out HashSet<ObjectGuid>? guardians) && guardians.Count > 0
                && guardians.All(guid => system?.FindCreature(guid) is not { IsAlive: true }))
            {
                HandlePortalEvent(eventId, EncounterState.Fail);
                guardians.Clear();
            }
        }
    }

    /// <summary>instance_scholomance::DoSpawnGandlingIfCan: SAY_GANDLING_SPAWN only when the sixth room boss dies, not on a player's entry.</summary>
    private void SpawnGandlingIfReady(bool byPlayerEnter)
    {
        if (Encounters[TypeGandling] == EncounterState.Done || GetSingleCreatureFromStorage(NpcGandling) is not null
            || !Enumerable.Range((int)TypeMalicia, 6).All(i => Encounters[i] == EncounterState.Done)
            || Instance.Players.Count == 0)
        {
            return;
        }

        if (Instance.FindUpdater<CreatureMapSystem>()?.SummonInstanceCreature(NpcGandling, 180.771f, -5.4286f, 75.5702f, 1.29154f) is { } gandling
            && !byPlayerEnter)
        {
            gandling.System?.SayText(gandling, -1289000);
        }
    }
}
