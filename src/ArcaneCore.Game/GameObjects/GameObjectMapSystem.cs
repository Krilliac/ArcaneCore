using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.SpawnGroups;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Loot;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using GridCoord = ArcaneCore.Game.Maps.Grid.GridCoord;
using GridDefines = ArcaneCore.Game.Maps.Grid.GridDefines;
using MapGrid = ArcaneCore.Game.Maps.Grid.Grid;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// The game objects of one map: database spawns bucketed by grid and created when the map
/// loads a grid, despawn/respawn timers, door and button auto-close, chest loot hand-off, and
/// per-viewer quest dynamic flags. Attached to its <see cref="Map"/> as an <see cref="IMapUpdater"/>.
/// Behaviour re-implemented from vmangos GameObject::Update/Use and ObjectGridLoader (no code copied).
/// Thread affinity: world thread only.
/// </summary>
public sealed partial class GameObjectMapSystem : IMapUpdater, IViewerFieldFilter
{
    /// <summary>INTERACTION_DISTANCE (vmangos ObjectDefines.h), the default for types without their own distance.</summary>
    public const float InteractionDistance = 5.0f;

    /// <summary>
    /// The per-type interaction distance (vmangos GameObjectDefines.h:759-785 GameObjectInfo::GetInteractionDistance):
    /// quest givers and text objects 5.55556, binders 10, chairs and fishing nodes 100, area damage 0, otherwise
    /// <see cref="InteractionDistance"/>. GameObject.cpp:2584-2609 IsAtInteractDistance compares with '&lt;='.
    /// Limit: the display-bounds oriented box test (GameObjectDisplayInfoAddon.HasBounds) needs model bounds this
    /// codebase does not load, so only the no-bounds centre-distance branch is implemented.
    /// </summary>
    public static float InteractionDistanceFor(GameObjectType type) => type switch
    {
        GameObjectType.QuestGiver or GameObjectType.Text or GameObjectType.FlagStand => 5.55556f,
        GameObjectType.Binder => 10.0f,
        GameObjectType.Chair or GameObjectType.FishingNode => 100.0f,
        GameObjectType.AreaDamage => 0.0f,
        _ => InteractionDistance,
    };

    /// <summary>How often quest dynamic flags are re-evaluated for viewers (the quest journal has no change events yet).</summary>
    public const uint QuestFlagRefreshMs = 1000;

    /// <summary>Consecutive refused Use/OpenLock attempts by the same player on the same object within <see cref="UseRefusedLoopWindowMs"/> before reporting <c>gameobject/use-refused-loop</c>.</summary>
    public const int UseRefusedLoopThreshold = 5;

    /// <summary>Time window (ms) for <c>gameobject/use-refused-loop</c> tracking.</summary>
    public const long UseRefusedLoopWindowMs = 10_000;

    private GameObjectContent _content;
    private readonly ILogger _logger;
    private readonly Dictionary<GridCoord, List<GameObjectSpawn>> _spawnsByGrid = [];
    private readonly Dictionary<GridCoord, List<GameObject>> _grids = [];
    private readonly Dictionary<uint, long> _respawnAt = [];
    private readonly Dictionary<uint, LootBag> _unloadedLoot = [];
    private readonly Dictionary<ObjectGuid, GameObject> _objects = [];
    private readonly List<GameObject> _updateScratch = [];
    private bool _updateScratchInUse;
    private readonly Dictionary<ObjectGuid, Dictionary<Player, uint>> _questFlagsSent = [];
    private readonly Dictionary<uint, uint[]> _questLootItems = [];
    private LootContent? _questLootContent; // the loot tables _questLootItems was computed from (a loot reload replaces them)
    private readonly HashSet<uint> _warnedMissingTemplates = [];
    private readonly Dictionary<ObjectGuid, long> _despawnAt = [];
    private readonly List<ObjectGuid> _despawnScratch = [];
    private long _nextDespawnAtMs = long.MaxValue;
    private readonly Dictionary<uint, uint> _spawnEntries = [];
    private readonly Dictionary<GameObjectType, Func<Player, GameObject, GameObjectUseResult>> _useHandlers = [];
    private readonly Dictionary<(ObjectGuid Player, ObjectGuid Go), (long WindowStartMs, int Count, bool Reported)> _refusedUseAttempts = [];
    private readonly HashSet<ObjectGuid> _reportedDoorCollisionMissing = [];
    private readonly HashSet<ObjectGuid> _reportedDoorStateDesync = [];
    private CreatureMapSystem? _subscribedCreatures;
    private long _clockMs;
    private long _nextQuestRefreshMs;
    private uint _nextTemporaryCounter;

    /// <summary>How many objects needed per-object work in the last <see cref="Update"/> (diagnostics, tests; world thread).</summary>
    internal int LastUpdatedObjectCount { get; private set; }

    /// <summary>How many quest objects currently hold per-viewer dynamic flag baselines in <c>_questFlagsSent</c> (diagnostics, tests).</summary>
    internal int QuestFlagsTrackedObjectCount => _questFlagsSent.Count;

    public GameObjectMapSystem(Map map, GameObjectContent content, LootService? loot = null, ILootQuestJournal? quests = null, ILogger? logger = null, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(content);
        Map = map;
        if (random is not null)
        {
            Random = random; // before InitializePools, which hands it to the pool state
        }

        _content = content;
        Loot = loot;
        Quests = quests;
        _logger = logger ?? NullLogger.Instance;

        uint maxGuid = 0;
        foreach (GameObjectSpawn spawn in content.GetSpawns(map.MapId))
        {
            Invariant.Check(
                float.IsFinite(spawn.X) && float.IsFinite(spawn.Y) && float.IsFinite(spawn.Z) && float.IsFinite(spawn.Orientation),
                "gameobject-spawn-finite-coords",
                $"GameObjectSpawn {spawn.Guid} on map {map.MapId} has non-finite coordinates ({spawn.X}, {spawn.Y}, {spawn.Z}, {spawn.Orientation})");
            GridCoord grid = GridDefines.ComputeGridCoord(spawn.X, spawn.Y);
            if (!_spawnsByGrid.TryGetValue(grid, out List<GameObjectSpawn>? list))
            {
                _spawnsByGrid[grid] = list = [];
            }

            list.Add(spawn);
            maxGuid = Math.Max(maxGuid, spawn.Guid);
            _spawnEntries[spawn.Guid] = spawn.Entry;
        }

        _nextTemporaryCounter = maxGuid + 1;
        InitializePools();
        InitializeSpawnGroups();
        if (loot is not null)
        {
            loot.Objects = this;
        }

        EnsureCreatureLifecycleSubscribed();
        Map.Grids.GridLoaded += grid => LoadGrid(new GridCoord(grid.Coord.X, grid.Coord.Y));
        Map.Grids.GridUnloading += grid => UnloadGrid(new GridCoord(grid.Coord.X, grid.Coord.Y));
        foreach (MapGrid grid in Map.Grids.LoadedGrids.ToArray())
        {
            if (grid.ObjectDataLoaded)
            {
                LoadGrid(new GridCoord(grid.Coord.X, grid.Coord.Y));
            }
        }
    }

    /// <summary>
    /// Drop any loot bag held for a creature whose corpse decayed, respawned or left the map, closing any open loot window
    /// so decayed corpses do not accumulate in <see cref="LootService"/> until the next kill's stale-bag prune. World thread.
    /// </summary>
    public void ForgetCreatureLoot(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        Loot?.ForgetLoot(creature);
    }

    /// <summary>Subscribe to <paramref name="creatures"/>'s corpse-removal and respawn callbacks once so corpse loot is forgotten at decay/respawn.</summary>
    public void SubscribeCreatureLifecycle(CreatureMapSystem creatures)
    {
        ArgumentNullException.ThrowIfNull(creatures);
        if (ReferenceEquals(_subscribedCreatures, creatures))
        {
            return;
        }

        if (_subscribedCreatures is not null)
        {
            _subscribedCreatures.CorpseRemoving -= ForgetCreatureLoot;
            _subscribedCreatures.Respawning -= ForgetCreatureLoot;
        }

        _subscribedCreatures = creatures;
        creatures.CorpseRemoving += ForgetCreatureLoot;
        creatures.Respawning += ForgetCreatureLoot;
    }

    private void EnsureCreatureLifecycleSubscribed()
    {
        if (_subscribedCreatures is null && Map.FindUpdater<CreatureMapSystem>() is { } creatures)
        {
            SubscribeCreatureLifecycle(creatures);
        }
    }

    /// <summary>
    /// Use reloaded content (live reload, world thread): templates and locks looked up from now on come from it, and every object
    /// this system tracks is rebound to the new template of its entry (an object whose entry the content no longer lists keeps the one it has).
    /// The spawns are not reloaded, so the grids and respawn bookkeeping stay as they are.
    /// </summary>
    public void ReplaceContent(GameObjectContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _content = content;
        foreach (GameObject go in _objects.Values)
        {
            if (content.FindTemplate(go.Entry) is { } template)
            {
                go.ReplaceTemplate(template);
                RefreshQuestFilter(go);
            }
        }
    }

    /// <summary>
    /// Re-check which chests are quest related after the loot tables were replaced (live reload, world thread): a chest that gained or
    /// lost a quest item row starts or stops sparkling for its viewers.
    /// </summary>
    public void RefreshChestQuestFilters()
    {
        foreach (GameObject go in _objects.Values)
        {
            if ((GameObjectType)go.Template.Type == GameObjectType.Chest)
            {
                RefreshQuestFilter(go);
            }
        }
    }

    public Map Map { get; }

    public LootService? Loot { get; }

    public ILootQuestJournal? Quests { get; }

    public IGameObjectQuestGiver? QuestGiver { get; set; }

    /// <summary>Skill values for lock checks; defaults to the inventory's requirements seam (vmangos GetSkillValue).</summary>
    public Func<Player, uint, uint> SkillValue { get; set; } = static (player, skill) => player.Inventory.Requirements.SkillValue(player.Inventory, skill);

    /// <summary>
    /// Takes a mounted user off the mount before a use of an object that does not allow mounted use (vmangos GameObject::Use,
    /// GameObject.cpp:1414-1415: <c>RemoveSpellsCausingAura(SPELL_AURA_MOUNTED)</c>). The world wires it to the spell system; null (tests
    /// without spells) leaves the user mounted.
    /// </summary>
    public Action<Player>? Dismount { get; set; }

    /// <summary>
    /// Drops auras whose AuraInterruptFlags include <c>AURA_INTERRUPT_LOOTING_CANCELS</c> (0x800) before a direct use
    /// (vmangos <c>WorldSession::HandleGameObjectUseOpcode</c>, SpellHandler.cpp:241-258: once the object is spawned, not generic,
    /// interactable and in reach, and <c>PlayerCanUse</c>, <c>RemoveAurasWithInterruptFlags</c> then <c>GameObject::Use</c>). Stealth,
    /// Vanish and Shadowmeld carry this bit. The world wires it to the spell system; null (tests without spells) leaves the auras.
    /// </summary>
    public Action<Player>? BreakLootingAuras { get; set; }

    /// <summary>Raised after a successful use (scripts, events, the spells area's linked traps/spells).</summary>
    public event Action<Player, GameObject>? Used;

    /// <summary>
    /// Objects of this map that a direct use (CMSG_GAMEOBJ_USE, <see cref="Use"/>) may not touch, only an opening (<see cref="OpenLock"/>): a
    /// battleground sets it for its banners. True refuses the use as <see cref="GameObjectUseResult.Locked"/> before anything else happens.
    /// </summary>
    public Func<Player, GameObject, bool>? DirectUseRefused { get; set; }

    public long ClockMs => _clockMs;

    /// <summary>
    /// The whole-second clock of the retail door/goober timers (vmangos reads <c>time(nullptr)</c>, GameObject.cpp:572-590):
    /// an auto-close of R seconds resets at the first whole second after use + R, so the observed delay is in (R, R+1].
    /// </summary>
    public long ClockSeconds => _clockMs / 1000;

    /// <summary>Behaviour switches (section <c>GameObjects</c>); defaults are retail.</summary>
    public GameObjectOptions Options { get; set; } = new();

    /// <summary>The random source of respawn delays (injectable for tests).</summary>
    public Random Random { get; set; } = Random.Shared;

    public int LoadedGridCount => _grids.Count;

    /// <summary>Every tracked object: spawned ones and despawned ones waiting to respawn.</summary>
    public IReadOnlyCollection<GameObject> GameObjects => _objects.Values;

    public GameObject? Find(ObjectGuid guid) => _objects.GetValueOrDefault(guid);

    /// <summary>The respawn time (on <see cref="ClockMs"/>) kept for a spawn whose grid unloaded while it was despawned.</summary>
    public long? PendingRespawnAt(uint spawnGuid) => _respawnAt.TryGetValue(spawnGuid, out long at) ? at : null;

    // --- IMapUpdater -------------------------------------------------------------------------

    public void Update(Map map, uint diffMs)
    {
        if (!ReferenceEquals(map, Map))
        {
            throw new InvalidOperationException($"game object system of map {Map.MapId} updated by map {map.MapId}");
        }

        _clockMs += diffMs;
        // World:Parallel:Ungated=GameObjectMapSystem: only the quiet-object scan (ScanObjects: reads of this map's own objects) runs outside
        // the world's serial gate; scripts, respawns, despawns, traps and spawn groups reach shared services and hold it. Outside the parallel
        // maps phase, or when the map gates this updater, Serial() is a no-op or a reentrant entry.
        bool reentrant = _updateScratchInUse;
        List<GameObject> walk;
        using (Serial())
        {
            EnsureCreatureLifecycleSubscribed();
            UpdateAis(diffMs);
            UpdateElevators();
            // The walk's snapshot (respawns and despawns change the map while it runs): the object list, rebuilt only when the object set
            // changed (same order as _objects), so no copy of every object every tick. A reentrant update walks a copy of its own.
            walk = reentrant ? new List<GameObject>(_objects.Values) : WalkList();
            if (walk.Count == 0)
            {
                LastUpdatedObjectCount = 0;
                UpdateRest();
                return;
            }
        }

        _updateScratchInUse = true;
        try
        {
            int[] needing = reentrant ? new int[walk.Count] : NeedingBuffer(walk.Count);
            int count = ScanObjects(walk, needing);
            using (Serial())
            {
                LastUpdatedObjectCount = count;
                UpdateObjects(walk, needing, count);
                UpdateRest();
            }
        }
        finally
        {
            if (!reentrant)
            {
                _updateScratchInUse = false;
            }
        }
    }

    private void UpdateRest()
    {
        UpdateSpawnGroups();
        if (_despawnAt.Count > 0 && _clockMs >= _nextDespawnAtMs)
        {
            long next = long.MaxValue;
            _despawnScratch.Clear();
            foreach ((ObjectGuid guid, long at) in _despawnAt)
            {
                if (at <= _clockMs)
                {
                    _despawnScratch.Add(guid);
                }
                else if (at < next)
                {
                    next = at;
                }
            }

            _nextDespawnAtMs = next;
            for (int i = 0; i < _despawnScratch.Count; i++)
            {
                ObjectGuid guid = _despawnScratch[i];
                try
                {
                    if (_objects.TryGetValue(guid, out GameObject? expired))
                    {
                        Remove(expired);
                    }
                    else
                    {
                        _despawnAt.Remove(guid);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("map.gameobject", ex);
                }
            }

            _despawnScratch.Clear();
        }

        if (_clockMs >= _nextQuestRefreshMs)
        {
            _nextQuestRefreshMs = _clockMs + QuestFlagRefreshMs;
            RefreshQuestFlags();
        }
    }

    /// <summary>The world's serial gate (a no-op outside the parallel maps phase, reentrant when the map already gates this updater).</summary>
    private WorldRuntime.SerialScope Serial() => Map.EnterSerialAndFlush();

    private bool _walkListStale = true;
    private bool _transportsStale = true;
    private readonly List<GameObject> _transports = [];
    private readonly List<Player> _trapPlayersScratch = [];
    private int[] _needing = [];

    /// <summary>Every tracked object in <c>_objects</c> order, rebuilt when the object set changed (only by a non-reentrant update).</summary>
    private List<GameObject> WalkList()
    {
        if (_walkListStale)
        {
            _updateScratch.Clear();
            _updateScratch.AddRange(_objects.Values);
            _walkListStale = false;
        }

        return _updateScratch;
    }

    private int[] NeedingBuffer(int count)
        => _needing.Length >= count ? _needing : _needing = new int[Math.Max(count, _needing.Length * 2)];

    /// <summary>The transports among the tracked objects in <c>_objects</c> order, rebuilt when the object set changed.</summary>
    private List<GameObject> Transports()
    {
        if (_transportsStale)
        {
            _transports.Clear();
            foreach (GameObject go in _objects.Values)
            {
                if (go.Type == GameObjectType.Transport)
                {
                    _transports.Add(go);
                }
            }

            _transportsStale = false;
        }

        return _transports;
    }

    /// <summary>
    /// The indexes in <paramref name="walk"/> of the objects this update has work for (<see cref="UpdateObjects"/>), into <paramref name="needing"/>;
    /// the count. A quiet object (spawned, no loot reset, activation or restock due, not a trap, spell caster or door with pending work) would only
    /// be read by the walk. Reads this map's own objects only, so it may run outside the serial gate. An object another object's work changes later
    /// in the same walk is seen on the next update.
    /// </summary>
    private int ScanObjects(List<GameObject> walk, int[] needing)
    {
        long clockSeconds = ClockSeconds;
        int playerCount = Map.PlayerCount;
        int count = 0;
        for (int i = 0; i < walk.Count; i++)
        {
            GameObject go = walk[i];
            bool needs;
            if (!go.IsSpawned)
            {
                needs = go.RespawnAtMs > 0 && go.RespawnAtMs <= _clockMs;
            }
            else
            {
                GameObjectLootState loot = go.LootState;
                GameObjectType type = go.Type;
                needs = loot == GameObjectLootState.JustDeactivated
                    || (go.ResetAfterSecond is { } resetAfter && resetAfter < clockSeconds)
                    || (type == GameObjectType.Chest && loot == GameObjectLootState.NotReady && go.RestockAfterSecond is { } after && after <= clockSeconds)
                    || (type == GameObjectType.Door && NeedsDoorAudit(go))
                    || (type == GameObjectType.SpellCaster && loot == GameObjectLootState.Ready
                        && go.Template.Charges() is var scCharges and not 0 && go.UseCount >= scCharges)
                    || (type == GameObjectType.Trap && NeedsTrapUpdate(go, loot, playerCount));
            }

            if (needs)
            {
                needing[count++] = i;
            }
        }

        return count;
    }

    /// <summary>
    /// Whether a spawned door has an unreported <c>door/collision-missing</c> or <c>door/state-desync</c> anomaly, or has recovered from one
    /// that was previously reported and must clear its reported set in <see cref="AuditDoorObject"/>.
    /// </summary>
    private bool NeedsDoorAudit(GameObject go)
    {
        bool closed = go.State == GameObjectState.Ready;
        bool missingCollision = closed && (
            go.Template.DisplayId == 0
            || !float.IsFinite(go.Template.Size)
            || go.Template.Size <= 0f);
        if (missingCollision
            ? !_reportedDoorCollisionMissing.Contains(go.Guid)
            : _reportedDoorCollisionMissing.Count > 0 && _reportedDoorCollisionMissing.Contains(go.Guid))
        {
            return true;
        }

        byte spawnState = go.Spawn?.State ?? (byte)GameObjectState.Ready;
        bool desync = go.LootState == GameObjectLootState.Activated
            && closed
            && (go.Flags & GameObjectFlags.InUse) == 0
            && spawnState == (byte)GameObjectState.Ready;
        return desync
            ? !_reportedDoorStateDesync.Contains(go.Guid)
            : _reportedDoorStateDesync.Count > 0 && _reportedDoorStateDesync.Contains(go.Guid);
    }

    private bool NeedsTrapUpdate(GameObject go, GameObjectLootState loot, int playerCount)
    {
        uint charges = go.Template.Charges();
        if (!go.OwnerGuid.IsEmpty && charges != 0)
        {
            return false; // hunter trap: owned by TrapSystem
        }

        if (!go.TrapArmed)
        {
            return true;
        }

        if (loot != GameObjectLootState.Ready)
        {
            return false;
        }

        if (charges != 0 && go.UseCount >= charges)
        {
            return true;
        }

        return playerCount > 0
            && go.CooldownUntilMs < _clockMs
            && Ranged.TrapRules.TriggerRadius(go.Template, Ranged.TrapRadiusSource.Vmangos) > 0f;
    }

    private void UpdateObjects(List<GameObject> walk, int[] needing, int count)
    {
        for (int n = 0; n < count; n++)
        {
            GameObject go = walk[needing[n]];
            try
            {
                if (!go.IsSpawned)
                {
                    if (go.RespawnAtMs > 0 && go.RespawnAtMs <= _clockMs)
                    {
                        Respawn(go);
                    }

                    continue;
                }

                if (go.LootState == GameObjectLootState.JustDeactivated)
                {
                    if (!TryStartRestock(go))
                    {
                        Despawn(go);
                    }

                    continue;
                }

                if (go.ResetAfterSecond is { } resetAfter && resetAfter < ClockSeconds)
                {
                    ActivationExpired(go);
                }

                UpdateRestock(go);
                UpdateTypeBehaviour(go);
                if (go.Type == GameObjectType.Door)
                {
                    AuditDoorObject(go);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("map.gameobject", ex);
            }
        }
    }

    /// <summary>
    /// Audits a door gameobject for missing closed-door collision (<c>door/collision-missing</c>) and
    /// state mismatch between its activation/encounter state and its visual/collision state (<c>door/state-desync</c>).
    /// </summary>
    public void AuditDoorObject(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        if (go.Type != GameObjectType.Door || !go.IsSpawned)
        {
            return;
        }

        bool closed = go.State == GameObjectState.Ready;
        bool missingCollision = closed && (
            go.Template.DisplayId == 0
            || !float.IsFinite(go.Template.Size)
            || go.Template.Size <= 0f);
        if (missingCollision)
        {
            if (_reportedDoorCollisionMissing.Add(go.Guid))
            {
                AnomalyLedger.Shared.Report(
                    "door",
                    "collision-missing",
                    $"go {go.Guid} entry {go.Entry} map {Map.MapId}",
                    $"Closed door (State={go.State}, DisplayId={go.Template.DisplayId}, Size={go.Template.Size}) has no active collision model.", AnomalySeverity.Warning);
            }
        }
        else if (_reportedDoorCollisionMissing.Count > 0)
        {
            _reportedDoorCollisionMissing.Remove(go.Guid);
        }

        string? desyncDetail = null;
        byte spawnState = go.Spawn?.State ?? (byte)GameObjectState.Ready;
        if (go.LootState == GameObjectLootState.Activated
            && go.State == GameObjectState.Ready
            && (go.Flags & GameObjectFlags.InUse) == 0 // every real activation (use, toggle) sets InUse; a script that opens a door and a later toggle that closes it leave Activated+Ready legitimately
            && spawnState == (byte)GameObjectState.Ready)
        {
            desyncDetail = $"Door has LootState=Activated from Ready spawn state but visual/collision State is still Ready.";
        }

        if (desyncDetail is not null)
        {
            if (_reportedDoorStateDesync.Add(go.Guid))
            {
                AnomalyLedger.Shared.Report(
                    "door",
                    "state-desync",
                    $"go {go.Guid} entry {go.Entry} map {Map.MapId}",
                    desyncDetail, AnomalySeverity.Warning);
            }
        }
        else if (_reportedDoorStateDesync.Count > 0)
        {
            _reportedDoorStateDesync.Remove(go.Guid);
        }
    }

    public void OnPlayerRemoved(Map map, Player player)
    {
        OnRitualParticipantLeft(player);
        Loot?.OnPlayerLeft(player);
        if (_questFlagsSent.Count > 0 && player.Session.WantsUpdateObjects)
        {
            foreach (Dictionary<Player, uint> sent in _questFlagsSent.Values)
            {
                sent.Remove(player);
            }
        }
    }

    // --- use --------------------------------------------------------------------------------

    /// <summary>
    /// CMSG_GAMEOBJ_USE (vmangos HandleGameObjectUseOpcode → GameObject::Use): the object must be
    /// spawned in the player's map, interactable, within INTERACTION_DISTANCE, and the player
    /// alive. Then by type: doors and buttons toggle (auto-closing after data2 ms); chests check
    /// their lock and quest gate and open their loot; goobers check their quest, give quest
    /// credit, show page text / custom animation; text objects show their page; quest givers go to
    /// <see cref="QuestGiver"/>; mailboxes answer Ok (the mail area opens the UI).
    /// </summary>
    public GameObjectUseResult Use(Player player, ObjectGuid guid)
    {
        ArgumentNullException.ThrowIfNull(player);
        GameObject? go = _objects.GetValueOrDefault(guid);
        GameObjectUseResult result = UseCore(player, guid, go);
        RecordUseAttemptOutcome(player, guid, go, result);
        return result;
    }

    private GameObjectUseResult UseCore(Player player, ObjectGuid guid, GameObject? go)
    {
        GameObjectUseResult result = CheckUsable(player, go);
        if (result != GameObjectUseResult.Ok || go is null)
        {
            return result;
        }

        if (DirectUseRefused?.Invoke(player, go) == true)
        {
            return GameObjectUseResult.Locked;
        }

        // HandleGameObjectUseOpcode (SpellHandler.cpp:241-258) strips AURA_INTERRUPT_LOOTING_CANCELS before GameObject::Use, once the
        // object is spawned, not generic, interactable and in reach. A cast aimed at the object already does this (Spell::prepare); a
        // click did not, so Stealth survived an unlocked chest or a door. CheckUsable is that gate here (it also refuses a dead user,
        // which the opcode does not). A use that Use then refuses (lock, quest, immunity) has already broken the auras.
        if (go.Type != GameObjectType.Generic)
        {
            BreakLootingAuras?.Invoke(player);
        }

        // GameObject::Use (GameObject.cpp:1405-1407): the object's script may take the use over, before anything else (and then nothing
        // else happens: no use event either).
        if (AiOf(go)?.OnUse(this, go, player) == true)
        {
            return GameObjectUseResult.Ok;
        }

        // GameObject::Use (GameObject.cpp:1409-1416): an immune user is ignored by objects that cannot be used under immunity,
        // and a mounted user is taken off the mount unless the object allows mounted use.
        if (IsRefusedForImmunity(player, go))
        {
            return GameObjectUseResult.Immune;
        }

        if (!go.Template.IsUsableMounted() && MountService.IsMounted(player))
        {
            Dismount?.Invoke(player);
        }

        // cmangos GameObject::Use runs the ScriptDev2 GOUse script after the mount check; a locked object reaches it only once its lock
        // is open (the direct-use lock here, the open-lock spell in OpenLock).
        bool scriptTookUse = CheckDirectLock(player, go) == GameObjectUseResult.Ok && InstanceScriptTookUse(player, go);

        // An area owns a type (fishing bobbers: ArcaneCore.Game.Fishing) and registers its handler instead of editing this switch.
        result = _useHandlers.TryGetValue(go.Type, out Func<Player, GameObject, GameObjectUseResult>? useHandler) ? useHandler(player, go) : go.Type switch
        {
            GameObjectType.Door or GameObjectType.Button => UseDoorOrButton(player, go),
            GameObjectType.Chest => UseChest(player, go),
            GameObjectType.Goober => UseGoober(player, go, scriptTookUse: scriptTookUse),
            GameObjectType.Text => UseText(player, go),
            GameObjectType.Chair => UseChair(player, go),
            GameObjectType.Camera => UseCamera(player, go),
            GameObjectType.QuestGiver => QuestGiver is { } giver && giver.OpenQuestMenu(player, go) ? GameObjectUseResult.Ok : GameObjectUseResult.Unsupported,
            GameObjectType.Mailbox => GameObjectUseResult.Ok,
            GameObjectType.SpellCaster => UseSpellCaster(player, go),
            GameObjectType.SummoningRitual => UseRitual(player, go),
            GameObjectType.FlagStand => UseFlagStand(player, go),
            GameObjectType.AreaDamage => UseAreaDamage(player, go),
            GameObjectType.SpellFocus => UseSpellFocus(player, go),
            // A meeting stone is used through CMSG_MEETINGSTONE_JOIN, never through this opcode (GameObject.cpp:1836-1841).
            GameObjectType.Generic or GameObjectType.Trap or GameObjectType.Binder or GameObjectType.MeetingStone
                or GameObjectType.MapObject or GameObjectType.AuctionHouse or GameObjectType.GuardPost
                or GameObjectType.Transport or GameObjectType.MoTransport or GameObjectType.DuelArbiter => GameObjectUseResult.NotUsable,
            _ => GameObjectUseResult.Unsupported,
        };

        if (result == GameObjectUseResult.Ok)
        {
            Map.FindUpdater<Instances.Scripts.InstanceData>()?.OnObjectUsed(player, go);
            Used?.Invoke(player, go);
        }

        return result;
    }

    /// <summary>
    /// Take over the use of every object of <paramref name="type"/> (after the common checks: spawned, same map, alive, interactable,
    /// in reach). A later registration replaces the earlier one. The handler's result is the use result; <see cref="Used"/> fires on Ok.
    /// </summary>
    public void RegisterUseHandler(GameObjectType type, Func<Player, GameObject, GameObjectUseResult> handler)
        => _useHandlers[type] = handler ?? throw new ArgumentNullException(nameof(handler));

    /// <summary>The Lock.dbc entry <paramref name="lockId"/>, or null (lockable items ask the object system, which owns the lock content).</summary>
    public LockEntry? FindLock(uint lockId) => _content.FindLock(lockId);

    /// <summary>The template of <paramref name="entry"/>, or null (runtime summons of spell effects check the object type with it).</summary>
    public GameObjectTemplate? FindTemplate(uint entry) => _content.FindTemplate(entry);

    /// <summary>
    /// The spells area's open-lock effect (herb gathering, mining, lockpicking, opening with a
    /// key): <paramref name="lockType"/> is the effect's misc value, <paramref name="keyItemId"/>
    /// the casting item. On success a chest opens its loot and a door/button activates. The cast
    /// time and skill-ups belong to the spell (not done here): <paramref name="onChestOpened"/> runs
    /// with the chest once its loot window really opened, which for a dungeon chest is only after
    /// its generation committed (later than this call returning <see cref="GameObjectUseResult.Ok"/>),
    /// and at once when the chest's script takes the open over (vmangos Spell::EffectOpenLock rolls the
    /// gathering skill after SendLoot whatever the object did with it, SpellEffects.cpp:2163-2207).
    /// <paramref name="onScriptTookOpen"/> runs (after <paramref name="onChestOpened"/>) only in that script case, so a caller can tell an
    /// <see cref="GameObjectUseResult.Ok"/> that left no loot window on purpose from one that did not.
    /// </summary>
    public GameObjectUseResult OpenLock(Player player, ObjectGuid guid, LockType lockType, uint keyItemId = 0, uint skillBonus = 0,
        Action<GameObject>? onChestOpened = null, Action<GameObject>? onScriptTookOpen = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        GameObject? go = _objects.GetValueOrDefault(guid);
        GameObjectUseResult result = OpenLockCore(player, go, lockType, keyItemId, skillBonus, onChestOpened, onScriptTookOpen);
        RecordUseAttemptOutcome(player, guid, go, result);
        return result;
    }

    private GameObjectUseResult OpenLockCore(Player player, GameObject? go, LockType lockType, uint keyItemId, uint skillBonus,
        Action<GameObject>? onChestOpened, Action<GameObject>? onScriptTookOpen)
    {
        GameObjectUseResult result = CheckUsable(player, go);
        if (result != GameObjectUseResult.Ok || go is null)
        {
            return result;
        }

        // Spell::EffectOpenLock (SpellEffects.cpp:2117-2118) returns before opening (and before any skill-up) for an immune caster.
        if (IsRefusedForImmunity(player, go))
        {
            return GameObjectUseResult.Immune;
        }

        uint lockId = GameObjectLocks.LockIdOf(go.Template);
        LockEntry? entry = _content.FindLock(lockId);
        result = lockId != 0 && entry is null ? GameObjectUseResult.Locked
            : GameObjectLocks.CheckOpenLock(entry, player, lockType, keyItemId, SkillValue, skillBonus);
        if (result != GameObjectUseResult.Ok)
        {
            return result;
        }

        // mangos-classic Spell::SendLoot (SpellEffects.cpp:2147-2158): a trap opened by an "Attacking" lock (the Maraudon larva spewer, lock
        // 99) is only activated - no GameObject::Use, no use event. Its activation is what the object's script watches. A disarm or a
        // lockless trap stays refused here as before.
        if (go.Type == GameObjectType.Trap)
        {
            if (lockType != LockType.OpenAttacking)
            {
                return GameObjectUseResult.NotUsable;
            }

            go.LootState = GameObjectLootState.Activated;
            return GameObjectUseResult.Ok;
        }

        // A script whose plain click is refused but whose validated opening acts (a Molten Core rune) answers OnUnlockedUse first.
        if (AiOf(go)?.OnUnlockedUse(this, go, player) == true)
        {
            return ScriptTookOpen(go, onChestOpened, onScriptTookOpen);
        }

        // Spell::SendLoot hands the other types to GameObject::Use (mangos-classic GameObject.cpp:1488-1493, vmangos :1405-1407), so the
        // object's script runs on the spell path too - as for CMSG_GAMEOBJ_USE in Use: a script that takes the use over ends it.
        if (AiOf(go)?.OnUse(this, go, player) == true)
        {
            return ScriptTookOpen(go, onChestOpened, onScriptTookOpen);
        }

        // Spell::SendLoot (SpellEffects.cpp:2048-2068) hands a door, button, spell focus, goober or chest to GameObject::Use, whose button and
        // chest branches spring the linked trap (GameObject.cpp:1441-1455, 1472-1479) - before the chest loot, whatever the quest gate says.
        if (go.Type is GameObjectType.Chest or GameObjectType.Button)
        {
            TriggerLinkedTrap(go, player);
        }

        // GameObject::Use, reached through Spell::SendLoot, runs the ScriptDev2 GOUse script of the opened object.
        bool scriptTookUse = (go.Type is GameObjectType.Chest or GameObjectType.Door or GameObjectType.Button or GameObjectType.SpellFocus
            or GameObjectType.Goober) && InstanceScriptTookUse(player, go);

        result = go.Type switch
        {
            // The chest quest gate of UseChest holds for the spell path too: a gathering node tied to a quest opens only for that quest.
            // Spell::SendLoot hands the chest to GameObject::Use too, so a key or lockpick opening starts the chest event as the click does.
            GameObjectType.Chest => ChestQuestAllows(player, go) ? OpenChestStartingEvent(player, go, onChestOpened) : GameObjectUseResult.NeedsQuest,
            GameObjectType.Door or GameObjectType.Button => ActivateDoorOrButton(go, go.Template.AutoCloseSeconds()),
            GameObjectType.SpellFocus => UseSpellFocus(player, go),
            GameObjectType.Goober => UseGoober(player, go, lockChecked: true, scriptTookUse: scriptTookUse),
            _ => GameObjectUseResult.NotUsable,
        };
        if (result == GameObjectUseResult.Ok)
        {
            // The instance's object scripts (ScriptDev2 GOUse_* and goober event ids) hear a lock opened by a spell as they hear a use.
            Map.FindUpdater<Instances.Scripts.InstanceData>()?.OnObjectUsed(player, go);
            Used?.Invoke(player, go);
        }

        return result;
    }

    internal const int MaxTrackedRefusedUseAttempts = 1024;

    internal int TrackedRefusedUseAttempts => _refusedUseAttempts.Count;

    private void PruneRefusedUseAttempts(long nowMs)
    {
        List<(ObjectGuid Player, ObjectGuid Go)>? expired = null;
        foreach (KeyValuePair<(ObjectGuid Player, ObjectGuid Go), (long WindowStartMs, int Count, bool Reported)> entry in _refusedUseAttempts)
        {
            if (nowMs - entry.Value.WindowStartMs > UseRefusedLoopWindowMs)
            {
                (expired ??= []).Add(entry.Key);
            }
        }

        if (expired is not null)
        {
            foreach ((ObjectGuid Player, ObjectGuid Go) key in expired)
            {
                _refusedUseAttempts.Remove(key);
            }
        }

        if (_refusedUseAttempts.Count >= MaxTrackedRefusedUseAttempts)
        {
            _refusedUseAttempts.Clear(); // still full of live episodes: a hard bound beats unbounded growth
        }
    }

    internal void RecordUseAttemptOutcome(Player player, ObjectGuid guid, GameObject? go, GameObjectUseResult result)
    {
        var key = (player.Guid, guid);
        if (result == GameObjectUseResult.Ok)
        {
            _refusedUseAttempts.Remove(key);
            return;
        }

        if (result is GameObjectUseResult.Unsupported or GameObjectUseResult.NotUsable or GameObjectUseResult.Locked)
        {
            ReportUseDefect(player, guid, go, result);
        }

        long nowMs = _clockMs;
        if (_refusedUseAttempts.Count >= MaxTrackedRefusedUseAttempts)
        {
            // Bound the table: drop every episode whose window has already lapsed (they would restart at count 1 anyway).
            PruneRefusedUseAttempts(nowMs);
        }

        if (!_refusedUseAttempts.TryGetValue(key, out (long WindowStartMs, int Count, bool Reported) state)
            || nowMs - state.WindowStartMs > UseRefusedLoopWindowMs)
        {
            state = (nowMs, 1, false);
        }
        else
        {
            state.Count++;
        }

        if (state.Count >= UseRefusedLoopThreshold && !state.Reported)
        {
            state.Reported = true;
            string entryLabel = go is not null ? $"entry {go.Entry} " : string.Empty;
            AnomalyLedger.Shared.Report(
                "gameobject",
                "use-refused-loop",
                $"player {player.Guid} go {guid} {entryLabel}map {Map.MapId}",
                $"GameObject use refused {state.Count} times within {UseRefusedLoopWindowMs} ms (last reason={result}).", AnomalySeverity.Warning);
        }

        _refusedUseAttempts[key] = state;
    }

    /// <summary>Anomaly code of a use refused for a reason the content or the server is at fault for, not the player.</summary>
    public const string UseFailedCode = "use-failed";

    /// <summary>Anomaly code of a use refused because the object's lock id has no lock_template row (a content defect).</summary>
    public const string UseFailedUnknownLockCode = "use-failed-unknown-lock";

    /// <summary>
    /// Report a refusal that is a defect rather than a player mistake: an object type the server cannot use at all (Unsupported), a
    /// chest/door/button/goober refused although it is not flagged non-interactive or in use (NotUsable), or a lock id with no
    /// lock_template row (Locked, reported under <see cref="UseFailedUnknownLockCode"/>). TooFar, Dead, NeedsQuest, MissingKey, SkillTooLow, InUse, LevelTooLow, Immune, NotFound and OnCooldown are the
    /// player's doing and never reach here. A QuestGiver refusal (Unsupported) is reported only when the quest giver seam is null: a wired seam may refuse transiently (a settlement pending). Strings are built only when a report is due; ids and guid low only, no names.
    /// </summary>
    private void ReportUseDefect(Player player, ObjectGuid guid, GameObject? go, GameObjectUseResult result)
    {
        if (go is null)
        {
            return;
        }

        string why;
        string code = UseFailedCode;
        switch (result)
        {
            case GameObjectUseResult.Unsupported when go.Type == GameObjectType.QuestGiver:
                if (QuestGiver is not null)
                {
                    return; // the seam is wired: a refusal may be transient (a settlement pending) or the player's, not a defect
                }

                why = "no quest giver service is wired";
                break;
            case GameObjectUseResult.Unsupported:
                why = "no handler for the object's type or its loot is unavailable";
                break;
            case GameObjectUseResult.NotUsable
                when go.Type is GameObjectType.Chest or GameObjectType.Door or GameObjectType.Button or GameObjectType.Goober
                    && ((uint)go.Flags & 0x11u) == 0 && go.LootState != GameObjectLootState.NotReady:
                why = "an interactable object is not flagged unusable yet refuses the use";
                break;
            case GameObjectUseResult.Locked when GameObjectLocks.LockIdOf(go.Template) is var lockId and not 0 && _content.FindLock(lockId) is null:
                why = $"lock {lockId} has no lock_template row";
                code = UseFailedUnknownLockCode;
                break;
            default:
                return;
        }

        AnomalyLedger.Shared.Report(
            "gameobject",
            code,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"gameobject entry {go.Entry} type {go.Template.Type}"),
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"use refused ({result}): {why}; spawn {go.Spawn?.Guid.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} guid {guid.Low} map {Map.MapId} at {go.X:0.##} {go.Y:0.##} {go.Z:0.##}; player guid {player.Guid.Low}"),
            AnomalySeverity.Warning);
    }

    /// <summary>
    /// An interactable object of <paramref name="type"/> near the player (vmangos
    /// GetGameObjectIfCanInteractWith) — the mail and auction areas validate their mailbox /
    /// auctioneer object through this.
    /// </summary>
    public GameObject? FindInteractable(Player player, ObjectGuid guid, GameObjectType type)
    {
        ArgumentNullException.ThrowIfNull(player);
        GameObject? go = _objects.GetValueOrDefault(guid);
        return go is not null && go.Type == type && CheckUsable(player, go) == GameObjectUseResult.Ok ? go : null;
    }

    /// <summary>
    /// vmangos GameObjectFocusCheck (GridNotifiers.h:586-606): the spawned GAMEOBJECT_TYPE_SPELL_FOCUS object whose data0
    /// is <paramref name="focusId"/> and whose data1 radius reaches <paramref name="caster"/>: the 3D distance between the
    /// centres is strictly below data1 plus both bounding radii (WorldObject::IsWithinDistInMap with SizeFactor::BoundingRadius,
    /// Object.cpp:1738-1752), in the caster's map. Deterministic: the lowest spawn guid wins when several match.
    /// Limit: vmangos first narrows the search with a 10 yard grid visit (Spell.cpp:7236-7240), which covers whole cells and so
    /// never excludes an object that the distance test accepts for the focus distances in the data (at most 7); it is not modelled.
    /// </summary>
    public GameObject? FindSpellFocus(WorldObject caster, uint focusId)
    {
        ArgumentNullException.ThrowIfNull(caster);
        GameObject? best = null;
        foreach (GameObject go in _objects.Values)
        {
            if (!go.IsSpawned || go.Type != GameObjectType.SpellFocus || go.Template.GetData(0) != focusId || !ReferenceEquals(go.Map, caster.Map))
            {
                continue;
            }

            float dx = go.X - caster.X;
            float dy = go.Y - caster.Y;
            float dz = go.Z - caster.Z;
            float reach = go.Template.GetData(1) + go.BoundingRadius + caster.BoundingRadius;
            if (((dx * dx) + (dy * dy) + (dz * dz)) < reach * reach && (best is null || go.Guid.Counter < best.Guid.Counter))
            {
                best = go;
            }
        }

        return best;
    }

    /// <summary>Whether <see cref="FindSpellFocus"/> finds a focus object for <paramref name="player"/>.</summary>
    public bool HasSpellFocusNearby(Player player, uint focusId)
    {
        ArgumentNullException.ThrowIfNull(player);
        return FindSpellFocus(player, focusId) is not null;
    }

    private GameObjectUseResult CheckUsable(Player player, GameObject? go)
    {
        if (go is null || !go.IsSpawned || !ReferenceEquals(go.Map, player.Map))
        {
            return GameObjectUseResult.NotFound;
        }

        if (!player.IsAlive)
        {
            return GameObjectUseResult.Dead;
        }

        if ((go.Flags & GameObjectFlags.NoInteract) != 0)
        {
            return GameObjectUseResult.NotUsable;
        }

        // GameObject::IsAtInteractDistance (GameObject.cpp:2584-2609): the centre-to-centre 3D distance, no bounding radii.
        float dx = go.X - player.X;
        float dy = go.Y - player.Y;
        float dz = go.Z - player.Z;
        float reach = InteractionDistanceFor(go.Type);
        return (dx * dx) + (dy * dy) + (dz * dz) > reach * reach ? GameObjectUseResult.TooFar : GameObjectUseResult.Ok;
    }

    private GameObjectUseResult UseDoorOrButton(Player player, GameObject go)
    {
        GameObjectUseResult locked = CheckDirectLock(player, go);
        if (locked != GameObjectUseResult.Ok)
        {
            return locked;
        }

        GameObjectUseResult result = ActivateDoorOrButton(go, go.Template.AutoCloseSeconds());
        if (go.Type == GameObjectType.Button)
        {
            TriggerLinkedTrap(go, player); // GameObject::Use, button (GameObject.cpp:1441-1455)
        }

        return result;
    }

    /// <summary>GameObject::Use, spell focus (GameObject.cpp:1534-1539): only its linked trap reacts to a click.</summary>
    private GameObjectUseResult UseSpellFocus(Player player, GameObject go)
    {
        if (go.Template.LinkedTrapEntry() == 0)
        {
            return GameObjectUseResult.NotUsable;
        }

        TriggerLinkedTrap(go, player);
        return GameObjectUseResult.Ok;
    }

    private GameObjectUseResult CheckDirectLock(Player player, GameObject go) => CheckDirectLock(player, go, out _);

    /// <summary><see cref="CheckDirectLock(Player, GameObject)"/>, naming the key from the bags that satisfied the lock (or null).</summary>
    private GameObjectUseResult CheckDirectLock(Player player, GameObject go, out Item? key)
    {
        key = null;
        uint lockId = GameObjectLocks.LockIdOf(go.Template);
        LockEntry? entry = _content.FindLock(lockId);
        return lockId != 0 && entry is null ? GameObjectUseResult.Locked
            : GameObjectLocks.CheckDirectUse(entry, player, out key);
    }

    /// <summary>
    /// vmangos UseDoorOrButton (GameObject.cpp:1370-1383): only a ready object activates; its state flips (ready ↔ active)
    /// with GO_FLAG_IN_USE set, and it returns after <paramref name="autoCloseSeconds"/> whole seconds (none: stays).
    /// The template column holds seconds * 0x10000 and is converted by <see cref="GameObjectInfoView.AutoCloseSeconds"/>.
    /// </summary>
    private GameObjectUseResult ActivateDoorOrButton(GameObject go, uint autoCloseSeconds, bool alternative = false)
    {
        if (go.LootState != GameObjectLootState.Ready)
        {
            return GameObjectUseResult.InUse;
        }

        go.State = go.State == GameObjectState.Ready
            ? alternative ? GameObjectState.ActiveAlternative : GameObjectState.Active
            : GameObjectState.Ready;
        go.Flags |= GameObjectFlags.InUse;
        go.LootState = GameObjectLootState.Activated;
        go.ResetAfterSecond = autoCloseSeconds > 0 ? ClockSeconds + autoCloseSeconds : null;
        return GameObjectUseResult.Ok;
    }

    /// <summary>
    /// GameObject::Update, GO_ACTIVATED (GameObject.cpp:572-597), once the object's timer passed: a goober leaves use and is
    /// deactivated (GO_JUST_DEACTIVATED sets its state back to ready, :606-623, then the next update despawns it unless it never
    /// despawns); a partly looted chest is deactivated (despawns, respawns fresh); a door or button resets.
    /// </summary>
    private static void ActivationExpired(GameObject go)
    {
        switch (go.Type)
        {
            case GameObjectType.Goober:
                go.Flags &= ~GameObjectFlags.InUse;
                go.State = GameObjectState.Ready;
                go.ResetAfterSecond = null;
                go.LootState = GameObjectLootState.JustDeactivated;
                break;
            case GameObjectType.Chest:
                go.ResetAfterSecond = null;
                go.LootState = GameObjectLootState.JustDeactivated;
                break;
            default:
                ResetToReady(go);
                break;
        }
    }

    /// <summary>vmangos ResetDoorOrButton: back to the spawn state, not in use, ready again.</summary>
    private static void ResetToReady(GameObject go)
    {
        go.State = (GameObjectState)(go.Spawn?.State ?? (byte)GameObjectState.Ready);
        go.Flags &= ~GameObjectFlags.InUse;
        go.LootState = GameObjectLootState.Ready;
        go.ResetAfterSecond = null;
    }

    private GameObjectUseResult UseChest(Player player, GameObject go)
    {
        // GameObject::Use, chest (GameObject.cpp:1472-1479): the click springs the chest's linked trap, whatever the lock or quest say.
        TriggerLinkedTrap(go, player);
        if (!ChestQuestAllows(player, go))
        {
            return GameObjectUseResult.NeedsQuest;
        }

        GameObjectUseResult locked = CheckDirectLock(player, go, out Item? key);
        if (locked != GameObjectUseResult.Ok)
        {
            return locked;
        }

        GameObjectUseResult opened = OpenChestStartingEvent(player, go);
        if (opened == GameObjectUseResult.Ok && key is not null)
        {
            UseUpKey(player, key);
        }

        return opened;
    }

    /// <summary>
    /// Opens the chest and, once it opened, starts its event (chest.eventId, data6). mangos-classic GameObject::Use, GAMEOBJECT_TYPE_CHEST
    /// (GameObject.cpp:1548-1560) starts the event, and both chest paths reach that Use: the direct use and the spell path (Spell::SendLoot,
    /// SpellEffects.cpp:2142-2145, which shows the loot after it). ArcaneCore starts it only for a use that passed the quest gate, the lock
    /// and the open itself, so a refused use (or a crafted CMSG_GAMEOBJ_USE on a locked chest) cannot spring the ambush; cmangos has no
    /// server-side quest gate or direct-use lock check to refuse it. Limit: a restocking chest (GO_NOT_READY) refuses the open here and so
    /// starts no event, where cmangos would start it with the empty loot window.
    /// </summary>
    private GameObjectUseResult OpenChestStartingEvent(Player player, GameObject go, Action<GameObject>? onOpened = null)
    {
        GameObjectUseResult opened = OpenChest(player, go, onOpened);
        if (opened == GameObjectUseResult.Ok)
        {
            StartDbEvent(go.Template.GetData(6), player, go);
        }

        return opened;
    }

    /// <summary>
    /// The chest quest gate (chest.questId, data8): the quest must be incomplete for the user. vmangos only uses the column for the
    /// per-viewer activate flag (GameObject::ActivateToQuest, GameObject.cpp:1217-1220), which keeps a client from using the chest;
    /// ArcaneCore enforces it on every server path that opens a chest so a crafted request cannot get around it.
    /// </summary>
    private bool ChestQuestAllows(Player player, GameObject go)
    {
        uint questId = go.Template.GetData(8);
        return questId == 0 || Quests?.IsQuestIncomplete(player, questId) == true;
    }

    /// <summary>A script took the open over: a chest never reaches its loot window, so its open callback runs now.</summary>
    private static GameObjectUseResult ScriptTookOpen(GameObject go, Action<GameObject>? onChestOpened, Action<GameObject>? onScriptTookOpen)
    {
        if (go.Type == GameObjectType.Chest)
        {
            onChestOpened?.Invoke(go);
            onScriptTookOpen?.Invoke(go);
        }

        return GameObjectUseResult.Ok;
    }

    /// <summary>vmangos CannotBeUsedUnderImmunity (GameObjectDefines.h:602-619) against UNIT_FLAG_IMMUNE.</summary>
    private static bool IsRefusedForImmunity(Player player, GameObject go)
        => go.Template.CannotBeUsedUnderImmunity() && (player.UnitFlags & UnitFlags.Immune) != 0;

    private GameObjectUseResult OpenChest(Player player, GameObject go, Action<GameObject>? onOpened = null)
    {
        if (Loot is null)
        {
            return GameObjectUseResult.Unsupported;
        }

        // Ordinary instance unload can preserve its bind while recreating the whole map. A chest
        // there is only usable when its consumed and remaining loot is stored with the logical
        // save (otherwise opening would reroll awards); a runtime chest has no spawn to key on.
        LootStateKey? key = DurableKeyOf(go);
        if (Map.InstanceId != 0 && key is null)
        {
            return GameObjectUseResult.Unsupported;
        }

        if (go.LootState == GameObjectLootState.JustDeactivated)
        {
            return GameObjectUseResult.InUse;
        }

        // A restocking chest (GO_NOT_READY) has no loot until its restock time ran out. Limit: vmangos sends the empty loot window and
        // restarts the restock timer when it is closed (Player::SendLoot generates loot only for GO_READY, Player.cpp:7672); here the
        // open is refused and the timer keeps running.
        if (go.LootState == GameObjectLootState.NotReady)
        {
            return GameObjectUseResult.NotUsable;
        }

        if (ChestLevelRefuses(player, go))
        {
            return GameObjectUseResult.LevelTooLow;
        }

        LootResult opened = key is { } durable
            ? Loot.OpenDurableGameObject(player, go, go.Template.GetData(1), durable, onOpened)
            : Loot.OpenGameObject(player, go, go.Template.GetData(1), onOpened);
        return opened switch
        {
            LootResult.Ok => GameObjectUseResult.Ok,
            LootResult.NotAllowed => GameObjectUseResult.InUse,
            LootResult.Unsupported => GameObjectUseResult.Unsupported,
            _ => GameObjectUseResult.NotUsable,
        };
    }

    /// <summary>The durable key of an instance chest whose loot can be stored now, or null (shared-copy map, runtime object, no live save or store).</summary>
    private LootStateKey? DurableKeyOf(GameObject go)
        => Map.InstanceId != 0 && go.Spawn is { } spawn && go.Type == GameObjectType.Chest && Loot?.Durable?.CanPersist(Map) == true
            ? new LootStateKey(Map.InstanceId, spawn.Guid) : null;

    /// <summary>The object currently tracking a database spawn (spawned or waiting to respawn), or null while its grid is unloaded.</summary>
    internal GameObject? FindBySpawn(uint spawnGuid)
        => _spawnEntries.TryGetValue(spawnGuid, out uint entry)
            ? _objects.GetValueOrDefault(SpawnObjectGuid(entry, spawnGuid)) : null;

    /// <summary>The guid of the object of a database spawn (<see cref="GameObject.HighGuidOf"/>: elevators and trams are HIGHGUID_TRANSPORT).</summary>
    internal ObjectGuid SpawnObjectGuid(uint entry, uint spawnGuid)
        => ObjectGuid.WithEntry(GameObject.HighGuidOf(_content.FindTemplate(entry)?.Type ?? 0), entry, spawnGuid);

    /// <summary>
    /// A durable take committed while no live bag represented the chest (its grid was unloaded or
    /// reloaded meanwhile): a chest that ended up consumed despawns now, like a looted-out one.
    /// </summary>
    internal void OnDurableRecordCommitted(LootStateRecord record)
    {
        if (record.Consumed && FindBySpawn(record.Key.SpawnGuid) is { IsSpawned: true } go && Tracks(go)
            && go.Loot is not { IsEmpty: false })
        {
            Despawn(go);
        }
    }

    /// <summary>
    /// vmangos DoLootRelease (LootHandler.cpp:503-511): a partly looted chest stays GO_ACTIVATED with its loot and despawns this
    /// many seconds after the release, so it comes back with fresh loot.
    /// </summary>
    public const uint PartlyLootedChestDespawnSeconds = 5 * 60;

    /// <summary>
    /// Called by the loot service when the last viewer released a chest's loot (vmangos
    /// DoLootRelease for a game object): looted out → despawn and respawn later; otherwise the
    /// object stays activated with the remaining loot (LootHandler.cpp:503-511) and a chest
    /// despawns <see cref="PartlyLootedChestDespawnSeconds"/> later. A dungeon chest whose
    /// loot is stored with the instance save (<see cref="LootBag.DurableKey"/>) keeps its
    /// stored leftovers and stays ready: despawning it would not reroll them.
    /// </summary>
    internal void OnLootReleased(GameObject go, LootBag bag, Player? releaser = null)
    {
        if (!Tracks(go) || !ReferenceEquals(go.Loot, bag))
        {
            return;
        }

        if (bag.IsEmpty)
        {
            // DoLootRelease (LootHandler.cpp:435-487): a mineral vein may stay for another open; anything else is used up.
            if (bag.DurableKey is null && VeinStaysAfterLooting(releaser, go))
            {
                ReadyVeinAgain(go);
            }
            else
            {
                go.LootState = GameObjectLootState.JustDeactivated;
                AiOf(go)?.OnLootedOut(this, go, releaser);
            }
        }
        else if (bag.DurableKey is not null)
        {
            go.LootState = GameObjectLootState.Ready;
        }
        else
        {
            ActivateWithLeftovers(go);
        }
    }

    /// <summary>A chest holding leftovers: activated, despawning <see cref="PartlyLootedChestDespawnSeconds"/> from now (the whole-second clock).</summary>
    private void ActivateWithLeftovers(GameObject go)
    {
        go.LootState = GameObjectLootState.Activated;
        if (go.Type == GameObjectType.Chest)
        {
            go.ResetAfterSecond = ClockSeconds + PartlyLootedChestDespawnSeconds;
        }
    }

    /// <summary>
    /// The map's instance script hears of the use (<see cref="Instances.Scripts.InstanceData.OnGameObjectUse"/>); true when it took the use
    /// (cmangos <c>scriptReturnValue</c>).
    /// </summary>
    private bool InstanceScriptTookUse(Player player, GameObject go)
        => Map.FindUpdater<Instances.Scripts.InstanceData>()?.OnGameObjectUse(player, go) == true;

    /// <param name="scriptTookUse">The ScriptDev2 GOUse script returned true: the goober still activates, but casts no spell (cmangos
    /// GameObject::Use, GAMEOBJECT_TYPE_GOOBER: <c>if (!scriptReturnValue) ... else return;</c> before <c>spellId = goober.spellId</c>).</param>
    private GameObjectUseResult UseGoober(Player player, GameObject go, bool lockChecked = false, bool scriptTookUse = false)
    {
        if (!lockChecked)
        {
            GameObjectUseResult locked = CheckDirectLock(player, go);
            if (locked != GameObjectUseResult.Ok)
            {
                return locked;
            }
        }

        if (go.CooldownUntilMs > _clockMs)
        {
            return GameObjectUseResult.OnCooldown;
        }

        // An object still in use (activated until its auto-close time) refuses the use before anything else, so a repeated or crafted
        // CMSG_GAMEOBJ_USE in that window neither shows the page again nor re-fires the goober event. The client offers no use of a
        // GO_FLAG_IN_USE object and cmangos has no server-side refusal; ArcaneCore's InUse check stands for it, as the chest path does.
        uint autoCloseSeconds = go.Template.AutoCloseSeconds();
        if (autoCloseSeconds > 0 && go.LootState != GameObjectLootState.Ready)
        {
            return GameObjectUseResult.InUse;
        }

        // GameObject::Use, goober (GameObject.cpp:1547-1575): the page text or gossip comes first; only a positive questId gates the rest.
        ShowGooberPageOrGossip(player, go);
        // mangos-classic GameObject::Use, GAMEOBJECT_TYPE_GOOBER (GameObject.cpp:1683-1688): goober.eventId (data2) starts before the
        // questId gate. vmangos (GameObject.cpp:1564-1580) starts it only after the gate passes.
        StartDbEvent(go.Template.GetData(2), player, go);
        int questId = GooberQuestId(go);
        if (questId > 0 && Quests?.IsQuestIncomplete(player, (uint)questId) != true)
        {
            return GameObjectUseResult.NeedsQuest;
        }

        Quests?.GameObjectUsed(player, go.Entry, go.Guid);
        TriggerLinkedTrap(go, player); // GameObject.cpp:1593

        if (go.Template.GetData(6) is var cooldown and not 0)
        {
            go.CooldownUntilMs = _clockMs + (cooldown * 1000L);
        }

        go.UseCount++;

        // GameObject::Use, goober (GameObject.cpp:1593-1606): in use and activated; an object with a custom animation (the display
        // list, or goober.customAnim (data4) together with an auto-close time) plays animation 0, any other one shows its active
        // state. The auto-close time (data3, whole seconds) runs out in GameObject::Update (:578-585): the object is deactivated
        // then, and a consumable one (data5) or one that may despawn goes away (ActivationExpired, Despawn).
        go.Flags |= GameObjectFlags.InUse;
        go.LootState = GameObjectLootState.Activated;
        if (GameObjectInfoView.HasCustomAnim(go.GetUInt32(UpdateFields.GameobjectDisplayid)) || (autoCloseSeconds > 0 && go.Template.GetData(4) != 0))
        {
            Map.BroadcastInRange(go, 0, WorldOpcode.SmsgGameobjectCustomAnim, GameObjectPackets.CustomAnim(go.Guid, 0), includeSelf: false);
        }
        else
        {
            go.State = GameObjectState.Active;
        }

        go.ResetAfterSecond = ClockSeconds + autoCloseSeconds;
        if (!scriptTookUse)
        {
            CastGooberSpell(player, go);
        }

        return GameObjectUseResult.Ok;
    }

    /// <summary>
    /// Where a chair user is moved when they sit (same-map teleport). Defaults to <see cref="NearTeleportSink"/> (vmangos
    /// <c>Unit::NearTeleportTo</c>: the player is relocated at once and the client is told with MSG_MOVE_TELEPORT_ACK).
    /// </summary>
    public ITeleportSink Teleports { get; set; } = new NearTeleportSink();

    /// <summary>
    /// GAMEOBJECT_TYPE_CHAIR (GameObject.cpp:1515-1533 with PlayerCanUse :2229-2236): the user must be within 3 yards (3D) of the
    /// nearest slot, then needs line of sight to the chair; they are moved to the slot at the chair orientation and sit with
    /// SIT_LOW_CHAIR plus the chair height. A refused use is silent for the client. A mounted user was already taken off the
    /// mount by <see cref="Use"/> (GameObject::Use, review finding 58). An occupied slot is not refused (neither does vmangos refuse it).
    /// </summary>
    private GameObjectUseResult UseChair(Player player, GameObject go)
    {
        (float slotX, float slotY) = GameObjectChairs.ClosestSlot(go, player.X, player.Y);
        float dx = slotX - player.X;
        float dy = slotY - player.Y;
        float dz = go.Z - player.Z;
        if (MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz)) > GameObjectChairs.MaxSitDistance)
        {
            return GameObjectUseResult.TooFar;
        }

        if (!player.IsWithinLineOfSight(go))
        {
            return GameObjectUseResult.LineOfSight;
        }

        if (!Teleports.Teleport(player, Map.MapId, slotX, slotY, go.Z, go.Orientation))
        {
            return GameObjectUseResult.NotUsable;
        }

        player.SetStandState(GameObjectChairs.SeatedState(go.Template));
        return GameObjectUseResult.Ok;
    }

    /// <summary>
    /// GAMEOBJECT_TYPE_CAMERA (GameObject.cpp:1613-1634): SMSG_TRIGGER_CINEMATIC with data1 when it is set. The event id (data2)
    /// needs the scripts engine, which this codebase does not have, so it is not run. Cinematic ids are not validated against CinematicSequences.dbc.
    /// </summary>
    private static GameObjectUseResult UseCamera(Player player, GameObject go)
    {
        if (go.Template.GetData(1) is var cinematic and not 0)
        {
            player.Session.Send(WorldOpcode.SmsgTriggerCinematic, CinematicPackets.TriggerCinematic(cinematic));
        }

        return GameObjectUseResult.Ok;
    }

    private static GameObjectUseResult UseText(Player player, GameObject go)
    {
        if (go.Template.GetData(0) == 0)
        {
            return GameObjectUseResult.NotUsable;
        }

        player.Session.Send(WorldOpcode.SmsgGameobjectPagetext, GameObjectPackets.PageText(go.Guid));
        return GameObjectUseResult.Ok;
    }

    // --- spawning ----------------------------------------------------------------------------

    /// <summary>
    /// Place a runtime object (GM command, script, summoned chest). It despawns for good after
    /// <paramref name="despawnAfterSeconds"/> (0 = stays until removed) and never respawns.
    /// </summary>
    public GameObject? Summon(uint entry, float x, float y, float z, float orientation, uint despawnAfterSeconds = 0)
    {
        Invariant.Check(
            float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) && float.IsFinite(orientation),
            "gameobject-summon-finite-coords",
            $"GameObject summon entry {entry} on map {Map.MapId} has non-finite coordinates ({x}, {y}, {z}, {orientation})");

        if (_content.FindTemplate(entry) is not { } template)
        {
            return null;
        }

        var go = new GameObject(_nextTemporaryCounter++, template, null) { CreatedAtMs = _clockMs };
        go.SetPosition(x, y, z, orientation);
        go.InitializeFields();
        AddToWorld(go);
        if (despawnAfterSeconds > 0)
        {
            long at = _clockMs + (despawnAfterSeconds * 1000L);
            _despawnAt[go.Guid] = at;
            if (at < _nextDespawnAtMs)
            {
                _nextDespawnAtMs = at;
            }
        }

        return go;
    }

    /// <summary>Take a runtime object out of the world for good (GM .gobject delete).</summary>
    public bool Remove(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        if (!Tracks(go))
        {
            return false;
        }

        _objects.Remove(go.Guid);
        _elevatorControls.Remove(go.Guid);
        OnObjectMembershipChanged(go);
        IndexRitual(go, tracked: false);
        Loot?.ForgetLoot(go);
        if (_questFlagsSent.Count > 0)
        {
            _questFlagsSent.Remove(go.Guid);
        }

        if (_despawnAt.Count > 0)
        {
            _despawnAt.Remove(go.Guid);
        }

        if (_reportedDoorCollisionMissing.Count > 0)
        {
            _reportedDoorCollisionMissing.Remove(go.Guid);
        }

        if (_reportedDoorStateDesync.Count > 0)
        {
            _reportedDoorStateDesync.Remove(go.Guid);
        }

        if (go.IsSpawned)
        {
            Map.RemoveObject(go);
        }

        GridCoord currentGrid = GridDefines.ComputeGridCoord(go.X, go.Y);
        bool removedFromGrid = _grids.TryGetValue(currentGrid, out List<GameObject>? currentList) && currentList.Remove(go);
        if (!removedFromGrid && go.Spawn is { } spawn)
        {
            GridCoord spawnGrid = GridDefines.ComputeGridCoord(spawn.X, spawn.Y);
            if (spawnGrid != currentGrid && _grids.TryGetValue(spawnGrid, out List<GameObject>? spawnList))
            {
                removedFromGrid = spawnList.Remove(go);
            }
        }

        if (!removedFromGrid)
        {
            foreach (List<GameObject> grid in _grids.Values)
            {
                if (grid.Remove(go))
                {
                    break;
                }
            }
        }

        go.System = null;
        OnGroupMemberUntracked(go);
        return true;
    }

    /// <summary>
    /// Despawn now (vmangos GO_JUST_DEACTIVATED → SetRespawnTime): a database spawn with a
    /// non-negative spawntimesecs comes back after that delay (at least one second); a negative one stays despawned
    /// until <see cref="ForceRespawn"/>; a runtime object is removed unless it is <see cref="GameObject.SpawnedByDefault"/>.
    /// </summary>
    public void Despawn(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        if (!Tracks(go))
        {
            return;
        }

        if (go.Spawn is null && go.SpawnedByDefault)
        {
            // GameObject.cpp GO_JUST_DEACTIVATED: not deleted (spawned by default), loot cleared, GO_READY, and no respawn delay so it stays.
            Loot?.ForgetLoot(go);
            go.LootState = GameObjectLootState.Ready;
            return;
        }

        if (go.Spawn is null)
        {
            Remove(go);
            return;
        }

        Loot?.ForgetLoot(go);
        if (go.NeverDespawns)
        {
            // GameObject.cpp:671 (<c>if (!m_respawnDelayTime) return;</c>): a NODESPAWN spawn stays in the world, only its loot and state reset.
            go.LootState = GameObjectLootState.Ready;
            return;
        }

        if (_groupOfSpawn.ContainsKey(go.Spawn.Guid))
        {
            DespawnGroupMember(go, go.Spawn);
            return;
        }

        if (go.IsSpawned)
        {
            // GameObject.cpp:654-657: despawn-at-action objects and anything with animprogress > 0 play the despawn animation first.
            if (go.Template.IsDespawnAtAction() || go.GetUInt32(UpdateFields.GameobjectAnimprogress) > 0)
            {
                byte[] anim = GameObjectPackets.DespawnAnim(go.Guid);
                foreach (Player observer in Map.ObserversOf(go))
                {
                    observer.Session.Send(WorldOpcode.SmsgGameobjectDespawnAnim, anim);
                }
            }

            Map.RemoveObject(go);
        }

        _questFlagsSent.Remove(go.Guid);
        go.LootState = GameObjectLootState.JustDeactivated;
        go.RespawnAtMs = go.Spawn.SpawnTimeSeconds >= 0 ? _clockMs + (DurableKeyOf(go) is null ? ScaleRespawnDelayMs(go.Spawn, RespawnDelayMs(go)) : RespawnDelayMs(go)) : 0;
        OnPoolMemberDespawned(go); // cmangos GameObject.cpp GO_JUST_DEACTIVATED: a pooled object lets its pool choose what comes back
    }

    /// <summary>Respawn a despawned object now (GM command, script, event spawn of a negative spawntimesecs object).</summary>
    public void ForceRespawn(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        if (Tracks(go) && !go.IsSpawned)
        {
            Respawn(go);
        }
    }

    /// <summary>
    /// vmangos ComputeRespawnDelay (GameObject.cpp:698-704, GameObject.cpp:694) of the delay rolled at load: spawn flag 0x04 scales it by
    /// urand(90,110)/100; the dynamic flag (0x08, realm population scaling) is not modelled. At least one second.
    /// </summary>
    private long RespawnDelayMs(GameObject go)
    {
        uint seconds = go.RolledRespawnSeconds;
        if (go.Spawn is { } spawn && (spawn.SpawnFlags & 0x04) != 0)
        {
            seconds = (uint)((float)(seconds * (uint)Random.Next(90, 111)) / 100f);
        }

        return Math.Max(1000L, seconds * 1000L);
    }

    /// <summary>vmangos GetRandomRespawnTime (GameObject.cpp:706-709), rolled once when the spawn loads (GameObject.cpp:1002).</summary>
    private uint RollRespawnSeconds(GameObjectSpawn spawn)
    {
        uint min = (uint)Math.Abs((long)spawn.SpawnTimeSeconds);
        uint max = spawn.SpawnTimeMaxSeconds is { } m ? (uint)Math.Abs((long)m) : min;
        return Options.RandomRespawn && max > min ? (uint)Random.NextInt64(min, (long)max + 1) : min;
    }

    private bool Tracks(GameObject go)
        => _objects.TryGetValue(go.Guid, out GameObject? current) && ReferenceEquals(current, go);

    private void Respawn(GameObject go)
    {
        go.InitializeFields();
        go.RespawnAtMs = 0;
        go.CooldownUntilMs = 0;
        go.ClearChangedFields();
        if (_mapWideElevators.Contains(go))
        {
            // A map-wide elevator (a Deeprun Tram car) comes back as one: active, seen by every player on the map.
            Map.AddObject(go, active: true);
            Map.AddMapWideObject(go);
        }
        else
        {
            Map.AddObject(go);
        }

        if (go.Spawn is { SpawnTimeSeconds: >= 0 })
        {
            // AzerothCore reaches AI()->Reset() only for m_spawnedByDefault objects (GameObject.cpp:656-665): a negative spawntimesecs object
            // brought back by a script or event keeps its script state.
            AiOf(go)?.OnRespawn(this, go);
        }

        Map.FindUpdater<Instances.Scripts.InstanceData>()?.OnObjectSpawn(go);
        RespawnLinkedTrap(go); // GameObject::Update, GO_READY respawn (GameObject.cpp:427-437)
    }

    private void LoadGrid(GridCoord coord)
    {
        if (_grids.ContainsKey(coord))
        {
            return;
        }

        var list = new List<GameObject>();
        _grids[coord] = list;
        if (!_spawnsByGrid.TryGetValue(coord, out List<GameObjectSpawn>? spawns))
        {
            return;
        }

        LoadSpawns(list, spawns);
    }

    /// <summary>Create the objects of <paramref name="spawns"/> in an already registered grid (a grid load, or one event spawn coming back: <see cref="RefreshSpawns"/>).</summary>
    private void LoadSpawns(List<GameObject> list, IEnumerable<GameObjectSpawn> spawns)
    {
        foreach (GameObjectSpawn spawn in spawns)
        {
            if (_spawnGate is { } gate && !gate.AllowsGameObject(spawn.Guid))
            {
                continue; // an event spawn whose event is not running (vmangos leaves game_event_gameobject guids out of the grid at load)
            }

            if ((spawn.SpawnFlags & 0x02) != 0)
            {
                continue; // SPAWN_FLAG_DISABLED: GameObject::LoadFromDB refuses it (GameObject.cpp:969, ObjectDefines.h:128)
            }

            if (PoolRefusesAtLoad(spawn))
            {
                continue; // a pooled spawn exists only while its pool has it out (cmangos ObjectMgr::LoadGameObjects, IsNotPartOfPoolOrEvent)
            }

            if (_groupOfSpawn.TryGetValue(spawn.Guid, out SpawnGroupState? group))
            {
                LoadGroupMember(list, group, spawn); // its spawn group decides whether and as what it exists
                continue;
            }

            // cmangos GameObject::LoadFromDB (GameObject.cpp:907): a spawn with gameobject_spawn_entry rows becomes one of them, chosen
            // uniformly each time the object is created; an in-place respawn keeps it (none of classic-db's entries is a dynamic-guid one).
            IReadOnlyList<uint> alternatives = _content.GetSpawnEntries(spawn.Guid);
            GameObjectTemplate? template = alternatives.Count > 0 ? ChooseSpawnEntry(alternatives) : _content.FindTemplate(spawn.Entry);
            if (template is null)
            {
                uint warnKey = alternatives.Count > 0 ? spawn.Guid | 0x8000_0000u : spawn.Entry;
                if (_warnedMissingTemplates.Add(warnKey))
                {
                    _logger.LogWarning(
                        alternatives.Count > 0
                            ? "gameobject spawn {Guid} on map {MapId} has no gameobject_template for any of its gameobject_spawn_entry rows; skipped"
                            : "gameobject spawn {Guid} on map {MapId} uses missing gameobject_template {Entry}; skipped",
                        spawn.Guid, Map.MapId, spawn.Entry);
                }

                continue;
            }

            LoadSpawn(list, spawn, template);
        }
    }

    /// <summary>Create the object of one database spawn as <paramref name="template"/> (its own entry, or the one chosen for it).</summary>
    private void LoadSpawn(List<GameObject> list, GameObjectSpawn spawn, GameObjectTemplate template)
    {
        Invariant.Check(
            float.IsFinite(spawn.X) && float.IsFinite(spawn.Y) && float.IsFinite(spawn.Z) && float.IsFinite(spawn.Orientation),
            "gameobject-spawn-finite-coords",
            $"GameObjectSpawn {spawn.Guid} on map {Map.MapId} has non-finite coordinates ({spawn.X}, {spawn.Y}, {spawn.Z}, {spawn.Orientation})");
        _spawnEntries[spawn.Guid] = template.Entry;
        var go = new GameObject(spawn.Guid, template, spawn) { CreatedAtMs = _clockMs };
        go.RolledRespawnSeconds = RollRespawnSeconds(spawn);
        go.System = this;
        _objects[go.Guid] = go;
        OnObjectMembershipChanged(go);
        IndexRitual(go, tracked: true);
        list.Add(go);
        ConfigureQuestFlags(go);
        NotifyInstanceScript(go);
        if (DurableKeyOf(go) is { } durableKey && Loot?.Durable is { } durable)
        {
            // The stored chest decides: a consumed one stays despawned until its stored respawn
            // time; contents are rebuilt lazily when it is opened (never while an operation is in flight).
            _unloadedLoot.Remove(spawn.Guid);
            _respawnAt.Remove(spawn.Guid);
            if (durable.Find(durableKey) is { Consumed: true } consumed && consumed.RespawnAtUnix > durable.UnixNow)
            {
                go.LootState = GameObjectLootState.JustDeactivated;
                go.RespawnAtMs = consumed.RespawnAtUnix == long.MaxValue
                    ? 0 : _clockMs + Math.Max(1L, consumed.RespawnAtUnix - durable.UnixNow) * 1000L;
                return;
            }

            if (spawn.SpawnTimeSeconds < 0 && durable.Find(durableKey) is null)
            {
                go.LootState = GameObjectLootState.JustDeactivated; // spawned by events/scripts only
                return;
            }

            go.ClearChangedFields();
            Map.AddObject(go);
            return;
        }

        if (_unloadedLoot.Remove(spawn.Guid, out LootBag? remaining) && Loot is { } lootService)
        {
            // The leftovers come back with the chest, which is still the partly looted one: its despawn timer starts again.
            lootService.RestoreGameObjectLoot(go, remaining);
            if (go.Loot is not null)
            {
                ActivateWithLeftovers(go);
            }
        }

        bool pending = _respawnAt.Remove(spawn.Guid, out long respawnAt);
        if (pending && respawnAt > _clockMs)
        {
            go.LootState = GameObjectLootState.JustDeactivated;
            go.RespawnAtMs = respawnAt;
            return;
        }

        if (!pending && spawn.SpawnTimeSeconds < 0)
        {
            go.LootState = GameObjectLootState.JustDeactivated; // spawned by events/scripts only
            return;
        }

        go.ClearChangedFields();
        Map.AddObject(go);
    }

    private void UnloadGrid(GridCoord coord)
    {
        if (!_grids.Remove(coord, out List<GameObject>? list))
        {
            return;
        }

        foreach (GameObject go in list)
        {
            bool durable = go.Loot?.DurableKey is not null || DurableKeyOf(go) is not null;
            if (go.Spawn is { } spawn && go.IsSpawned && go.Loot is { IsEmpty: false } remaining && !durable)
            {
                _unloadedLoot[spawn.Guid] = remaining;
            }

            if (go.Spawn is not null && !go.IsSpawned && go.RespawnAtMs > 0 && !durable)
            {
                _respawnAt[go.Spawn.Guid] = go.RespawnAtMs;
            }

            _objects.Remove(go.Guid);
            _elevatorControls.Remove(go.Guid); // a lift reloaded with its grid runs the default schedule again
            OnObjectMembershipChanged(go);
            IndexRitual(go, tracked: false);
            if (_questFlagsSent.Count > 0)
            {
                _questFlagsSent.Remove(go.Guid);
            }

            if (_despawnAt.Count > 0)
            {
                _despawnAt.Remove(go.Guid);
            }

            if (_reportedDoorCollisionMissing.Count > 0)
            {
                _reportedDoorCollisionMissing.Remove(go.Guid);
            }

            if (_reportedDoorStateDesync.Count > 0)
            {
                _reportedDoorStateDesync.Remove(go.Guid);
            }

            Loot?.ForgetLoot(go);
            if (go.IsSpawned)
            {
                Map.RemoveObject(go);
            }

            go.System = null;
            OnGroupMemberUntracked(go);
        }
    }

    /// <summary>
    /// Marks cached object lists stale when <paramref name="go"/> joins or leaves <see cref="_objects"/>.
    /// Avoids invalidating <see cref="_transports"/> or <see cref="_aiObjects"/> when a non-transport, unscripted object changes.
    /// </summary>
    private void OnObjectMembershipChanged(GameObject go)
    {
        _walkListStale = true;
        if (go.Type == GameObjectType.Transport)
        {
            _transportsStale = true;
        }

        if (_aiObjectsStale)
        {
            return;
        }

        object? key = _fallbackAi?.HandlesKey;
        if (!ReferenceEquals(key, _aiObjectsKey) || (key is null && _fallbackAi is not null) || AiOf(go) is not null)
        {
            _aiObjectsStale = true;
        }
    }

    /// <summary>
    /// The object list of the grid under (<paramref name="x"/>, <paramref name="y"/>), loading that grid here first when it is not loaded yet
    /// (as <see cref="CreatureMapSystem.SpawnTemporary"/> does). A runtime object placed or moved into a grid nobody is near must still sit in
    /// a grid list: <see cref="UnloadGrid"/> only removes what the lists hold, and a later <see cref="LoadGrid"/> builds a fresh list without
    /// it, so an object outside every list would stay in <see cref="_objects"/> and in the map until <see cref="Remove"/>.
    /// </summary>
    private List<GameObject> GridListOf(float x, float y)
    {
        GridCoord coord = GridDefines.ComputeGridCoord(x, y);
        if (!_grids.TryGetValue(coord, out List<GameObject>? list))
        {
            LoadGrid(coord);
            list = _grids[coord];
        }

        return list;
    }

    private void AddToWorld(GameObject go)
    {
        go.System = this;
        _objects[go.Guid] = go;
        OnObjectMembershipChanged(go);
        IndexRitual(go, tracked: true);
        GridListOf(go.X, go.Y).Add(go);
        ConfigureQuestFlags(go);
        NotifyInstanceScript(go);
        go.ClearChangedFields();
        Map.AddObject(go);
    }

    /// <summary>
    /// vmangos GameObject::AddToWorld -> ZoneScript::OnGameObjectCreate: the instance script hears of every object placed in its map, before
    /// anyone sees it (a door of a finished encounter is created open).
    /// </summary>
    private void NotifyInstanceScript(GameObject go) => Map.FindUpdater<Instances.Scripts.InstanceData>()?.OnObjectCreate(go);

    // --- quest dynamic flags -----------------------------------------------------------------

    private void ConfigureQuestFlags(GameObject go) => go.ViewerFieldFilter = IsQuestRelated(go.Template) ? this : null;

    /// <summary>
    /// <see cref="ConfigureQuestFlags"/> for an object that already exists (live reload): when the filter turns on or off, the viewers' sent
    /// baselines are dropped and one dynamic flags update is forced, so each viewer is sent the flags the new filter answers.
    /// </summary>
    private void RefreshQuestFilter(GameObject go)
    {
        bool was = go.ViewerFieldFilter is not null;
        ConfigureQuestFlags(go);
        if (was == (go.ViewerFieldFilter is not null))
        {
            return;
        }

        _questFlagsSent.Remove(go.Guid);
        go.ForceFieldUpdate(UpdateFields.GameobjectDynFlags);
    }

    /// <summary>A chest with a quest or quest loot, or a goober with a quest: what the quest dynamic flags apply to.</summary>
    private bool IsQuestRelated(GameObjectTemplate template) => (GameObjectType)template.Type switch
    {
        GameObjectType.Chest => template.GetData(8) != 0 || QuestLootItems(template.GetData(1)).Length > 0,
        GameObjectType.Goober => template.GetData(1) != 0,
        _ => false,
    };

    /// <summary>Quest items (negative chance) a chest's loot template can drop, including one level of references.</summary>
    private uint[] QuestLootItems(uint lootId)
    {
        if (lootId == 0 || Loot is null)
        {
            return [];
        }

        LootContent content = Loot.Content;
        if (!ReferenceEquals(content, _questLootContent))
        {
            _questLootItems.Clear(); // computed from the tables a live reload replaced
            _questLootContent = content;
        }

        if (!_questLootItems.TryGetValue(lootId, out uint[]? items))
        {
            IEnumerable<LootStoreRow> rows = content.GetRows(LootTableKind.GameObject, lootId);
            rows = rows.Concat(rows.Where(r => r.MinCountOrRef < 0)
                .SelectMany(r => content.GetRows(LootTableKind.Reference, (uint)-r.MinCountOrRef)));
            items = [.. rows.Where(r => r.ChanceOrQuestChance < 0 && r.MinCountOrRef >= 0).Select(r => r.Item).Distinct()];
            _questLootItems[lootId] = items;
        }

        return items;
    }

    private bool NeedsAnyQuestLootItem(Player viewer, uint lootId)
    {
        if (Quests is null)
        {
            return false;
        }

        uint[] items = QuestLootItems(lootId);
        for (int i = 0; i < items.Length; i++)
        {
            if (Quests.NeedsQuestItem(viewer, items[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// vmangos GameObject::ActivateToQuest + BuildValuesUpdate: a chest whose quest is incomplete
    /// or whose loot holds an item the viewer's quests need, and a goober whose quest is
    /// incomplete, get GO_DYNFLAG_LO_ACTIVATE | GO_DYNFLAG_LO_SPARKLE for that viewer only.
    /// </summary>
    public GameObjectDynFlags QuestFlagsFor(GameObject go, Player viewer)
    {
        ArgumentNullException.ThrowIfNull(go);
        ArgumentNullException.ThrowIfNull(viewer);
        if (Quests is null)
        {
            return GameObjectDynFlags.None;
        }

        bool active = go.Type switch
        {
            GameObjectType.Chest => (go.Template.GetData(8) is var q and not 0 && Quests.IsQuestIncomplete(viewer, q))
                || NeedsAnyQuestLootItem(viewer, go.Template.GetData(1)),
            // GameObject::ActivateToQuest (GameObject.cpp:1245-1250): questId -1 activates the goober for everyone.
            GameObjectType.Goober => GooberQuestId(go) is var gq && (gq == -1 || (gq > 0 && Quests.IsQuestIncomplete(viewer, (uint)gq))),
            _ => false,
        };
        return active ? GameObjectDynFlags.Activate | GameObjectDynFlags.Sparkle : GameObjectDynFlags.None;
    }

    uint IViewerFieldFilter.Filter(WorldObject obj, int index, uint value, Player viewer)
    {
        if (index != UpdateFields.GameobjectDynFlags || obj is not GameObject go)
        {
            return value;
        }

        // This answer is what the viewer's create block or values update carries, so it is the baseline RefreshQuestFlags compares against.
        uint quest = (uint)QuestFlagsFor(go, viewer);
        if (viewer.Session.WantsUpdateObjects)
        {
            if (!_questFlagsSent.TryGetValue(go.Guid, out Dictionary<Player, uint>? sent))
            {
                _questFlagsSent[go.Guid] = sent = new(ReferenceEqualityComparer.Instance);
            }

            sent[viewer] = quest;
        }

        return value | quest;
    }

    private bool HasUpdateObjectPlayers()
    {
        foreach (Player player in Map.PlayerValues)
        {
            if (player.Session.WantsUpdateObjects)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Re-send GAMEOBJECT_DYN_FLAGS for quest objects whose answer differs, for one of their viewers, from what that viewer was last sent
    /// (recorded by <see cref="IViewerFieldFilter.Filter"/>; a viewer never sent the field counts as having seen 0).
    /// Walks only the visible sets of players whose session wants update packets, skipping bot-only maps entirely.
    /// </summary>
    private void RefreshQuestFlags()
    {
        if (Quests is null || !HasUpdateObjectPlayers())
        {
            return;
        }

        foreach (Player viewer in Map.PlayerValues)
        {
            if (!viewer.Session.WantsUpdateObjects)
            {
                continue;
            }

            foreach (ObjectGuid guid in viewer.VisibleObjects)
            {
                if (guid.IsPlayer
                    || !_objects.TryGetValue(guid, out GameObject? go)
                    || !go.IsSpawned
                    || !ReferenceEquals(go.ViewerFieldFilter, this))
                {
                    continue;
                }

                try
                {
                    if (!_questFlagsSent.TryGetValue(guid, out Dictionary<Player, uint>? sent))
                    {
                        _questFlagsSent[guid] = sent = new(ReferenceEqualityComparer.Instance);
                    }

                    // A viewer never sent this field (not built yet) has seen none of it: any quest flag is news to it.
                    uint now = (uint)QuestFlagsFor(go, viewer);
                    if (sent.GetValueOrDefault(viewer) != now)
                    {
                        sent[viewer] = now;
                        go.ForceFieldUpdate(UpdateFields.GameobjectDynFlags);
                    }
                    else
                    {
                        sent[viewer] = now;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("map.gameobject", ex);
                }
            }
        }
    }
}
