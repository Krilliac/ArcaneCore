using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.World;

/// <summary>
/// cmangos <c>world_map_kalimdor</c> (AI/ScriptDevAI/scripts/kalimdor/world_kalimdor.cpp, world_kalimdor.h), the parts the area trigger
/// scripts need: Wanted: Murkdeep (q4740: three Greymist Coastrunners, then two Warriors, then Murkdeep and a Hunter) and the Hive Tower
/// drones timer (q1126, TYPE_HIVE). The other encounters (Omen, Freedom Creatures, the gong) keep their state without behaviour here.
/// </summary>
public sealed class KalimdorWorldMapData : WorldMapData
{
    public const uint MapKalimdor = 1;

    public const uint NpcGreymistCoastrunner = 2202, NpcGreymistWarrior = 2205, NpcGreymistHunter = 2206, NpcMurkdeep = 10323;
    public const uint QuestWantedMurkdeep = 4740;
    public const uint NpcHiveAshiDrone = 13136;

    /// <summary>world_kalimdor.h Encounters.</summary>
    public const uint TypeOmen = 0, TypeHive = 1, TypeFreedomCreatures = 3, TypeGongTime = 4;
    private const int MaxEncounter = 5;

    public const uint HiveDronesTimerMs = 5 * 60 * 1000;

    /// <summary>world_kalimdor.h aSpawnLocations: POS_IDX_MURKDEEP_SPAWN, POS_IDX_MURKDEEP_MOVE (both guesswork in cmangos).</summary>
    public static readonly (float X, float Y, float Z, float O) MurkdeepSpawn = (4981.031f, 597.955f, -1.361f, 4.82f);
    public static readonly (float X, float Y, float Z) MurkdeepMove = (4988.970f, 547.002f, 5.379f);

    /// <summary>world_kalimdor.h POS_IDX_HIVE_DRONES_START..STOP.</summary>
    public static readonly (float X, float Y, float Z, float O)[] HiveDroneSpawns =
    [
        (-7185.94f, 443.29f, 26.59f, 4.8458f), (-7180.59f, 441.33f, 26.68f, 4.1798f), (-7176.63f, 437.42f, 26.84f, 3.9348f),
    ];

    private int _murkdeepAddsKilled;
    private uint _dronesTimerMs;

    public KalimdorWorldMapData(Map map)
        : base(map, MaxEncounter)
    {
    }

    /// <summary>The Murkdeep wave kill counter (m_uiMurkdeepAdds_KilledAddCount), for tests.</summary>
    public int MurkdeepAddsKilled => _murkdeepAddsKilled;

    public override void Initialize()
    {
        base.Initialize();
        _murkdeepAddsKilled = 0;
        _dronesTimerMs = 0;
    }

    public override void OnCreatureCreate(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (creature.Template.Entry == NpcMurkdeep)
        {
            StoreCreature(creature);
        }
    }

    public override void OnCreatureDeath(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        bool temporary = creature.Spawn is null; // IsTemporarySummon: only the ones summoned for the Murkdeep quest count
        switch (creature.Template.Entry)
        {
            case NpcGreymistCoastrunner when temporary:
                if (++_murkdeepAddsKilled == 3)
                {
                    _murkdeepAddsKilled = 0;
                    SummonWave(NpcGreymistWarrior, NpcGreymistWarrior);
                }

                break;
            case NpcGreymistWarrior when temporary:
                if (++_murkdeepAddsKilled == 2)
                {
                    _murkdeepAddsKilled = 0;
                    SummonWave(NpcMurkdeep, NpcGreymistHunter);
                }

                break;
        }
    }

    /// <summary>
    /// The Greymist summons of the Murkdeep event (at_murkdeep and OnCreatureDeath): each comes out of the water near the spawn point
    /// (TEMPSPAWN_DEAD_DESPAWN) and runs to a point near the shore.
    /// </summary>
    public int SummonWave(params uint[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (Instance.FindUpdater<CreatureMapSystem>() is not { } creatures)
        {
            return 0;
        }

        int summoned = 0;
        foreach (uint entry in entries)
        {
            (float x, float y, float z) = RandomPointAround(MurkdeepSpawn.X, MurkdeepSpawn.Y, MurkdeepSpawn.Z, 5f);
            if (creatures.SummonDeadDespawnAt(entry, x, y, z, MurkdeepSpawn.O) is not { } add)
            {
                continue;
            }

            (float mx, float my, float mz) = RandomPointAround(MurkdeepMove.X, MurkdeepMove.Y, MurkdeepMove.Z, 5f);
            add.Motion.MovePoint(0, mx, my, mz, run: true); // SetWalk(false) + MovePoint
            summoned++;
        }

        return summoned;
    }

    public override uint GetData(uint type) => type < MaxEncounter ? Encounters[type] : 0;

    public override void SetData(uint type, uint data)
    {
        if (type >= MaxEncounter)
        {
            return;
        }

        if (type == TypeHive && data == EncounterState.InProgress)
        {
            _dronesTimerMs = HiveDronesTimerMs;
        }

        Encounters[type] = data;
    }

    public override void Update(uint diffMs)
    {
        // The Hive Tower area trigger summons drones again only after the five minute timer.
        if (_dronesTimerMs == 0)
        {
            return;
        }

        if (_dronesTimerMs <= diffMs)
        {
            _dronesTimerMs = 0;
            SetData(TypeHive, EncounterState.NotStarted);
        }
        else
        {
            _dronesTimerMs -= diffMs;
        }
    }
}
