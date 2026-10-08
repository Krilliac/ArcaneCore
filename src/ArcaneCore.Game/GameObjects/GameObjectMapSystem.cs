using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Loot;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    private GameObjectContent _content;
    private readonly ILogger _logger;
    private readonly Dictionary<GridCoord, List<GameObjectSpawn>> _spawnsByGrid = [];
    private readonly Dictionary<GridCoord, List<GameObject>> _grids = [];
    private readonly Dictionary<uint, long> _respawnAt = [];
    private readonly Dictionary<uint, LootBag> _unloadedLoot = [];
    private readonly Dictionary<ObjectGuid, GameObject> _objects = [];
    private readonly Dictionary<ObjectGuid, Dictionary<Player, uint>> _questFlagsSent = [];
    private readonly Dictionary<uint, uint[]> _questLootItems = [];
    private readonly HashSet<uint> _warnedMissingTemplates = [];
    private readonly Dictionary<ObjectGuid, long> _despawnAt = [];
    private readonly Dictionary<uint, uint> _spawnEntries = [];
    private readonly Dictionary<GameObjectType, Func<Player, GameObject, GameObjectUseResult>> _useHandlers = [];
    private long _clockMs;
    private long _nextQuestRefreshMs;
    private uint _nextTemporaryCounter;

    public GameObjectMapSystem(Map map, GameObjectContent content, LootService? loot = null, ILootQuestJournal? quests = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(content);
        Map = map;
        _content = content;
        Loot = loot;
        Quests = quests;
        _logger = logger ?? NullLogger.Instance;

        uint maxGuid = 0;
        foreach (GameObjectSpawn spawn in content.GetSpawns(map.MapId))
        {
            GridCoord grid = CreatureMapSystem.ComputeGrid(spawn.X, spawn.Y);
            if (!_spawnsByGrid.TryGetValue(grid, out List<GameObjectSpawn>? list))
            {
                _spawnsByGrid[grid] = list = [];
            }

            list.Add(spawn);
            maxGuid = Math.Max(maxGuid, spawn.Guid);
            _spawnEntries[spawn.Guid] = spawn.Entry;
        }

        _nextTemporaryCounter = maxGuid + 1;
        if (loot is not null)
        {
            loot.Objects = this;
        }

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

    /// <summary>Raised after a successful use (scripts, events, the spells area's linked traps/spells).</summary>
    public event Action<Player, GameObject>? Used;

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
        UpdateAis(diffMs);
        UpdateElevators();
        foreach (GameObject go in _objects.Values.ToArray())
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
        }

        foreach ((ObjectGuid guid, long at) in _despawnAt.ToArray())
        {
            if (at <= _clockMs && _objects.TryGetValue(guid, out GameObject? expired))
            {
                Remove(expired);
            }
        }

        if (_clockMs >= _nextQuestRefreshMs)
        {
            _nextQuestRefreshMs = _clockMs + QuestFlagRefreshMs;
            RefreshQuestFlags();
        }
    }

    public void OnPlayerRemoved(Map map, Player player)
    {
        OnRitualParticipantLeft(player);
        Loot?.OnPlayerLeft(player);
        foreach (Dictionary<Player, uint> sent in _questFlagsSent.Values)
        {
            sent.Remove(player);
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
        GameObjectUseResult result = CheckUsable(player, go);
        if (result != GameObjectUseResult.Ok)
        {
            return result;
        }

        // GameObject::Use (GameObject.cpp:1405-1407): the object's script may take the use over, before anything else (and then nothing
        // else happens: no use event either).
        if (AiOf(go!)?.OnUse(this, go!, player) == true)
        {
            return GameObjectUseResult.Ok;
        }

        // GameObject::Use (GameObject.cpp:1409-1416): an immune user is ignored by objects that cannot be used under immunity,
        // and a mounted user is taken off the mount unless the object allows mounted use.
        if (IsRefusedForImmunity(player, go!))
        {
            return GameObjectUseResult.Immune;
        }

        if (!go!.Template.IsUsableMounted() && MountService.IsMounted(player))
        {
            Dismount?.Invoke(player);
        }

        // An area owns a type (fishing bobbers: ArcaneCore.Game.Fishing) and registers its handler instead of editing this switch.
        result = _useHandlers.TryGetValue(go!.Type, out Func<Player, GameObject, GameObjectUseResult>? useHandler) ? useHandler(player, go) : go.Type switch
        {
            GameObjectType.Door or GameObjectType.Button => UseDoorOrButton(player, go),
            GameObjectType.Chest => UseChest(player, go),
            GameObjectType.Goober => UseGoober(player, go),
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
    /// time and skill-ups belong to the spell (not done here).
    /// </summary>
    public GameObjectUseResult OpenLock(Player player, ObjectGuid guid, LockType lockType, uint keyItemId = 0, uint skillBonus = 0)
    {
        ArgumentNullException.ThrowIfNull(player);
        GameObject? go = _objects.GetValueOrDefault(guid);
        GameObjectUseResult result = CheckUsable(player, go);
        if (result != GameObjectUseResult.Ok)
        {
            return result;
        }

        // Spell::EffectOpenLock (SpellEffects.cpp:2117-2118) returns before opening (and before any skill-up) for an immune caster.
        if (IsRefusedForImmunity(player, go!))
        {
            return GameObjectUseResult.Immune;
        }

        uint lockId = GameObjectLocks.LockIdOf(go!.Template);
        LockEntry? entry = _content.FindLock(lockId);
        result = lockId != 0 && entry is null ? GameObjectUseResult.Locked
            : GameObjectLocks.CheckOpenLock(entry, player, lockType, keyItemId, SkillValue, skillBonus);
        if (result != GameObjectUseResult.Ok)
        {
            return result;
        }

        // Spell::SendLoot (SpellEffects.cpp:2048-2068) hands a door, button, spell focus, goober or chest to GameObject::Use, whose button and
        // chest branches spring the linked trap (GameObject.cpp:1441-1455, 1472-1479) - before the chest loot, whatever the quest gate says.
        if (go.Type is GameObjectType.Chest or GameObjectType.Button)
        {
            TriggerLinkedTrap(go, player);
        }

        result = go.Type switch
        {
            // The chest quest gate of UseChest holds for the spell path too: a gathering node tied to a quest opens only for that quest.
            GameObjectType.Chest => ChestQuestAllows(player, go) ? OpenChest(player, go) : GameObjectUseResult.NeedsQuest,
            GameObjectType.Door or GameObjectType.Button => ActivateDoorOrButton(go, go.Template.AutoCloseSeconds()),
            GameObjectType.SpellFocus => UseSpellFocus(player, go),
            GameObjectType.Goober => UseGoober(player, go, lockChecked: true),
            _ => GameObjectUseResult.NotUsable,
        };
        if (result == GameObjectUseResult.Ok)
        {
            Used?.Invoke(player, go);
        }

        return result;
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
    private GameObjectUseResult ActivateDoorOrButton(GameObject go, uint autoCloseSeconds)
    {
        if (go.LootState != GameObjectLootState.Ready)
        {
            return GameObjectUseResult.InUse;
        }

        go.State = go.State == GameObjectState.Ready ? GameObjectState.Active : GameObjectState.Ready;
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

        GameObjectUseResult opened = OpenChest(player, go);
        if (opened == GameObjectUseResult.Ok && key is not null)
        {
            UseUpKey(player, key);
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

    /// <summary>vmangos CannotBeUsedUnderImmunity (GameObjectDefines.h:602-619) against UNIT_FLAG_IMMUNE.</summary>
    private static bool IsRefusedForImmunity(Player player, GameObject go)
        => go.Template.CannotBeUsedUnderImmunity() && (player.UnitFlags & UnitFlags.Immune) != 0;

    private GameObjectUseResult OpenChest(Player player, GameObject go)
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
            ? Loot.OpenDurableGameObject(player, go, go.Template.GetData(1), durable)
            : Loot.OpenGameObject(player, go, go.Template.GetData(1));
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

    private GameObjectUseResult UseGoober(Player player, GameObject go, bool lockChecked = false)
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

        // GameObject::Use, goober (GameObject.cpp:1547-1575): the page text or gossip comes first; only a positive questId gates the rest.
        ShowGooberPageOrGossip(player, go);
        int questId = GooberQuestId(go);
        if (questId > 0 && Quests?.IsQuestIncomplete(player, (uint)questId) != true)
        {
            return GameObjectUseResult.NeedsQuest;
        }

        uint autoCloseSeconds = go.Template.AutoCloseSeconds();
        if (autoCloseSeconds > 0 && go.LootState != GameObjectLootState.Ready)
        {
            return GameObjectUseResult.InUse;
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
        CastGooberSpell(player, go);
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
            _despawnAt[go.Guid] = _clockMs + (despawnAfterSeconds * 1000L);
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
        IndexRitual(go, tracked: false);
        Loot?.ForgetLoot(go);
        _questFlagsSent.Remove(go.Guid);
        _despawnAt.Remove(go.Guid);
        if (go.IsSpawned)
        {
            Map.RemoveObject(go);
        }

        foreach (List<GameObject> grid in _grids.Values)
        {
            grid.Remove(go);
        }

        go.System = null;
        return true;
    }

    /// <summary>
    /// Despawn now (vmangos GO_JUST_DEACTIVATED → SetRespawnTime): a database spawn with a
    /// non-negative spawntimesecs comes back after that delay (at least one second); a negative one stays despawned
    /// until <see cref="ForceRespawn"/>; a runtime object is removed.
    /// </summary>
    public void Despawn(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        if (!Tracks(go))
        {
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
        go.RespawnAtMs = go.Spawn.SpawnTimeSeconds >= 0 ? _clockMs + RespawnDelayMs(go) : 0;
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
        Map.AddObject(go);
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

            GameObjectTemplate? template = _content.FindTemplate(spawn.Entry);
            if (template is null)
            {
                if (_warnedMissingTemplates.Add(spawn.Entry))
                {
                    _logger.LogWarning("gameobject spawn {Guid} on map {MapId} uses missing gameobject_template {Entry}; skipped", spawn.Guid, Map.MapId, spawn.Entry);
                }

                continue;
            }

            var go = new GameObject(spawn.Guid, template, spawn) { CreatedAtMs = _clockMs };
            go.RolledRespawnSeconds = RollRespawnSeconds(spawn);
            go.System = this;
            _objects[go.Guid] = go;
            IndexRitual(go, tracked: true);
            list.Add(go);
            ConfigureQuestFlags(go);
            if (DurableKeyOf(go) is { } durableKey)
            {
                // The stored chest decides: a consumed one stays despawned until its stored respawn
                // time; contents are rebuilt lazily when it is opened (never while an operation is in flight).
                _unloadedLoot.Remove(spawn.Guid);
                _respawnAt.Remove(spawn.Guid);
                if (Loot!.Durable!.Find(durableKey) is { Consumed: true } consumed && consumed.RespawnAtUnix > Loot.Durable.UnixNow)
                {
                    go.LootState = GameObjectLootState.JustDeactivated;
                    go.RespawnAtMs = consumed.RespawnAtUnix == long.MaxValue
                        ? 0 : _clockMs + Math.Max(1L, consumed.RespawnAtUnix - Loot.Durable.UnixNow) * 1000L;
                    continue;
                }

                if (spawn.SpawnTimeSeconds < 0 && Loot.Durable.Find(durableKey) is null)
                {
                    go.LootState = GameObjectLootState.JustDeactivated; // spawned by events/scripts only
                    continue;
                }

                go.ClearChangedFields();
                Map.AddObject(go);
                continue;
            }

            if (_unloadedLoot.Remove(spawn.Guid, out LootBag? remaining))
            {
                // The leftovers come back with the chest, which is still the partly looted one: its despawn timer starts again.
                Loot!.RestoreGameObjectLoot(go, remaining);
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
                continue;
            }

            if (!pending && spawn.SpawnTimeSeconds < 0)
            {
                go.LootState = GameObjectLootState.JustDeactivated; // spawned by events/scripts only
                continue;
            }

            go.ClearChangedFields();
            Map.AddObject(go);
        }
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
            IndexRitual(go, tracked: false);
            _questFlagsSent.Remove(go.Guid);
            _despawnAt.Remove(go.Guid);
            Loot?.ForgetLoot(go);
            if (go.IsSpawned)
            {
                Map.RemoveObject(go);
            }

            go.System = null;
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
        GridCoord coord = CreatureMapSystem.ComputeGrid(x, y);
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
        IndexRitual(go, tracked: true);
        GridListOf(go.X, go.Y).Add(go);
        ConfigureQuestFlags(go);
        go.ClearChangedFields();
        Map.AddObject(go);
    }

    // --- quest dynamic flags -----------------------------------------------------------------

    private void ConfigureQuestFlags(GameObject go)
    {
        bool questRelated = go.Type switch
        {
            GameObjectType.Chest => go.Template.GetData(8) != 0 || QuestLootItems(go.Template.GetData(1)).Length > 0,
            GameObjectType.Goober => go.Template.GetData(1) != 0,
            _ => false,
        };
        go.ViewerFieldFilter = questRelated ? this : null;
    }

    /// <summary>Quest items (negative chance) a chest's loot template can drop, including one level of references.</summary>
    private uint[] QuestLootItems(uint lootId)
    {
        if (lootId == 0 || Loot is null)
        {
            return [];
        }

        if (!_questLootItems.TryGetValue(lootId, out uint[]? items))
        {
            IEnumerable<LootStoreRow> rows = Loot.Content.GetRows(LootTableKind.GameObject, lootId);
            rows = rows.Concat(rows.Where(r => r.MinCountOrRef < 0)
                .SelectMany(r => Loot.Content.GetRows(LootTableKind.Reference, (uint)-r.MinCountOrRef)));
            items = [.. rows.Where(r => r.ChanceOrQuestChance < 0 && r.MinCountOrRef >= 0).Select(r => r.Item).Distinct()];
            _questLootItems[lootId] = items;
        }

        return items;
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
                || QuestLootItems(go.Template.GetData(1)).Any(item => Quests.NeedsQuestItem(viewer, item)),
            // GameObject::ActivateToQuest (GameObject.cpp:1245-1250): questId -1 activates the goober for everyone.
            GameObjectType.Goober => GooberQuestId(go) is var gq && (gq == -1 || (gq > 0 && Quests.IsQuestIncomplete(viewer, (uint)gq))),
            _ => false,
        };
        return active ? GameObjectDynFlags.Activate | GameObjectDynFlags.Sparkle : GameObjectDynFlags.None;
    }

    uint IViewerFieldFilter.Filter(WorldObject obj, int index, uint value, Player viewer)
        => index == UpdateFields.GameobjectDynFlags && obj is GameObject go ? value | (uint)QuestFlagsFor(go, viewer) : value;

    /// <summary>Re-send GAMEOBJECT_DYN_FLAGS for quest objects whose answer changed for one of their viewers.</summary>
    private void RefreshQuestFlags()
    {
        foreach (GameObject go in _objects.Values)
        {
            if (!go.IsSpawned || !ReferenceEquals(go.ViewerFieldFilter, this))
            {
                continue;
            }

            if (!_questFlagsSent.TryGetValue(go.Guid, out Dictionary<Player, uint>? sent))
            {
                _questFlagsSent[go.Guid] = sent = new(ReferenceEqualityComparer.Instance);
            }

            bool changed = false;
            foreach (Player viewer in Map.ObserversOf(go))
            {
                uint now = (uint)QuestFlagsFor(go, viewer);
                if (sent.TryGetValue(viewer, out uint before) && before != now)
                {
                    changed = true;
                }

                sent[viewer] = now;
            }

            if (changed)
            {
                go.ForceFieldUpdate(UpdateFields.GameobjectDynFlags);
            }
        }
    }
}
