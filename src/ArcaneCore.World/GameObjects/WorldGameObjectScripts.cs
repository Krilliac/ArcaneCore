using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// go_andorhal_tower (mangos-classic world/go_scripts.cpp:39-69, registered in AddSC_go_scripts, :510-565): using a tower while All Along the Watchtowers (5097
/// Alliance, 5098 Horde) is incomplete credits the tower's kill-credit entry. It handles every use, as GOUse returns true. Quest status and
/// the credit are the world's quest service, passed in. Beacon Torch spell 17016 reaches it through effect 86 Disturb, which asks
/// <see cref="IGameObjectAi.OnUse"/> first.
/// </summary>
internal sealed class AndorhalTowerAi(Func<Player, uint, bool> questIncomplete, Action<Player, uint> credit) : IGameObjectAi
{
    public const uint QuestWatchtowersAlliance = 5097;
    public const uint QuestWatchtowersHorde = 5098;

    /// <summary>GO_ANDORHAL_TOWER_1..4 and the NPC_ANDORHAL_TOWER_1..4 each credits (go_scripts.cpp:41-52).</summary>
    public static IReadOnlyDictionary<uint, uint> Credits { get; } = new Dictionary<uint, uint>
    {
        [176094] = 10902,
        [176095] = 10903,
        [176096] = 10904,
        [176097] = 10905,
    };

    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
    }

    public bool OnUse(GameObjectMapSystem objects, GameObject go, Unit user)
    {
        if (user is Player player && Credits.TryGetValue(go.Entry, out uint creditEntry)
            && (questIncomplete(player, QuestWatchtowersAlliance) || questIncomplete(player, QuestWatchtowersHorde)))
            credit(player, creditEntry);
        return true;
    }
}

/// <summary>
/// go_dragon_head (go_scripts.cpp:358-397, registered in AddSC_go_scripts): when a head comes into the world, the nearest living herald within 30 yd of
/// it casts Rallying Cry of the Dragonslayer (22888, triggered). Deviation: cmangos runs JustSpawned on every spawn including the grid load
/// of a head (GameObject.cpp:966-967); this fires only on the not-spawned to spawned transition seen here, which is the quest-end respawn
/// the heads are made for (dbscripts_on_quest_end command 9).
/// </summary>
internal sealed class DragonHeadAi : IGameObjectAi
{
    public const uint SpellRallyingCry = 22888;
    public const float SearchRange = 30f;

    /// <summary>Head entry to herald: Onyxia H/A 179556/179558, Nefarian H/A 179881/179882 (go_scripts.cpp:358-370).</summary>
    public static IReadOnlyDictionary<uint, uint> Heralds { get; } = new Dictionary<uint, uint>
    {
        [179556] = 14392, // Overlord Runthak
        [179558] = 14394, // Major Mattingly
        [179881] = 14720, // High Overlord Saurfang
        [179882] = 14721, // Field Marshal Afrasiabi
    };

    private readonly Dictionary<ObjectGuid, bool> _spawned = [];

    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
        bool was = _spawned.GetValueOrDefault(go.Guid, go.IsSpawned);
        _spawned[go.Guid] = go.IsSpawned;
        if (was || !go.IsSpawned || !Heralds.TryGetValue(go.Entry, out uint npcEntry)
            || objects.Map.FindUpdater<CreatureMapSystem>() is not { } creatures) return;
        if (creatures.CreaturesOfEntryInRange(go, npcEntry, SearchRange).FirstOrDefault(c => c.IsAlive) is { } herald)
            creatures.CastSpell(herald, SpellRallyingCry, null, triggered: true);
    }
}

/// <summary>
/// go_unadorned_spike (go_scripts.cpp:421-445, registered in AddSC_go_scripts): when the stake becomes Activated (quest 4974's end script uses it,
/// command 13), the nearest living Thrall within 30 yd casts Warchief's Blessing (16609, triggered).
/// </summary>
internal sealed class UnadornedSpikeAi : IGameObjectAi
{
    public const uint Thrall = 4949;
    public const uint SpellWarchiefsBlessing = 16609;
    public const float SearchRange = 30f;

    private readonly Dictionary<ObjectGuid, GameObjectLootState> _states = [];

    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
        GameObjectLootState was = _states.GetValueOrDefault(go.Guid, go.LootState);
        _states[go.Guid] = go.LootState;
        if (was == go.LootState || go.LootState != GameObjectLootState.Activated
            || objects.Map.FindUpdater<CreatureMapSystem>() is not { } creatures) return;
        if (creatures.CreaturesOfEntryInRange(go, Thrall, SearchRange).FirstOrDefault(c => c.IsAlive) is { } thrall)
            creatures.CastSpell(thrall, SpellWarchiefsBlessing, null, triggered: true);
    }
}

/// <summary>
/// go_transpolyporter_bb (go_scripts.cpp:261-276, 356, AddSC_go_scripts): the Booty Bay transpolyporter trap takes only a player carrying at least
/// one Goblin Transponder (item 9173; HasItemCount does not look in the bank). The trap's own spell (11362) is cast by the engine.
/// </summary>
internal sealed class TranspolyporterAi : IGameObjectAi
{
    public const uint ItemGoblinTransponder = 9173;

    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
    }

    public bool AcceptsTrapTarget(GameObjectMapSystem objects, GameObject go, Unit candidate)
        => candidate is Player player && player.Inventory.GetItemCount(ItemGoblinTransponder) >= 1;
}

/// <summary>
/// go_containment_coffer (go_scripts.cpp:451-486, registered in AddSC_go_scripts): 2 s after it is first seen, the nearest living Rift Spawn within 5 yd
/// uses the coffer once (a button that toggles and fires its linked trap), and the script does nothing more.
/// </summary>
internal sealed class ContainmentCofferAi : IGameObjectAi
{
    public const uint RiftSpawn = 6492;
    public const float SearchRange = 5f;
    public const uint StartDelayMs = 2000;

    private readonly Dictionary<ObjectGuid, (uint TimerMs, bool Activated)> _state = [];

    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
        (uint timer, bool activated) = _state.TryGetValue(go.Guid, out var known) ? known : (StartDelayMs, false);
        if (activated) return;
        if (timer >= diffMs)
        {
            _state[go.Guid] = (timer - diffMs, false); // cmangos: m_startTimer < diff fires, else the timer runs down
            return;
        }

        if (objects.Map.FindUpdater<CreatureMapSystem>() is not { } creatures
            || creatures.CreaturesOfEntryInRange(go, RiftSpawn, SearchRange).FirstOrDefault(c => c.IsAlive) is not { } rift)
        {
            _state[go.Guid] = (timer, false);
            return;
        }

        objects.UseByUnit(rift, go);
        _state[go.Guid] = (0, true);
    }
}
