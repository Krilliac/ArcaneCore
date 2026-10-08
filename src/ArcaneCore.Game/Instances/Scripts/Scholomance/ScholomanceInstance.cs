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
    public const uint NpcNecrofiend = 11551, NpcRisenAberration = 10485, NpcDiseasedGhoul = 10495, NpcReanimatedCorpse = 10481;
    public const uint NpcStudent = 10475, NpcVectus = 10432, NpcMarduk = 10433;
    public const uint EventDawnGambit = 5140, SpellStudentTransform = 18115, FactionScourge = 233;

    /// <summary>scholomance.h aEntranceRoomSpawnLocs: four corners of four, then the patrolling necrofiend.</summary>
    private static readonly (float X, float Y, float Z, float O)[] EntranceRoomSpawns =
    [
        (186.036f, 94.5f, 104.72f, 1.29154f), (179.117f, 95.5166f, 104.81f, 1.29154f), (180.612f, 100.176f, 104.80f, 1.29154f),
        (185.926f, 100.079f, 104.80f, 1.29154f), (178.999f, 75.2952f, 104.72f, 1.29154f), (185.558f, 77.276f, 104.72f, 1.29154f),
        (187.556f, 70.4334f, 104.72f, 1.29154f), (180.51f, 82.3917f, 104.72f, 1.29154f), (212.915f, 70.6005f, 104.80f, 1.29154f),
        (221.199f, 77.0037f, 104.72f, 1.29154f), (214.381f, 76.233f, 104.80f, 1.29154f), (218.64f, 71.5957f, 104.72f, 1.29154f),
        (221.249f, 94.9361f, 104.72f, 1.29154f), (214.406f, 101.903f, 104.72f, 1.29154f), (217.521f, 95.4237f, 104.72f, 1.29154f),
        (223.296f, 105.101f, 104.72f, 1.29154f), (209.233f, 73.2819f, 104.80f, 1.29154f),
    ];

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
    private readonly HashSet<ObjectGuid> _entranceRoom = [];
    private readonly HashSet<ObjectGuid> _viewingRoomStudents = [];
    private bool _roomReset;
    private uint _gambitTimer;

    /// <summary>Whether the entrance room was reset after Rattlegore's death (m_bIsRoomReset).</summary>
    public bool EntranceRoomReset => _roomReset;

    public override void Initialize()
    {
        base.Initialize();
        _guardiansByEvent.Clear();
        _activePortals.Clear();
        _pendingGuardians.Clear();
        _currentPortal = 0;
        _kirtonosTimer = 0;
        _entranceRoom.Clear();
        _viewingRoomStudents.Clear();
        _roomReset = false;
        _gambitTimer = 0;
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
        uint entry = creature.Template.Entry;
        if (entry is NpcGandling or NpcVectus or NpcMarduk)
        {
            StoreCreature(creature);
        }
        else if (entry is NpcReanimatedCorpse or NpcDiseasedGhoul or NpcRisenAberration)
        {
            // instance_scholomance::OnCreatureCreate: the entrance-room mobs (in aEntranceRoom's volume) go when Rattlegore dies.
            if (Encounters[TypeRattlegore] != EncounterState.Done && creature.Z > 104.0f
                && creature.X - 174.13f < 54 && creature.Y - 63.84f < 44)
            {
                _entranceRoom.Add(creature.Guid);
            }
        }
        else if (entry == NpcStudent)
        {
            _viewingRoomStudents.Add(creature.Guid);
        }
        else if (creature.Template.Entry == NpcGuardian && _currentPortal != 0)
        {
            _guardiansByEvent.GetValueOrDefault(_currentPortal)?.Add(creature.Guid);
        }
    }

    // instance_scholomance::OnPlayerEnter calls DoSpawnGandlingIfCan(true): a reload spawn is silent.
    public override void OnPlayerEnter(Player player)
    {
        SpawnGandlingIfReady(byPlayerEnter: true);
        if (Encounters[TypeRattlegore] == EncounterState.Done)
        {
            RespawnEntranceRoom();
        }
    }

    /// <summary>
    /// instance_scholomance::DoRespawnEntranceRoom (instance_scholomance.cpp:107-158): once per instance load, the stored entrance-room mobs
    /// are despawned and four smaller groups take their place - two Risen Aberrations, a Diseased Ghoul and a Diseased Ghoul or Reanimated
    /// Corpse each, shuffled over the corner's four points - plus a Necrofiend patrolling its entry's path (TEMPSPAWN_DEAD_DESPAWN).
    /// </summary>
    private void RespawnEntranceRoom()
    {
        if (_roomReset || Instance.Players.Count == 0 || Instance.FindUpdater<CreatureMapSystem>() is not { } creatures)
        {
            return;
        }

        foreach (ObjectGuid guid in _entranceRoom)
        {
            if (creatures.FindCreature(guid) is { } mob)
            {
                creatures.ForcedDespawn(mob, 0);
            }
        }

        for (int group = 0; group < 4; group++)
        {
            List<uint> mobs = [NpcRisenAberration, NpcRisenAberration, NpcDiseasedGhoul,
                creatures.RandomInt(0, 1) == 0 ? NpcReanimatedCorpse : NpcDiseasedGhoul];
            for (int i = mobs.Count - 1; i > 0; i--)
            {
                int j = creatures.RandomInt(0, i);
                (mobs[i], mobs[j]) = (mobs[j], mobs[i]);
            }

            for (int i = 0; i < 4; i++)
            {
                (float x, float y, float z, float o) = EntranceRoomSpawns[(4 * group) + i];
                creatures.SummonForInstance(mobs[i], x, y, z, o);
            }
        }

        (float nx, float ny, float nz, float no) = EntranceRoomSpawns[16];
        if (creatures.SummonForInstance(NpcNecrofiend, nx, ny, nz, no) is { } necrofiend)
        {
            creatures.ChangeMovement(necrofiend, 2, 0, 0); // MoveWaypoint: the creature_movement_template path of 11551
        }

        _roomReset = true;
    }

    public override bool OnGameObjectUse(Player player, GameObject go)
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

        return false; // a database script, not a ScriptDev2 GOUse: the use goes on
    }

    public override bool OnSpellEvent(Unit caster, uint eventId)
    {
        // ProcessEventId_dawn_gambit: the Dawn's Gambit event starts the 12 s transform timer (HandleDawnGambitEvent's first call).
        if (eventId == EventDawnGambit)
        {
            if (_gambitTimer == 0)
            {
                _gambitTimer = 12_000;
            }

            return true;
        }

        // instance_scholomance::ProcessEventId_event_spell_gandling_shadow_portal (Shadow Portal 17950 → ShadowPortalScript → the room
        // portal's SEND_EVENT), then the dbscripts_on_event guardians this script stands in for.
        if (!PortalGuardians.ContainsKey(eventId))
        {
            return false;
        }

        if (caster is Creature)
        {
            HandlePortalEvent(eventId, EncounterState.Special);
            _pendingGuardians.Add((eventId, 2_000));
        }

        return true;
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
            case TypeRattlegore when data == EncounterState.Done:
                Encounters[type] = data;
                RespawnEntranceRoom();
                break;
        }

        Encounters[type] = data;
        if (data == EncounterState.Done)
        {
            SpawnGandlingIfReady(byPlayerEnter: false);
            SaveToDB();
        }
    }

    /// <summary>
    /// instance_scholomance::HandleDawnGambitEvent's second call (instance_scholomance.cpp:438-469): the viewing-room students turn Scourge
    /// (until they respawn), stand, wander within 2 yd and cast Viewing Room Student Transform on themselves; Marduk and Vectus turn Scourge
    /// too, and Vectus yells.
    /// </summary>
    private void DawnGambitTransform()
    {
        if (Instance.FindUpdater<CreatureMapSystem>() is not { } creatures)
        {
            return;
        }

        foreach (ObjectGuid guid in _viewingRoomStudents)
        {
            if (creatures.FindCreature(guid) is { IsAlive: true } student)
            {
                student.FactionTemplate = FactionScourge;
                student.StandState = StandState.Stand;
                creatures.ChangeMovement(student, 1, 2, 0);
                creatures.CastSpell(student, SpellStudentTransform, student, triggered: false);
            }
        }

        if (GetSingleCreatureFromStorage(NpcMarduk) is { } marduk)
        {
            marduk.FactionTemplate = FactionScourge;
        }

        if (GetSingleCreatureFromStorage(NpcVectus) is { } vectus)
        {
            vectus.FactionTemplate = FactionScourge;
            creatures.SayText(vectus, -1289001); // YELL_VECTUS_GAMBIT
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
        if (_gambitTimer > 0)
        {
            if (diffMs >= _gambitTimer)
            {
                _gambitTimer = 0;
                DawnGambitTransform();
            }
            else
            {
                _gambitTimer -= diffMs;
            }
        }

        if (_kirtonosTimer > 0)
        {
            if (diffMs >= _kirtonosTimer)
            {
                _kirtonosTimer = 0;
                // dbscripts_on_go_use 2890009 command 10 with 900000: TEMPSPAWN_TIMED_OOC_OR_DEAD_DESPAWN (ScriptMgr.cpp:2062), so a
                // dead Kirtonos keeps his corpse (and loot) until it decays.
                Instance.FindUpdater<CreatureMapSystem>()?.SummonInstanceCreatureTimedOocOrDead(NpcKirtonos, 309.65f, 93.47f, 101.66f, 0.03f, 900_000);
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
                // dbscripts_on_event 5618-5623 command 10 with 300000: TEMPSPAWN_TIMED_OOC_OR_DEAD_DESPAWN.
                Instance.FindUpdater<CreatureMapSystem>()?.SummonInstanceCreatureTimedOocOrDead(NpcGuardian, x, y, z, orientation, 300_000);
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

        if (Instance.FindUpdater<CreatureMapSystem>()?.SummonForInstance(NpcGandling, 180.771f, -5.4286f, 75.5702f, 1.29154f) is { } gandling
            && !byPlayerEnter)
        {
            gandling.System?.SayText(gandling, -1289000);
        }
    }
}
