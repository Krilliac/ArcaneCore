using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Stratholme;

/// <summary>
/// ScriptDev2 instance_stratholme (mangos-classic
/// src/game/AI/ScriptDevAI/scripts/eastern_kingdoms/stratholme/stratholme.cpp:
/// SetData, DoSortZiggurats, ThazudinAcolyteJustDied, OnCreatureDeath, Update) and
/// stratholmeScripts.cpp:GOUse_go_gauntlet_gate, GOUse_go_service_gate.
/// </summary>
[InstanceScript(MapId)]
public sealed class StratholmeInstance(Map instance) : ScriptedInstance(instance, 10)
{
    public const uint MapId = 329;
    public const uint TypeBaronRun = 0, TypeBaroness = 1, TypeNerub = 2,
        TypePallid = 3, TypeRamstein = 4, TypeBaron = 5, TypeBarthilasRun = 6,
        TypeAurius = 7, TypeBlackGuards = 8, TypePostmaster = 9;

    public const uint GoServiceEntrance = 175368, GoGauntletGate1 = 175357,
        GoSlaughterGate = 175358, GoZiggurat1 = 175380, GoZiggurat2 = 175379,
        GoZiggurat3 = 175381, GoSlaughterhouse = 175405, GoBaronDoor = 175796,
        GoGauntletPort = 175374, GoSlaughterPort = 175373, GoYsidaCage = 181071;
    public const uint NpcBaroness = 10436, NpcNerub = 10437, NpcPallid = 10438,
        NpcRamstein = 10439, NpcBaron = 10440, NpcAcolyte = 10399,
        NpcCrystal = 10415, NpcBileAbom = 10416, NpcVenomAbom = 10417,
        NpcMindless = 11030, NpcBlackGuard = 10394, NpcYsida = 16031,
        NpcBarthilas = 10435;
    public const uint SpellBaronUltimatum = 27861, SpellYsidaFreed = 27773;

    private static readonly uint[] ZigguratDoors = [GoZiggurat1, GoZiggurat2, GoZiggurat3];
    private readonly HashSet<ObjectGuid>[] _acolytes = [[], [], []];
    private readonly ObjectGuid[] _crystals = new ObjectGuid[3];
    private readonly HashSet<ObjectGuid> _unassignedAcolytes = [];
    private readonly HashSet<ObjectGuid> _unassignedCrystals = [];
    private readonly HashSet<ObjectGuid> _abominations = [];
    private readonly HashSet<ObjectGuid> _mindless = [];
    private readonly HashSet<ObjectGuid> _guards = [];
    private uint _baronRunTimer;
    private uint _mindlessTimer;
    private uint _guardsTimer;
    private uint _slaughterDoorTimer;
    private uint _mindlessCount;
    private int _baronWarnings;
    private bool _slaughterDoorOpen;
    private bool _ramsteinSummoned;
    private bool _announcerChosen;
    private ObjectGuid _acolyteAnnouncer;

    public uint BaronRunRemainingMs => _baronRunTimer;
    public uint MindlessSummoned => _mindlessCount;

    public override void Initialize()
    {
        base.Initialize();
        foreach (HashSet<ObjectGuid> acolytes in _acolytes)
        {
            acolytes.Clear();
        }

        Array.Clear(_crystals);
        _unassignedAcolytes.Clear();
        _unassignedCrystals.Clear();
        _abominations.Clear();
        _mindless.Clear();
        _guards.Clear();
        _baronRunTimer = _mindlessTimer = _guardsTimer = _slaughterDoorTimer = _mindlessCount = 0;
        _baronWarnings = 0;
        _slaughterDoorOpen = _ramsteinSummoned = _announcerChosen = false;
        _acolyteAnnouncer = default;
    }

    // The original Save()/Load() serializes the first eight fields, even though MAX_ENCOUNTER is ten.
    public override string? GetSaveData() => string.Join(' ', Encounters.Take(8));

    protected override uint AfterLoad(int index, uint state)
    {
        uint loaded = base.AfterLoad(index, state);
        return index is >= 1 and <= 3 && loaded == EncounterState.Done ? EncounterState.Special : loaded;
    }

    public override uint GetData(uint type) => type < Encounters.Length && type != TypeBlackGuards ? Encounters[type] : 0;

    public override void OnPlayerEnter(Player player)
    {
        if (Encounters[TypeBaronRun] is EncounterState.Done or EncounterState.Fail && GetSingleCreatureFromStorage(NpcYsida) is null)
        {
            Creature? ysida = Instance.FindUpdater<CreatureMapSystem>()?.SummonInstanceCreature(NpcYsida, 4041.9f, -3337.6f, 115.06f, 3.82f);
            if (ysida is not null && Encounters[TypeBaronRun] == EncounterState.Fail)
            {
                ysida.System?.KillCreature(ysida);
            }
        }
    }

    public override void OnGameObjectUse(Player player, GameObject go)
    {
        if (go.Entry == GoGauntletGate1 && Encounters[TypeBaronRun] == EncounterState.NotStarted)
        {
            foreach (Player participant in Instance.Players)
            {
                CastPlayerSpell?.Invoke(participant, SpellBaronUltimatum);
            }

            SetData(TypeBaronRun, EncounterState.InProgress);
        }
        else if (go.Entry == GoServiceEntrance && Encounters[TypeBarthilasRun] == EncounterState.NotStarted)
        {
            SetData(TypeBarthilasRun, EncounterState.InProgress);
        }
    }

    public override void OnObjectCreate(GameObject go)
    {
        switch (go.Entry)
        {
            case GoZiggurat1 or GoZiggurat2 or GoZiggurat3:
                int index = Array.IndexOf(ZigguratDoors, go.Entry);
                OpenIf(go, Encounters[TypeBaroness + (uint)index] is EncounterState.Done or EncounterState.Special);
                break;
            case GoSlaughterhouse:
                OpenIf(go, Encounters[TypeRamstein] == EncounterState.Done || Encounters[TypeBlackGuards] == EncounterState.Done);
                break;
            case GoBaronDoor:
                OpenIf(go, Encounters[TypeBlackGuards] == EncounterState.Done);
                break;
            case GoGauntletPort or GoSlaughterPort:
                OpenIf(go, ZigguratsCleared);
                break;
            case GoSlaughterGate:
                OpenIf(go, Encounters[TypeRamstein] == EncounterState.Done);
                break;
            case GoServiceEntrance or GoGauntletGate1 or GoYsidaCage:
                break;
            default:
                return;
        }

        StoreGameObject(go);
    }

    public override void OnCreatureCreate(Creature creature)
    {
        switch (creature.Template.Entry)
        {
            case NpcBaron or NpcYsida or NpcBarthilas:
                StoreCreature(creature);
                break;
            case NpcAcolyte:
                _unassignedAcolytes.Add(creature.Guid);
                break;
            case NpcCrystal:
                _unassignedCrystals.Add(creature.Guid);
                break;
            case NpcBileAbom or NpcVenomAbom:
                _abominations.Add(creature.Guid);
                break;
            case NpcMindless:
                _mindless.Add(creature.Guid);
                break;
            case NpcBlackGuard:
                _guards.Add(creature.Guid);
                break;
        }
    }

    public override void SetData(uint type, uint data)
    {
        if (type >= Encounters.Length)
        {
            return;
        }

        switch (type)
        {
            case TypeBaronRun:
                if (data == EncounterState.InProgress)
                {
                    if (Encounters[type] is EncounterState.InProgress or EncounterState.Fail)
                    {
                        return;
                    }

                    _baronRunTimer = 45u * 60u * 1000u;
                    _baronWarnings = 0;
                    Announce(NpcBaron, -1329009);
                    if (GetSingleCreatureFromStorage(NpcYsida) is null)
                    {
                        Instance.FindUpdater<CreatureMapSystem>()?.SummonInstanceCreature(NpcYsida, 4044.78f, -3333.68f, 115.53f, 4.15f);
                    }
                }
                else if (data == EncounterState.Done)
                {
                    _baronRunTimer = 0;
                }

                break;
            case TypeBaroness or TypeNerub or TypePallid:
                if (data == EncounterState.Done)
                {
                    SortZiggurats();
                    DoUseDoorOrButton(ZigguratDoors[type - TypeBaroness]);
                }

                break;
            case TypeRamstein:
                if (data == EncounterState.Special)
                {
                    if (Encounters[type] is not (EncounterState.Special or EncounterState.Done))
                    {
                        DoUseDoorOrButton(GoGauntletPort);
                    }

                    if (!_ramsteinSummoned && !LiveAbominations())
                    {
                        _ramsteinSummoned = true;
                        OpenSlaughterhouse(true);
                        _slaughterDoorTimer = 10_000;
                        Announce(NpcBaron, -1329013);
                        if (Instance.FindUpdater<CreatureMapSystem>()?.SummonInstanceCreature(NpcRamstein, 4032.643f, -3378.546f, 119.752f, 4.74f) is { } ramstein)
                        {
                            ramstein.Motion.MovePoint(0, 4032.843f, -3390.246f, 119.732f, run: true);
                        }
                    }
                }
                else if (data == EncounterState.Done)
                {
                    DoUseDoorOrButton(GoSlaughterGate);
                    OpenSlaughterhouse(false);
                    _mindlessTimer = 500;
                    _mindlessCount = 0;
                    _mindless.Clear();
                    for (int i = 0; i < 5; i++)
                    {
                        Instance.FindUpdater<CreatureMapSystem>()?.SummonInstanceCreature(NpcBlackGuard, 4032.602f, -3378.506f, 119.752f, 4.74f);
                    }
                }
                else if (data == EncounterState.Fail && Encounters[type] != EncounterState.Fail)
                {
                    DoUseDoorOrButton(GoGauntletPort);
                    _ramsteinSummoned = false;
                }

                break;
            case TypeBaron:
                if (data == EncounterState.InProgress)
                {
                    SetDoor(GoBaronDoor, false);
                }
                else if (data == EncounterState.Done)
                {
                    if (Encounters[TypeBaronRun] == EncounterState.InProgress)
                    {
                        SetData(TypeBaronRun, EncounterState.Done);
                        CreatureMapSystem? creatures = Instance.FindUpdater<CreatureMapSystem>();
                        foreach (Player player in Instance.Players)
                        {
                            creatures?.RemoveAuras(player, SpellBaronUltimatum);
                            CreatureCredit?.Invoke(player, NpcYsida, GetSingleCreatureFromStorage(NpcYsida)?.Guid ?? default);
                            CastPlayerSpell?.Invoke(player, SpellYsidaFreed);
                        }

                        DoUseDoorOrButton(GoYsidaCage);
                        Announce(NpcYsida, -1329015);
                        if (GetSingleCreatureFromStorage(NpcYsida) is { } ysida)
                        {
                            ysida.Motion.MovePoint(0, 4041.9f, -3337.6f, 115.06f, run: false);
                        }
                    }
                }

                if (data != EncounterState.InProgress)
                {
                    SetDoor(GoBaronDoor, true);
                    SetDoor(GoGauntletPort, true);
                }

                break;
            case TypeBlackGuards:
                if (Encounters[type] == data)
                {
                    return;
                }

                if (data == EncounterState.Done)
                {
                    Announce(NpcBaron, -1329014);
                    DoUseDoorOrButton(GoBaronDoor);
                }

                Encounters[type] = data;
                return; // SD2 does not save this event.
            case TypePostmaster:
                Encounters[type] = data;
                return;
        }

        Encounters[type] = data;
        if (type is TypeBaroness or TypeNerub or TypePallid && data == EncounterState.Special && ZigguratsCleared)
        {
            Announce(NpcBaron, -1329007);
            DoUseDoorOrButton(GoGauntletPort);
            DoUseDoorOrButton(GoSlaughterPort);
        }

        SaveIfDone(data);
    }

    private bool ZigguratsCleared => Encounters[TypeBaroness] == EncounterState.Special
        && Encounters[TypeNerub] == EncounterState.Special && Encounters[TypePallid] == EncounterState.Special;

    public override void OnCreatureEnterCombat(Creature creature)
    {
        switch (creature.Template.Entry)
        {
            case NpcBaroness: SetData(TypeBaroness, EncounterState.InProgress); break;
            case NpcNerub: SetData(TypeNerub, EncounterState.InProgress); break;
            case NpcPallid: SetData(TypePallid, EncounterState.InProgress); break;
            case NpcRamstein: SetData(TypeRamstein, EncounterState.InProgress); break;
            case NpcBaron: SetData(TypeBaron, EncounterState.InProgress); break;
            case NpcBileAbom or NpcVenomAbom: SetData(TypeRamstein, EncounterState.Special); break;
            case NpcMindless or NpcBlackGuard: SetData(TypeBlackGuards, EncounterState.InProgress); break;
        }
    }

    public override void OnCreatureEvade(Creature creature)
    {
        switch (creature.Template.Entry)
        {
            case NpcBaroness: SetData(TypeBaroness, EncounterState.Fail); break;
            case NpcNerub: SetData(TypeNerub, EncounterState.Fail); break;
            case NpcPallid: SetData(TypePallid, EncounterState.Fail); break;
            case NpcRamstein: SetData(TypeRamstein, EncounterState.Fail); break;
            case NpcBaron: SetData(TypeBaron, EncounterState.Fail); break;
            case NpcMindless or NpcBlackGuard: SetData(TypeBlackGuards, EncounterState.Fail); break;
        }
    }

    public override void OnCreatureDeath(Creature creature)
    {
        switch (creature.Template.Entry)
        {
            case NpcBaroness: SetData(TypeBaroness, EncounterState.Done); break;
            case NpcNerub: SetData(TypeNerub, EncounterState.Done); break;
            case NpcPallid: SetData(TypePallid, EncounterState.Done); break;
            case NpcRamstein: SetData(TypeRamstein, EncounterState.Done); break;
            case NpcBaron: SetData(TypeBaron, EncounterState.Done); break;
            case NpcAcolyte: AcolyteDied(creature); break;
            case NpcBileAbom or NpcVenomAbom:
                _abominations.Remove(creature.Guid);
                SetData(TypeRamstein, EncounterState.Special);
                break;
            case NpcMindless:
                if (_mindless.Remove(creature.Guid) && _mindless.Count == 0 && _mindlessCount >= 30)
                {
                    _guardsTimer = 60_000;
                }

                break;
            case NpcBlackGuard:
                if (_guards.Remove(creature.Guid) && _guards.Count == 0)
                {
                    SetData(TypeBlackGuards, EncounterState.Done);
                }

                break;
        }
    }

    public override void Update(uint diffMs)
    {
        if (_baronRunTimer > 0 && Encounters[TypeBaron] != EncounterState.InProgress)
        {
            if (_baronWarnings == 0 && _baronRunTimer <= 10 * 60_000)
            {
                Announce(NpcBaron, -1329010);
                _baronWarnings++;
            }
            else if (_baronWarnings == 1 && _baronRunTimer <= 5 * 60_000)
            {
                Announce(NpcBaron, -1329011);
                _baronWarnings++;
            }
            else if (_baronWarnings == 2 && _baronRunTimer <= 5 * 60_000 - 10_000)
            {
                Announce(NpcYsida, -1329019);
                _baronWarnings++;
            }

            if (diffMs >= _baronRunTimer)
            {
                if (Encounters[TypeBaronRun] != EncounterState.Fail)
                {
                    SetData(TypeBaronRun, EncounterState.Fail);
                    DoUseDoorOrButton(GoYsidaCage);
                    if (GetSingleCreatureFromStorage(NpcYsida) is { } ysida)
                    {
                        ysida.Motion.MovePoint(0, 4041.9f, -3337.6f, 115.06f, run: false);
                    }
                    Announce(NpcBaron, -1329012);
                    _baronRunTimer = 8_000;
                }
                else
                {
                    if (GetSingleCreatureFromStorage(NpcBaron) is { } baron && GetSingleCreatureFromStorage(NpcYsida) is { } ysida)
                    {
                        baron.System?.CastSpell(baron, 27640, ysida, triggered: false); // SPELL_BARON_SOUL_DRAIN
                    }
                    Announce(NpcYsida, -1329020);
                    _baronRunTimer = 0;
                }
            }
            else
            {
                _baronRunTimer -= diffMs;
            }
        }

        if (_slaughterDoorTimer != 0)
        {
            if (diffMs >= _slaughterDoorTimer)
            {
                OpenSlaughterhouse(false);
                _slaughterDoorTimer = 0;
            }
            else
            {
                _slaughterDoorTimer -= diffMs;
            }
        }

        if (_mindlessTimer != 0 && _mindlessCount < 30)
        {
            if (diffMs >= _mindlessTimer)
            {
                if (Instance.FindUpdater<CreatureMapSystem>()?.SummonInstanceCreature(NpcMindless, 3969.357f, -3391.871f, 119.116f, 5.91f) is { } undead)
                {
                    undead.Motion.MovePoint(0, 4033.044f, -3431.031f, 119.055f, run: true);
                    _mindlessCount++;
                }

                _mindlessTimer = _mindlessCount == 30 ? 0u : 400u;
            }
            else
            {
                _mindlessTimer -= diffMs;
            }
        }

        if (_guardsTimer != 0)
        {
            if (diffMs >= _guardsTimer)
            {
                OpenSlaughterhouse(true);
                _guardsTimer = 0;
            }
            else
            {
                _guardsTimer -= diffMs;
            }
        }
    }

    private bool LiveAbominations()
    {
        CreatureMapSystem? system = Instance.FindUpdater<CreatureMapSystem>();
        _abominations.RemoveWhere(guid => system?.FindCreature(guid) is not { IsAlive: true });
        return _abominations.Count > 0;
    }

    private void SortZiggurats()
    {
        CreatureMapSystem? system = Instance.FindUpdater<CreatureMapSystem>();
        // SD2 reserves the highest acolyte as the announcer, then assigns each other acolyte
        // within 35 yards of a door, and its crystal within 50 yards (DoSortZiggurats).
        if (!_announcerChosen)
        {
            Creature? announcer = _unassignedAcolytes.Select(g => system?.FindCreature(g)).OfType<Creature>()
                .OrderByDescending(c => c.Z).FirstOrDefault();
            if (announcer is not null)
            {
                _unassignedAcolytes.Remove(announcer.Guid);
                _announcerChosen = true;
                _acolyteAnnouncer = announcer.Guid;
            }
        }

        for (int i = 0; i < 3; i++)
        {
            GameObject? door = GetSingleGameObjectFromStorage(ZigguratDoors[i]);
            if (door is null)
            {
                continue;
            }

            foreach (ObjectGuid guid in _unassignedAcolytes.ToArray())
            {
                if (system?.FindCreature(guid) is { IsAlive: true } acolyte && DistanceSquared(acolyte, door) <= 35 * 35)
                {
                    _acolytes[i].Add(guid);
                    _unassignedAcolytes.Remove(guid);
                }
            }

            foreach (ObjectGuid guid in _unassignedCrystals.ToArray())
            {
                if (system?.FindCreature(guid) is { } crystal && DistanceSquared(crystal, door) <= 50 * 50)
                {
                    _crystals[i] = guid;
                    _unassignedCrystals.Remove(guid);
                    break;
                }
            }
        }
    }

    private void AcolyteDied(Creature acolyte)
    {
        for (int i = 0; i < 3; i++)
        {
            if (_acolytes[i].Remove(acolyte.Guid) && _acolytes[i].Count == 0)
            {
                Announce(NpcAcolyte, -1329004 - i);
                if (Instance.FindUpdater<CreatureMapSystem>()?.FindCreature(_crystals[i]) is { } crystal)
                {
                    crystal.System?.KillCreature(crystal);
                }

                SetData(TypeBaroness + (uint)i, EncounterState.Special);
                return;
            }
        }
    }

    private void OpenSlaughterhouse(bool open)
    {
        if (_slaughterDoorOpen != open)
        {
            DoUseDoorOrButton(GoSlaughterhouse);
            _slaughterDoorOpen = open;
        }
    }

    private void SetDoor(uint entry, bool open)
    {
        if (GetSingleGameObjectFromStorage(entry) is { } go && (go.State == GameObjectState.Active) != open)
        {
            DoUseDoorOrButton(entry);
        }
    }

    private void Announce(uint entry, int textId)
    {
        Creature? creature = entry == NpcAcolyte
            ? Instance.FindUpdater<CreatureMapSystem>()?.FindCreature(_acolyteAnnouncer)
            : GetSingleCreatureFromStorage(entry);
        if (creature is not null)
        {
            creature.System?.SayText(creature, textId);
        }
    }

    private static float DistanceSquared(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }
}
