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
    public const uint NpcMrSmite = 646;

    private readonly HashSet<uint> _spawnedPatrols = [];
    private uint _ironDoorTimer;
    private int _doorStep;

    public override void Initialize()
    {
        base.Initialize();
        _ironDoorTimer = 0;
        _doorStep = 0;
        _spawnedPatrols.Clear();
        if (Instance.FindUpdater<CreatureMapSystem>() is { } creatures)
        {
            creatures.RegisterScriptOnlySpawns([3_600_200, 3_600_201, 3_600_202, 3_600_203, 3_600_204, 3_600_205, 3_600_206]);
            creatures.RegisterEntryAi(NpcMrSmite, c => new MrSmiteAi(c, this));
        }

        Instance.FindUpdater<GameObjectMapSystem>()?.RegisterAi(GoDefiasCannon, new CannonAi(this));
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
                if (Encounters[TypeIronCladDoor] == EncounterState.Done)
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
                    SpawnPatrol(type);
                }

                Encounters[type] = data;
                break;
            case TypeIronCladDoor:
                if (data == EncounterState.Done && Encounters[type] != EncounterState.Done)
                {
                    _ironDoorTimer = 500;
                }

                Encounters[type] = data;
                break;
            default: return;
        }

        SaveIfDone(data);
    }

    public override uint GetData(uint type) => type < 4 ? Encounters[type] : 0;

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
        if (_ironDoorTimer == 0)
        {
            return;
        }

        if (_ironDoorTimer > diffMs)
        {
            _ironDoorTimer -= diffMs;
            return;
        }

        if (_doorStep++ == 0)
        {
            DoUseDoorOrButton(GoIronCladDoor);
            if (GetSingleGameObjectFromStorage(GoIronCladDoor) is { } door)
            {
                door.State = GameObjectState.ActiveAlternative;
                if (Instance.FindUpdater<CreatureMapSystem>() is { } creatures)
                {
                    foreach (uint guid in new uint[] { 3_600_148, 3_600_149, 3_600_150 })
                    {
                        Creature? guard = creatures.Creatures.FirstOrDefault(c => c.Spawn?.Guid == guid);
                        guard?.Motion.MovePoint(0, door.X, door.Y, door.Z, run: guid != 3_600_150);
                    }
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
            if (instance.GetData(TypeIronCladDoor) != EncounterState.Done)
            {
                instance.SetData(TypeIronCladDoor, EncounterState.Done);
            }

            return false; // GOUse_go_defias_cannon lets the cannon's normal animation run
        }
    }
}
