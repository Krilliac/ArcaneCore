using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.BlackrockSpire;

/// <summary>
/// ScriptDev2 instance_blackrock_spire (mangos-classic
/// src/game/AI/ScriptDevAI/scripts/eastern_kingdoms/blackrock_spire/instance_blackrock_spire.cpp:
/// OnObjectCreate, SetData, OnCreatureDeath, OnCreatureEvade, DoSendNextStadiumWave, Update,
/// AreaTrigger_at_blackrock_spire). Map 229 includes both LBRS and UBRS.
/// </summary>
[InstanceScript(MapId)]
public sealed class BlackrockSpireInstance(Map instance) : ScriptedInstance(instance, 6)
{
    public const uint MapId = 229;
    public const uint TypeRoomEvent = 0, TypeEmberseer = 1, TypeFlamewreath = 2,
        TypeStadium = 3, TypeDrakkisath = 4, TypeValthalak = 5;

    public const uint GoEmberseerIn = 175244, GoEmberseerCombat = 175705, GoEmberseerOut = 175153;
    public const uint GoGythEntry = 164726, GoGythCombat = 175185, GoGythExit = 175186;
    public const uint GoDrakkisathDoor1 = 175946, GoDrakkisathDoor2 = 175947;
    public const uint GoFatherFlame = 175245, GoDragonspine = 164725;
    public const uint NpcNefarius = 10162, NpcRend = 10429, NpcGyth = 10339, NpcBeast = 10430, NpcEmberseer = 9816;
    public const uint EventAltarEmberseer = 4884, SpellEmberseerGrowing = 16048;
    public const uint NpcDrakkisath = 10363, NpcSolakar = 10264, NpcWhelp = 10442,
        NpcDragon = 10447, NpcHandler = 10742, NpcBlackhandElite = 10317;
    public const uint NpcIncarcerator = 10316;
    private const uint NpcRoomSummoner = 9818, NpcRoomVeteran = 9819, NpcRookeryGuardian = 10258, NpcRookeryHatcher = 10683;
    private static readonly uint[] RoomRunes = [175197, 175199, 175195, 175200, 175198, 175196, 175194];
    private static readonly uint[] EmberseerRunes = [175266, 175267, 175268, 175269, 175270, 175271, 175272];
    private static readonly uint[] Braziers = [175528, 175529, 175530, 175531, 175532, 175533];

    // blackrock_spire.h aStadiumEventNpcs: seven waves, in source order.
    private static readonly uint[][] StadiumWaves =
    [
        [NpcWhelp, NpcWhelp, NpcWhelp, NpcDragon],
        [NpcWhelp, NpcWhelp, NpcWhelp, NpcDragon],
        [NpcWhelp, NpcWhelp, NpcDragon, NpcHandler],
        [NpcWhelp, NpcWhelp, NpcDragon, NpcHandler],
        [NpcWhelp, NpcWhelp, NpcWhelp, NpcDragon, NpcHandler],
        [NpcWhelp, NpcWhelp, NpcDragon, NpcDragon, NpcHandler],
        [NpcWhelp, NpcWhelp, NpcDragon, NpcDragon, NpcHandler],
    ];
    // blackrock_spire.h aStadiumSpectators/aSpectatorsSpawnLocs/aSpectatorsTargetLocs.
    private static readonly (uint Entry, float X, float Y, float Z, float O, float Tx, float Ty, float Tz)[] Spectators =
    [
        (9819, 163.3209f, -340.9818f, 111.0216f, 4.818223f, 160.619f, -395.826f, 121.9752f),
        (9819, 164.2471f, -339.0313f, 111.0368f, 1.413717f, 162.1428f, -395.1175f, 121.9751f),
        (9819, 161.124f, -339.5178f, 111.0381f, 3.001966f, 158.6822f, -395.7097f, 121.9753f),
        (10317, 162.5045f, -337.8101f, 111.0367f, 4.13643f, 164.384f, -395.3787f, 121.9751f),
        (9819, 160.9896f, -337.7715f, 111.0368f, 1.117011f, 156.9669f, -395.2188f, 121.9752f),
        (9819, 161.8347f, -335.7923f, 111.0352f, 2.286381f, 166.2515f, -395.0366f, 121.975f),
        (9819, 113.9726f, -366.0805f, 116.9195f, 6.252025f, 143.814f, -396.7092f, 121.9753f),
        (9819, 112.7245f, -368.9635f, 116.9307f, 4.677482f, 145.3893f, -396.1959f, 121.9752f),
        (9819, 110.5757f, -368.2123f, 116.9278f, 4.310963f, 142.1598f, -396.0284f, 121.9752f),
        (10317, 109.3343f, -366.4785f, 116.9261f, 2.740167f, 147.7274f, -396.3042f, 121.9753f),
        (9819, 110.1331f, -363.9824f, 116.9272f, 0.5235988f, 139.9446f, -396.7277f, 121.9753f),
        (9819, 111.9971f, -363.0948f, 116.929f, 5.951573f, 149.3754f, -395.7497f, 121.975f),
    ];

    private readonly HashSet<ObjectGuid> _waveMobs = [];
    private readonly HashSet<ObjectGuid> _spectators = [];
    private readonly HashSet<ObjectGuid> _roomMobs = [];
    private readonly HashSet<ObjectGuid>[] _roomAssigned = [[], [], [], [], [], [], []];
    private readonly HashSet<ObjectGuid> _incarcerators = [];
    private int _stadiumWave;
    private int _stadiumDialogueStep;
    private uint _stadiumTimer;
    private uint _flamewreathTimer;
    private int _flamewreathWave;
    private uint _dragonspineTimer;
    private int _dragonspineStep;
    private bool _upperDoorOpened;
    private bool _beastIntroDone;
    private bool _beastOutOfLair;

    public int StadiumWave => _stadiumWave;
    public bool BeastIntroDone => _beastIntroDone;
    public void TrackRend(Creature rend)
    {
        if (Encounters[TypeStadium] == EncounterState.InProgress)
        {
            _waveMobs.Add(rend.Guid);
        }
    }

    public override void Initialize()
    {
        base.Initialize();
        _stadiumTimer = 0;
        _stadiumWave = 0;
        _stadiumDialogueStep = 0;
        _waveMobs.Clear();
        _spectators.Clear();
        _roomMobs.Clear();
        _incarcerators.Clear();
        foreach (HashSet<ObjectGuid> room in _roomAssigned)
        {
            room.Clear();
        }
        _flamewreathTimer = _dragonspineTimer = 0;
        _flamewreathWave = _dragonspineStep = 0;
        _upperDoorOpened = false;
        _beastIntroDone = _beastOutOfLair = false;
    }

    public override void OnObjectCreate(GameObject go)
    {
        switch (go.Entry)
        {
            case GoEmberseerIn:
                OpenIf(go, Encounters[TypeRoomEvent] == EncounterState.Done);
                break;
            case GoEmberseerOut:
                OpenIf(go, Encounters[TypeEmberseer] == EncounterState.Done);
                break;
            case GoGythExit:
                OpenIf(go, Encounters[TypeStadium] == EncounterState.Done);
                break;
            case GoEmberseerCombat or GoGythEntry or GoGythCombat or GoDrakkisathDoor1 or GoDrakkisathDoor2
                or GoFatherFlame or GoDragonspine:
                break;
            default:
                if (!RoomRunes.Contains(go.Entry) && !EmberseerRunes.Contains(go.Entry) && !Braziers.Contains(go.Entry))
                {
                    return;
                }
                break;
        }

        StoreGameObject(go);
    }

    public override void OnCreatureCreate(Creature creature)
    {
        if (creature.Template.Entry is NpcNefarius or NpcRend or NpcGyth or NpcBeast or NpcEmberseer or NpcSolakar)
        {
            StoreCreature(creature);
        }
        else if (creature.Template.Entry is NpcRoomSummoner or NpcRoomVeteran)
        {
            _roomMobs.Add(creature.Guid);
        }
        else if (creature.Template.Entry == NpcIncarcerator)
        {
            _incarcerators.Add(creature.Guid);
        }
    }

    /// <summary>mangos-classic SPELL_FINKLE_IS_EINHORN: summons Finkle Einhorn out of The Beast's corpse.</summary>
    public const uint SpellFinkleIsEinhorn = 16710;

    /// <summary>
    /// instance_blackrock_spire::OnCreatureDespawn (mangos-classic instance_blackrock_spire.cpp:488-492): when The Beast's corpse is removed
    /// it casts Finkle is Einhorn on itself (TRIGGERED_OLD_TRIGGERED), which brings out Finkle Einhorn.
    /// </summary>
    public override void OnCreatureDespawn(Creature creature)
    {
        if (creature.Template.Entry == NpcBeast)
        {
            creature.System?.CastSpell(creature, SpellFinkleIsEinhorn, creature, triggered: true);
        }
    }

    /// <summary>
    /// GOUse_go_father_flame: StartflamewreathEventIfCan, and the script takes the use (returns true). StartflamewreathEventIfCan only arms
    /// the wave timer; it does not set TYPE_FLAMEWREATH.
    /// </summary>
    public override bool OnGameObjectUse(Player player, GameObject go)
    {
        if (go.Entry != GoFatherFlame)
        {
            return false;
        }

        if (Encounters[TypeFlamewreath] is not (EncounterState.Done or EncounterState.InProgress)
            && Encounters[TypeDrakkisath] != EncounterState.Done && GetSingleCreatureFromStorage(NpcSolakar) is not { IsAlive: true })
        {
            _flamewreathTimer = 1;
            _flamewreathWave = 0;
        }

        return true;
    }

    /// <summary>ProcessEventId_event_spell_altar_emberseer (scriptdev2.sql event 4884, sent by the Blackrock Altar's ritual spell 16533).</summary>
    public override bool OnSpellEvent(Unit caster, uint eventId)
    {
        if (eventId != EventAltarEmberseer)
        {
            return false;
        }

        if (caster is Player)
        {
            StartEmberseerEvent();
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

        if (type == TypeRoomEvent && data == EncounterState.Done)
        {
            DoUseDoorOrButton(GoEmberseerIn);
        }
        else if (type == TypeEmberseer)
        {
            if (Encounters[type] == data)
            {
                return;
            }

            DoUseDoorOrButton(GoEmberseerCombat);
            if (data == EncounterState.Fail)
            {
                foreach (ObjectGuid guid in _incarcerators)
                {
                    if (Instance.FindUpdater<CreatureMapSystem>()?.FindCreature(guid) is { } incarcerator)
                    {
                        if (!incarcerator.IsAlive)
                        {
                            incarcerator.System?.ForceRespawn(incarcerator);
                        }

                        incarcerator.UnitFlags |= UnitFlags.ImmuneToNpc | UnitFlags.ImmuneToPlayer;
                    }
                }

                UseEmberseerRunes(reset: true);
            }
            else if (data == EncounterState.Done)
            {
                UseEmberseerRunes(reset: false);
                DoUseDoorOrButton(GoEmberseerOut);
            }
        }
        else if (type == TypeStadium)
        {
            if (Encounters[type] == data)
            {
                return;
            }

            DoUseDoorOrButton(GoGythEntry);
            if (data == EncounterState.InProgress)
            {
                // instance_blackrock_spire::JustDidDialogueStep starts the first wave after the intro.
                if (GetSingleCreatureFromStorage(NpcNefarius) is { } nefarius)
                {
                    nefarius.System?.SayText(nefarius, -1229004);
                }
                _stadiumDialogueStep = 0;
                _stadiumTimer = 7_000;
            }
            else if (data == EncounterState.Done)
            {
                DespawnEventCreatures();
                DoUseDoorOrButton(GoGythExit);
            }
            else if (data == EncounterState.Fail)
            {
                // SetData(TYPE_STADIUM, FAIL): Nefarius, Rend and Gyth go with the spectators (and the wave mobs, which the reference
                // despawns one by one as they evade), so area trigger 2026 can start the event again without duplicates.
                CreatureMapSystem? creatures = Instance.FindUpdater<CreatureMapSystem>();
                foreach (uint entry in new[] { NpcNefarius, NpcRend, NpcGyth })
                {
                    if (GetSingleCreatureFromStorage(entry) is { } eventNpc)
                    {
                        creatures?.ForcedDespawn(eventNpc, 0);
                    }
                }

                DespawnEventCreatures();
                _stadiumWave = 0;
                _stadiumDialogueStep = 0;
                _stadiumTimer = 0;
            }
        }
        else if (type == TypeFlamewreath && data == EncounterState.Fail)
        {
            _flamewreathTimer = 0;
            _flamewreathWave = 0;
        }

        Encounters[type] = data;
        SaveIfDone(data);
    }

    public override void OnAreaTrigger(Player player, uint triggerId)
    {
        if (!player.IsAlive || player.IsGameMaster)
        {
            return;
        }

        if (triggerId == 2046)
        {
            if (!_upperDoorOpened && player.Inventory.GetItemCount(12344, false) > 0)
            {
                _upperDoorOpened = true;
                _dragonspineTimer = 100;
                _dragonspineStep = 0;
            }

            SortRoomEventMobs();
        }
        else if (triggerId == 2026 && GetData(TypeStadium) is not (EncounterState.InProgress or EncounterState.Done))
        {
            CreatureMapSystem? creatures = Instance.FindUpdater<CreatureMapSystem>();
            if (creatures?.SummonInstanceCreature(NpcNefarius, 164.63f, -444.04f, 121.97f, 3.22f) is { } nefarius)
            {
                nefarius.FactionTemplate = 103;
                nefarius.UnitFlags |= UnitFlags.ImmuneToPlayer;
            }

            if (creatures?.SummonInstanceCreature(NpcRend, 161.01f, -443.73f, 121.97f, 6.26f) is { } rend)
            {
                rend.UnitFlags |= UnitFlags.ImmuneToPlayer;
            }
            SetData(TypeStadium, EncounterState.InProgress);
        }
        else if (triggerId is 2066 or 2067 && GetSingleCreatureFromStorage(NpcBeast) is { } beast)
        {
            if (!_beastIntroDone)
            {
                float[] x = [98.09f, 99.81f, 96.92f];
                float[] y = [-563.45f, -561.47f, -560.98f];
                float[] z = [109.86f, 109.24f, 110.18f];
                for (uint i = 0; i < 3; i++)
                {
                    if (beast.System?.SummonDeadDespawn(beast, NpcBlackhandElite, x[i], y[i], z[i], 2.4f) is { } elite)
                    {
                        elite.System?.ChangeMovement(elite, 2, i, 0);
                    }
                }

                _beastIntroDone = true;
            }

            if (triggerId == 2066 && !_beastOutOfLair)
            {
                beast.System?.ChangeMovement(beast, 2, 0, 0);
                _beastOutOfLair = true;
            }
        }
    }

    public override void OnCreatureEnterCombat(Creature creature)
    {
        if (creature.Template.Entry == NpcIncarcerator)
        {
            SetData(TypeEmberseer, EncounterState.InProgress);
        }
    }

    public override void OnCreatureEvade(Creature creature)
    {
        if (creature.Template.Entry == NpcIncarcerator && GetSingleCreatureFromStorage(NpcEmberseer) is { } emberseer)
        {
            emberseer.AI?.EnterEvadeMode();
        }
        else if (creature.Template.Entry is NpcSolakar or NpcRookeryGuardian or NpcRookeryHatcher)
        {
            SetData(TypeFlamewreath, EncounterState.Fail);
        }

        if (creature.Template.Entry is NpcGyth or NpcRend or NpcWhelp or NpcDragon or NpcHandler && creature.Spawn is null)
        {
            SetData(TypeStadium, EncounterState.Fail);
        }
    }

    public override void OnCreatureDeath(Creature creature)
    {
        switch (creature.Template.Entry)
        {
            case NpcRoomSummoner or NpcRoomVeteran:
                if (Encounters[TypeRoomEvent] == EncounterState.InProgress)
                {
                    for (int i = 0; i < RoomRunes.Length; i++)
                    {
                        if (_roomAssigned[i].Remove(creature.Guid) && _roomAssigned[i].Count == 0)
                        {
                            DoUseDoorOrButton(RoomRunes[i]);
                        }
                    }

                    if (_roomAssigned.All(room => room.Count == 0))
                    {
                        SetData(TypeRoomEvent, EncounterState.Done);
                    }
                }
                break;
            case NpcSolakar:
                SetData(TypeFlamewreath, EncounterState.Done);
                break;
            case NpcDrakkisath:
                SetData(TypeDrakkisath, EncounterState.Done);
                DoUseDoorOrButton(GoDrakkisathDoor1);
                DoUseDoorOrButton(GoDrakkisathDoor2);
                break;
            case NpcGyth or NpcRend:
                if (_waveMobs.Remove(creature.Guid) && _waveMobs.Count == 0)
                {
                    if (GetSingleCreatureFromStorage(NpcNefarius) is { } nefarius)
                    {
                        nefarius.System?.SayText(nefarius, -1229018);
                    }
                    SetData(TypeStadium, EncounterState.Done);
                }
                break;
            default:
                if (_waveMobs.Remove(creature.Guid) && _waveMobs.Count == 0)
                {
                    SendNextStadiumWave();
                }
                break;
        }
    }

    public override void Update(uint diffMs)
    {
        if (_stadiumTimer > 0)
        {
            if (diffMs >= _stadiumTimer)
            {
                _stadiumTimer = 0;
                SendNextStadiumWave();
            }
            else
            {
                _stadiumTimer -= diffMs;
            }
        }

        if (_flamewreathTimer > 0)
        {
            if (diffMs >= _flamewreathTimer)
            {
                _flamewreathTimer = 0;
                SendNextFlamewreathWave();
            }
            else
            {
                _flamewreathTimer -= diffMs;
            }
        }

        if (_dragonspineTimer > 0)
        {
            if (diffMs >= _dragonspineTimer)
            {
                _dragonspineStep++;
                if (_dragonspineStep <= 3)
                {
                    DoUseDoorOrButton(Braziers[(_dragonspineStep - 1) * 2]);
                    DoUseDoorOrButton(Braziers[(_dragonspineStep - 1) * 2 + 1]);
                    _dragonspineTimer = 1_000;
                }
                else
                {
                    DoUseDoorOrButton(GoDragonspine);
                    _dragonspineTimer = 0;
                }
            }
            else
            {
                _dragonspineTimer -= diffMs;
            }
        }
    }

    /// <summary>instance_blackrock_spire::DoProcessEmberseerEvent, started by the altar event script.</summary>
    public void StartEmberseerEvent()
    {
        if (Encounters[TypeEmberseer] is EncounterState.Done or EncounterState.InProgress || _incarcerators.Count == 0
            || GetSingleCreatureFromStorage(NpcEmberseer) is not { } emberseer
            || emberseer.System?.HasAura(emberseer, SpellEmberseerGrowing) == true)
        {
            return;
        }

        emberseer.System?.SayText(emberseer, -1229000);
        emberseer.System?.CastSpell(emberseer, SpellEmberseerGrowing, emberseer, triggered: true);
        foreach (ObjectGuid guid in _incarcerators)
        {
            if (Instance.FindUpdater<CreatureMapSystem>()?.FindCreature(guid) is { IsAlive: true } incarcerator)
            {
                incarcerator.System?.InterruptCast(incarcerator);
                incarcerator.UnitFlags &= ~(UnitFlags.ImmuneToNpc | UnitFlags.ImmuneToPlayer);
            }
        }
    }

    public void UseEmberseerRunes(bool reset)
    {
        foreach (uint entry in EmberseerRunes)
        {
            if (GetSingleGameObjectFromStorage(entry) is { } rune
                && (rune.State == GameObjectState.Active) == reset)
            {
                DoUseDoorOrButton(entry);
            }
        }
    }

    private void SortRoomEventMobs()
    {
        if (Encounters[TypeRoomEvent] != EncounterState.NotStarted)
        {
            return;
        }

        CreatureMapSystem? creatures = Instance.FindUpdater<CreatureMapSystem>();
        for (int i = 0; i < RoomRunes.Length; i++)
        {
            if (GetSingleGameObjectFromStorage(RoomRunes[i]) is not { } rune)
            {
                continue;
            }

            foreach (ObjectGuid guid in _roomMobs)
            {
                if (creatures?.FindCreature(guid) is { IsAlive: true } mob
                    && (mob.X - rune.X) * (mob.X - rune.X) + (mob.Y - rune.Y) * (mob.Y - rune.Y) + (mob.Z - rune.Z) * (mob.Z - rune.Z) < 100f)
                {
                    _roomAssigned[i].Add(guid);
                }
            }
        }

        SetData(TypeRoomEvent, EncounterState.InProgress);
    }

    private void SendNextFlamewreathWave()
    {
        CreatureMapSystem? creatures = Instance.FindUpdater<CreatureMapSystem>();
        if (_flamewreathWave < 6)
        {
            for (int i = 0; i < 2; i++)
            {
                uint entry = _flamewreathWave == 0 || creatures?.RandomInt(0, 1) == 0 ? NpcRookeryHatcher : NpcRookeryGuardian;
                // DoSendNextFlamewreathWave: TEMPSPAWN_TIMED_OOC_OR_DEAD_DESPAWN, 300000.
                if (creatures?.SummonInstanceCreatureTimedOocOrDead(entry, 51.11098f, -266.0549f, 92.87846f, 0f, 300_000) is { } mob
                    && _flamewreathWave == 0 && i == 1)
                {
                    mob.System?.SayText(mob, -1229020);
                }
            }

            _flamewreathTimer = _flamewreathWave < 4 ? 30_000u : 40_000u;
            _flamewreathWave++;
        }
        else
        {
            creatures?.SummonInstanceCreatureTimedOocOrDead(NpcSolakar, 51.11098f, -266.0549f, 92.87846f, 0f, 3_600_000); // HOUR * IN_MILLISECONDS
            SetData(TypeFlamewreath, EncounterState.Special);
        }
    }

    private void SendNextStadiumWave()
    {
        if (Encounters[TypeStadium] != EncounterState.InProgress || GetSingleCreatureFromStorage(NpcNefarius) is not { } nefarius)
        {
            return;
        }

        if (_stadiumDialogueStep == 0)
        {
            nefarius.System?.SayText(nefarius, -1229005);
            _stadiumDialogueStep = 1;
            _stadiumTimer = 5_000;
            return;
        }

        if (_stadiumDialogueStep == 1)
        {
            SpawnSpectators(nefarius);
            _stadiumDialogueStep = 2;
        }

        if (_stadiumWave < StadiumWaves.Length)
        {
            foreach (uint entry in StadiumWaves[_stadiumWave])
            {
                if (nefarius.System?.SummonDeadDespawn(nefarius, entry, 210f, -420.3f, 110.94f, 3.14f) is { } mob)
                {
                    _waveMobs.Add(mob.Guid);
                    mob.Motion.MovePoint(0, 163.62f, -420.33f, 110.47f, run: true);
                }
            }

            DoUseDoorOrButton(GoGythCombat);
            _stadiumWave++;
            // DoSendNextStadiumWave: the timer stops once the seventh wave is out; the Gyth intro waits for its last death.
            _stadiumTimer = _stadiumWave < StadiumWaves.Length ? 60_000u : 0u;
        }
        else if (_stadiumWave == StadiumWaves.Length)
        {
            nefarius.System?.SayText(nefarius, -1229014);
            _stadiumWave++;
            _stadiumTimer = 3_000;
        }
        else if (_stadiumWave == StadiumWaves.Length + 1)
        {
            if (GetSingleCreatureFromStorage(NpcRend) is { } rend)
            {
                rend.System?.SayText(rend, -1229015);
            }

            _stadiumWave++;
            _stadiumTimer = 2_000;
        }
        else if (_stadiumWave == StadiumWaves.Length + 2)
        {
            nefarius.System?.SayText(nefarius, -1229016);
            // JustDidDialogueStep(SAY_NEFARIUS_WARCHIEF): Rend leaves the balcony (ForcedDespawn 5000 towards aStadiumLocs[6]);
            // boss_gyth summons him again when Gyth falls under 11%.
            if (GetSingleCreatureFromStorage(NpcRend) is { IsAlive: true } rend && rend.System is { } rendSystem)
            {
                rend.Motion.MovePoint(0, 165.74f, -466.46f, 116.80f, run: true);
                rendSystem.ForcedDespawn(rend, 5_000);
            }

            _stadiumWave++;
            _stadiumTimer = 30_000;
        }
        else if (_stadiumWave == StadiumWaves.Length + 3)
        {
            if (nefarius.System?.SummonDeadDespawn(nefarius, NpcGyth, 210.14f, -397.54f, 111.1f, 0f) is { } gyth)
            {
                _waveMobs.Add(gyth.Guid);
                gyth.Motion.MovePoint(0, 163.62f, -420.33f, 110.47f, run: true);
            }

            _stadiumWave++;
            DoUseDoorOrButton(GoGythCombat);
        }
    }

    private void SpawnSpectators(Creature nefarius)
    {
        foreach ((uint entry, float x, float y, float z, float orientation, float tx, float ty, float tz) in Spectators)
        {
            if (nefarius.System?.SummonDeadDespawn(nefarius, entry, x, y, z, orientation) is { } spectator)
            {
                spectator.ReactState = CreatureReactState.Defensive;
                spectator.Motion.MovePoint(0, tx, ty, tz, run: true);
                _spectators.Add(spectator.Guid);
            }
        }
    }

    private void DespawnEventCreatures()
    {
        CreatureMapSystem? system = Instance.FindUpdater<CreatureMapSystem>();
        foreach (ObjectGuid guid in _waveMobs.Concat(_spectators))
        {
            if (system?.FindCreature(guid) is { } mob)
            {
                system.ForcedDespawn(mob, 0);
            }
        }

        _waveMobs.Clear();
        _spectators.Clear();
        _stadiumTimer = 0;
    }
}
