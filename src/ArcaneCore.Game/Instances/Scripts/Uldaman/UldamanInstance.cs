using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Uldaman;

/// <summary>ScriptDev2 instance_uldaman (mangos-classic uldaman/instance_uldaman.cpp:
/// OnObjectCreate, OnCreatureCreate, SetData, StartEvent, DoResetKeeperEvent, OnCreatureDeath/Evade, Update).</summary>
[InstanceScript(MapId)]
public sealed class UldamanInstance(Map map) : ScriptedInstance(map, 2)
{
    public const uint MapId = 70;
    public const uint TypeAltar = 1, TypeArchaedas = 2, DataEventStarter = 3;
    public const uint TempleDoorUpper = 124367, TempleDoorLower = 141869, AncientVault = 124369, AncientTreasure = 141979;
    public const uint Archaedas = 2748, Custodian = 7309, Hallshaper = 7077, Guardian = 7076, VaultWarder = 10120, StoneKeeper = 4857;
    public const uint SpellStoned = 10255, SpellFreezeAnim = 16245;
    public const uint AltarKeeperEvent = 2228, AltarArchaedasEvent = 2268;
    private readonly List<ObjectGuid> _wardens = [];
    private readonly List<ObjectGuid> _keepers = [];
    private ObjectGuid _starter;
    private uint _keeperCooldown;
    private uint _keepersFallen;
    private bool _archaedasRegistered;
    private bool _spawnHooksRegistered;

    /// <summary>mangos-classic WORLD_STATE_CUSTOM_SPAWN_ANNORA (World/WorldStateDefines.h:162): 1 once the Cleft Scorpid group is gone.</summary>
    public const uint VariableSpawnAnnora = 700001;

    /// <summary>
    /// The classic-db z2815 spawn groups behind it (Updates/Instances/070_uldaman.sql, imported as 7000000/7000001): "Uldaman - Cleft Scorpid
    /// (10) - Annora" with the creature spawns 7000200-7000209, and "Uldaman - Annora (11073)" with spawn 7000324 and WorldState condition
    /// 700001 (CONDITION_WORLDSTATE, variable 700001 equal to 1). The source checks group id 700000 (instance_uldaman.cpp:83-87); the database
    /// it ships with numbers the group 7000000, so the port names the group by its members.
    /// </summary>
    public const uint ScorpidGroup = 7000000, AnnoraSpawnGuid = 7000324;
    public static readonly uint[] ScorpidSpawnGuids = [.. Enumerable.Range(7000200, 10).Select(i => (uint)i)];

    public override void Initialize()
    {
        base.Initialize();
        // instance_uldaman::Initialize: GetVariableManager().SetVariable(WORLD_STATE_CUSTOM_SPAWN_ANNORA, 0).
        SetVariable(VariableSpawnAnnora, 0);
        if (!_spawnHooksRegistered)
        {
            _spawnHooksRegistered = true;
            RegisterCreatureGroup(ScorpidGroup, ScorpidSpawnGuids);
            GateCreatureSpawnOnVariable(AnnoraSpawnGuid, VariableSpawnAnnora, 1);
        }
    }

    /// <summary>instance_uldaman::OnCreatureGroupDespawn: the last Cleft Scorpid of the group is gone, Annora appears.</summary>
    protected override void OnCreatureGroupDespawn(uint groupId, Creature last)
    {
        if (groupId == ScorpidGroup)
        {
            SetVariable(VariableSpawnAnnora, 1);
        }
    }

    public uint KeepersFallen => _keepersFallen;
    public uint KeeperCooldownMs => _keeperCooldown;

    public override void OnObjectCreate(GameObject go)
    {
        switch (go.Entry)
        {
            case TempleDoorUpper or TempleDoorLower:
                OpenIf(go, GetData(TypeAltar) == EncounterState.Done);
                break;
            case AncientVault:
                OpenIf(go, GetData(TypeArchaedas) == EncounterState.Done);
                break;
            case AncientTreasure:
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
            case Hallshaper or Custodian: _wardens.Add(creature.Guid); break;
            case StoneKeeper: _keepers.Add(creature.Guid); break;
            case Archaedas:
                StoreCreature(creature);
                if (!_archaedasRegistered && creature.System is { } system)
                {
                    _archaedasRegistered = true;
                    system.RegisterEntryAi(Archaedas, c => new ArchaedasAi(c, this), rebuildExisting: creature.AI is not null);
                }
                break;
        }
    }

    public override void SetData(uint type, uint data)
    {
        if (type == TypeAltar)
        {
            if (data == EncounterState.Done)
            {
                DoUseDoorOrButton(TempleDoorUpper);
                DoUseDoorOrButton(TempleDoorLower);
            }
            else if (data == EncounterState.InProgress)
            {
                ResetKeepers();
                _keeperCooldown = 5000;
            }
            else if (data == EncounterState.NotStarted)
            {
                ResetKeepers();
                _keepersFallen = 0;
                _keeperCooldown = 0;
            }
            Encounters[0] = data;
        }
        else if (type == TypeArchaedas)
        {
            if (data == EncounterState.Done)
            {
                DoUseDoorOrButton(AncientVault);
                if (GetSingleGameObjectFromStorage(AncientTreasure) is { } treasure)
                    Instance.FindUpdater<GameObjectMapSystem>()?.ForceRespawn(treasure);
            }
            Encounters[1] = data;
        }
        else return;
        SaveIfDone(data);
    }

    public override uint GetData(uint type) => type switch { TypeAltar => Encounters[0], TypeArchaedas => Encounters[1], _ => 0 };
    public override void SetData64(uint type, ulong data)
    {
        if (type == DataEventStarter) _starter = new ObjectGuid(data);
    }
    public override ulong GetData64(uint type) => type == DataEventStarter ? _starter.Value : 0;

    public void StartEvent(uint eventId, Player player)
    {
        _starter = player.Guid;
        if (eventId == AltarKeeperEvent && GetData(TypeAltar) == EncounterState.NotStarted)
            SetData(TypeAltar, EncounterState.InProgress);
        else if (eventId == AltarArchaedasEvent && GetData(TypeArchaedas) is EncounterState.NotStarted or EncounterState.Fail)
            SetData(TypeArchaedas, EncounterState.Special);
    }

    public override void OnCreatureEvade(Creature creature)
    {
        if (creature.Template.Entry == StoneKeeper) SetData(TypeAltar, EncounterState.NotStarted);
    }

    public override void OnCreatureDeath(Creature creature)
    {
        if (creature.Template.Entry != StoneKeeper) return;
        _keepersFallen++;
        if (_keepers.Count == _keepersFallen) SetData(TypeAltar, EncounterState.Done);
        else _keeperCooldown = 5000;
    }

    public override void Update(uint diffMs)
    {
        if (GetData(TypeAltar) != EncounterState.InProgress || _keeperCooldown == 0) return;
        if (_keeperCooldown > diffMs) { _keeperCooldown -= diffMs; return; }
        _keeperCooldown = 0;
        if (Instance.FindUpdater<CreatureMapSystem>() is not { } creatures) return;
        foreach (ObjectGuid guid in _keepers)
        {
            if (creatures.FindCreature(guid) is not { IsAlive: true } keeper || keeper.Combat.Victim is not null) continue;
            Player? player = Instance.FindPlayer(_starter);
            if (player is null || !player.IsAlive)
                player = Instance.Players.FirstOrDefault(p => p.IsAlive && DistanceSq(p, keeper) <= 50 * 50);
            if (player is null) { SetData(TypeAltar, EncounterState.NotStarted); return; }
            creatures.RemoveAuras(keeper, SpellStoned);
            keeper.AI?.AttackStart(player);
            break;
        }
    }

    public Creature? ClosestDwarfNotInCombat(Creature searcher)
    {
        CreatureMapSystem? creatures = Instance.FindUpdater<CreatureMapSystem>();
        return _wardens.Select(g => creatures?.FindCreature(g))
            .Where(c => c is { IsAlive: true } && c.Combat.Victim is null)
            .OrderBy(c => DistanceSq(c!, searcher)).FirstOrDefault();
    }

    private void ResetKeepers()
    {
        if (Instance.FindUpdater<CreatureMapSystem>() is not { } creatures) return;
        foreach (ObjectGuid guid in _keepers)
            if (creatures.FindCreature(guid) is { IsAlive: false } keeper) creatures.ForceRespawn(keeper);
    }

    private static float DistanceSq(WorldObject a, WorldObject b)
        => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z);
}
