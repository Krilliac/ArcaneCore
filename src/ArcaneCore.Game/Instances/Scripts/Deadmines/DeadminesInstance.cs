using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Deadmines;

/// <summary>Deadmines (map 36), mangos-classic deadmines/instance_deadmines.cpp:
/// OnCreatureDeath, OnObjectCreate, SetData, Load and Update. Script text IDs are from deadmines.h.</summary>
[InstanceScript(MapId)]
public sealed class DeadminesInstance(Map map) : ScriptedInstance(map, 4)
{
    public const uint MapId = 36;
    public const uint TypeRhahkzor = 0, TypeSneed = 1, TypeGilnid = 2, TypeIronCladDoor = 3;
    public const uint GoFactoryDoor = 13965, GoMastRoomDoor = 16400, GoFoundryDoor = 16399;
    public const uint GoIronCladDoor = 16397, GoDefiasCannon = 16398, GoSmiteChest = 144111;
    public const uint GoDefiasGunpowder = 17155, GoDoorLever = 101833, TypeGunpowderEvent = 5000;
    public const uint NpcMrSmite = 646;

    private readonly HashSet<uint> _spawnedPatrols = [];
    private uint _rhahkzorPatrolTimer;
    private uint _gilnidPatrolTimer;
    private uint _ironDoorTimer;
    private int _doorStep;
    private bool _gunpowderUsed;

    public override void Initialize()
    {
        base.Initialize();
        _ironDoorTimer = 0;
        _rhahkzorPatrolTimer = 0;
        _gilnidPatrolTimer = 0;
        _doorStep = 0;
        _gunpowderUsed = false;
        _spawnedPatrols.Clear();
        if (Instance.FindUpdater<CreatureMapSystem>() is { } creatures)
        {
            creatures.RegisterScriptOnlySpawns([3_600_200, 3_600_201, 3_600_202, 3_600_203, 3_600_204, 3_600_205, 3_600_206]);
            creatures.RegisterEntryAi(NpcMrSmite, c => new MrSmiteAi(c, this));
        }

        if (Instance.FindUpdater<GameObjectMapSystem>() is { } objects)
        {
            objects.RegisterAi(GoDefiasCannon, new CannonAi(this));
            objects.RegisterAi(GoDefiasGunpowder, new GunpowderAi(this));
            objects.RegisterAi(GoDoorLever, new DoorLeverAi(this));
        }
    }

    public override void Load(string data)
    {
        base.Load(data);
        for (uint type = 0; type <= TypeGilnid; type++)
        {
            if (Encounters[type] == EncounterState.Done)
            {
                SpawnPatrol(type);
            }
        }
    }

    public override void OnCreatureCreate(Creature creature)
    {
        if (creature.Entry == NpcMrSmite)
        {
            StoreCreature(creature);
        }
    }

    public override void OnCreatureDeath(Creature creature)
    {
        switch (creature.Entry)
        {
            case 644: SetData(TypeRhahkzor, EncounterState.Done); break;
            case 643: SetData(TypeSneed, EncounterState.Done); break;
            case 1763: SetData(TypeGilnid, EncounterState.Done); break;
        }
    }

    public override void OnObjectCreate(GameObject go)
    {
        switch (go.Entry)
        {
            case GoFactoryDoor: OpenIf(go, Encounters[TypeRhahkzor] == EncounterState.Done); break;
            case GoMastRoomDoor: OpenIf(go, Encounters[TypeSneed] == EncounterState.Done); break;
            case GoFoundryDoor: OpenIf(go, Encounters[TypeGilnid] == EncounterState.Done); break;
            case GoIronCladDoor:
                if (Encounters[TypeIronCladDoor] is EncounterState.InProgress or EncounterState.Done)
                {
                    go.State = GameObjectState.ActiveAlternative;
                }

                break;
            case GoDefiasCannon or GoSmiteChest or 180024: break;
            default: return;
        }

        StoreGameObject(go);
    }

    public override void OnPlayerEnter(Player player)
    {
        if (QuestCompleteUnrewarded(player, 7938) && GetSingleGameObjectFromStorage(180024) is { } chest)
        {
            Instance.FindUpdater<GameObjectMapSystem>()?.ForceRespawn(chest);
        }
    }

    public override void SetData(uint type, uint data)
    {
        switch (type)
        {
            case TypeRhahkzor or TypeSneed or TypeGilnid:
                if (data == EncounterState.Done && Encounters[type] != EncounterState.Done)
                {
                    DoUseDoorOrButton(type switch
                    {
                        TypeRhahkzor => GoFactoryDoor,
                        TypeSneed => GoMastRoomDoor,
                        _ => GoFoundryDoor,
                    });
                    // vmangos instance_deadmines::OnCreatureDeath/Update waits 60 s after
                    // Rhahkzor and 30 s after Gilnid before revealing their patrols.
                    // The already completed state on load is restored in Load above.
                    if (type == TypeRhahkzor)
                    {
                        _rhahkzorPatrolTimer = 60_000;
                    }
                    else if (type == TypeGilnid)
                    {
                        _gilnidPatrolTimer = 30_000;
                    }
                    else
                    {
                        SpawnPatrol(type);
                    }
                }

                Encounters[type] = data;
                break;
            case TypeIronCladDoor:
                if (data == EncounterState.InProgress && Encounters[type] == EncounterState.NotStarted)
                {
                    // vmangos instance_deadmines::SetData(TYPE_DEFIAS_ENDDOOR) opens
                    // the Ironclad door on cannon use, then waits 3 s for Smite's alarm.
                    if (GetSingleGameObjectFromStorage(GoIronCladDoor) is { State: GameObjectState.Ready })
                    {
                        DoUseDoorOrButton(GoIronCladDoor, alternative: true);
                    }

                    _ironDoorTimer = 3_000;
                }

                Encounters[type] = data;
                break;
            case TypeGunpowderEvent:
                _gunpowderUsed = data != 0;
                return;
            default: return;
        }

        SaveIfDone(data);
    }

    public override uint GetData(uint type) => type == TypeGunpowderEvent ? (_gunpowderUsed ? 1u : 0u)
        : type < 4 ? Encounters[type] : 0;

    // The GUIDs are the scripted spawns in instance_deadmines::SpawnFirst/Second/ThirdDeadminesPatrol.
    private void SpawnPatrol(uint type)
    {
        if (Instance.FindUpdater<CreatureMapSystem>() is not { } creatures)
        {
            return;
        }

        uint[] guids = type switch
        {
            TypeRhahkzor => [3_600_200, 3_600_201],
            TypeSneed => [3_600_202, 3_600_203],
            TypeGilnid => [3_600_204, 3_600_205, 3_600_206],
            _ => [],
        };
        foreach (uint guid in guids)
        {
            if (_spawnedPatrols.Contains(guid))
            {
                continue;
            }

            if (creatures.SpawnScripted(guid) is not null)
            {
                _spawnedPatrols.Add(guid);
            }
        }
    }

    public override void Update(uint diffMs)
    {
        if (_rhahkzorPatrolTimer != 0)
        {
            if (_rhahkzorPatrolTimer > diffMs)
            {
                _rhahkzorPatrolTimer -= diffMs;
            }
            else
            {
                _rhahkzorPatrolTimer = 0;
                SpawnPatrol(TypeRhahkzor);
            }
        }

        if (_gilnidPatrolTimer != 0)
        {
            if (_gilnidPatrolTimer > diffMs)
            {
                _gilnidPatrolTimer -= diffMs;
            }
            else
            {
                _gilnidPatrolTimer = 0;
                SpawnPatrol(TypeGilnid);
            }
        }

        if (_ironDoorTimer == 0)
        {
            return;
        }

        if (_ironDoorTimer > diffMs)
        {
            _ironDoorTimer -= diffMs;
            return;
        }

        if (GetSingleCreatureFromStorage(NpcMrSmite) is null)
        {
            _ironDoorTimer = 0; // vmangos Update stops the alarm when Smite is absent.
            return;
        }

        if (_doorStep++ == 0)
        {
            if (Instance.FindUpdater<CreatureMapSystem>() is { } creatures)
            {
                foreach (uint guid in new uint[] { 3_600_148, 3_600_149, 3_600_150 })
                {
                    Creature? guard = creatures.Creatures.FirstOrDefault(c => c.Spawn?.Guid == guid);
                    guard?.Motion.MovePoint(0, -99.6611f, -671.071655f, 7.42241f, run: true);
                }
            }

            SayAlarm(-1036000);
            _ironDoorTimer = 15_000;
        }
        else
        {
            SayAlarm(-1036001);
            _doorStep = 0;
            _ironDoorTimer = 0;
        }
    }

    private void SayAlarm(int id)
    {
        if (GetSingleCreatureFromStorage(NpcMrSmite) is { } smite)
        {
            Instance.FindUpdater<CreatureMapSystem>()?.SayText(smite, id);
        }
    }

    internal GameObject? SmiteChest => GetSingleGameObjectFromStorage(GoSmiteChest);

    private sealed class CannonAi(DeadminesInstance instance) : IGameObjectAi
    {
        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;
        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs) { }
        public bool OnUse(GameObjectMapSystem objects, GameObject go, Unit user)
        {
            if (instance.GetData(TypeIronCladDoor) is not (EncounterState.InProgress or EncounterState.Done))
            {
                instance.SetData(TypeIronCladDoor, EncounterState.InProgress);
            }

            return false; // GOUse_go_defias_cannon lets the cannon's normal animation run
        }
    }

    private sealed class DoorLeverAi(DeadminesInstance instance) : IGameObjectAi
    {
        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;
        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs) { }

        // vmangos deadmines.cpp GOHello_go_door_lever_dm: GO_STATE_READY is 1.
        // False lets the closed door's normal/DB-script lever use proceed;
        // true suppresses it when the door is already open or missing.
        public bool OnUse(GameObjectMapSystem objects, GameObject go, Unit user)
            => instance.GetSingleGameObjectFromStorage(GoIronCladDoor)?.State != GameObjectState.Ready;
    }

    private sealed class GunpowderAi(DeadminesInstance instance) : IGameObjectAi
    {
        private readonly Dictionary<ObjectGuid, (ObjectGuid Overseer, byte Stage)> _routes = [];
        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

        // vmangos deadmines.cpp go_defias_gunpowderAI::SummonedMovementInform:
        // continue from point 0 to point 1, then make the end point the overseer's home.
        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
        {
            if (!_routes.TryGetValue(go.Guid, out var route)
                || objects.Map.FindUpdater<CreatureMapSystem>() is not { } creatures)
            {
                return;
            }

            Creature? overseer = creatures.FindCreature(route.Overseer);
            if (overseer is null || !overseer.IsAlive)
            {
                _routes.Remove(go.Guid);
                return;
            }

            if (overseer.Motion.CurrentType == MovementGeneratorType.Point)
            {
                return;
            }

            if (route.Stage == 0 && Near(overseer, -128.925980f, -616.494629f, 13.532340f))
            {
                overseer.Motion.MovePoint(1, -115.263672f, -617.396118f, 13.579387f, run: false);
                _routes[go.Guid] = (route.Overseer, 1);
            }
            else if (route.Stage == 1 && Near(overseer, -115.263672f, -617.396118f, 13.579387f))
            {
                creatures.SetHomePosition(overseer, -115.263672f, -617.396118f, 13.579387f, 6.182347f);
                _routes.Remove(go.Guid);
            }
        }

        public bool OnUse(GameObjectMapSystem objects, GameObject go, Unit user)
        {
            if (instance.GetData(TypeGunpowderEvent) == 0)
            {
                if (objects.Map.FindUpdater<CreatureMapSystem>() is { } creatures
                    && creatures.SummonFromGameObject(634, -131.290833f, -591.243103f, 18.077190f, 4.792192f,
                        310_000, outOfCombat: true) is { } overseer)
                {
                    overseer.Motion.MovePoint(0, -128.925980f, -616.494629f, 13.532340f, run: false);
                    _routes[go.Guid] = (overseer.Guid, 0);
                }

                instance.SetData(TypeGunpowderEvent, 1);
            }

            return true; // GOHello_go_defias_gunpowder handles the use.
        }

        private static bool Near(Creature creature, float x, float y, float z)
        {
            float dx = creature.X - x, dy = creature.Y - y, dz = creature.Z - z;
            // After the point generator stops, allow a three-yard end gap so
            // a terrain path that ends short does not stall this route.
            return dx * dx + dy * dy + dz * dz <= 3f * 3f;
        }
    }
}
