using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// ScriptDev2 instance_maraudon (mangos-classic
/// AI/ScriptDevAI/scripts/kalimdor/maraudon/instance_maraudon.cpp:
/// OnObjectCreate, SetData, OnCreatureDeath, OnCreatureEvade and Update).
/// </summary>
[InstanceScript(MapId)]
public sealed class MaraudonInstance(Map map) : ScriptedInstance(map, 1)
{
    public const uint MapId = 349;
    public const uint TypeNoxxion = 0;
    public const uint NpcNoxxion = 13282;
    public const uint NpcSpewedLarva = 13533;
    public const uint GoLarvaSpewer = 178559;
    public const uint GoCorruptionSpewer = 178570;

    private uint _larvaSpewRemainingMs = 60_000;
    private bool _aiRegistered;
    private bool _objectAiRegistered;

    public uint LarvaSpewRemainingMs => _larvaSpewRemainingMs;

    public override void Initialize()
    {
        base.Initialize();
        _larvaSpewRemainingMs = 60_000;
    }

    public override void OnCreatureCreate(Creature creature)
    {
        if (creature.Template.Entry != NpcNoxxion)
        {
            return;
        }

        StoreCreature(creature);
        if (!_aiRegistered && Instance.FindUpdater<CreatureMapSystem>() is { } creatures)
        {
            _aiRegistered = true;
            creatures.RegisterEntryAi(NpcNoxxion, c => new NoxxionAI(c));
        }
    }

    public override void OnObjectCreate(GameObject go)
    {
        if (go.Entry is not (GoLarvaSpewer or GoCorruptionSpewer))
        {
            return;
        }

        StoreGameObject(go);
        if (go.Entry == GoLarvaSpewer)
        {
            if (GetData(TypeNoxxion) == EncounterState.Done)
            {
                go.LootState = GameObjectLootState.Activated;
            }

            if (!_objectAiRegistered && Instance.FindUpdater<GameObjectMapSystem>() is { } objects)
            {
                _objectAiRegistered = true;
                objects.RegisterAi(GoLarvaSpewer, new LarvaSpewerAI(this));
            }
        }
    }

    public override void OnCreatureDeath(Creature creature)
    {
        if (creature.Template.Entry == NpcNoxxion)
        {
            SetData(TypeNoxxion, EncounterState.Done);
        }
    }

    public override void OnCreatureEvade(Creature creature)
    {
        if (creature.Template.Entry == NpcNoxxion)
        {
            SetData(TypeNoxxion, EncounterState.Fail);
        }
    }

    public override void SetData(uint type, uint data)
    {
        if (type != TypeNoxxion || Encounters[0] == data)
        {
            return;
        }

        Encounters[0] = data;
        if (data == EncounterState.Done && GetSingleGameObjectFromStorage(GoCorruptionSpewer) is { } corruption)
        {
            corruption.LootState = GameObjectLootState.Activated;
        }

        if (data is EncounterState.Done or EncounterState.Special)
        {
            _larvaSpewRemainingMs = 0;
            if (GetSingleGameObjectFromStorage(GoLarvaSpewer) is { } larva)
            {
                larva.LootState = GameObjectLootState.Activated;
            }
        }

        SaveIfDone(data);
    }

    public override uint GetData(uint type) => type == TypeNoxxion ? Encounters[0] : 0;

    public override void Update(uint diffMs)
    {
        if (_larvaSpewRemainingMs == 0)
        {
            return;
        }

        if (_larvaSpewRemainingMs > diffMs)
        {
            _larvaSpewRemainingMs -= diffMs;
            return;
        }

        // instance_maraudon::Update: one larva each minute, ten minute absolute lifetime.
        if (GetSingleGameObjectFromStorage(GoLarvaSpewer) is not null)
        {
            Instance.FindUpdater<CreatureMapSystem>()?.SummonFromGameObject(
                NpcSpewedLarva, 937.213f, -377.967f, -50.346f, 2.578f, 600_000);
        }

        _larvaSpewRemainingMs = 60_000;
    }

    private sealed class LarvaSpewerAI(MaraudonInstance instance) : IGameObjectAi
    {
        private readonly Dictionary<ObjectGuid, GameObjectLootState> _lastState = [];
        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, ArcaneCore.Game.Entities.Unit target) => false;

        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
        {
            if (_lastState.TryGetValue(go.Guid, out GameObjectLootState previous)
                && previous != GameObjectLootState.Activated && go.LootState == GameObjectLootState.Activated
                && instance.GetData(TypeNoxxion) != EncounterState.Done)
            {
                instance.SetData(TypeNoxxion, EncounterState.Special);
            }
            _lastState[go.Guid] = go.LootState;
        }
    }
}
