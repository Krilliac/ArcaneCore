using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.RazorfenDowns;

/// <summary>ScriptDev2 instance_razorfen_downs (mangos-classic razorfen_downs/instance_razorfen_downs.cpp:
/// SetData, OnCreatureDeath, DoSpawnWaveIfCan and ProcessEventId_event_go_tutenkash_gong).</summary>
[InstanceScript(MapId)]
public sealed class RazorfenDownsInstance(Map map) : ScriptedInstance(map, 1)
{
    public const uint MapId = 129, TypeTutenKash = 0;
    public const uint TombFiend = 7349, TombReaver = 7351, TutenKash = 7355, Gong = 148917;
    private static readonly (uint Entry, int Count)[] Waves = [(TombFiend, 8), (TombReaver, 4), (TutenKash, 1)];
    private static readonly (float X, float Y, float Z, float O)[] Locations =
        [(2484.83f, 811.11f, 43.40f, 1.67f), (2546.03f, 902.77f, 47.16f, 5.04f)];
    private readonly HashSet<ObjectGuid> _waveMobs = [];
    private int _waveCounter;
    private bool _belnistraszRegistered;
    public int WaveCounter => _waveCounter;
    public int WaveMobCount => _waveMobs.Count;

    public override void OnCreatureCreate(Creature creature)
    {
        if (creature.Template.Entry is TombFiend or TombReaver) _waveMobs.Add(creature.Guid);
        if (creature.Template.Entry == BelnistraszAi.Entry && !_belnistraszRegistered && creature.System is { } system)
        {
            _belnistraszRegistered = true;
            system.RegisterEntryAi(BelnistraszAi.Entry, c => new BelnistraszAi(c), rebuildExisting: creature.AI is not null);
        }
    }

    public override void OnObjectCreate(GameObject go)
    {
        if (go.Entry != Gong) return;
        StoreGameObject(go);
        Instance.FindUpdater<GameObjectMapSystem>()?.RegisterAi(Gong, new GongAi(this));
    }

    public override void OnCreatureDeath(Creature creature)
    {
        if (GetData(TypeTutenKash) != EncounterState.InProgress) return;
        if (creature.Template.Entry is TombFiend or TombReaver)
        {
            _waveMobs.Remove(creature.Guid);
            if (_waveMobs.Count == 0 && GetSingleGameObjectFromStorage(Gong) is { } gong)
            {
                gong.Flags &= ~GameObjectFlags.NoInteract;
                Instance.FindUpdater<GameObjectMapSystem>()?.ForceRespawn(gong);
            }
        }
        else if (creature.Template.Entry == TutenKash) SetData(TypeTutenKash, EncounterState.Done);
    }

    public override void SetData(uint type, uint data)
    {
        if (type != TypeTutenKash) return;
        Encounters[0] = data;
        if (data == EncounterState.InProgress) _waveCounter++;
        SaveIfDone(data);
    }

    public override uint GetData(uint type) => type == TypeTutenKash ? Encounters[0] : 0;

    public bool SpawnWaveIfCan(GameObject gong)
    {
        if (gong.Entry != Gong || GetData(TypeTutenKash) == EncounterState.Done || _waveMobs.Count > 0 || _waveCounter >= Waves.Length)
            return false;

        if (Instance.FindUpdater<CreatureMapSystem>() is not { } creatures ||
            creatures.Content.FindTemplate(Waves[_waveCounter].Entry) is not { } template) return false;

        Instance.FindUpdater<GameObjectMapSystem>()?.SendCustomAnim(gong, 0);
        for (int i = 0; i < Waves[_waveCounter].Count; i++)
        {
            var point = Locations[i % 2];
            // SD2 GameObject::GetRandomPoint around each corridor center, within five yards.
            double angle = Random.Shared.NextDouble() * Math.Tau;
            double radius = Math.Sqrt(Random.Shared.NextDouble()) * 5;
            Creature summoned = creatures.SpawnTemporary(template,
                point.X + (float)(Math.Cos(angle) * radius), point.Y + (float)(Math.Sin(angle) * radius), point.Z, point.O);
            summoned.Motion.MovePoint(0, gong.X, gong.Y, gong.Z, run: true);
        }

        SetData(TypeTutenKash, EncounterState.InProgress);
        gong.Flags |= GameObjectFlags.NoInteract;
        return true;
    }

    private sealed class GongAi(RazorfenDownsInstance instance) : IGameObjectAi
    {
        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;
        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs) { }
        public bool OnUse(GameObjectMapSystem objects, GameObject go, Unit user)
        {
            if (user is Player) instance.SpawnWaveIfCan(go);
            return true;
        }
    }
}
