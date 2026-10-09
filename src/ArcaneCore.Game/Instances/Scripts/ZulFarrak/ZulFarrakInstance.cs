using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Instances.Scripts.ZulFarrak;

/// <summary>ScriptDev2 instance_zulfarrak (mangos-classic zulfarrak/instance_zulfarrak.cpp:
/// OnCreatureCreate, OnObjectCreate, SetData, combat/evade/death hooks and Update).</summary>
[InstanceScript(MapId)]
public sealed class ZulFarrakInstance(Map map) : ScriptedInstance(map, 9)
{
    public const uint MapId = 209;
    public const uint TypeVelratha = 0, TypeGahzrilla = 1, TypeAntusul = 2, TypeTheka = 3, TypeZumrah = 4;
    public const uint TypeNekrum = 5, TypeSezzziz = 6, TypeChief = 7, TypePyramid = 8;
    public const uint Antusul = 8127, SergeantBly = 7604, ShallowGrave = 128403, EndDoor = 146084;
    public const uint GahzrillaGong = 141832;
    /// <summary>dbscripts_on_event ids of event_go_zulfarrak_gong and event_spell_unlocking (as relays: RelayScriptCatalog.EventRelayId).</summary>
    public const uint GongEvent = 2488, PyramidEvent = 2609;
    public const uint AntusulTrigger = 1447;
    private static readonly Dictionary<uint, uint> BossTypes = new()
    {
        [7795] = TypeVelratha, [7273] = TypeGahzrilla, [Antusul] = TypeAntusul, [7272] = TypeTheka,
        [7271] = TypeZumrah, [7796] = TypeNekrum, [7275] = TypeSezzziz, [7267] = TypeChief,
    };
    private static readonly HashSet<uint> PyramidTrollEntries = [7787, 7788, 7789, 8876, 8877];
    private readonly List<ObjectGuid> _graves = [];
    private readonly List<ObjectGuid> _pyramidTrolls = [];
    private uint _pyramidTimer;
    private bool _zumrahRegistered;
    private readonly GahzrillaGongAi _gongAi = new();

    public IReadOnlyList<ObjectGuid> ShallowGraves => _graves;
    public Creature? FindAntusul() => GetSingleCreatureFromStorage(Antusul);
    public int PyramidTrollsRemaining => _pyramidTrolls.Count;
    public uint PyramidTimerMs => _pyramidTimer;

    public override void OnCreatureCreate(Creature creature)
    {
        uint entry = creature.Template.Entry;
        if (entry is Antusul or SergeantBly) StoreCreature(creature);
        if (PyramidTrollEntries.Contains(entry)) _pyramidTrolls.Add(creature.Guid);
        if (entry == 7271 && !_zumrahRegistered && creature.System is { } system)
        {
            _zumrahRegistered = true;
            system.RegisterEntryAi(7271, c => new ZumrahAi(c, this), rebuildExisting: creature.AI is not null);
        }
    }

    public override void OnObjectCreate(GameObject go)
    {
        if (go.Entry == ShallowGrave) _graves.Add(go.Guid);
        else if (go.Entry == GahzrillaGong)
            Instance.FindUpdater<GameObjectMapSystem>()?.RegisterAi(GahzrillaGong, _gongAi);
        else if (go.Entry == EndDoor)
        {
            StoreGameObject(go);
            OpenIf(go, GetData(TypePyramid) == EncounterState.Done);
        }
    }

    public override void OnCreatureEnterCombat(Creature creature) => SetBossState(creature, EncounterState.InProgress);
    public override void OnCreatureEvade(Creature creature) => SetBossState(creature, EncounterState.Fail);
    public override void OnCreatureDeath(Creature creature) => SetBossState(creature, EncounterState.Done);

    private void SetBossState(Creature creature, uint state)
    {
        if (BossTypes.TryGetValue(creature.Template.Entry, out uint type)) SetData(type, state);
    }

    public override void SetData(uint type, uint data)
    {
        if (type >= 9) return;
        Encounters[type] = data;
        if (type is TypeNekrum or TypeSezzziz && data == EncounterState.Done
            && GetData(type == TypeNekrum ? TypeSezzziz : TypeNekrum) == EncounterState.Done)
            SetData(TypePyramid, EncounterState.Done);
        if (type == TypePyramid)
        {
            if (data == EncounterState.InProgress) _pyramidTimer = 20_000;
            else if (data == EncounterState.Done)
            {
                _pyramidTimer = 0;
                // Deviation: instance_zulfarrak.cpp only opens GO_END_DOOR in OnObjectCreate once the pyramid is DONE; live, the door is
                // blown by Weegli's DB script (vmangos instance_zulfarrak.cpp EVENT_END_DOOR), which this port does not include. Opening
                // it here keeps Chief Ukorz Sandscalp reachable until that escort is ported.
                if (GetSingleGameObjectFromStorage(EndDoor) is { } door) door.State = GameObjectState.Active;
            }
        }
        SaveIfDone(data);
    }

    public override uint GetData(uint type) => type < 9 ? Encounters[type] : 0;

    /// <summary>
    /// ProcessEventId_event_go_zulfarrak_gong and ProcessEventId_event_spell_unlocking (zulfarrak.cpp:39-75), reached through
    /// ScriptedEvents.Start for every start of event 2488 or 2609 (the SEND_EVENT of Unlocking 10738, a goober or chest event). For a
    /// player source this handler owns the event: an allowed start runs the event's imported copy (the relay
    /// <see cref="RelayScriptCatalog.EventRelayId"/>, the same rows as dbscripts_on_event), and true keeps the dbscripts_on_event script
    /// itself from running a second wave; a later start finds the encounter started and does nothing, as cmangos returns true then. Any
    /// other source gets false and the DB script, as in cmangos.
    /// </summary>
    public override bool OnSpellEvent(Unit caster, uint eventId)
    {
        if (eventId is not (PyramidEvent or GongEvent) || caster is not Player player)
        {
            return false;
        }

        if (eventId == PyramidEvent ? StartPyramid() : StartGahzrilla())
        {
            Instance.FindUpdater<CreatureMapSystem>()?.StartRelayScript(RelayScriptCatalog.EventRelayId(eventId), player, null);
        }

        return true;
    }

    /// <summary>event_spell_unlocking: start once and permit the DB relay to spawn its trolls.</summary>
    public bool StartPyramid()
    {
        if (GetData(TypePyramid) != EncounterState.NotStarted) return false;
        SetData(TypePyramid, EncounterState.InProgress);
        return true;
    }

    /// <summary>event_go_zulfarrak_gong: permit a DB summon for the first start or after failure.</summary>
    public bool StartGahzrilla()
    {
        if (GetData(TypeGahzrilla) is not (EncounterState.NotStarted or EncounterState.Fail)) return false;
        SetData(TypeGahzrilla, EncounterState.InProgress);
        return true;
    }

    public bool TriggerGahzrillaGong(Player player, GameObject gong)
    {
        if (gong.Entry != GahzrillaGong || !StartGahzrilla()) return false;
        Instance.FindUpdater<CreatureMapSystem>()?.StartRelayScript(RelayScriptCatalog.EventRelayId(GongEvent), player, gong);
        return true;
    }

    public override void Update(uint diffMs)
    {
        if (_pyramidTimer == 0) return;
        if (_pyramidTimer > diffMs)
        {
            _pyramidTimer -= diffMs;
            return;
        }

        if (_pyramidTrolls.Count == 0)
        {
            _pyramidTimer = (uint)Random.Shared.Next(3000, 10001);
            return;
        }

        int index = Random.Shared.Next(_pyramidTrolls.Count);
        ObjectGuid guid = _pyramidTrolls[index];
        _pyramidTrolls.RemoveAt(index);
        if (Instance.FindUpdater<CreatureMapSystem>() is { } creatures && creatures.FindCreature(guid) is { IsAlive: true } troll
            && troll.Combat.Victim is null && GetSingleCreatureFromStorage(SergeantBly) is { } bly)
        {
            if (!bly.IsAlive)
            {
                _pyramidTimer = 0;
                return;
            }

            double angle = Random.Shared.NextDouble() * Math.Tau;
            double radius = Math.Sqrt(Random.Shared.NextDouble()) * 4;
            troll.Motion.MovePoint(0, bly.X + (float)(Math.Cos(angle) * radius),
                bly.Y + (float)(Math.Sin(angle) * radius), bly.Z, run: true);
        }
        _pyramidTimer = Random.Shared.Next(3) == 0 ? 1000u : (uint)Random.Shared.Next(3000, 10001);
    }

    private sealed class GahzrillaGongAi : IGameObjectAi
    {
        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;
        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs) { }
        public bool OnUse(GameObjectMapSystem objects, GameObject go, Unit user)
        {
            if (user is Player player && go.Map?.FindUpdater<InstanceData>() is ZulFarrakInstance instance)
                instance.TriggerGahzrillaGong(player, go);
            return true;
        }
    }
}
