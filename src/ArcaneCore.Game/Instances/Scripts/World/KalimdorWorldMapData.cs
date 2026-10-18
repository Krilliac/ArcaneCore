using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Instances.Scripts.World;

/// <summary>
/// cmangos <c>world_map_kalimdor</c> (AI/ScriptDevAI/scripts/kalimdor/world_kalimdor.cpp, world_kalimdor.h), the parts the area trigger
/// scripts need: Wanted: Murkdeep (q4740: three Greymist Coastrunners, then two Warriors, then Murkdeep and a Hunter) and the Hive Tower
/// drones timer (q1126, TYPE_HIVE), and Freedom for All Creatures (q2969, TYPE_FREEDOM_CREATURES: the freed Captured Sprite Darters counted
/// to six, and the cage door <c>go_cage_door</c>, feralas.cpp:400-437). The other encounters (Omen, the gong) keep their state without
/// behaviour here.
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

    /// <summary>feralas.cpp Freedom for All Creatures: the cage door, the Grimtotem Raider it brings and the six darters that must get away.</summary>
    public const uint GoCageDoor = 143979, NpcGrimtotemRaider = 7725, FreedSpriteDartersNeeded = 6;
    public const float CageDoorSearchRange = 20f;

    /// <summary>feralas.cpp raiderSpawnPos.</summary>
    public static readonly (float X, float Y, float Z, float O) RaiderSpawn = (-4515.35f, 811.36f, 62.99f, 3.60244f);

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
    private uint _freedSpriteDarters;

    /// <summary>m_freedSpriteDarter, for tests.</summary>
    public uint FreedSpriteDarters => _freedSpriteDarters;

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
        _freedSpriteDarters = 0;
    }

    public override void OnCreatureCreate(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (creature.Template.Entry == NpcMurkdeep)
        {
            StoreCreature(creature);
        }
        else if (creature.Template.Entry == FelhoundTrackerAi.Entry && !_felhoundRegistered && creature.System is { } system)
        {
            // azshara.cpp npc_felhound_tracker is a ScriptedPetAI on the guardian the Azsharite quest item summons: a map entry script, so the
            // guardian keeps it instead of the generic pet AI (SummonService asks HasEntryAi), as Razorfen Kraul's Snufflenose Gopher does.
            _felhoundRegistered = true;
            system.RegisterEntryAi(FelhoundTrackerAi.Entry, c => new FelhoundTrackerAi(c), rebuildExisting: creature.AI is not null);
        }
    }

    private bool _felhoundRegistered;

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

        if (type == TypeFreedomCreatures)
        {
            if (data == EncounterState.InProgress)
            {
                _freedSpriteDarters = 0;
            }
            else if (data == EncounterState.Special && ++_freedSpriteDarters >= FreedSpriteDartersNeeded)
            {
                data = EncounterState.Done;
            }
        }

        Encounters[type] = data;
    }

    /// <summary>
    /// go_cage_door (feralas.cpp:400-437): every use restarts the count (IN_PROGRESS), frees the Captured Sprite Darters within 20 yd of the
    /// door (faction 495 until their respawn, the escort started at a run) and brings a Grimtotem Raider that attacks the player; Kindal
    /// Moonweaver within 20 yd of the player calls it out and attacks it. The script takes the use.
    /// </summary>
    public override bool OnGameObjectUse(Player player, GameObject go)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(go);
        if (go.Entry is GoBlackwoodGrainStores or GoBlackwoodNutStores or GoBlackwoodFruitStores)
        {
            FurbolgFood(player, go);
            return true;
        }

        if (go.Entry != GoCageDoor)
        {
            return false;
        }

        SetData(TypeFreedomCreatures, EncounterState.InProgress);
        if (Instance.FindUpdater<CreatureMapSystem>() is not { } creatures)
        {
            return true;
        }

        foreach (Creature darter in creatures.CreaturesOfEntryInRange(go, CapturedSpriteDarterAI.Entry, CageDoorSearchRange))
        {
            darter.SetFactionTemporary(FactionEscortNeutralFriendActive, TemporaryFactionFlags.RestoreRespawn);
            (darter.AI as CapturedSpriteDarterAI)?.StartFlight(player);
        }

        if (creatures.SummonDeadDespawnAt(NpcGrimtotemRaider, RaiderSpawn.X, RaiderSpawn.Y, RaiderSpawn.Z, RaiderSpawn.O) is { } raider)
        {
            raider.AI?.AttackStart(player);
            if (creatures.CreaturesOfEntryInRange(player, KindalMoonweaverAI.Entry, CageDoorSearchRange)
                    .FirstOrDefault(k => k.IsAlive) is { AI: KindalMoonweaverAI kindal })
            {
                kindal.AttackRaider(raider);
            }
        }

        return true;
    }

    /// <summary>darkshore.cpp go_furbolg_food: the three Blackwood stores (scriptdev2.sql binds 175329-175331) and the warrior they draw.</summary>
    public const uint GoBlackwoodGrainStores = 175329, GoBlackwoodNutStores = 175330, GoBlackwoodFruitStores = 175331, NpcBlackwoodWarrior = 2168;
    public const float FurbolgFoodRange = 20f;
    public const uint FurbolgWarriorLifetimeMs = 120_000;

    /// <summary>
    /// GOUse_go_furbolg_food (darkshore.cpp:870-890): the nearest living Blackwood Warrior within 20 yd of the stores attacks the player if
    /// it is out of combat and can; with none in range a new one appears at a random point within 20 yd (two minutes, out-of-combat timer)
    /// and attacks. The script takes the use.
    /// </summary>
    private void FurbolgFood(Player player, GameObject go)
    {
        if (Instance.FindUpdater<CreatureMapSystem>() is not { } creatures)
        {
            return;
        }

        if (creatures.ClosestOfEntry(go, NpcBlackwoodWarrior, FurbolgFoodRange, aliveOnly: true) is { } warrior)
        {
            if (!warrior.Combat.IsInCombat && Instance.Combat.Hooks.CanAttack(warrior, player))
            {
                warrior.AI?.AttackStart(player);
            }

            return;
        }

        (float x, float y, float z) = RandomPointAround(go.X, go.Y, go.Z, FurbolgFoodRange);
        if (creatures.SummonDeadDespawnAt(NpcBlackwoodWarrior, x, y, z, 0f) is { } summoned)
        {
            creatures.MarkTimedOutOfCombatDespawn(summoned, FurbolgWarriorLifetimeMs);
            summoned.AI?.AttackStart(player);
        }
    }

    /// <summary>azshara.cpp event_arcanite_buoy: the event, Maws's spawn point and facing, and the radius that keeps a second Maws away.</summary>
    public const uint EventArcaniteBuoy = 9542;
    public const float MawsSearchRange = 200f, LightningSearchRange = 20f;
    public static readonly (float X, float Y, float Z, float O) MawsSpawn = (3561.73f, -6647.2f, -7.5f, 1.6f);

    /// <summary>
    /// ProcessEventId_arcanite_buoy (azshara.cpp:336-366): a player's buoy brings Maws (manual despawn: <see cref="MawsAI"/> goes on its own)
    /// running along its entry path, unless a Maws is already within 200 yd (then the event is not handled), and respawns a despawned Theatric
    /// Lightning within 20 yd of the player.
    /// </summary>
    public override bool OnSpellEvent(Unit caster, uint eventId)
    {
        ArgumentNullException.ThrowIfNull(caster);
        if (eventId != EventArcaniteBuoy)
        {
            return false;
        }

        if (caster is not Player player)
        {
            return true;
        }

        if (Instance.FindUpdater<CreatureMapSystem>() is not { } creatures
            || creatures.ClosestOfEntry(player, MawsAI.Entry, MawsSearchRange, aliveOnly: true) is not null)
        {
            return false;
        }

        if (creatures.SummonDeadDespawnAt(MawsAI.Entry, MawsSpawn.X, MawsSpawn.Y, MawsSpawn.Z, MawsSpawn.O) is { } maws)
        {
            creatures.SetScriptRun(maws, true);
            creatures.StartEntryWaypointPath(maws, 0);
            if (Instance.FindUpdater<GameObjectMapSystem>() is { } objects)
            {
                GameObject? lightning = null;
                float bestSq = LightningSearchRange * LightningSearchRange;
                foreach (GameObject go in objects.GameObjects)
                {
                    float dx = go.X - player.X, dy = go.Y - player.Y, dz = go.Z - player.Z, sq = (dx * dx) + (dy * dy) + (dz * dz);
                    if (go.Entry == MawsAI.GoTheatricLightning && sq <= bestSq)
                    {
                        lightning = go;
                        bestSq = sq;
                    }
                }

                if (lightning is { IsSpawned: false })
                {
                    objects.ForceRespawn(lightning);
                }
            }
        }

        return true;
    }

    /// <summary>FACTION_ESCORT_N_FRIEND_ACTIVE.</summary>
    public const uint FactionEscortNeutralFriendActive = 495;

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
