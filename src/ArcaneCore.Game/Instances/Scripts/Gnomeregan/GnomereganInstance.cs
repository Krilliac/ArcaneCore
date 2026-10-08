using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Gnomeregan;

/// <summary>ScriptDev2 instance_gnomeregan (mangos-classic gnomeregan/instance_gnomeregan.cpp:
/// OnCreatureCreate, OnObjectCreate, SetData, GetData, DoActivateBombFace, DoDeactivateBombFace).</summary>
[InstanceScript(MapId)]
public sealed class GnomereganInstance(Map map) : ScriptedInstance(map, 2)
{
    public const uint MapId = 90;
    public const uint TypeGrubbis = 1, TypeThermaplugg = 2, TypeExplosiveCharge = 3;
    public const uint ChargeUse = 5;
    public const uint Blastmaster = 7998, RedRocket = 103820, CaveNorth = 146085, CaveSouth = 146086;
    public const uint ExplosiveCharge = 144065, FinalChamber = 142207;
    public static readonly uint[] FaceEntries = [142211, 142210, 142209, 142208, 142213, 142212];
    public static readonly uint[] ButtonEntries = [142214, 142215, 142216, 142217, 142218, 142219];

    private readonly List<ObjectGuid> _rockets = [];
    private readonly List<ObjectGuid> _charges = [];
    private readonly List<ObjectGuid> _spawnedCharges = [];
    private readonly ObjectGuid?[,] _sortedCharges = new ObjectGuid?[2, 2];
    private readonly ObjectGuid?[] _faces = new ObjectGuid?[6];
    private readonly bool[] _faceActive = new bool[6];
    private readonly uint[] _faceBombTimers = new uint[6];
    private bool _emiRegistered;
    private bool _thermapluggRegistered;
    private bool _kernobeeRegistered;
    private readonly BombButtonAi _buttonAi = new();

    public bool FaceActive(int index) => index is >= 0 and < 6 && _faceActive[index];

    /// <summary>sBombFace::m_uiBombTimer (gnomeregan.h): 3000 ms from DoActivateBombFace, 0 after DoDeactivateBombFace; Thermaplugg counts it down.</summary>
    public uint FaceBombTimer(int index) => index is >= 0 and < 6 ? _faceBombTimers[index] : 0;

    public void SetFaceBombTimer(int index, uint ms)
    {
        if (index is >= 0 and < 6) _faceBombTimers[index] = ms;
    }

    public GameObject? FaceObject(int index) => index is >= 0 and < 6 && _faces[index] is { } guid
        ? Instance.FindUpdater<GameObjectMapSystem>()?.Find(guid) : null;
    public GameObject? CaveObject(uint entry) => entry is CaveNorth or CaveSouth ? GetSingleGameObjectFromStorage(entry) : null;
    public void ToggleCave(uint entry)
    {
        if (entry is CaveNorth or CaveSouth) DoUseDoorOrButton(entry);
    }

    public override void OnCreatureCreate(Creature creature)
    {
        if (creature.Template.Entry == Blastmaster) StoreCreature(creature);
        if (creature.System is not { } system) return;
        switch (creature.Template.Entry)
        {
            case Blastmaster when !_emiRegistered:
                _emiRegistered = true;
                system.RegisterEntryAi(Blastmaster, c => new EmiShortfuseAi(c, this), rebuildExisting: creature.AI is not null);
                break;
            case 7800 when !_thermapluggRegistered:
                _thermapluggRegistered = true;
                system.RegisterEntryAi(7800, c => new ThermapluggAi(c, this), rebuildExisting: creature.AI is not null);
                break;
            case 7850 when !_kernobeeRegistered:
                _kernobeeRegistered = true;
                system.RegisterEntryAi(7850, c => new KernobeeAi(c), rebuildExisting: creature.AI is not null);
                break;
        }
    }

    public override void OnObjectCreate(GameObject go)
    {
        switch (go.Entry)
        {
            case CaveNorth or CaveSouth or FinalChamber:
                StoreGameObject(go);
                break;
            case RedRocket:
                _rockets.Add(go.Guid);
                break;
            case ExplosiveCharge:
                _charges.Add(go.Guid);
                break;
            default:
                int face = Array.IndexOf(FaceEntries, go.Entry);
                if (face >= 0) _faces[face] = go.Guid;
                if (Array.IndexOf(ButtonEntries, go.Entry) >= 0)
                    Instance.FindUpdater<GameObjectMapSystem>()?.RegisterAi(go.Entry, _buttonAi);
                break;
        }
    }

    public override void SetData(uint type, uint data)
    {
        if (type == TypeGrubbis)
        {
            Encounters[0] = data;
            if (data == EncounterState.InProgress) SortCharges();
            if (data == EncounterState.Fail) SetData(TypeExplosiveCharge, ChargeUse);
            if (data == EncounterState.Done && Instance.FindUpdater<GameObjectMapSystem>() is { } objects)
                foreach (ObjectGuid guid in _rockets)
                    if (objects.Find(guid) is { } rocket) objects.ForceRespawn(rocket);
        }
        else if (type == TypeThermaplugg)
        {
            Encounters[1] = data;
            if (GetSingleGameObjectFromStorage(FinalChamber) is { } door)
            {
                if (data == EncounterState.InProgress)
                {
                    door.Flags |= GameObjectFlags.Locked;
                    if (door.State == GameObjectState.Active) DoUseDoorOrButton(FinalChamber);
                }
                else if (data is EncounterState.Done or EncounterState.Fail)
                {
                    door.Flags &= ~GameObjectFlags.Locked;
                    if (door.State == GameObjectState.Ready) DoUseDoorOrButton(FinalChamber);
                }
            }

            if (data == EncounterState.InProgress) ActivateBombFace(2);
            if (data is EncounterState.Done or EncounterState.Fail)
                for (int i = 0; i < 6; i++) DeactivateBombFace(i);
        }
        else if (type == TypeExplosiveCharge)
        {
            if (data is >= 1 and <= 4)
            {
                ObjectGuid? guid = _sortedCharges[(data - 1) / 2, (data - 1) % 2];
                if (guid is { } chargeGuid && Instance.FindUpdater<GameObjectMapSystem>() is { } objects && objects.Find(chargeGuid) is { } charge)
                {
                    objects.ForceRespawn(charge);
                    _spawnedCharges.Add(chargeGuid);
                }
            }
            else if (data == ChargeUse && GetSingleCreatureFromStorage(Blastmaster) is { } emi
                     && Instance.FindUpdater<GameObjectMapSystem>() is { } objects)
            {
                foreach (ObjectGuid guid in _spawnedCharges)
                    if (objects.Find(guid) is { } charge) objects.UseByUnit(emi, charge);
                _spawnedCharges.Clear();
            }
            return;
        }
        else return;

        SaveIfDone(data);
    }

    public override uint GetData(uint type) => type switch
    {
        TypeGrubbis => Encounters[0],
        TypeThermaplugg => Encounters[1],
        _ => 0,
    };

    public void ActivateBombFace(int index)
    {
        if (index is < 0 or >= 6 || _faceActive[index]) return;
        ToggleFace(index);
        _faceActive[index] = true;
        _faceBombTimers[index] = 3000;
    }

    public void DeactivateBombFace(int index)
    {
        if (index is < 0 or >= 6 || !_faceActive[index]) return;
        ToggleFace(index);
        _faceActive[index] = false;
        _faceBombTimers[index] = 0;
    }

    private void ToggleFace(int index)
    {
        if (_faces[index] is { } guid && Instance.FindUpdater<GameObjectMapSystem>() is { } objects && objects.Find(guid) is { } face)
            objects.ToggleDoorOrButton(face);
    }

    private void SortCharges()
    {
        if (Instance.FindUpdater<GameObjectMapSystem>() is not { } objects
            || GetSingleGameObjectFromStorage(CaveSouth) is not { } south
            || GetSingleGameObjectFromStorage(CaveNorth) is not { } north) return;

        int[] count = [0, 0];
        foreach (GameObject charge in _charges.Select(objects.Find).OfType<GameObject>().OrderBy(go => go.Y))
        {
            float southDistance = DistanceSq(charge, south);
            float northDistance = DistanceSq(charge, north);
            int side = southDistance < northDistance && count[0] < 2 ? 0 : 1;
            if (count[side] < 2) _sortedCharges[side, count[side]++] = charge.Guid;
        }
        _charges.Clear();
    }

    private static float DistanceSq(GameObject a, GameObject b)
        => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z);

    private sealed class BombButtonAi : IGameObjectAi
    {
        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, ArcaneCore.Game.Entities.Unit target) => false;
        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs) { }
        public bool OnUse(GameObjectMapSystem objects, GameObject go, ArcaneCore.Game.Entities.Unit user)
        {
            if (user is not ArcaneCore.Game.Entities.Player || go.Map?.FindUpdater<InstanceData>() is not GnomereganInstance script)
                return false;
            int index = Array.IndexOf(ButtonEntries, go.Entry);
            if (index >= 0) script.DeactivateBombFace(index);
            return false;
        }
    }
}
