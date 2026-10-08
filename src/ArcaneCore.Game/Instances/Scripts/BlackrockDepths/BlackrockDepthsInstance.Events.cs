using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// mangos-classic AI/ScriptDevAI/scripts/eastern_kingdoms/blackrock_depths/
/// instance_blackrock_depths.cpp (OnCreatureCreate, OnObjectCreate, SetData, OnCreatureEnterCombat,
/// OnCreatureEvade, OnCreatureDeath, DoCallNextDwarf, HandleBarPatrons, HandleBarPatrol, Update).
/// </summary>
public sealed partial class BlackrockDepthsInstance
{
    public const uint NpcEmperor = 9019;
    public const uint NpcPrincess = 8929;
    public const uint NpcAmbassadorFlamelash = 9156;
    public const uint NpcMagmus = 9938;
    public const uint NpcPlugger = 9499;
    public const uint NpcPhalanx = 9502;
    public const uint NpcRocknot = 9503;
    public const uint NpcNagmara = 9500;
    public const uint NpcRibbly = 9543;
    public const uint NpcFireguardDestroyer = 8911;
    public const uint NpcAnvilrageOfficer = 8895;
    public const uint GoBarDoor = 170571;
    public const uint GoArenaSpoils = 181074;
    public const uint GoFirstRune = 170578;
    public const uint GoGolemRoomNorth = 170573;
    public const uint GoGolemRoomSouth = 170574;
    public const uint GoThroneRoom = 170575;
    public const uint GoSecretDoor = 174553;
    public const uint GoSecretSafe = 161495;

    private static readonly uint[] ArenaMobs =
        [16049, 16050, 16051, 16052, 16053, 16054, 16055, 16058, 16059,
         8925, 8926, 8927, 8928, 8933, 8932, 9027, 9028, 9029, 9030, 9031, 9032];
    private static readonly uint[] ArenaCrowd = [8916, 8896, 8902, 8904, 8893, 8894, 8895];
    private static readonly uint[] BarPatrons = [9545, 9547, 9554];

    /// <summary>Thunderbrew Lager Keg (go_bar_beer_keg; three spawns in classic-db z2815).</summary>
    public const uint GoBeerKeg = 164911;

    /// <summary>
    /// Every gameobject_template with ScriptName go_relic_coffer_door in classic-db z2815: twelve entries, each spawned once in map 230
    /// (there is no 174565). MAX_RELIC_DOORS (12) counts uses of these, so the vault needs all of them.
    /// </summary>
    public static readonly uint[] RelicCofferDoors =
        [174554, 174555, 174556, 174557, 174558, 174559, 174560, 174561, 174562, 174563, 174564, 174566];

    /// <summary>Both go_shadowforge_brazier entries of classic-db z2815: the first lit sets TYPE_LYCEUM IN_PROGRESS, the second DONE.</summary>
    public static readonly uint[] ShadowforgeBraziers = [174744, 174745];
    // blackrock_depths.h aPatronsEmotes: EMOTE_ONESHOT_EXCLAMATION (5), CHEER (4) twice, LAUGH (11) three times.
    private static readonly uint[] PatronEmotes = [5, 4, 4, 11, 11, 11];
    private readonly HashSet<ObjectGuid> _arenaCrowd = [];
    private readonly HashSet<ObjectGuid> _barPatrons = [];
    private readonly HashSet<ObjectGuid> _barPatrol = [];
    private readonly HashSet<ObjectGuid> _vaultCreatures = [];
    // Tomb dwarves made hostile by DoCallNextDwarf (TEMPFACTION_RESTORE_RESPAWN | TEMPFACTION_RESTORE_REACH_HOME).
    private readonly HashSet<ObjectGuid> _tombHostile = [];
    private int _stolenAles;
    private int _barAleCount;
    private int _cofferDoorsOpened;
    private int _brokenKegs;
    private uint _patronEmoteMs = 2000;
    private uint _patrolMs;
    private uint _dagranYellMs;
    private uint _dwarfFightMs;
    private int _dwarfRound;
    private bool _barDoorOpen;
    private bool _entryAisRegistered;
    private bool _mugAiRegistered;
    private float _arenaCenterX, _arenaCenterY, _arenaCenterZ;

    public int StolenAles => _stolenAles;
    public int BarAleCount => _barAleCount;
    public int CofferDoorsOpened => _cofferDoorsOpened;
    public int BrokenKegs => _brokenKegs;
    public bool BarDoorOpen => _barDoorOpen;
    internal GameObject? RuneAt(int index) => GetSingleGameObjectFromStorage(GoFirstRune + (uint)index);
    internal Creature? Princess => GetSingleCreatureFromStorage(NpcPrincess);
    internal Creature? Emperor => GetSingleCreatureFromStorage(NpcEmperor);

    public override void Initialize()
    {
        base.Initialize();
        _stolenAles = _barAleCount = _dwarfRound = _cofferDoorsOpened = _brokenKegs = 0;
        _patronEmoteMs = 2000;
        _patrolMs = _dagranYellMs = _dwarfFightMs = 0;
        _barDoorOpen = false;
        _arenaCrowd.Clear();
        _barPatrons.Clear();
        _barPatrol.Clear();
        _vaultCreatures.Clear();
        _tombHostile.Clear();
    }

    private void RecordDepthsCreature(Creature creature)
    {
        uint entry = creature.Template.Entry;
        if (entry == NpcPrincess && CanReplacePrincess()
            && Instance.FindUpdater<CreatureMapSystem>()?.UpdateEntry(creature, 10076) == true)
            return; // the priestess is no longer the princess EmperorDagranAI tries to rescue
        if (entry is NpcEmperor or NpcPrincess or NpcMagmus or NpcPlugger or NpcPhalanx or NpcRocknot or NpcNagmara or NpcRibbly)
            StoreCreature(creature);
        if (Array.IndexOf(ArenaCrowd, entry) >= 0 && InArenaCrowdVolume(creature))
        {
            _arenaCrowd.Add(creature.Guid);
            if (GetData(TypeRingOfLaw) == EncounterState.Done) creature.FactionTemplate = 15;
        }
        if (Array.IndexOf(BarPatrons, entry) >= 0)
        {
            _barPatrons.Add(creature.Guid);
            if (Encounters[11] == EncounterState.Done) MakePatronHostile(creature);
        }
        if (entry is NpcRocknot or NpcNagmara && Encounters[11] == EncounterState.Done)
            creature.System?.ForcedDespawn(creature, 0);
        if (entry == 9476 || entry == 8905 && Math.Abs(creature.Z + 50.134f) <= 1f
            && (creature.X - 821.905f) * (creature.X - 821.905f)
                + (creature.Y + 338.382f) * (creature.Y + 338.382f) <= 400f)
        {
            _vaultCreatures.Add(creature.Guid);
            if (entry == 8905) creature.System?.CastSpell(creature, 10255, creature, triggered: true);
        }

        if (!_entryAisRegistered && Instance.FindUpdater<CreatureMapSystem>() is { } creatures)
        {
            _entryAisRegistered = true;
            creatures.RegisterEntryAi(NpcAmbassadorFlamelash, c => new AmbassadorFlamelashAI(c, this));
            creatures.RegisterEntryAi(9033, c => new GeneralAngerforgeAI(c));
            creatures.RegisterEntryAi(9018, c => new HighInterrogatorGerstahnAI(c));
            creatures.RegisterEntryAi(NpcEmperor, c => new EmperorDagranAI(c, this));
            creatures.RegisterEntryAi(NpcPrincess, c => new MoiraBronzebeardAI(c, this));
            creatures.RegisterEntryAi(NpcPlugger, c => new PluggerAI(c));
            creatures.RegisterEntryAi(NpcPhalanx, c => new PhalanxAI(c, this));
            creatures.RegisterEntryAi(10096, c => new GrimstoneAI(c, this));
        }
    }

    /// <summary>instance_blackrock_depths.cpp CanReplacePrincess: every present player has turned in their faction's rescue quest.</summary>
    private bool CanReplacePrincess()
    {
        Player[] players = [.. Instance.Players];
        IInstanceQuestRewards? quests = Instance.FindUpdater<IInstanceQuestRewards>();
        return players.Length > 0 && quests is not null
            && players.All(p => quests.IsRewarded(p, p.Team == Team.Alliance ? 4362u : 4003u) == true);
    }

    private static bool InArenaCrowdVolume(Creature creature)
    {
        float dx = creature.X - 595.78f, dy = creature.Y + 188.65f;
        return creature.Z is >= -38.63f and <= -28.63f && dx * dx + dy * dy <= 69f * 69f;
    }

    private void RecordDepthsObject(GameObject go)
    {
        if (go.Entry is 165738 or 165739 && !_mugAiRegistered && Instance.FindUpdater<GameObjectMapSystem>() is { } mugObjects)
        {
            _mugAiRegistered = true;
            mugObjects.RegisterAi(165738, new BarMugAI(this));
            mugObjects.RegisterAi(165739, new BarMugAI(this));
        }
        if (go.Entry is GoBarDoor or GoArenaSpoils or GoGolemRoomNorth or GoGolemRoomSouth or GoThroneRoom
            or GoSecretDoor or GoSecretSafe or 170607
            || go.Entry is >= 161522 and <= 161525 || go.Entry is >= GoFirstRune and <= GoFirstRune + 6)
            StoreGameObject(go);
        if (go.Entry == GoBarDoor)
        {
            if (GetData(TypeRocknot) == EncounterState.Done)
            {
                go.State = GameObjectState.ActiveAlternative;
                _barDoorOpen = true;
            }
            else if (Encounters[10] == EncounterState.Done || Encounters[11] == EncounterState.Done)
            {
                DoUseDoorOrButton(GoBarDoor);
                _barDoorOpen = true;
            }
        }
    }

    /// <summary>blackrock_depths.cpp GOUse_go_bar_beer_keg, go_relic_coffer_door and go_shadowforge_brazier.</summary>
    public override void OnObjectUsed(Player player, GameObject go)
    {
        uint entry = go.Entry;
        if (entry == GoBeerKeg)
        {
            if (GetData(TypeHurley) is not (EncounterState.InProgress or EncounterState.Done))
                SetData(TypeHurley, EncounterState.Special);
        }
        else if (Array.IndexOf(RelicCofferDoors, entry) >= 0)
        {
            if (GetData(TypeVault) is not (EncounterState.InProgress or EncounterState.Done))
                SetData(TypeVault, EncounterState.Special);
        }
        else if (Array.IndexOf(ShadowforgeBraziers, entry) >= 0)
        {
            SetData(TypeLyceum, GetData(TypeLyceum) == EncounterState.InProgress ? EncounterState.Done : EncounterState.InProgress);
        }
    }

    private void HandleMugUse(Player player)
    {
        if (GetData(TypePlugger) is EncounterState.InProgress or EncounterState.Done
            || GetSingleCreatureFromStorage(NpcPlugger) is not { IsAlive: true, AI: PluggerAI ai }) return;
        SetData(TypePlugger, EncounterState.Special);
        if (GetData(TypePlugger) == EncounterState.InProgress) ai.AttackThief(player);
        else ai.WarnThief(player);
    }

    /// <summary>blackrock_depths.cpp GOUse_go_bar_ale_mug: warn or attack before the chest's own use.</summary>
    private sealed class BarMugAI(BlackrockDepthsInstance instance) : IGameObjectAi
    {
        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;
        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs) { }
        public bool OnUse(GameObjectMapSystem objects, GameObject go, Unit user)
        {
            if (user is Player player) instance.HandleMugUse(player);
            return false;
        }
    }

    private void SetDepthsData(uint type, uint data)
    {
        if (type is < TypeRingOfLaw or > TypeNagmara) return;
        switch (type)
        {
            case TypeRingOfLaw:
                if (data == EncounterState.Done)
                {
                    if (Encounters[0] == EncounterState.Special) RespawnDepthsObject(GoArenaSpoils);
                    else foreach (ObjectGuid guid in _arenaCrowd)
                        if (Instance.FindUpdater<CreatureMapSystem>()?.FindCreature(guid) is { } crowd) crowd.FactionTemplate = 15;
                }
                Encounters[0] = data;
                break;
            case TypeVault:
                if (data == EncounterState.Special)
                {
                    if (++_cofferDoorsOpened == 12)
                    {
                        SetData(TypeVault, EncounterState.InProgress);
                        CreatureMapSystem? creatures = Instance.FindUpdater<CreatureMapSystem>();
                        Creature? last = null;
                        foreach (ObjectGuid guid in _vaultCreatures)
                        {
                            if (creatures?.FindCreature(guid) is not { } construct) continue;
                            creatures.RemoveAuras(construct, 10255);
                            last = construct;
                        }
                        if (last is not null)
                            creatures?.SummonDeadDespawn(last, 9476, 821.905f, -338.382f, -50.134f, 3.78736f);
                    }
                    return;
                }
                if (data == EncounterState.Done)
                {
                    DoUseDoorOrButton(GoSecretDoor);
                    if (GetSingleGameObjectFromStorage(GoSecretSafe) is { } safe)
                        safe.Flags &= ~GameObjectFlags.NoInteract;
                }
                Encounters[1] = data;
                break;
            case TypeRocknot:
                if (data == EncounterState.Special) { _barAleCount++; return; }
                if (data == EncounterState.Done) { ReactBarPatrons(); _barDoorOpen = true; }
                Encounters[2] = data;
                break;
            case TypeLyceum:
                if (data == EncounterState.Done)
                {
                    DoUseDoorOrButton(GoGolemRoomNorth);
                    DoUseDoorOrButton(GoGolemRoomSouth);
                    if (GetSingleCreatureFromStorage(NpcMagmus) is { } magmus)
                        Instance.FindUpdater<CreatureMapSystem>()?.SayText(magmus, -1230070);
                }
                Encounters[4] = data;
                break;
            case TypeIronHall:
                if (data is EncounterState.InProgress or EncounterState.Fail or EncounterState.Done)
                {
                    DoUseDoorOrButton(GoGolemRoomNorth);
                    DoUseDoorOrButton(GoGolemRoomSouth);
                    if (data == EncounterState.Done) DoUseDoorOrButton(GoThroneRoom);
                }
                Encounters[5] = data;
                break;
            case TypeFlamelash:
                for (int i = 0; i < 7; i++) DoUseDoorOrButton(GoFirstRune + (uint)i);
                return; // the original does not store TYPE_FLAMELASH
            case TypeHurley:
                if (data == EncounterState.Special)
                {
                    if (++_brokenKegs == 3 && GetSingleCreatureFromStorage(NpcPlugger) is { } plugger)
                    {
                        CreatureMapSystem? creatures = Instance.FindUpdater<CreatureMapSystem>();
                        if (creatures?.SummonDeadDespawn(plugger, 9537, 856.0867f, -149.7469f, -49.6719f, 0.05949629f) is { } hurley)
                        {
                            for (int i = 0; i < 3; i++)
                            {
                                if (creatures.SummonDeadDespawn(plugger, 9541, 856.0867f, -149.7469f, -49.6719f, 0.05949629f) is { } crony)
                                {
                                    crony.UnitFlags |= UnitFlags.ImmuneToPlayer | UnitFlags.ImmuneToNpc;
                                    crony.Motion.MoveFollow(hurley, 1f, 0);
                                }
                            }
                            SetData(TypeHurley, EncounterState.InProgress);
                        }
                    }
                    return;
                }
                Encounters[8] = data;
                break;
            case TypeBar:
                if (data == EncounterState.InProgress) StartBarPatrol();
                Encounters[10] = data;
                break;
            case TypePlugger:
                if (data == EncounterState.Special && GetSingleCreatureFromStorage(NpcPlugger) is not null
                    && ++_stolenAles == 3) data = EncounterState.InProgress;
                Encounters[11] = data;
                break;
            default:
                Encounters[type - 1] = data;
                break;
        }
        SaveIfDone(data);
    }

    public override void OnCreatureEnterCombat(Creature creature)
    {
        if (creature.Template.Entry == NpcMagmus) SetData(TypeIronHall, EncounterState.InProgress);
    }

    public override void OnCreatureEvade(Creature creature)
    {
        uint entry = creature.Template.Entry;
        if (GetData(TypeRingOfLaw) is EncounterState.InProgress or EncounterState.Special && Array.IndexOf(ArenaMobs, entry) >= 0)
        {
            SetData(TypeRingOfLaw, EncounterState.Fail);
            return;
        }
        if (Array.IndexOf(TombDwarves, entry) >= 0) SetData(TypeTombOfSeven, EncounterState.Fail);
        if (entry == NpcMagmus) SetData(TypeIronHall, EncounterState.Fail);
    }

    public override void OnCreatureDeath(Creature creature)
    {
        uint entry = creature.Template.Entry;
        if (entry is 8905 or 9476 && GetData(TypeVault) == EncounterState.InProgress)
        {
            _vaultCreatures.Remove(creature.Guid);
            if (_vaultCreatures.Count == 0) SetData(TypeVault, EncounterState.Done);
        }
        if (Array.IndexOf(TombDwarves, entry) >= 0)
        {
            if (entry == 9039) SetData(TypeTombOfSeven, EncounterState.Done);
            else if (GetData(TypeTombOfSeven) == EncounterState.InProgress && _dwarfRound > 0
                && entry == TombDwarves[_dwarfRound - 1]) CallNextDwarf();
        }
        else if (entry == NpcMagmus) SetData(TypeIronHall, EncounterState.Done);
        else if (entry == 9537) SetData(TypeHurley, EncounterState.Done);
        else if (entry == NpcRibbly && GetData(TypeBar) is not (EncounterState.InProgress or EncounterState.Done)
            && GetData(TypePlugger) is not (EncounterState.InProgress or EncounterState.Done)) SetData(TypeBar, EncounterState.InProgress);
        else if (entry == NpcPlugger)
        {
            if (GetSingleCreatureFromStorage(NpcPhalanx)?.AI is PhalanxAI phalanx) phalanx.Start();
            MakeBarHostile();
            SetData(TypePlugger, EncounterState.InProgress);
        }
        else if (entry == 8904 && Emperor is { IsAlive: true } emperor && _dagranYellMs == 0)
        {
            Instance.FindUpdater<CreatureMapSystem>()?.SayText(emperor, -1230060 -
                (Instance.FindUpdater<CreatureMapSystem>()?.RandomInt(0, 3) ?? 0));
            _dagranYellMs = 30_000;
        }
    }

    private void CallNextDwarf()
    {
        if (_dwarfRound >= TombDwarves.Length) { _dwarfFightMs = 0; return; }
        if (GetSingleCreatureFromStorage(TombDwarves[_dwarfRound]) is { } dwarf && Instance.Players.FirstOrDefault() is { } player)
        {
            // SetFactionTemporary(FACTION_DWARF_HOSTILE, TEMPFACTION_RESTORE_RESPAWN | TEMPFACTION_RESTORE_REACH_HOME): a respawn
            // re-reads the template faction; reaching home after an evade is OnCreatureReachedHome below.
            dwarf.FactionTemplate = 754;
            _tombHostile.Add(dwarf.Guid);
            dwarf.AI?.AttackStart(player);
        }
        _dwarfFightMs = 30_000;
        _dwarfRound++;
    }

    private void ResetDwarfRound() { _dwarfRound = 0; _dwarfFightMs = 0; }

    /// <summary>TEMPFACTION_RESTORE_REACH_HOME of DoCallNextDwarf: a tomb dwarf back home after a wipe takes its template faction again.</summary>
    public override void OnCreatureReachedHome(Creature creature)
    {
        if (_tombHostile.Remove(creature.Guid) && creature.IsAlive)
            creature.FactionTemplate = creature.Template.Faction;
    }

    private void RespawnDepthsObject(uint entry)
    {
        if (GetSingleGameObjectFromStorage(entry) is { IsSpawned: false } go)
            Instance.FindUpdater<GameObjectMapSystem>()?.ForceRespawn(go);
    }

    private static void MakePatronHostile(Creature patron)
    {
        patron.FactionTemplate = 54;
        patron.StandState = StandState.Stand;
        patron.System?.SetDefaultRandomMovement(patron, 2);
    }

    private void MakeBarHostile()
    {
        foreach (ObjectGuid guid in _barPatrons)
            if (Instance.FindUpdater<CreatureMapSystem>()?.FindCreature(guid) is { } patron) MakePatronHostile(patron);
        if (GetSingleCreatureFromStorage(NpcRocknot) is { } rocknot)
        {
            Instance.FindUpdater<CreatureMapSystem>()?.SayText(rocknot, -1230047);
            rocknot.System?.ForcedDespawn(rocknot, 0);
        }
        if (GetSingleCreatureFromStorage(NpcNagmara) is { } nagmara)
        {
            nagmara.System?.CastSpell(nagmara, 15341, nagmara, triggered: true);
            nagmara.System?.ForcedDespawn(nagmara, 0);
        }
    }

    private void ReactBarPatrons()
    {
        GameObject? keg = GetSingleGameObjectFromStorage(170607);
        if (keg is null) return;
        CreatureMapSystem? creatures = Instance.FindUpdater<CreatureMapSystem>();
        foreach (ObjectGuid guid in _barPatrons)
            if (creatures?.FindCreature(guid) is { } patron && patron.Z > keg.Z - 1
                && (patron.X - keg.X) * (patron.X - keg.X) + (patron.Y - keg.Y) * (patron.Y - keg.Y) <= 18 * 18)
            {
                // HandleBarPatrons(PATRON_PISSED): urand(0, 4) -> 0 says PATRON_3, 1-2 PATRON_2, 3-4 PATRON_1.
                int roll = creatures.RandomInt(0, 4);
                creatures.SayText(patron, roll == 0 ? -1230039 : roll <= 2 ? -1230038 : -1230037);
            }
    }

    /// <summary>HandleBarPatrons(PATRON_EMOTE): about 4% of the patrons emote each second until the Plugger event is done.</summary>
    private void EmoteBarPatrons()
    {
        if (GetData(TypePlugger) == EncounterState.Done || Instance.FindUpdater<CreatureMapSystem>() is not { } creatures) return;
        foreach (ObjectGuid guid in _barPatrons)
            if (creatures.RandomInt(0, 100) < 4 && creatures.FindCreature(guid) is { IsAlive: true } patron)
                creatures.PlayEmote(patron, PatronEmotes[creatures.RandomInt(0, PatronEmotes.Length - 1)]);
    }

    private void StartBarPatrol()
    {
        if (GetData(TypeBar) == EncounterState.Done || GetSingleCreatureFromStorage(NpcPlugger) is not { } plugger) return;
        if (!_barDoorOpen) { DoUseDoorOrButton(GoBarDoor); _barDoorOpen = true; }
        CreatureMapSystem? creatures = Instance.FindUpdater<CreatureMapSystem>();
        foreach (uint entry in new uint[] { NpcFireguardDestroyer, NpcAnvilrageOfficer, NpcAnvilrageOfficer })
        {
            float x = 872.7059f + (creatures?.RandomInt(-200, 200) ?? 0) / 100f;
            float y = -232.5491f + (creatures?.RandomInt(-200, 200) ?? 0) / 100f;
            if (creatures?.SummonDeadDespawn(plugger, entry, x, y, -43.7525f, 2.069044f) is { } patrol)
            {
                _barPatrol.Add(patrol.Guid);
                patrol.Motion.MovePoint(0, 865.5645f, -219.7471f, -43.7033f, run: false);
            }
        }
        _patrolMs = 4000;
    }

    public override void Update(uint diffMs)
    {
        if (_dwarfFightMs > 0)
        {
            if (_dwarfFightMs <= diffMs) CallNextDwarf(); else _dwarfFightMs -= diffMs;
        }
        if (_dagranYellMs > 0) _dagranYellMs = _dagranYellMs <= diffMs ? 0 : _dagranYellMs - diffMs;
        if (_patronEmoteMs > 0)
        {
            if (_patronEmoteMs <= diffMs) { EmoteBarPatrons(); _patronEmoteMs = 1000; } else _patronEmoteMs -= diffMs;
        }
        if (_patrolMs > 0)
        {
            if (_patrolMs <= diffMs)
            {
                CreatureMapSystem? creatures = Instance.FindUpdater<CreatureMapSystem>();
                Creature? fireguard = _barPatrol.Select(g => creatures?.FindCreature(g)).FirstOrDefault(c => c?.Template.Entry == NpcFireguardDestroyer);
                if (fireguard is not null && GetData(TypeBar) == EncounterState.InProgress)
                {
                    creatures?.SayText(fireguard, -1230048);
                    SetData(TypeBar, EncounterState.Special);
                    _patrolMs = 2000;
                }
                else if (fireguard is not null && GetData(TypeBar) == EncounterState.Special)
                {
                    creatures?.SayText(fireguard, -1230049);
                    SetData(TypeBar, EncounterState.Done);
                    _patrolMs = 0;
                }
                // HandleBarPatrol(1/2) finds no Fireguard Destroyer: the original leaves its elapsed timer alone and looks again next tick.
                else if (GetData(TypeBar) is not (EncounterState.InProgress or EncounterState.Special)) _patrolMs = 0;
            }
            else _patrolMs -= diffMs;
        }
    }

    /// <summary>blackrock_depths.cpp AreaTrigger_at_ring_of_law: start once per instance, spawning Grimstone.</summary>
    public bool EnterRingOfLaw(Player player, float centerX, float centerY, float centerZ)
    {
        if (player.IsGameMaster || GetData(TypeRingOfLaw) is EncounterState.InProgress or EncounterState.Done or EncounterState.Special)
            return false;
        _arenaCenterX = centerX; _arenaCenterY = centerY; _arenaCenterZ = centerZ;
        SetData(TypeRingOfLaw, GetData(TypeRingOfLaw) == 5 ? EncounterState.Special : EncounterState.InProgress);
        Instance.FindUpdater<CreatureMapSystem>()?.SummonForInstance(10096, 625.559f, -205.618f, -52.735f, 2.609f);
        return true;
    }

    internal (float X, float Y, float Z) ArenaCenter => (_arenaCenterX, _arenaCenterY, _arenaCenterZ);
    internal IReadOnlyCollection<ObjectGuid> ArenaCrowdGuids => _arenaCrowd;
    internal void UseArenaDoor(uint entry) => DoUseDoorOrButton(entry);
}
