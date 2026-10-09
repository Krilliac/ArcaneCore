using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Instances.Scripts.RuinsOfAhnQiraj;

/// <summary>mangos-classic ruins_of_ahnqiraj/ruins_of_ahnqiraj.cpp
/// DoSpawnAndorovIfCan, DoSortArmyWaves, DoSendNextArmyWave, OnCreatureEvade, OnCreatureDeath, SetData(TYPE_RAJAXX) and Update;
/// boss_ossirian.cpp RespawnFirstCrystal, DoSpawnNextCrystal and GOUse_go_ossirian_crystal.
/// <para>
/// Army waves: the reference sends the creatures of each <c>AQ20_*</c> creature-group string id. This host has no such groups, so
/// DoSortArmyWaves gives every Qiraji Warrior (15387) and Swarmguard Needler (15344) within 50 yards of a captain to its closest captain:
/// each soldier belongs to exactly one wave. A wave is sent as the reference sends it (SetInCombatWithZone, AttackClosestEnemy). The next
/// wave follows three minutes later or as soon as the current one is dead ("after 2 min or after the previous wave is finished",
/// DoSendNextArmyWave; the reference only shortcuts the last captain's wave, OnCreatureGroupDespawn(AQ20_ZERRAN)).
/// </para></summary>
public sealed partial class RuinsOfAhnQirajInstance
{
    private const uint Rajaxx = 15341, QirajiWarrior = 15387, SwarmguardNeedler = 15344, OssirianTrigger = 15590, OssirianCrystal = 180619;
    private static readonly uint[] ArmyCaptains = [15391, 15392, 15389, 15390, 15386, 15388, 15385];
    private static readonly int[] WaveTexts = [0, 0, -1509005, -1509006, -1509007, -1509008, -1509009];
    private static readonly uint[] Weaknesses = [25177, 25178, 25180, 25181, 25183];
    private readonly List<ObjectGuid>[] _waves = [.. ArmyCaptains.Select(_ => new List<ObjectGuid>())];
    private readonly HashSet<ObjectGuid> _waveMembers = [];
    // Ossirian triggers that already summoned their crystal this attempt (used or still standing).
    private readonly HashSet<ObjectGuid> _crystalTriggers = [];
    // RespawnFirstCrystal is due (instance start, a trigger appeared, Ossirian reset or failed) and has not found what it needs yet.
    private bool _firstCrystalPending = true;
    private uint _waveDelay;
    private int _wave;

    /// <summary>General Andorov (15471) from <c>m_npcEntryGuidStore</c>.</summary>
    public Creature? FindAndorov() => GetSingleCreatureFromStorage(AndorovAI.Entry);

    public override void OnPlayerEnter(Player player)
    {
        if (GetData(0) == EncounterState.Done) SpawnAndorovIfReady();
    }

    private void SpawnAndorovIfReady()
    {
        if (GetSingleCreatureFromStorage(AndorovAI.Entry) is not null || Instance.Players.Count == 0 ||
            Instance.FindUpdater<CreatureMapSystem>() is not { } creatures) return;
        (uint entry, float x, float y, float z, float o)[] positions =
        [
            (AndorovAI.Entry, -8660.4f, 1510.29f, 32.449f, 2.2184f),
            (AndorovAI.KaldoreiElite, -8655.84f, 1509.78f, 32.462f, 2.33341f),
            (AndorovAI.KaldoreiElite, -8657.39f, 1506.28f, 32.418f, 2.33346f),
            (AndorovAI.KaldoreiElite, -8660.96f, 1504.9f, 32.1567f, 2.33306f),
            (AndorovAI.KaldoreiElite, -8664.45f, 1506.44f, 32.0944f, 2.33302f)
        ];
        foreach (var p in positions)
            if (creatures.Content.FindTemplate(p.entry) is { } template)
                creatures.SpawnTemporary(template, p.x, p.y, p.z, p.o);
    }

    /// <summary>DoSortArmyWaves: the wave members are fixed when the event starts, then the first wave is sent.</summary>
    private void BeginRajaxxWaves()
    {
        _wave = 0;
        _waveDelay = 0;
        _waveMembers.Clear();
        SortArmyWaves();
        SendNextWave();
    }

    private void SortArmyWaves()
    {
        foreach (List<ObjectGuid> wave in _waves) wave.Clear();
        if (Instance.FindUpdater<CreatureMapSystem>() is not { } creatures) return;
        Creature?[] captains = [.. ArmyCaptains.Select(GetSingleCreatureFromStorage)];
        for (int i = 0; i < captains.Length; i++)
            if (captains[i] is { IsAlive: true } captain) _waves[i].Add(captain.Guid);
        foreach (Creature soldier in creatures.Creatures.Where(c => c.IsAlive && c.Entry is QirajiWarrior or SwarmguardNeedler))
        {
            int closest = -1;
            float best = 2500; // 50 yards
            for (int i = 0; i < captains.Length; i++)
            {
                if (captains[i] is not { } captain) continue;
                float dx = soldier.X - captain.X, dy = soldier.Y - captain.Y;
                float distance = (dx * dx) + (dy * dy);
                if (distance <= best)
                {
                    best = distance;
                    closest = i;
                }
            }
            if (closest >= 0) _waves[closest].Add(soldier.Guid);
        }
    }

    private void EndRajaxxWaves(uint state)
    {
        _waveDelay = 0;
        _waveMembers.Clear();
        // SetData(TYPE_RAJAXX, DONE): a living Andorov hears AI_EVENT_CUSTOM_A.
        if (state == EncounterState.Done && GetSingleCreatureFromStorage(AndorovAI.Entry) is { IsAlive: true } andorov)
            andorov.ReceiveAiEvent(AndorovAI.AiEventRajaxxDefeated, andorov, andorov);
    }

    /// <summary>DoSendNextArmyWave.</summary>
    private void SendNextWave()
    {
        if (GetData(1) != EncounterState.InProgress || Instance.FindUpdater<CreatureMapSystem>() is not { } creatures)
            return;
        _waveMembers.Clear();
        if (_wave == ArmyCaptains.Length)
        {
            // The last wave is General Rajaxx himself.
            if (GetSingleCreatureFromStorage(Rajaxx) is { IsAlive: true } boss)
            {
                creatures.SayText(boss, -1509010);
                creatures.SetInCombatWithZone(boss);
                creatures.AttackClosestEnemy(boss);
            }
            _waveDelay = 0;
            _wave++;
            return;
        }
        if (_wave > ArmyCaptains.Length) return;

        foreach (ObjectGuid guid in _waves[_wave])
        {
            if (Instance.FindObject(guid) is not Creature { IsAlive: true } member) continue;
            _waveMembers.Add(guid);
            creatures.SetInCombatWithZone(member);
            creatures.AttackClosestEnemy(member);
        }
        // Yell on each wave (except the first two).
        if (WaveTexts[_wave] != 0 && GetSingleCreatureFromStorage(Rajaxx) is { } rajaxx)
            creatures.SayText(rajaxx, WaveTexts[_wave]);
        _wave++;
        // A wave that is already dead is followed at once, as one that dies is.
        _waveDelay = _waveMembers.Count == 0 ? 1u : 180000u;
    }

    public override void OnCreatureEvade(Creature creature)
    {
        // ruins_of_ahnqiraj.cpp OnCreatureEvade: Rajaxx or any army captain evading fails the event.
        if (GetData(1) == EncounterState.InProgress &&
            (creature.Entry == Rajaxx || Array.IndexOf(ArmyCaptains, creature.Entry) >= 0))
            SetData(1, EncounterState.Fail);
    }

    public override void OnCreatureDeath(Creature creature)
    {
        // OnCreatureDeath(NPC_RAJAXX): his death completes the event (his ClassicDB EventAI has no instance action).
        if (creature.Entry == Rajaxx)
        {
            SetData(1, EncounterState.Done);
            return;
        }

        if (GetData(1) != EncounterState.InProgress || !_waveMembers.Remove(creature.Guid) || _waveMembers.Count != 0)
            return;
        _waveDelay = 1;
    }

    public override void Update(uint diffMs)
    {
        if (_firstCrystalPending) TrySpawnFirstCrystal();
        if (GetData(1) != EncounterState.InProgress || _waveDelay == 0) return;
        _waveDelay = _waveDelay > diffMs ? _waveDelay - diffMs : 0;
        if (_waveDelay == 0) SendNextWave();
    }

    /// <summary>GOUse_go_ossirian_crystal: the trigger within 10 yards casts a random weakness (no encounter-state gate, so a crystal used
    /// before the pull weakens him too). This host's weakness is cast at Ossirian, so a living Ossirian is needed.</summary>
    public override void OnObjectUsed(Player player, GameObject go)
    {
        if (go.Entry != OssirianCrystal || GetSingleCreatureFromStorage(15339) is not { IsAlive: true } boss ||
            Instance.FindUpdater<CreatureMapSystem>() is not { } creatures) return;
        Creature? trigger = creatures.Creatures.FirstOrDefault(c => c.Entry == OssirianTrigger && c.IsAlive &&
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
        List<Creature> free = [.. creatures.Creatures.Where(c => c.Entry == OssirianTrigger && c.IsAlive && !_crystalTriggers.Contains(c.Guid))];
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
            !objects.GameObjects.Any(g => g.Entry == OssirianCrystal && (g.X - trigger.X) * (g.X - trigger.X) + (g.Y - trigger.Y) * (g.Y - trigger.Y) <= 25))
            objects.Summon(OssirianCrystal, trigger.X, trigger.Y, trigger.Z, trigger.Orientation);
    }

    private void ResetCrystals()
    {
        _crystalTriggers.Clear();
        if (Instance.FindUpdater<GameObjectMapSystem>() is { } objects)
            foreach (GameObject crystal in objects.GameObjects.Where(g => g.Entry == OssirianCrystal).ToArray())
                objects.Remove(crystal);
        RespawnFirstCrystal();
    }

    /// <summary>boss_ossirianAI::RespawnFirstCrystal (every Ossirian reset): before the pull one crystal stands at the first trigger. It is
    /// requested on events (instance start, a trigger created, Ossirian's reset, a failed attempt) and tried on the next tick until the
    /// map has its game objects and triggers, never as a per-tick scan.</summary>
    public void RespawnFirstCrystal() => _firstCrystalPending = true;

    private void TrySpawnFirstCrystal()
    {
        if (GetData(5) == EncounterState.Done)
        {
            _firstCrystalPending = false;
            return;
        }

        if (Instance.FindUpdater<GameObjectMapSystem>() is not { } objects || Instance.FindUpdater<CreatureMapSystem>() is not { } creatures)
            return; // the map's systems are not there yet: try again next tick

        _firstCrystalPending = false;
        if (objects.GameObjects.Any(g => g.Entry == OssirianCrystal)) return;
        if (creatures.Creatures.FirstOrDefault(c => c.Entry == OssirianTrigger && c.IsAlive && !_crystalTriggers.Contains(c.Guid)) is { } trigger)
            SummonCrystal(creatures, trigger);
    }
}
