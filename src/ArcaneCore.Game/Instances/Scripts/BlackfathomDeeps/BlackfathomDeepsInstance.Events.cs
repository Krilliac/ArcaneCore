using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>ScriptDev2 instance_blackfathom_deeps::DoSpawnMobs, OnCreatureDeath, Update and the two GOUse handlers
/// (mangos-classic scripts/kalimdor/blackfathom_deeps/instance_blackfathom_deeps.cpp:73-113,184-289).
/// Coordinates and wave counts are from blackfathom_deeps.h. No content rows are synthesized.</summary>
public sealed partial class BlackfathomDeepsInstance
{
    public const uint NpcKelris = 4832, NpcBaronAquanis = 12876;
    public const uint GoFathomStone = 177964;

    private static readonly (float X, float Y, float Z, float O)[] SpawnLocations =
    [
        (-768.949f, -174.413f, -25.87f, 3.09f), (-768.888f, -164.238f, -25.87f, 3.09f),
        (-768.951f, -153.911f, -25.88f, 3.09f), (-867.782f, -174.352f, -25.87f, 6.27f),
        (-867.875f, -164.089f, -25.87f, 6.27f), (-867.859f, -153.927f, -25.88f, 6.27f),
    ];

    private static readonly (uint Entry, int Position, int Count)[][] Waves =
    [
        [(4825, 0, 1), (4825, 1, 1), (4825, 5, 1)],
        [(4978, 1, 1), (4978, 4, 1)],
        [(4823, 0, 1), (4823, 2, 1), (4823, 3, 1), (4823, 4, 1)],
        [(4977, 0, 2), (4977, 1, 1), (4977, 2, 2), (4977, 3, 1), (4977, 4, 2), (4977, 5, 2)],
    ];

    private readonly uint[] _waveTimers = new uint[4];
    private readonly HashSet<ObjectGuid> _waveMobs = [];
    private int _waveCount;

    public override void Initialize()
    {
        base.Initialize();
        _waveCount = 0;
        Array.Clear(_waveTimers);
        _waveMobs.Clear();
        if (Instance.FindUpdater<GameObjectMapSystem>() is { } objects)
        {
            var ai = new ShrineObjectAi(this);
            for (uint entry = GoShrine1; entry <= GoShrine4; entry++)
            {
                objects.RegisterAi(entry, ai);
            }

            objects.RegisterAi(GoFathomStone, ai);
        }
    }

    public override void OnCreatureCreate(Creature creature)
    {
        if (creature.Entry == NpcKelris)
        {
            StoreCreature(creature);
        }
    }

    private void QueueNextWave()
    {
        if (_waveCount < Waves.Length)
        {
            _waveTimers[_waveCount++] = 3_000;
        }
    }

    private void WaveMobDied(Creature creature)
    {
        if (creature.Entry == NpcBaronAquanis)
        {
            SetData(TypeAquanis, EncounterState.Done);
        }

        if (Encounters[1] == EncounterState.InProgress && _waveMobs.Remove(creature.Guid)
            && _waveCount == Waves.Length && _waveMobs.Count == 0 && _waveTimers.All(t => t == 0))
        {
            SetData(TypeShrine, EncounterState.Done);
        }
    }

    public override void OnCreatureDeath(Creature creature) => WaveMobDied(creature);

    public override void Update(uint diffMs)
    {
        if (Encounters[1] != EncounterState.InProgress)
        {
            return;
        }

        for (int i = 0; i < _waveTimers.Length; i++)
        {
            if (_waveTimers[i] == 0)
            {
                continue;
            }

            if (_waveTimers[i] > diffMs)
            {
                _waveTimers[i] -= diffMs;
                continue;
            }

            _waveTimers[i] = 0;
            SpawnWave(i);
        }

    }

    private void SpawnWave(int index)
    {
        Creature? kelris = GetSingleCreatureFromStorage(NpcKelris);
        CreatureMapSystem? creatures = Instance.FindUpdater<CreatureMapSystem>();
        if (kelris is null || creatures is null)
        {
            return;
        }

        foreach ((uint entry, int position, int count) in Waves[index])
        {
            (float x, float y, float z, float o) = SpawnLocations[position];
            for (int k = 0; k < count; k++)
            {
                float adjustedY = count > 1 ? y - GameObjectMapSystem.InteractionDistance / 2
                    + k * GameObjectMapSystem.InteractionDistance / count : y;
                Creature? summon = creatures.SummonCorpseDespawn(kelris, entry, x, adjustedY, z, o);
                if (summon is not null)
                {
                    _waveMobs.Add(summon.Guid);
                    summon.Motion.MovePoint(0, kelris.Home.X, kelris.Home.Y, kelris.Home.Z, run: false);
                }
            }
        }
    }

    private sealed class ShrineObjectAi(BlackfathomDeepsInstance instance) : IGameObjectAi
    {
        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;
        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs) { }

        public bool OnUse(GameObjectMapSystem objects, GameObject go, Unit user)
        {
            if (go.Entry == GoFathomStone)
            {
                if (instance.GetData(TypeAquanis) == EncounterState.NotStarted
                    && objects.Map.FindUpdater<CreatureMapSystem>() is { } creatures
                    && creatures.Content.FindTemplate(NpcBaronAquanis) is { } template)
                {
                    Creature baron = creatures.SpawnTemporary(template, -782.21f, -63.26f, -42.43f, 2.36f);
                    creatures.MarkCorpseDespawn(baron);
                }

                if (instance.GetData(TypeAquanis) == EncounterState.NotStarted)
                    instance.SetData(TypeAquanis, EncounterState.InProgress);

                return false; // GOUse_go_fathom_stone lets GameObject::Use continue
            }

            if (instance.GetData(TypeShrine) == EncounterState.Done || instance.GetData(TypeKelris) != EncounterState.Done)
            {
                return true; // GOUse_go_fire_of_akumai suppresses the normal use
            }

            instance.SetData(TypeShrine, EncounterState.InProgress);
            return false;
        }
    }
}
