using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.ScarletMonastery;

/// <summary>ScriptDev2 instance_scarlet_monastery (mangos-classic scarlet_monastery/instance_scarlet_monastery.cpp:
/// OnCreatureCreate, OnCreatureDeath, OnObjectCreate, OnCreatureRespawn, SetData, GetData).</summary>
[InstanceScript(MapId)]
public sealed class ScarletMonasteryInstance(Map map) : ScriptedInstance(map, 2)
{
    public const uint MapId = 189;
    public const uint TypeMograineAndWhitemane = 1, TypeAshbringer = 2;
    public const uint Mograine = 3976, Whitemane = 3977, Vorrel = 3981, Vishas = 3983;
    public const uint WhitemaneDoor = 104600, ChapelDoor = 104591;
    public const int VorrelText = -1189015, AshbringerText = -1189036;
    public const uint CathedralTrigger = 4089, CorruptedAshbringer = 22691;
    private static readonly uint[] FriendlyEntries = [4294, 4295, 4298, 4299, 4300, 4301, 4302, 4303, 4540, 4542];
    private readonly HashSet<ObjectGuid> _friendly = [];
    private readonly HashSet<uint> _registeredAis = [];
    private bool _damageSubscribed;

    public Creature? FindMograine() => GetSingleCreatureFromStorage(Mograine);
    public Creature? FindWhitemane() => GetSingleCreatureFromStorage(Whitemane);

    public override void OnCreatureCreate(Creature creature)
    {
        uint entry = creature.Template.Entry;
        if (entry is Mograine or Whitemane or Vorrel) StoreCreature(creature);
        if (entry == Mograine || Array.IndexOf(FriendlyEntries, entry) >= 0) _friendly.Add(creature.Guid);
        if (GetData(TypeAshbringer) == EncounterState.InProgress) MakeFriendly(creature);
        if (creature.System is { } system && _registeredAis.Add(entry))
        {
            switch (entry)
            {
                case Mograine: system.RegisterEntryAi(entry, c => new MograineAi(c, this), rebuildExisting: creature.AI is not null); break;
                case Whitemane: system.RegisterEntryAi(entry, c => new WhitemaneAi(c, this), rebuildExisting: creature.AI is not null); break;
                case 3975: system.RegisterEntryAi(entry, c => new HerodAi(c), rebuildExisting: creature.AI is not null); break;
                case 6487: system.RegisterEntryAi(entry, c => new DoanAi(c), rebuildExisting: creature.AI is not null); break;
            }
        }
        if (entry == Mograine && !_damageSubscribed)
        {
            _damageSubscribed = true;
            Instance.Combat.DamageTaken += OnDamageTaken;
        }
    }

    public override void OnCreatureDeath(Creature creature)
    {
        if (creature.Template.Entry == Vishas && GetSingleCreatureFromStorage(Vorrel) is { } vorrel)
            Instance.FindUpdater<CreatureMapSystem>()?.SayText(vorrel, VorrelText);
    }

    public override void OnCreatureRespawn(Creature creature)
    {
        if (GetData(TypeAshbringer) == EncounterState.InProgress) MakeFriendly(creature);
    }

    public override void OnObjectCreate(GameObject go)
    {
        if (go.Entry is WhitemaneDoor or ChapelDoor)
        {
            StoreGameObject(go);
            if (go.Entry == ChapelDoor && GetData(TypeAshbringer) == EncounterState.InProgress)
                OpenIf(go, true);
        }
    }

    public override void OnObjectSpawn(GameObject go)
    {
        if (go.Entry == ChapelDoor && GetData(TypeAshbringer) == EncounterState.InProgress
            && go.State == GameObjectState.Ready)
            DoUseDoorOrButton(ChapelDoor);
    }

    public override void SetData(uint type, uint data)
    {
        if (type == TypeMograineAndWhitemane)
        {
            if (data == EncounterState.InProgress) DoUseDoorOrButton(WhitemaneDoor);
            if (data == EncounterState.Fail)
            {
                // The wipe: both are read alive before either is despawned (ForcedDespawn kills at once). The one-shot 30 s respawn is
                // SetRespawnDelay(30, true); without it they would come back on their ClassicDB 43200 s delay.
                Creature? whitemane = GetSingleCreatureFromStorage(Whitemane);
                if (whitemane is null) return;
                Creature? mograine = GetSingleCreatureFromStorage(Mograine);
                if (mograine is null) return;
                bool whitemaneAlive = whitemane.IsAlive, mograineAlive = mograine.IsAlive;
                Encounters[0] = EncounterState.Fail; // a despawn below may evade the other boss, whose EnterEvadeMode must not fail it again
                if (whitemaneAlive && mograineAlive)
                {
                    DespawnForWipe(whitemane, WipeRespawnSeconds);
                    DespawnForWipe(mograine, WipeRespawnSeconds);
                    DoUseDoorOrButton(WhitemaneDoor);
                    Encounters[0] = EncounterState.NotStarted;
                    return;
                }
                if (!mograineAlive && whitemaneAlive)
                {
                    DespawnForWipe(whitemane, WipeRespawnSeconds);
                    DoUseDoorOrButton(WhitemaneDoor);
                    Encounters[0] = data;
                    return;
                }
                if (!whitemaneAlive && mograineAlive)
                {
                    DespawnForWipe(mograine, null);
                    Encounters[0] = data;
                    return;
                }
            }
            Encounters[0] = data;
        }
        else if (type == TypeAshbringer)
        {
            if (data == EncounterState.InProgress)
            {
                DoUseDoorOrButton(ChapelDoor);
                if (GetSingleCreatureFromStorage(Whitemane) is { IsAlive: true } whitemane && !whitemane.Combat.IsInCombat)
                    whitemane.System?.ForcedDespawn(whitemane, 0);

                if (Instance.FindUpdater<CreatureMapSystem>() is { } creatures)
                    foreach (ObjectGuid guid in _friendly)
                        if (creatures.FindCreature(guid) is { } npc) MakeFriendly(npc);
            }
            Encounters[1] = data;
        }
    }

    public override uint GetData(uint type) => type switch
    {
        TypeMograineAndWhitemane => Encounters[0],
        TypeAshbringer => Encounters[1],
        _ => 0,
    };

    /// <summary>at_cathedral_entrance: caller checks the player is alive, not a GM and carries item 22691.</summary>
    public bool EnterCathedral()
    {
        if (GetData(TypeAshbringer) != EncounterState.NotStarted) return false;
        SetData(TypeAshbringer, EncounterState.InProgress);
        if (GetSingleCreatureFromStorage(Mograine) is { } mograine)
            Instance.FindUpdater<CreatureMapSystem>()?.SayText(mograine, AshbringerText);
        return true;
    }

    public void CompleteAshbringer(ArcaneCore.Game.Entities.Unit caster)
    {
        if (GetData(TypeAshbringer) == EncounterState.InProgress)
        {
            if (GetSingleCreatureFromStorage(Mograine) is { } mograine)
                Instance.FindUpdater<CreatureMapSystem>()?.StartRelayScript(9001, mograine, caster);
            SetData(TypeAshbringer, EncounterState.Done);
        }
    }

    /// <summary>Mograine's and Whitemane's respawn after a wipe (instance_scarlet_monastery.cpp SetData FAIL: SetRespawnDelay(30, true)).</summary>
    public const uint WipeRespawnSeconds = 30;

    private static void DespawnForWipe(Creature creature, uint? respawnSeconds)
    {
        if (respawnSeconds is { } seconds) creature.RespawnDelayOnceSeconds = seconds;
        creature.System?.ForcedDespawn(creature, 0);
    }

    private void MakeFriendly(Creature creature)
    {
        if (_friendly.Contains(creature.Guid) && creature.IsAlive && !creature.Combat.IsInCombat && creature.FactionTemplate != 35)
            creature.FactionTemplate = 35;
    }

    private void OnDamageTaken(ArcaneCore.Game.Entities.Unit attacker, ArcaneCore.Game.Entities.Unit victim, uint damage)
    {
        // Combat reports damage before its invincibility clamp. This is ScriptDev2 CombatAI::JustPreventedDeath's seam.
        if (victim is Creature { AI: MograineAi ai } mograine && mograine.Template.Entry == Mograine
            && damage >= mograine.Health)
            ai.OnLethalDamage();
    }
}
