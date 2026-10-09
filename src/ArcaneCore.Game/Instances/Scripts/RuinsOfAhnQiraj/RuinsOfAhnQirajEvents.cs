using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Instances.Scripts.RuinsOfAhnQiraj;

/// <summary>mangos-classic ruins_of_ahnqiraj/ruins_of_ahnqiraj.cpp
/// DoSpawnAndorovIfCan, DoSortArmyWaves, DoSendNextArmyWave, OnCreatureEvade and Update;
/// boss_ossirian.cpp RespawnFirstCrystal, DoSpawnNextCrystal and GOUse_go_ossirian_crystal.</summary>
public sealed partial class RuinsOfAhnQirajInstance
{
    private static readonly uint[] ArmyCaptains = [15391, 15392, 15389, 15390, 15386, 15388, 15385];
    private static readonly int[] WaveTexts = [0, 0, -1509005, -1509006, -1509007, -1509008, -1509009];
    private static readonly uint[] Weaknesses = [25177, 25178, 25180, 25181, 25183];
    private readonly HashSet<ObjectGuid> _waveMembers = [];
    // Ossirian triggers that already summoned their crystal this attempt (used or still standing).
    private readonly HashSet<ObjectGuid> _crystalTriggers = [];
    private uint _waveDelay;
    private int _wave;

    public override void OnPlayerEnter(Player player)
    {
        if (GetData(0) == EncounterState.Done) SpawnAndorovIfReady();
    }

    private void SpawnAndorovIfReady()
    {
        if (GetSingleCreatureFromStorage(15471) is not null || Instance.Players.Count == 0 ||
            Instance.FindUpdater<CreatureMapSystem>() is not { } creatures) return;
        (uint entry, float x, float y, float z, float o)[] positions =
        [
            (15471, -8660.4f, 1510.29f, 32.449f, 2.2184f),
            (15473, -8655.84f, 1509.78f, 32.462f, 2.33341f),
            (15473, -8657.39f, 1506.28f, 32.418f, 2.33346f),
            (15473, -8660.96f, 1504.9f, 32.1567f, 2.33306f),
            (15473, -8664.45f, 1506.44f, 32.0944f, 2.33302f)
        ];
        foreach (var p in positions)
            if (creatures.Content.FindTemplate(p.entry) is { } template)
                creatures.SpawnTemporary(template, p.x, p.y, p.z, p.o);
    }

    private void BeginRajaxxWaves()
    {
        _wave = 0;
        _waveDelay = 0;
        _waveMembers.Clear();
        SendNextWave();
    }

    private void EndRajaxxWaves(uint state)
    {
        _waveDelay = 0;
        _waveMembers.Clear();
        if (state == EncounterState.Done && GetSingleCreatureFromStorage(15471) is { IsAlive: true } andorov)
            Instance.FindUpdater<CreatureMapSystem>()?.SayText(andorov, -1509032);
    }

    private void SendNextWave()
    {
        if (GetData(1) != EncounterState.InProgress || Instance.FindUpdater<CreatureMapSystem>() is not { } creatures)
            return;
        _waveMembers.Clear();
        if (_wave == ArmyCaptains.Length)
        {
            if (GetSingleCreatureFromStorage(15341) is { IsAlive: true } boss &&
                Instance.Players.FirstOrDefault(p => p.IsAlive) is { } target)
            {
                creatures.SayText(boss, -1509010);
                boss.AI?.AttackStart(target);
            }
            _waveDelay = 0;
            _wave++;
            return;
        }
        if (_wave >= ArmyCaptains.Length) return;
        if (GetSingleCreatureFromStorage(ArmyCaptains[_wave]) is { IsAlive: true } captain)
        {
            if (WaveTexts[_wave] != 0 && GetSingleCreatureFromStorage(15341) is { } rajaxx)
                creatures.SayText(rajaxx, WaveTexts[_wave]);
            Unit? target = Instance.Players.FirstOrDefault(p => p.IsAlive);
            foreach (Creature member in creatures.Creatures.Where(c => c.IsAlive &&
                (ReferenceEquals(c, captain) || (c.Entry is 15387 or 15344) &&
                 (c.X - captain.X) * (c.X - captain.X) + (c.Y - captain.Y) * (c.Y - captain.Y) <= 2500)))
            {
                _waveMembers.Add(member.Guid);
                if (target is not null) member.AI?.AttackStart(target);
            }
        }
        _wave++;
        _waveDelay = 180000;
    }

    public override void OnCreatureEvade(Creature creature)
    {
        // ruins_of_ahnqiraj.cpp OnCreatureEvade: Rajaxx or any army captain evading fails the event.
        if (GetData(1) == EncounterState.InProgress &&
            (creature.Entry == 15341 || Array.IndexOf(ArmyCaptains, creature.Entry) >= 0))
            SetData(1, EncounterState.Fail);
    }

    public override void OnCreatureDeath(Creature creature)
    {
        if (GetData(1) != EncounterState.InProgress || !_waveMembers.Remove(creature.Guid) || _waveMembers.Count != 0)
            return;
        _waveDelay = 1;
    }

    public override void Update(uint diffMs)
    {
        EnsureCrystal();
        if (GetData(1) != EncounterState.InProgress || _waveDelay == 0) return;
        _waveDelay = _waveDelay > diffMs ? _waveDelay - diffMs : 0;
        if (_waveDelay == 0) SendNextWave();
    }

    public override void OnObjectUsed(Player player, GameObject go)
    {
        if (go.Entry != 180619 || GetData(5) != EncounterState.InProgress ||
            GetSingleCreatureFromStorage(15339) is not { IsAlive: true } boss ||
            Instance.FindUpdater<CreatureMapSystem>() is not { } creatures) return;
        Creature? trigger = creatures.Creatures.FirstOrDefault(c => c.Entry == 15590 && c.IsAlive &&
            (c.X - go.X) * (c.X - go.X) + (c.Y - go.Y) * (c.Y - go.Y) <= 100);
        if (trigger is null) return;
        uint spell = Weaknesses[creatures.RandomInt(0, Weaknesses.Length - 1)];
        creatures.CastSpell(trigger, spell, boss, triggered: false);
        // boss_ossirianAI::SpellHit despawns the used crystal; the next one comes from OssirianAI.OnSpellHit.
        Instance.FindUpdater<GameObjectMapSystem>()?.Remove(go);
        _crystalTriggers.Add(trigger.Guid);
    }

    /// <summary>boss_ossirianAI::DoSpawnNextCrystal(spawnCount): up to <paramref name="count"/> random triggers that have not had a
    /// crystal yet summon one.</summary>
    public void SpawnOssirianCrystals(int count)
    {
        if (Instance.FindUpdater<CreatureMapSystem>() is not { } creatures) return;
        List<Creature> free = [.. creatures.Creatures.Where(c => c.Entry == 15590 && c.IsAlive && !_crystalTriggers.Contains(c.Guid))];
        for (int spawned = 0; spawned < count && free.Count > 0; spawned++)
        {
            int pick = creatures.RandomInt(0, free.Count - 1);
            SummonCrystal(creatures, free[pick]);
            free.RemoveAt(pick);
        }
    }

    private void SummonCrystal(CreatureMapSystem creatures, Creature trigger)
    {
        _crystalTriggers.Add(trigger.Guid);
        // SD2 boss_ossirianAI::ReceiveAIEvent(AI_EVENT_CUSTOM_A) casts 25192 from the trigger. Its GO template is the fallback when this
        // host does not create the object for the imported spell.
        creatures.CastSpell(trigger, 25192, trigger, triggered: true);
        if (Instance.FindUpdater<GameObjectMapSystem>() is { } objects &&
            !objects.GameObjects.Any(g => g.Entry == 180619 && (g.X - trigger.X) * (g.X - trigger.X) + (g.Y - trigger.Y) * (g.Y - trigger.Y) <= 25))
            objects.Summon(180619, trigger.X, trigger.Y, trigger.Z, trigger.Orientation);
    }

    private void ResetCrystals()
    {
        _crystalTriggers.Clear();
        if (Instance.FindUpdater<GameObjectMapSystem>() is { } objects)
            foreach (GameObject crystal in objects.GameObjects.Where(g => g.Entry == 180619).ToArray())
                objects.Remove(crystal);
    }

    /// <summary>boss_ossirianAI::RespawnFirstCrystal (every reset) and the fight's guarantee that a crystal stands while triggers remain:
    /// whenever the encounter is not done and no crystal exists, the first unused trigger summons one.</summary>
    private void EnsureCrystal()
    {
        if (GetData(5) == EncounterState.Done ||
            Instance.FindUpdater<GameObjectMapSystem>() is not { } objects ||
            objects.GameObjects.Any(g => g.Entry == 180619) ||
            Instance.FindUpdater<CreatureMapSystem>() is not { } creatures ||
            creatures.Creatures.FirstOrDefault(c => c.Entry == 15590 && c.IsAlive &&
                !_crystalTriggers.Contains(c.Guid)) is not { } trigger) return;
        SummonCrystal(creatures, trigger);
    }
}
