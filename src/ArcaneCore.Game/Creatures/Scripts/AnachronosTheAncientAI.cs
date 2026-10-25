using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Anachronos the Ancient (entry 15381, Silithus, <c>npc_anachronos_the_ancient</c>): mangos-classic / vmangos
/// <c>npc_anachronos_the_ancientAI</c> and <c>QuestAccept_go_crystalline_tear</c> (scripts/kalimdor/silithus/silithus.cpp:1068-2046).
/// Drives the 55-stage vision of the War of the Shifting Sands for quest 8519 ("A Pawn on the Eternal Board") accepted from
/// the Crystalline Tear (GO 180633): summons Fandral Staghelm (15382), Merithra of the Dream (15378), Caelestrasz (15379),
/// Arygos (15380), Kaldorei Infantry (15423) and the Qiraji waves (15414, 15421, 15422, 15424), seals the Ahn'Qiraj gate
/// objects (176146, 176147, 176148), plays out the shattering of the Scepter of the Shifting Sands, and completes quest 8519
/// at stage 50.
/// </summary>
public sealed class AnachronosTheAncientAI(Creature creature) : CreatureAI(creature)
{
    public const uint Entry = 15381;
    public const string ScriptName = "npc_anachronos_the_ancient";

    public const uint NpcAnachronosQuestTrigger = 15454;
    public const uint NpcFandralStaghelm = 15382;
    public const uint NpcMerithraOfTheDream = 15378;
    public const uint NpcCaelestrasz = 15379;
    public const uint NpcArygos = 15380;
    public const uint NpcKaldoreiInfantry = 15423;
    public const uint NpcQirajiWasp = 15414;
    public const uint NpcQirajiDrone = 15421;
    public const uint NpcQirajiTank = 15422;
    public const uint NpcAnubisathConqueror = 15424;

    public const uint GoCrystallineTear = 180633;
    public const uint GoMerithraWake = 180604;
    public const uint GoAqBarrier = 176146;
    public const uint GoAqGateRoots = 176147;
    public const uint GoAqGateRunes = 176148;

    public const uint QuestAPawnOnTheEternalBoard = 8519;
    public const uint ItemHammerOfTheQiraji = 21128;
    public const uint HammerDisplayId = 33429;

    public const int SayAnachronosIntro1 = 10945;
    public const int SayFandralIntro2 = 10955;
    public const int SayMerithraIntro3 = 10959;
    public const int EmoteArygosNod = 11066;
    public const int SayCaelestraszIntro4 = 10960;
    public const int EmoteMerithraBow = 11076;
    public const int SayMerithraIntro5 = 10961;
    public const int SayMerithraAttack1 = 10962;
    public const int SayArygosAttack2 = 10963;
    public const int SayArygosAttack3 = 10964;
    public const int SayCaelestraszAttack4 = 10965;
    public const int SayCaelestraszAttack5 = 10966;
    public const int SayAnachronosSeal1 = 10946;
    public const int SayFandralSeal2 = 10956;
    public const int SayAnachronosSeal3 = 10947;
    public const int SayAnachronosSeal4 = 10948;
    public const int SayAnachronosSeal5 = 10949;
    public const int SayFandralSeal6 = 10957;
    public const int EmoteFandralExhausted = 11068;
    public const int SayAnachronosEpilogue1 = 10950;
    public const int SayAnachronosEpilogue2 = 10951;
    public const int SayAnachronosEpilogue3 = 10952;
    public const int EmoteAnachronosScepter = 11069;
    public const int SayFandralEpilogue4 = 10953;
    public const int SayFandralEpilogue5 = 10958;
    public const int EmoteFandralShatter = 11070;
    public const int SayAnachronosEpilogue6 = 10954;
    public const int SayFandralEpilogue7 = 10970;
    public const int EmoteAnachronosDisappointed = 11073;
    public const int EmoteFandralHateful = 11071;
    public const int SayAnachronosEpilogue8 = 10971;

    public const uint SpellTimeStop = 25158;
    public const uint SpellGlyphOfWarding = 25166;
    public const uint SpellPrismaticBarrier = 25159;
    public const uint SpellCallAncients = 25167;
    public const uint SpellShatterHammer = 25182;
    public const uint SpellThrowHammer = 25180;
    public const uint SpellFrostBreath = 25181;
    public const uint SpellFlameBreath = 25175;
    public const uint SpellNoxiousBreath = 25178;
    public const uint SpellGreenDragonTransform = 25105;
    public const uint SpellRedDragonTransform = 25106;
    public const uint SpellBlueDragonTransform = 25107;
    public const uint SpellBronzeDragonTransform = 25108;
    public const uint SpellDragonHover = 18430;

    public const uint PointIdDragonAttack = 0;
    public const uint PointIdExit = 1;
    public const uint PointIdGate = 2;
    public const uint PointIdScepter = 3;
    public const uint PointIdScepter1 = 4;
    public const uint PointIdScepter2 = 5;
    public const uint PointIdEpilogue = 6;

    public const uint EmoteOneshotTalk = 1;
    public const uint EmoteOneshotRoar = 15;
    public const uint EmoteOneshotBeg = 20;
    public const uint EmoteOneshotShrug = 22;
    public const uint EmoteOneshotPoint = 25;
    public const uint EmoteStateHoldThrown = 385;

    public static readonly (float X, float Y, float Z, float O) SpawnPosition = (-8028.301f, 1538.316f, 3.66f, 0f);

    private static readonly (float X, float Y, float Z, float O, uint Entry)[] s_eventLocations =
    [
        (-8085f, 1528f, 2.61f, 3.141592f, NpcKaldoreiInfantry),
        (-8080f, 1526f, 2.61f, 3.141592f, NpcKaldoreiInfantry),
        (-8085f, 1524f, 2.61f, 3.141592f, NpcKaldoreiInfantry),
        (-8080f, 1522f, 2.61f, 3.141592f, NpcKaldoreiInfantry),
        (-8085f, 1520f, 2.61f, 3.141592f, NpcKaldoreiInfantry),

        (-8085f, 1524f, 2.61f, 3.141592f, NpcKaldoreiInfantry),
        (-8080f, 1522f, 2.61f, 3.141592f, NpcKaldoreiInfantry),
        (-8085f, 1520f, 2.61f, 3.141592f, NpcKaldoreiInfantry),
        (-8080f, 1518f, 2.61f, 3.141592f, NpcKaldoreiInfantry),
        (-8085f, 1516f, 2.61f, 3.141592f, NpcKaldoreiInfantry),

        (-8085f, 1518f, 2.61f, 3.141592f, NpcKaldoreiInfantry),
        (-8080f, 1516f, 2.61f, 3.141592f, NpcKaldoreiInfantry),
        (-8085f, 1514f, 2.61f, 3.141592f, NpcKaldoreiInfantry),
        (-8080f, 1512f, 2.61f, 3.141592f, NpcKaldoreiInfantry),
        (-8085f, 1510f, 2.61f, 3.141592f, NpcKaldoreiInfantry),

        (-8082f, 1528f, 2.61f, 3.141592f, NpcKaldoreiInfantry),
        (-8078f, 1525f, 2.61f, 3.141592f, NpcKaldoreiInfantry),
        (-8082f, 1524f, 2.61f, 3.141592f, NpcKaldoreiInfantry),
        (-8078f, 1526f, 2.61f, 3.141592f, NpcKaldoreiInfantry),
        (-8082f, 1527f, 2.61f, 3.141592f, NpcKaldoreiInfantry),

        (-8095f, 1520f, 2.61f, 0f, NpcAnubisathConqueror),
        (-8095f, 1530f, 2.61f, 0f, NpcAnubisathConqueror),
        (-8095f, 1510f, 2.61f, 0f, NpcAnubisathConqueror),

        (-8100f, 1513f, 2.61f, 0f, NpcQirajiWasp),
        (-8106f, 1523f, 2.61f, 0f, NpcQirajiWasp),
        (-8112f, 1518f, 2.61f, 0f, NpcQirajiWasp),
        (-8108f, 1530f, 2.61f, 0f, NpcQirajiWasp),
        (-8100f, 1518f, 2.61f, 0f, NpcQirajiWasp),
        (-8107f, 1522f, 2.61f, 0f, NpcQirajiWasp),

        (-8106f, 1511f, 2.61f, 0f, NpcQirajiTank),
        (-8103f, 1516f, 2.61f, 0f, NpcQirajiTank),
        (-8106f, 1526f, 2.61f, 0f, NpcQirajiTank),
        (-8109f, 1522f, 2.61f, 0f, NpcQirajiTank),
        (-8109f, 1516f, 2.61f, 0f, NpcQirajiTank),
        (-8106f, 1518f, 2.61f, 0f, NpcQirajiTank),

        (-8110f, 1512f, 2.61f, 0f, NpcQirajiDrone),
        (-8113f, 1517f, 2.61f, 0f, NpcQirajiDrone),
        (-8116f, 1523f, 2.61f, 0f, NpcQirajiDrone),
        (-8112f, 1529f, 2.61f, 0f, NpcQirajiDrone),
        (-8100f, 1527f, 2.61f, 0f, NpcQirajiDrone),
        (-8104f, 1522f, 2.61f, 0f, NpcQirajiDrone),
    ];

    private uint _eventTimerMs = 100;
    private uint _gatesResetTimerMs;
    private byte _eventStage;
    private bool _rootsOpened, _runesOpened, _barrierOpened;

    private ObjectGuid _fandralGuid, _merithraGuid, _caelestraszGuid, _arygosGuid;
    private ObjectGuid _triggerGuid, _playerGuid;
    private ObjectGuid _merithraWakeGuid, _aqBarrierGuid, _aqGateRunesGuid, _aqGateRootsGuid;

    private readonly List<ObjectGuid> _qirajiArmy = [];
    private readonly List<ObjectGuid> _kaldoreiArmy = [];
    private readonly List<(Creature Who, uint Point, float X, float Y)> _trackedMoves = [];

    public byte EventStage => _eventStage;
    public uint EventTimerMs => _eventTimerMs;
    public uint GatesResetTimerMs => _gatesResetTimerMs;
    public ObjectGuid EventPlayerGuid => _playerGuid;
    public ObjectGuid EventTriggerGuid => _triggerGuid;
    public ObjectGuid FandralGuid => _fandralGuid;
    public ObjectGuid MerithraGuid => _merithraGuid;
    public ObjectGuid CaelestraszGuid => _caelestraszGuid;
    public ObjectGuid ArygosGuid => _arygosGuid;
    public IReadOnlyList<ObjectGuid> QirajiArmy => _qirajiArmy;
    public IReadOnlyList<ObjectGuid> KaldoreiArmy => _kaldoreiArmy;

    /// <summary>Optional test/integration callback invoked at stage 50 when quest 8519 completes.</summary>
    public Action<Player, uint>? QuestCompletedCallback { get; set; }

    /// <summary>
    /// QuestAccept_go_crystalline_tear (silithus.cpp:2029-2046): accepting quest 8519 from Crystalline Tear (180633) locates
    /// the nearest Anachronos Quest Trigger (15454) within 100 yd (or summons one at the tear if none is placed), summons
    /// Anachronos the Ancient (15381) at (-8028.301, 1538.316, 3.66, 0) if no Anachronos is already active nearby, and hands
    /// him the trigger and player GUIDs.
    /// </summary>
    public static bool TryStartFromQuestAccept(Player player, ObjectGuid giverGuid)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.Map?.FindUpdater<CreatureMapSystem>() is not { } creatures)
        {
            return false;
        }

        WorldObject center = player.Map.FindObject(giverGuid) ?? player;
        if (creatures.CreaturesOfEntryInRange(center, Entry, 200f).Any(c => c.IsAlive))
        {
            return true;
        }

        Creature? trigger = creatures.CreaturesOfEntryInRange(center, NpcAnachronosQuestTrigger, 100f)
            .FirstOrDefault(c => c.IsAlive)
            ?? creatures.SummonInstanceCreature(NpcAnachronosQuestTrigger, center.X, center.Y, center.Z, center.Orientation);

        Creature? anachronos = trigger is not null
            ? creatures.SummonCorpseDespawn(trigger, Entry, SpawnPosition.X, SpawnPosition.Y, SpawnPosition.Z, SpawnPosition.O)
            : creatures.SummonInstanceCreature(Entry, SpawnPosition.X, SpawnPosition.Y, SpawnPosition.Z, SpawnPosition.O);

        if (anachronos?.AI is AnachronosTheAncientAI ai)
        {
            ai.SetSceneParticipants(player.Guid, trigger?.Guid ?? default);
            return true;
        }

        return false;
    }

    public void SetSceneParticipants(ObjectGuid playerGuid, ObjectGuid triggerGuid)
    {
        _playerGuid = playerGuid;
        _triggerGuid = triggerGuid;
    }

    public override void OnRespawn()
    {
        _eventTimerMs = 100;
        _gatesResetTimerMs = 0;
        _eventStage = 0;
        _rootsOpened = false;
        _runesOpened = false;
        _barrierOpened = false;
    }

    public override void OnDeath(Unit? killer)
    {
        AbortScene();
        base.OnDeath(killer);
    }

    private Creature? FindCreature(ObjectGuid guid) => guid.IsEmpty ? null : System?.FindCreature(guid);

    private GameObject? FindGameObject(ObjectGuid guid)
        => guid.IsEmpty ? null : Me.Map?.FindUpdater<GameObjectMapSystem>()?.Find(guid);

    private GameObject? FindClosestGameObject(WorldObject center, uint entry, float range)
    {
        if (Me.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects)
        {
            return null;
        }

        float bestSq = range * range;
        GameObject? best = null;
        foreach (GameObject go in objects.GameObjects)
        {
            if (go.Entry != entry || !go.IsSpawned)
            {
                continue;
            }

            float dx = go.X - center.X, dy = go.Y - center.Y, dz = go.Z - center.Z;
            float sq = (dx * dx) + (dy * dy) + (dz * dz);
            if (sq <= bestSq)
            {
                bestSq = sq;
                best = go;
            }
        }

        return best;
    }

    private void MovePointTracked(Creature who, uint pointId, float x, float y, float z, bool run)
    {
        _trackedMoves.RemoveAll(m => ReferenceEquals(m.Who, who));
        _trackedMoves.Add((who, pointId, x, y));
        who.Motion.MovePoint(pointId, x, y, z, run);
    }

    private static void EquipScepter(Creature fandral, CreatureMapSystem? system)
    {
        if (system?.AiServices.ItemTemplateOf?.Invoke(ItemHammerOfTheQiraji) is { } proto)
        {
            fandral.SetVirtualItem(0, proto);
        }
        else
        {
            fandral.SetVirtualItem(0, HammerDisplayId, itemClass: 2, subclass: 10, material: 2, inventoryType: 17, sheath: 2);
        }
    }

    private static void UnequipScepter(Creature fandral) => fandral.ClearVirtualItem(0);

    private void BeginScene()
    {
        if (System is not { } system)
        {
            return;
        }

        Creature trigger = FindCreature(_triggerGuid) ?? Me;
        if (FindClosestGameObject(trigger, GoAqBarrier, 60f) is { } barrier)
        {
            _aqBarrierGuid = barrier.Guid;
        }

        if (FindClosestGameObject(trigger, GoAqGateRunes, 60f) is { } runes)
        {
            _aqGateRunesGuid = runes.Guid;
        }

        if (FindClosestGameObject(trigger, GoAqGateRoots, 60f) is { } roots)
        {
            _aqGateRootsGuid = roots.Guid;
        }

        if (system.SummonAt(Me, NpcFandralStaghelm, -8029.66f, 1536.68f, 3.261f, 3.19395f, null, 600_000, oocOrCorpse: false) is { } fandral)
        {
            _fandralGuid = fandral.Guid;
        }

        if (system.SummonAt(Me, NpcMerithraOfTheDream, -8034.432f, 1535.42f, 2.665f, 0.26179f, null, 600_000, oocOrCorpse: false) is { } merithra)
        {
            _merithraGuid = merithra.Guid;
        }

        if (system.SummonAt(Me, NpcCaelestrasz, -8032.167f, 1533.148f, 2.866f, 1.09956f, null, 600_000, oocOrCorpse: false) is { } caelestrasz)
        {
            _caelestraszGuid = caelestrasz.Guid;
        }

        if (system.SummonAt(Me, NpcArygos, -8034.62f, 1537.843f, 2.628f, 5.95157f, null, 600_000, oocOrCorpse: false) is { } arygos)
        {
            _arygosGuid = arygos.Guid;
        }
    }

    private void AbortScene()
    {
        if (System is not { } system)
        {
            return;
        }

        _eventStage = 0;
        _eventTimerMs = 0;

        if (FindCreature(_fandralGuid) is { } fandral)
        {
            system.ForcedDespawn(fandral, 0);
        }

        if (FindCreature(_merithraGuid) is { } merithra)
        {
            system.ForcedDespawn(merithra, 0);
        }

        if (FindCreature(_caelestraszGuid) is { } caelestrasz)
        {
            system.ForcedDespawn(caelestrasz, 0);
        }

        if (FindCreature(_arygosGuid) is { } arygos)
        {
            system.ForcedDespawn(arygos, 0);
        }

        foreach (ObjectGuid guid in _kaldoreiArmy)
        {
            if (FindCreature(guid) is { } kaldorei)
            {
                system.ForcedDespawn(kaldorei, 0);
            }
        }

        _kaldoreiArmy.Clear();

        foreach (ObjectGuid guid in _qirajiArmy)
        {
            if (FindCreature(guid) is { } qiraji)
            {
                system.ForcedDespawn(qiraji, 0);
            }
        }

        _qirajiArmy.Clear();
    }

    private void RecallDragons()
    {
        if (System is not { } system)
        {
            return;
        }

        if (FindCreature(_fandralGuid) is { } fandral)
        {
            system.SetFacingTo(fandral, 2.885f);
        }

        Me.UnitFlags |= UnitFlags.Spawning;
        system.SetFacingTo(Me, 2.758f);

        if (FindCreature(_merithraGuid) is { } merithra)
        {
            system.ForcedDespawn(merithra, 0);
        }

        if (FindCreature(_caelestraszGuid) is { } caelestrasz)
        {
            system.ForcedDespawn(caelestrasz, 0);
        }

        if (FindCreature(_arygosGuid) is { } arygos)
        {
            system.ForcedDespawn(arygos, 0);
        }
    }

    private void SetupTimerStop()
    {
        if (System is not { } system)
        {
            return;
        }

        int index = 0;
        foreach (ObjectGuid kaldoreiGuid in _kaldoreiArmy)
        {
            if (FindCreature(kaldoreiGuid) is not { } kaldorei)
            {
                continue;
            }

            kaldorei.UnitFlags |= UnitFlags.Spawning;
            system.StopCombatInPlace(kaldorei);
            if (index < _qirajiArmy.Count && FindCreature(_qirajiArmy[index]) is { } qiraji)
            {
                system.SetFacingTo(kaldorei, MathF.Atan2(qiraji.Y - kaldorei.Y, qiraji.X - kaldorei.X));
            }

            index++;
        }

        index = 0;
        foreach (ObjectGuid qirajiGuid in _qirajiArmy)
        {
            if (FindCreature(qirajiGuid) is not { } qiraji)
            {
                continue;
            }

            qiraji.UnitFlags |= UnitFlags.Spawning;
            system.StopCombatInPlace(qiraji);
            if (index < _kaldoreiArmy.Count && FindCreature(_kaldoreiArmy[index]) is { } kaldorei)
            {
                system.SetFacingTo(qiraji, MathF.Atan2(kaldorei.Y - qiraji.Y, kaldorei.X - qiraji.X));
            }

            index++;
        }
    }

    private void DoSummonWarriors()
    {
        if (System is not { } system)
        {
            return;
        }

        for (int i = 0; i < 20; i++)
        {
            var loc = s_eventLocations[i];
            if (system.SummonAt(Me, loc.Entry, loc.X, loc.Y, loc.Z, loc.O, null, 300_000, oocOrCorpse: false) is { } kaldorei)
            {
                _kaldoreiArmy.Add(kaldorei.Guid);
            }
        }

        for (int i = 20; i < 41; i++)
        {
            var loc = s_eventLocations[i];
            if (system.SummonAt(Me, loc.Entry, loc.X, loc.Y, loc.Z, loc.O, null, 300_000, oocOrCorpse: false) is { } qiraji)
            {
                _qirajiArmy.Add(qiraji.Guid);
            }
        }
    }

    private void EngageQirajiAndKaldorei()
    {
        if (System is not { } system)
        {
            return;
        }

        foreach (ObjectGuid guid in _kaldoreiArmy)
        {
            if (FindCreature(guid) is { } kaldorei)
            {
                kaldorei.SetFactionTemporary(1608, TemporaryFactionFlags.RestoreRespawn);
            }
        }

        Creature? anubisath1 = _qirajiArmy.Count > 0 ? FindCreature(_qirajiArmy[0]) : null;
        Creature? anubisath2 = _qirajiArmy.Count > 1 ? FindCreature(_qirajiArmy[1]) : null;
        Creature? anubisath3 = _qirajiArmy.Count > 2 ? FindCreature(_qirajiArmy[2]) : null;
        if (anubisath1 is not null)
        {
            anubisath1.SetFactionTemporary(14, TemporaryFactionFlags.RestoreRespawn);
        }

        if (anubisath2 is not null)
        {
            anubisath2.SetFactionTemporary(14, TemporaryFactionFlags.RestoreRespawn);
        }

        if (anubisath3 is not null)
        {
            anubisath3.SetFactionTemporary(14, TemporaryFactionFlags.RestoreRespawn);
        }

        int count = 0;
        foreach (ObjectGuid guid in _kaldoreiArmy)
        {
            if (FindCreature(guid) is { } kaldorei)
            {
                kaldorei.UnitFlags |= UnitFlags.RemoveClientControl;
                Creature? target = count < 7 ? anubisath1 : count < 14 ? anubisath2 : anubisath3;
                if (target is not null)
                {
                    system.AttackStart(kaldorei, target);
                    system.AttackStart(target, kaldorei);
                }
            }

            count++;
        }

        int qirajiIndex = 3;
        int kaldoreiIndex = 0;
        while (qirajiIndex < _qirajiArmy.Count && kaldoreiIndex < _kaldoreiArmy.Count)
        {
            if (FindCreature(_qirajiArmy[qirajiIndex]) is { } qiraji && FindCreature(_kaldoreiArmy[kaldoreiIndex]) is { } kaldorei)
            {
                qiraji.UnitFlags |= UnitFlags.RemoveClientControl;
                system.AttackStart(qiraji, kaldorei);
                system.AttackStart(kaldorei, qiraji);
            }

            qirajiIndex++;
            kaldoreiIndex++;
        }
    }

    public override void OnJustSummoned(Creature summoned)
    {
        switch (summoned.Entry)
        {
            case NpcFandralStaghelm:
                System?.SetFacingTo(summoned, MathF.Atan2(Me.Y - summoned.Y, Me.X - summoned.X));
                goto case NpcMerithraOfTheDream;
            case NpcMerithraOfTheDream:
            case NpcCaelestrasz:
            case NpcArygos:
                summoned.UnitFlags |= UnitFlags.ImmuneToNpc;
                break;
            case NpcKaldoreiInfantry:
                summoned.UnitFlags |= UnitFlags.ImmuneToPlayer;
                break;
            case NpcQirajiWasp:
            case NpcQirajiDrone:
            case NpcQirajiTank:
            case NpcAnubisathConqueror:
                summoned.UnitFlags |= UnitFlags.ImmuneToPlayer | UnitFlags.ImmuneToNpc;
                break;
        }
    }

    public override void OnMovementInform(MovementGeneratorType type, uint pointId)
    {
        if (type != MovementGeneratorType.Point || System is not { } system)
        {
            return;
        }

        _trackedMoves.RemoveAll(m => ReferenceEquals(m.Who, Me) && m.Point == pointId);
        switch (pointId)
        {
            case PointIdGate:
                system.SetFacingTo(Me, 3.01f);
                _eventStage = 27;
                _eventTimerMs = 2000;
                break;
            case PointIdScepter1:
                // Pick up the pieces
                system.SayText(Me, EmoteAnachronosScepter);
                Me.StandState = StandState.Kneel;
                _eventTimerMs = 2000;
                _eventStage = 47;
                break;
            case PointIdScepter2:
                // Pick up the second piece
                system.SayText(Me, SayAnachronosEpilogue8);
                Me.StandState = StandState.Kneel;
                _eventTimerMs = 4000;
                _eventStage = 50;
                break;
            case PointIdExit:
                if (_eventStage <= 55)
                {
                    system.CastSpell(Me, SpellDragonHover, Me, true);
                    _eventTimerMs = 1000;
                    _eventStage = 55;
                }
                break;
        }
    }

    public void OnSummonedMovementInform(Creature summoned, MovementGeneratorType motionType, uint pointId)
    {
        ArgumentNullException.ThrowIfNull(summoned);
        if (motionType != MovementGeneratorType.Point || System is not { } system)
        {
            return;
        }

        _trackedMoves.RemoveAll(m => ReferenceEquals(m.Who, summoned) && m.Point == pointId);
        if (summoned.Entry == NpcFandralStaghelm)
        {
            switch (pointId)
            {
                case PointIdEpilogue:
                    system.SetFacingTo(summoned, MathF.Atan2(Me.Y - summoned.Y, Me.X - summoned.X));
                    if (_eventStage >= 42)
                    {
                        UnequipScepter(summoned);
                        MovePointTracked(Me, PointIdScepter1, -8061.93f, 1536.5f, 2.61f, run: false);
                        _eventStage = 46;
                        _eventTimerMs = 0;
                    }
                    break;
                case PointIdScepter:
                    EquipScepter(summoned, system);
                    MovePointTracked(summoned, PointIdEpilogue, -8048.1f, 1537.2f, 2.61f, run: false);
                    break;
                case PointIdExit:
                    system.ForcedDespawn(summoned, 0);
                    break;
            }
        }
        else if (pointId == PointIdDragonAttack)
        {
            switch (summoned.Entry)
            {
                case NpcMerithraOfTheDream:
                    system.CastSpell(summoned, SpellGreenDragonTransform, summoned, false);
                    _eventTimerMs = 2000;
                    _eventStage = 10;
                    break;
                case NpcCaelestrasz:
                    system.CastSpell(summoned, SpellRedDragonTransform, summoned, false);
                    _eventStage = 21;
                    _eventTimerMs = 3000;
                    break;
                case NpcArygos:
                    system.CastSpell(summoned, SpellBlueDragonTransform, summoned, false);
                    _eventStage = 15;
                    _eventTimerMs = 3000;
                    break;
            }
        }
    }

    private static void SealGateObject(GameObject? go, GameObjectMapSystem? objects)
    {
        if (go is null)
        {
            return;
        }

        if (go.LootState == GameObjectLootState.Activated)
        {
            objects?.ToggleDoorOrButton(go);
        }

        go.State = GameObjectState.Ready;
        go.Flags &= ~GameObjectFlags.InUse;
        go.LootState = GameObjectLootState.Ready;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (System is not { } system)
        {
            return;
        }

        foreach ((Creature who, uint point, float x, float y) in _trackedMoves.ToArray())
        {
            if (!ReferenceEquals(who, Me) && (((who.X - x) * (who.X - x)) + ((who.Y - y) * (who.Y - y)) <= 1.0f || !who.Motion.HasActive(MovementGeneratorType.Point)))
            {
                _trackedMoves.RemoveAll(m => ReferenceEquals(m.Who, who) && m.Point == point);
                OnSummonedMovementInform(who, MovementGeneratorType.Point, point);
            }
        }

        if (_eventTimerMs != 0)
        {
            if (_eventTimerMs <= diffMs)
            {
                _eventTimerMs = 0;
                ExecuteStage(system);
                _eventStage++;
            }
            else
            {
                _eventTimerMs -= diffMs;
            }
        }

        if (_gatesResetTimerMs != 0)
        {
            if (_gatesResetTimerMs <= diffMs)
            {
                _gatesResetTimerMs = 0;
                if (_rootsOpened)
                {
                    if (FindGameObject(_aqGateRootsGuid) is { } roots)
                    {
                        roots.State = GameObjectState.Active;
                        _rootsOpened = false;
                        _gatesResetTimerMs = 1500;
                        return;
                    }
                }

                if (_runesOpened)
                {
                    if (FindGameObject(_aqGateRunesGuid) is { } runes)
                    {
                        runes.State = GameObjectState.Active;
                        _runesOpened = false;
                        _gatesResetTimerMs = 1500;
                        return;
                    }
                }

                if (_barrierOpened)
                {
                    if (FindGameObject(_aqBarrierGuid) is { } barrier)
                    {
                        barrier.State = GameObjectState.Active;
                        _barrierOpened = false;
                    }
                }
            }
            else
            {
                _gatesResetTimerMs -= diffMs;
            }
        }
    }

    private void ExecuteStage(CreatureMapSystem system)
    {
        GameObjectMapSystem? objects = Me.Map?.FindUpdater<GameObjectMapSystem>();
        switch (_eventStage)
        {
            case 0:
                BeginScene();
                DoSummonWarriors();
                system.SayText(Me, SayAnachronosIntro1);
                _eventTimerMs = 2000;
                break;
            case 1:
                EngageQirajiAndKaldorei();
                _eventTimerMs = 2000;
                break;
            case 2:
                if (FindCreature(_fandralGuid) is { } fandral2)
                {
                    system.SayText(fandral2, SayFandralIntro2, Me);
                }

                _eventTimerMs = 6000;
                break;
            case 3:
                if (FindCreature(_merithraGuid) is { } merithra3)
                {
                    if (FindCreature(_fandralGuid) is { } fandral3)
                    {
                        system.SetFacingTo(fandral3, MathF.Atan2(merithra3.Y - fandral3.Y, merithra3.X - fandral3.X));
                    }

                    system.SayText(merithra3, EmoteMerithraBow);
                }

                _eventTimerMs = 2000;
                break;
            case 4:
                if (FindCreature(_merithraGuid) is { } merithra4)
                {
                    system.SayText(merithra4, SayMerithraIntro3);
                }

                _eventTimerMs = 3000;
                break;
            case 5:
                if (FindCreature(_arygosGuid) is { } arygos5)
                {
                    if (FindCreature(_fandralGuid) is { } fandral5)
                    {
                        system.SetFacingTo(fandral5, MathF.Atan2(arygos5.Y - fandral5.Y, arygos5.X - fandral5.X));
                    }

                    system.SayText(arygos5, EmoteArygosNod);
                }

                _eventTimerMs = 4000;
                break;
            case 6:
                if (FindCreature(_caelestraszGuid) is { } caelestrasz6)
                {
                    if (FindCreature(_fandralGuid) is { } fandral6)
                    {
                        system.SetFacingTo(fandral6, MathF.Atan2(caelestrasz6.Y - fandral6.Y, caelestrasz6.X - fandral6.X));
                    }

                    system.SayText(caelestrasz6, SayCaelestraszIntro4);
                }

                _eventTimerMs = 8000;
                break;
            case 7:
                if (FindCreature(_merithraGuid) is { } merithra7)
                {
                    if (FindCreature(_fandralGuid) is { } fandral7)
                    {
                        system.SetFacingTo(fandral7, MathF.Atan2(merithra7.Y - fandral7.Y, merithra7.X - fandral7.X));
                    }

                    system.SayText(merithra7, SayMerithraIntro5);
                }

                _eventTimerMs = 5000;
                break;
            case 8:
                if (FindCreature(_merithraGuid) is { } merithra8)
                {
                    MovePointTracked(merithra8, PointIdDragonAttack, -8065f, 1530f, 2.61f, run: false);
                }

                break;
            case 9:
                break;
            case 10:
                if (FindCreature(_merithraGuid) is { } merithra10)
                {
                    system.SayText(merithra10, SayMerithraAttack1);
                }

                _eventTimerMs = 2000;
                break;
            case 11:
                if (FindCreature(_merithraGuid) is { } merithra11)
                {
                    system.PlayEmote(merithra11, EmoteOneshotRoar);
                }

                _eventTimerMs = 2000;
                break;
            case 12:
                if (FindCreature(_arygosGuid) is { } arygos12)
                {
                    system.SayText(arygos12, SayArygosAttack2);
                }

                _eventTimerMs = 4000;
                break;
            case 13:
                if (FindCreature(_arygosGuid) is { } arygos13)
                {
                    MovePointTracked(arygos13, PointIdDragonAttack, -8065f, 1530f, 2.61f, run: false);
                }

                if (FindCreature(_merithraGuid) is { } merithra13)
                {
                    system.CastSpell(merithra13, SpellNoxiousBreath, merithra13, false);
                    system.CastSpell(merithra13, SpellDragonHover, merithra13, true);
                }

                break;
            case 14:
                break;
            case 15:
                if (FindCreature(_arygosGuid) is { } arygos15)
                {
                    system.SayText(arygos15, SayArygosAttack3);
                }

                if (FindCreature(_merithraGuid) is { } merithra15)
                {
                    MovePointTracked(merithra15, PointIdExit, -8034.535f, 1535.14f, 2.61f, run: true);
                }

                _eventTimerMs = 4000;
                break;
            case 16:
                if (FindCreature(_merithraGuid) is { } merithra16)
                {
                    MovePointTracked(merithra16, PointIdExit, -8100f, 1530f, 50f, run: true);
                }

                _eventTimerMs = 1000;
                break;
            case 17:
                if (FindCreature(_arygosGuid) is { } arygos17)
                {
                    system.CastSpell(arygos17, SpellFrostBreath, arygos17, false);
                    system.CastSpell(arygos17, SpellDragonHover, arygos17, true);
                }

                _eventTimerMs = 3000;
                break;
            case 18:
                if (FindCreature(_caelestraszGuid) is { } caelestrasz18)
                {
                    system.SayText(caelestrasz18, SayCaelestraszAttack4);
                }

                _eventTimerMs = 3000;
                break;
            case 19:
                if (FindCreature(_arygosGuid) is { } arygos19)
                {
                    MovePointTracked(arygos19, PointIdExit, -8065f, 1530f, 7.61f, run: true);
                }

                if (FindCreature(_caelestraszGuid) is { } caelestrasz19)
                {
                    MovePointTracked(caelestrasz19, PointIdDragonAttack, -8065f, 1530f, 2.61f, run: false);
                }

                break;
            case 20:
                break;
            case 21:
                if (FindCreature(_arygosGuid) is { } arygos21)
                {
                    MovePointTracked(arygos21, PointIdExit, -8100f, 1530f, 50f, run: true);
                }

                if (FindCreature(_caelestraszGuid) is { } caelestrasz21)
                {
                    system.SayText(caelestrasz21, SayCaelestraszAttack5);
                }

                _eventTimerMs = 3000;
                break;
            case 22:
                if (objects?.Summon(GoMerithraWake, -8034.535f, 1534.14f, 2.61f, 0f, 70) is { } wake)
                {
                    _merithraWakeGuid = wake.Guid;
                }

                if (FindCreature(_caelestraszGuid) is { } caelestrasz22)
                {
                    system.CastSpell(caelestrasz22, SpellFlameBreath, caelestrasz22, false);
                    system.CastSpell(caelestrasz22, SpellDragonHover, caelestrasz22, true);
                }

                _eventTimerMs = 3000;
                break;
            case 23:
                if (FindGameObject(_merithraWakeGuid) is { } wakeGo)
                {
                    objects?.ToggleDoorOrButton(wakeGo, 60);
                    wakeGo.State = GameObjectState.Active;
                }

                system.SayText(Me, SayAnachronosSeal1);
                if (FindCreature(_fandralGuid) is { } fandral23)
                {
                    system.SetFacingTo(fandral23, MathF.Atan2(Me.Y - fandral23.Y, Me.X - fandral23.X));
                    system.SetFacingTo(Me, MathF.Atan2(fandral23.Y - Me.Y, fandral23.X - Me.X));
                }

                if (FindCreature(_caelestraszGuid) is { } caelestrasz23)
                {
                    MovePointTracked(caelestrasz23, PointIdExit, -8065f, 1530f, 7.61f, run: true);
                }

                _eventTimerMs = 2000;
                break;
            case 24:
                if (FindCreature(_caelestraszGuid) is { } caelestrasz24)
                {
                    MovePointTracked(caelestrasz24, PointIdExit, -8100f, 1530f, 50f, run: true);
                }

                if (FindCreature(_fandralGuid) is { } fandral24)
                {
                    system.SayText(fandral24, SayFandralSeal2);
                }

                _eventTimerMs = 3000;
                break;
            case 25:
                MovePointTracked(Me, PointIdGate, -8042.3f, 1538.4f, 2.61f, run: false);
                break;
            case 26:
                break;
            case 27:
                DoCast(Me, SpellTimeStop);
                SealGateObject(FindGameObject(_aqBarrierGuid), objects);
                _barrierOpened = true;
                SetupTimerStop();
                _eventTimerMs = 7000;
                break;
            case 28:
                DoCast(Me, SpellPrismaticBarrier);
                _eventTimerMs = 1000;
                break;
            case 29:
                SealGateObject(FindGameObject(_aqGateRunesGuid), objects);
                _runesOpened = true;
                _eventTimerMs = 4000;
                break;
            case 30:
                DoCast(Me, SpellGlyphOfWarding);
                _eventTimerMs = 4000;
                break;
            case 31:
                RecallDragons();
                system.SayText(Me, SayAnachronosSeal3);
                _eventTimerMs = 3000;
                break;
            case 32:
                if (FindCreature(_fandralGuid) is { } fandral32)
                {
                    MovePointTracked(fandral32, PointIdEpilogue, -8044.4f, 1537f, 2.61f, run: false);
                    system.CastSpell(fandral32, SpellCallAncients, fandral32, false);
                    SealGateObject(FindGameObject(_aqGateRootsGuid), objects);
                    _rootsOpened = true;
                    _gatesResetTimerMs = 10_000;
                    system.SayText(fandral32, SayFandralSeal6);
                }

                _eventTimerMs = 9000;
                break;
            case 33:
                if (FindCreature(_fandralGuid) is { } fandral33)
                {
                    system.SayText(fandral33, EmoteFandralExhausted);
                    fandral33.StandState = StandState.Kneel;
                    system.SetFacingTo(Me, MathF.Atan2(fandral33.Y - Me.Y, fandral33.X - Me.X));
                }

                _eventTimerMs = 1000;
                break;
            case 34:
                system.SayText(Me, SayAnachronosEpilogue1);
                _eventTimerMs = 6000;
                break;
            case 35:
                system.SayText(Me, SayAnachronosEpilogue2);
                _eventTimerMs = 5000;
                break;
            case 36:
                if (FindCreature(_fandralGuid) is { } fandral36)
                {
                    EquipScepter(fandral36, system);
                }

                system.SayText(Me, SayAnachronosEpilogue3);
                _eventTimerMs = 3000;
                break;
            case 37:
                if (FindCreature(_fandralGuid) is { } fandral37)
                {
                    fandral37.SetUInt32(UpdateFields.UnitNpcEmotestate, EmoteStateHoldThrown);
                }

                _eventTimerMs = 1000;
                break;
            case 38:
                if (FindCreature(_fandralGuid) is { } fandral38)
                {
                    fandral38.StandState = StandState.Stand;
                }

                _eventTimerMs = 1000;
                break;
            case 39:
                if (FindCreature(_fandralGuid) is { } fandral39)
                {
                    fandral39.SetUInt32(UpdateFields.UnitNpcEmotestate, 0);
                    system.SayText(fandral39, SayFandralEpilogue4);
                }

                _eventTimerMs = 3000;
                break;
            case 40:
                if (FindCreature(_fandralGuid) is { } fandral40)
                {
                    system.SetFacingTo(fandral40, MathF.Atan2(Me.Y - fandral40.Y, Me.X - fandral40.X));
                    system.SayText(fandral40, SayFandralEpilogue5);
                }

                _eventTimerMs = 9000;
                break;
            case 41:
                if (FindCreature(_fandralGuid) is { } fandral41)
                {
                    system.SayText(fandral41, EmoteFandralShatter);
                    system.CastSpell(fandral41, SpellShatterHammer, fandral41, false);
                }

                _eventTimerMs = 1000;
                break;
            case 42:
                if (FindCreature(_fandralGuid) is { } fandral42)
                {
                    MovePointTracked(fandral42, PointIdScepter, -8067.804f, 1536.383f, 2.61f, run: false);
                    system.CastSpell(fandral42, SpellThrowHammer, fandral42, false);
                }

                system.SayText(Me, SayAnachronosEpilogue6);
                _eventTimerMs = 2000;
                break;
            case 43:
                if (FindCreature(_fandralGuid) is { } fandral43)
                {
                    UnequipScepter(fandral43);
                }

                foreach (ObjectGuid guid in _kaldoreiArmy)
                {
                    if (FindCreature(guid) is { } kaldorei)
                    {
                        system.ForcedDespawn(kaldorei, 0);
                    }
                }

                _kaldoreiArmy.Clear();
                _eventTimerMs = 2000;
                break;
            case 44:
                foreach (ObjectGuid guid in _qirajiArmy)
                {
                    if (FindCreature(guid) is { } qiraji)
                    {
                        system.ForcedDespawn(qiraji, 0);
                    }
                }

                _qirajiArmy.Clear();
                break;
            case 45:
                break;
            case 46:
                break;
            case 47:
                system.SayText(Me, EmoteAnachronosDisappointed);
                Me.StandState = StandState.Stand;
                _eventTimerMs = 1000;
                break;
            case 48:
                MovePointTracked(Me, PointIdScepter2, -8059.82f, 1533.8f, 2.61f, run: false);
                break;
            case 49:
                break;
            case 50:
                if (Me.Map?.FindPlayer(_playerGuid) is { } player)
                {
                    system.RewardGroupEventExplored(player, QuestAPawnOnTheEternalBoard, Me);
                    system.QuestEventHappened(player, QuestAPawnOnTheEternalBoard);
                    QuestCompletedCallback?.Invoke(player, QuestAPawnOnTheEternalBoard);
                }

                _eventTimerMs = 4000;
                break;
            case 51:
                Me.StandState = StandState.Stand;
                if (FindCreature(_fandralGuid) is { } fandral51)
                {
                    system.SayText(fandral51, SayFandralEpilogue7);
                    system.SetFacingTo(Me, MathF.Atan2(fandral51.Y - Me.Y, fandral51.X - Me.X));
                }

                _eventTimerMs = 8000;
                break;
            case 52:
                if (FindCreature(_fandralGuid) is { } fandral52)
                {
                    system.SayText(fandral52, EmoteFandralHateful);
                    MovePointTracked(fandral52, PointIdExit, -8098.75f, 1515.1f, 2.6f, run: false);
                }

                _eventTimerMs = 4000;
                break;
            case 53:
                DoCast(Me, SpellBronzeDragonTransform);
                _eventTimerMs = 4000;
                break;
            case 54:
                MovePointTracked(Me, PointIdExit, -8041.5f, 1492.7f, 2.61f, run: true);
                break;
            case 55:
                MovePointTracked(Me, PointIdExit, -8008f, 1446f, 44f, run: true);
                system.ForcedDespawn(Me, 5000);
                break;
        }
    }
}
