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

    // stratholme.h stratholmeLocation[0] (Barthilas door run) and [1] (Barthilas teleport).
    private const float BarthilasRunX = 3725.577f, BarthilasRunY = -3599.484f, BarthilasRunZ = 142.367f;
    private const float BarthilasTeleportX = 4068.284f, BarthilasTeleportY = -3535.678f, BarthilasTeleportZ = 122.771f, BarthilasTeleportO = 2.50f;

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
    private uint _barthilasRunTimer;
    private uint _slaughterDoorTimer;
    private uint _slaughterSquareTimer;
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
        _baronRunTimer = _mindlessTimer = _guardsTimer = _slaughterDoorTimer = _slaughterSquareTimer = _mindlessCount = _barthilasRunTimer = 0;
        _baronWarnings = 0;
        _slaughterDoorOpen = _ramsteinSummoned = _announcerChosen = false;
        _acolyteAnnouncer = default;
        _postboxesUsed = 0;
        _usedPostboxes.Clear();
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
            Creature? ysida = Instance.FindUpdater<CreatureMapSystem>()?.SummonForInstance(NpcYsida, 4041.9f, -3337.6f, 115.06f, 3.82f);
            if (ysida is not null && Encounters[TypeBaronRun] == EncounterState.Fail)
            {
                ysida.System?.KillCreature(ysida);
            }
        }
    }

    /// <summary>GOUse_go_gauntlet_gate and GOUse_go_service_gate (stratholmeScripts.cpp): both return false, the gate opens as usual.</summary>
    public override bool OnGameObjectUse(Player player, GameObject go)
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
        else if (Array.IndexOf(GoPostboxes, go.Entry) >= 0)
        {
            if (_usedPostboxes.Add(go.Guid))
            {
                UsePostbox(player);
            }
        }

        return false;
    }

    /// <summary>The six ClassicDB postboxes with ScriptName go_stratholme_postbox (z2815 gameobject_template).</summary>
    public static readonly uint[] GoPostboxes = [176346, 176349, 176350, 176351, 176352, 176353];
    public const uint NpcUndeadPostman = 11142, SpellSummonPostmaster = 24627;
    private int _postboxesUsed;
    private readonly HashSet<ObjectGuid> _usedPostboxes = [];

    /// <summary>Postboxes the Postmaster event has counted in this instance (instance_stratholme::m_postboxesUsed).</summary>
    public int PostboxesUsed => _postboxesUsed;

    /// <summary>GOUse_go_stratholme_postbox (stratholmeScripts.cpp:95-124): every box brings three Undead Postmen; after two boxes the
    /// third one summons Postmaster Malown through spell 24627 and the event is done. Each box object counts once (the key unlocks it
    /// and SD2 relies on the box not being usable again).</summary>
    private void UsePostbox(Player player)
    {
        if (Encounters[TypePostmaster] == EncounterState.Done)
        {
            return;
        }

        if (Encounters[TypePostmaster] == EncounterState.Special)
        {
            CastPlayerSpell?.Invoke(player, SpellSummonPostmaster);
            SetData(TypePostmaster, EncounterState.Done);
        }
        else
        {
            SetData(TypePostmaster, EncounterState.InProgress);
        }

        // SummonCreature(NPC_UNDEAD_POSTMAN, random point within 3 yards, TEMPSPAWN_DEAD_DESPAWN) x3.
        CreatureMapSystem? creatures = Instance.FindUpdater<CreatureMapSystem>();
        if (creatures is null)
        {
            return;
        }

        for (int i = 0; i < 3; i++)
        {
            // WorldObject::GetRandomPoint: a uniform point in the 3 yard circle around the player.
            float angle = creatures.RandomInt(0, 35_999) * (MathF.PI / 18_000f);
            float distance = 3f * MathF.Sqrt(creatures.RandomInt(0, 10_000) / 10_000f);
            creatures.SummonForInstance(NpcUndeadPostman, player.X + (distance * MathF.Cos(angle)), player.Y + (distance * MathF.Sin(angle)), player.Z, 0f);
        }
    }

    /// <summary>instance_stratholme::OnCreatureRespawn (stratholme.cpp:761-768): once the run has begun, Barthilas comes back in the slaughterhouse.</summary>
    public override void OnCreatureRespawn(Creature creature)
    {
        if (creature.Template.Entry == NpcBarthilas && Encounters[TypeBarthilasRun] != EncounterState.NotStarted)
        {
            creature.System?.NearTeleport(creature, BarthilasTeleportX, BarthilasTeleportY, BarthilasTeleportZ, BarthilasTeleportO);
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
                        Instance.FindUpdater<CreatureMapSystem>()?.SummonForInstance(NpcYsida, 4044.78f, -3333.68f, 115.53f, 4.15f);
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
                        _slaughterSquareTimer = 20_000; // m_slaughterSquareTimer: the reference's own guess
                        DoUseDoorOrButton(GoGauntletPort);
                    }

                    if (!_ramsteinSummoned && !LiveAbominations())
                    {
                        _ramsteinSummoned = true;
                        OpenSlaughterhouse(true);
                        _slaughterDoorTimer = 10_000;
                        _slaughterSquareTimer = 0; // no more abominations to call
                        Announce(NpcBaron, -1329013);
                        if (Instance.FindUpdater<CreatureMapSystem>()?.SummonForInstance(NpcRamstein, 4032.643f, -3378.546f, 119.752f, 4.74f) is { } ramstein)
                        {
                            ramstein.Motion.MovePoint(0, 4033.044f, -3431.031f, 119.055f, run: true); // stratholmeLocation[5], the square (stratholme.cpp:293-294)
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
                        Instance.FindUpdater<CreatureMapSystem>()?.SummonForInstance(NpcBlackGuard, 4032.602f, -3378.506f, 119.752f, 4.74f);
                    }
                }
                else if (data == EncounterState.Fail && Encounters[type] != EncounterState.Fail)
                {
                    // Open the gauntlet port again and stop calling abominations; those already walking stop. Ramstein, if summoned,
                    // stays (TEMPSPAWN_DEAD_DESPAWN) and goes home, so he is not summoned a second time.
                    DoUseDoorOrButton(GoGauntletPort);
                    _slaughterSquareTimer = 0;
                    CreatureMapSystem? creatures = Instance.FindUpdater<CreatureMapSystem>();
                    foreach (ObjectGuid guid in _abominations)
                    {
                        if (creatures?.FindCreature(guid) is { } abomination && abomination.Motion.CurrentType == MovementGeneratorType.Point)
                        {
                            abomination.Motion.Remove(MovementGeneratorType.Point);
                        }
                    }
                }
                else if (data == EncounterState.InProgress && Encounters[type] == EncounterState.Fail)
                {
                    // After a fail, aggroing Ramstein means a new try at him: close the gauntlet port again.
                    DoUseDoorOrButton(GoGauntletPort);
                }

                break;
            case TypeBaron:
                if (data == EncounterState.InProgress)
                {
                    SetDoor(GoBaronDoor, false);
                    // A new try after a wipe closes the slaughterhouse gauntlet again (stratholme.cpp:352-357).
                    if (Encounters[type] == EncounterState.Fail)
                    {
                        SetDoor(GoGauntletPort, false);
                    }
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
            case TypeBarthilasRun:
                // stratholme.cpp:427-441: Barthilas, alive and out of combat, warns the Baron and runs off; 8 s later he is teleported to
                // the slaughterhouse (Update).
                if (data == EncounterState.InProgress && GetSingleCreatureFromStorage(NpcBarthilas) is { IsAlive: true } barthilas
                    && !barthilas.Combat.IsInCombat)
                {
                    barthilas.System?.SayText(barthilas, -1329008); // SAY_WARN_BARON
                    barthilas.Motion.MovePoint(0, BarthilasRunX, BarthilasRunY, BarthilasRunZ, run: true);
                    _barthilasRunTimer = 8_000;
                }

                break;
            case TypeBlackGuards:
                if (Encounters[type] == data)
                {
                    return;
                }

                // stratholme.cpp:442-462: a new try after a wipe closes the gauntlet port again; a wipe opens it so the group can leave.
                if ((data == EncounterState.InProgress && Encounters[type] == EncounterState.Fail) || data == EncounterState.Fail)
                {
                    DoUseDoorOrButton(GoGauntletPort);
                }

                if (data == EncounterState.Done)
                {
                    Announce(NpcBaron, -1329014);
                    DoUseDoorOrButton(GoBaronDoor);
                }

                Encounters[type] = data;
                return; // SD2 does not save this event.
            case TypePostmaster:
                // instance_stratholme::SetData (stratholme.cpp:463-474): after the second box, prepare the Postmaster.
                Encounters[type] = data;
                if (data == EncounterState.InProgress && ++_postboxesUsed == 2)
                {
                    Encounters[type] = EncounterState.Special;
                }

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
            case NpcRamstein:
                SetData(TypeRamstein, EncounterState.Fail);
                OpenSlaughterhouse(true); // Ramstein walks back into the slaughterhouse
                break;
            case NpcBaron: SetData(TypeBaron, EncounterState.Fail); break;
            case NpcBileAbom or NpcVenomAbom: SetData(TypeRamstein, EncounterState.Fail); break; // a wipe before Ramstein
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

        if (_slaughterSquareTimer != 0)
        {
            if (diffMs >= _slaughterSquareTimer)
            {
                CallNextAbomination();
                _slaughterSquareTimer = (uint)((Instance.FindUpdater<CreatureMapSystem>()?.RandomInt(30, 45) ?? 30) * 1000);
            }
            else
            {
                _slaughterSquareTimer -= diffMs;
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
                if (Instance.FindUpdater<CreatureMapSystem>()?.SummonForInstance(NpcMindless, 3969.357f, -3391.871f, 119.116f, 5.91f) is { } undead)
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

        if (_barthilasRunTimer != 0)
        {
            if (diffMs >= _barthilasRunTimer)
            {
                _barthilasRunTimer = 0;
                if (GetSingleCreatureFromStorage(NpcBarthilas) is { IsAlive: true } barthilas && !barthilas.Combat.IsInCombat)
                {
                    barthilas.Motion.Clear();
                    barthilas.System?.MoveIdle(barthilas);
                    barthilas.System?.NearTeleport(barthilas, BarthilasTeleportX, BarthilasTeleportY, BarthilasTeleportZ, BarthilasTeleportO);
                }

                SetData(TypeBarthilasRun, EncounterState.Done);
            }
            else
            {
                _barthilasRunTimer -= diffMs;
            }
        }

        if (_guardsTimer != 0)
        {
            if (diffMs >= _guardsTimer)
            {
                // stratholme.cpp:1055-1077: open the door and send each living, idle Black Guard to a random point within 10 yd of the
                // square (stratholmeLocation[5]).
                OpenSlaughterhouse(true);
                if (Instance.FindUpdater<CreatureMapSystem>() is { } system)
                {
                    foreach (ObjectGuid guid in _guards)
                    {
                        if (system.FindCreature(guid) is { IsAlive: true } guard && !guard.Combat.IsInCombat)
                        {
                            float angle = system.RandomInt(0, 359) * MathF.PI / 180f;
                            float distance = system.RandomInt(0, 10);
                            guard.Motion.MovePoint(0, 4033.044f + (distance * MathF.Cos(angle)), -3431.031f + (distance * MathF.Sin(angle)),
                                119.055f, run: false);
                        }
                    }
                }

                _guardsTimer = 0;
            }
            else
            {
                _guardsTimer -= diffMs;
            }
        }
    }

    /// <summary>
    /// instance_stratholme::Update, m_slaughterSquareTimer: the first living abomination that is neither walking already nor fighting walks
    /// to a point within 10 yards of the slaughter square port.
    /// </summary>
    private void CallNextAbomination()
    {
        if (Instance.FindUpdater<CreatureMapSystem>() is not { } creatures)
        {
            return;
        }

        foreach (ObjectGuid guid in _abominations)
        {
            if (creatures.FindCreature(guid) is not { IsAlive: true } abomination
                || abomination.Motion.CurrentType == MovementGeneratorType.Point)
            {
                continue;
            }

            if (!abomination.Combat.IsInCombat && GetSingleGameObjectFromStorage(GoSlaughterPort) is { } port)
            {
                float angle = creatures.RandomInt(0, 359) * MathF.PI / 180f;
                float distance = creatures.RandomInt(0, 10);
                abomination.Motion.MovePoint(0, port.X + (distance * MathF.Cos(angle)), port.Y + (distance * MathF.Sin(angle)), port.Z, run: false);
            }

            break;
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
