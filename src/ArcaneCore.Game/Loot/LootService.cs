using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Loot;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Loot;

/// <summary>
/// Creature corpse, chest, skinning and container loot for the whole world: generation,
/// per-player visibility, group distribution (free-for-all and round robin), money split and the
/// CMSG_LOOT / AUTOSTORE_LOOT_ITEM / LOOT_MONEY / LOOT_RELEASE flows. Behaviour re-implemented
/// from vmangos LootMgr, Player::SendLoot and LootHandler.cpp (no code copied).
/// Chests of dungeon instances keep their generated, remaining and consumed contents with the
/// logical instance save through <see cref="Durable"/>; everything else lives in memory only.
/// Thread affinity: world thread only.
/// </summary>
public sealed partial class LootService : IViewerFieldFilter
{
    /// <summary>UNIT_DYNFLAG_LOOTABLE.</summary>
    public const uint UnitDynFlagLootable = 0x0001;

    /// <summary>ITEM_FLAG_LOOTABLE (the item opens into loot: clams, lockboxes, bags of goods).</summary>
    public const uint ItemFlagLootable = 0x0004;

    /// <summary>ITEM_FLAG_PARTY_LOOT: every looter gets a copy (vmangos LootItem::freeforall).</summary>
    public const uint ItemFlagPartyLoot = 0x0800;

    /// <summary>MAX_NR_LOOT_ITEMS.</summary>
    public const int MaxLootItems = 16;

    /// <summary>MAX_NR_QUEST_ITEMS.</summary>
    public const int MaxQuestItems = 32;

    /// <summary>MAX_MONEY_AMOUNT.</summary>
    public const uint MaxMoneyAmount = 0x7FFFFFFE;

    private readonly Dictionary<ObjectGuid, (WorldObject Source, LootBag Bag)> _bags = [];
    private readonly Dictionary<Player, LootBag> _open = new(ReferenceEqualityComparer.Instance);
    private readonly Random _random;
    private readonly ILogger _logger;

    public LootService(LootContent content, LootOptions? options = null, Random? random = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        _content = content;
        Options = options ?? new LootOptions();
        _random = random ?? new Random();
        _generator = new LootGenerator(content, _random);
        _logger = logger ?? NullLogger.Instance;
        Rolls = new LootRollManager(this, _random);
    }

    // The content is immutable; the live reload (.reload all_loot, creature_loot_template, ...) swaps the whole of it on the
    // world thread, between ticks. Loot already generated keeps the items it rolled (vmangos rolls at corpse generation too).
    private LootContent _content;
    private LootGenerator _generator;

    public LootContent Content => _content;

    public LootOptions Options { get; }

    public LootGenerator Generator => _generator;

    /// <summary>
    /// The need/greed rolls of this map's loot (group loot, need before greed). Attach it with <see cref="Maps.Map.AddUpdater"/>
    /// so the roll timers run; votes arrive through <see cref="LootRollManager.Vote"/>.
    /// </summary>
    public LootRollManager Rolls { get; }

    /// <summary>Replace the loot tables (live reload, world thread); returns the content it replaced.</summary>
    public LootContent ReplaceContent(LootContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        LootContent previous = _content;
        _generator = new LootGenerator(content, _random);
        _content = content;
        return previous;
    }

    /// <summary>Item templates (display ids, party-loot and lootable flags). Unknown items are not generated.</summary>
    public IItemTemplateStore? Items { get; set; }

    public ILootQuestJournal? Quests { get; set; }

    public ILootGroups? Groups { get; set; }

    /// <summary>The creature options the maps run with (corpse decay); defaults when the creature feature is absent.</summary>
    public CreatureOptions CreatureOptions { get; set; } = new();

    /// <summary>
    /// Evaluates a loot row's condition_id for one recipient (the world daemon wires the conditions feature here,
    /// <c>GameObjectLootFeature</c>); null (no conditions area) skips conditioned rows, as a missing row does in cmangos.
    /// A conditioned row is generated when any recipient satisfies it (see docs/areas/loot-conditions-chest-gold.md).
    /// </summary>
    public Func<Player, uint, bool>? Conditions { get; set; }

    /// <summary>
    /// The durable store of chest loot in dungeon instances. Without it (or without a live logical
    /// save for the map) such chests stay refused: opening them would reroll awards after the map is recreated.
    /// </summary>
    public ILootStateCoordinator? Durable { get; set; }

    /// <summary>The object system of this service's map (set by <see cref="GameObjectMapSystem"/>).</summary>
    internal GameObjectMapSystem? Objects { get; set; }

    /// <summary>The loot <paramref name="player"/> has open, or null.</summary>
    public LootBag? OpenLootOf(Player player) => _open.GetValueOrDefault(player);

    /// <summary>The loot generated for <paramref name="source"/> (corpse, chest, item), or null.</summary>
    public LootBag? FindLoot(ObjectGuid source) => _bags.TryGetValue(source, out var entry) ? entry.Bag : null;

    public int ActiveLootCount => _bags.Count;

    // --- generation ---------------------------------------------------------------------

    /// <summary>
    /// Build loot for <paramref name="recipients"/> from template <paramref name="entry"/>
    /// (vmangos Loot::FillLoot): normal items first (at most 16), then quest items only some
    /// recipient needs (at most 32), each visible only to those recipients.
    /// </summary>
    /// <param name="zeroEntryIsATable">
    /// Entry 0 normally means "no loot id" and yields an empty bag; fishing_loot_template entry 0 is the failed-cast junk table
    /// (vmangos Player.cpp:7692 FillLoot(0, LootTemplates_Fishing)), so the fishing area asks for it explicitly.
    /// </param>
    public LootBag Generate(ObjectGuid source, LootSourceKind kind, LootType type, LootTableKind table, uint entry, IReadOnlyList<Player> recipients,
        bool zeroEntryIsATable = false)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        var bag = new LootBag(source, kind, type);
        foreach (Player player in recipients)
        {
            bag.Recipients.Add(player.Guid);
        }

        if (entry == 0 && !zeroEntryIsATable)
        {
            return bag;
        }

        var normal = new List<(RolledLoot Roll, ItemTemplate Template)>();
        var quest = new List<(RolledLoot Roll, ItemTemplate Template, List<Player> Needing)>();
        foreach (RolledLoot roll in Generator.Roll(table, entry))
        {
            if (Items?.Find(roll.ItemId) is not { } template)
            {
                continue; // vmangos drops rows with unknown items at load
            }

            if (roll.ConditionId != 0 && (Conditions is null || !recipients.Any(p => Conditions(p, roll.ConditionId))))
            {
                continue;
            }

            if (roll.IsQuestItem)
            {
                List<Player> needing = [.. recipients.Where(p => Quests?.NeedsQuestItem(p, roll.ItemId) == true)];
                if (needing.Count > 0 && quest.Count < MaxQuestItems)
                {
                    quest.Add((roll, template, needing));
                }
            }
            else if (normal.Count < MaxLootItems)
            {
                normal.Add((roll, template));
            }
        }

        byte slot = 0;
        foreach ((RolledLoot roll, ItemTemplate template) in normal)
        {
            bag.Add(new LootItem(slot++, roll.ItemId, roll.Count, false, (template.Flags & ItemFlagPartyLoot) != 0, template.DisplayId));
        }

        foreach ((RolledLoot roll, ItemTemplate template, List<Player> needing) in quest)
        {
            var item = new LootItem(slot++, roll.ItemId, roll.Count, true, (template.Flags & ItemFlagPartyLoot) != 0, template.DisplayId);
            foreach (Player p in needing)
            {
                item.AllowedLooters.Add(p.Guid);
            }

            bag.Add(item);
        }

        return bag;
    }

    /// <summary>
    /// The players who share loot with <paramref name="looter"/> at <paramref name="source"/>:
    /// the looter plus group members at group reward distance of the source (vmangos
    /// Group::GetMemberGuids with Player::IsAtGroupRewardDistance: 2D, strict, raid maps unlimited,
    /// world bosses +150 yd, a dead member counts through his corpse; see <see cref="GroupRewardRange"/>), in group order.
    /// </summary>
    public List<Player> RecipientsFor(Player looter, WorldObject source, out Group? group)
    {
        ArgumentNullException.ThrowIfNull(looter);
        ArgumentNullException.ThrowIfNull(source);
        group = Groups?.GroupOf(looter);
        if (group is null || looter.Map is not { } map)
        {
            return [looter];
        }

        var result = new List<Player>();
        foreach (GroupMemberSlot member in group.Members)
        {
            Player? player = member.Guid == looter.Guid ? looter : map.FindPlayer(member.Guid);
            if (player is null || (!ReferenceEquals(player, looter) && !GroupRewardRange.IsAtGroupRewardDistance(player, source, Options.RewardRange)))
            {
                continue;
            }

            result.Add(player);
        }

        return result;
    }

    /// <summary>
    /// The outcome of the looter selection for one loot source: who owns the shared loot
    /// (<see cref="Owner"/>, empty = open to every recipient) and where the group's looter pointer
    /// stands afterwards (<see cref="Looter"/>, <see cref="Changed"/> = differs from before).
    /// </summary>
    internal readonly record struct LooterPlan(ObjectGuid Owner, ObjectGuid Looter, bool Changed);

    /// <summary>
    /// vmangos Unit::Kill's two Group::UpdateLooterGuid calls (Unit.cpp:1037 and 1078) without any
    /// side effect: the first (ifneed) settles who loots THIS source, the second advances the
    /// pointer for the next one. Free-for-all leaves the loot open; master loot never moves the
    /// pointer. Only round robin loot is held by the looter the pointer names (<see cref="LootBag.Owner"/>); group loot and
    /// need before greed hand their items out through rolls and master loot through the master looter's gives
    /// (<see cref="ConfigureDistribution"/>), so they carry no owner. A lone recipient needs no owner.
    /// <paramref name="durable"/> (a chest stored with its instance) keeps the legacy owner for group loot and master loot too:
    /// a stored chest has no place for roll state or a master claim yet (docs/areas/group-loot-xp.md).
    /// </summary>
    private static LooterPlan PlanLooter(Group? group, IReadOnlyList<Player> recipients, HashSet<ObjectGuid> bagRecipients, bool durable = false)
    {
        if (group is null || group.LootMethod == LootMethod.FreeForAll)
        {
            return default;
        }

        ObjectGuid current = group.LooterGuid;
        if (group.LootMethod == LootMethod.MasterLoot)
        {
            return new LooterPlan(durable && recipients.Count > 1 && bagRecipients.Contains(current) ? current : default, current, false);
        }

        LooterSelection first = GroupLooterSelection.Next(group, current, true, bagRecipients.Contains);
        LooterSelection next = GroupLooterSelection.Next(group, first.Looter, false, bagRecipients.Contains);
        bool owned = recipients.Count > 1 && (durable || group.LootMethod == LootMethod.RoundRobin);
        return new LooterPlan(owned ? first.Looter : default, next.Looter, next.Looter != current);
    }

    /// <summary>
    /// Put the shared items of a freshly generated, non-durable bag under the group's loot method (vmangos Group::GroupLoot,
    /// NeedBeforeGreed and MasterLoot set is_underthreshold per item): group loot and need before greed roll the items whose
    /// quality reaches the threshold when the loot is first opened (<see cref="LootRollManager.Start"/>); master loot leaves
    /// them to the master looter. Per-player and quest items stay under the threshold. Solo loot, free-for-all and round robin
    /// stay open, and so does master loot whose master is not among the recipients.
    /// </summary>
    private void ConfigureDistribution(LootBag bag, Group? group, IReadOnlyList<Player> recipients)
    {
        if (group is null || recipients.Count < 2 || Items is not { } items)
        {
            return;
        }

        switch (group.LootMethod)
        {
            case LootMethod.GroupLoot or LootMethod.NeedBeforeGreed:
                bag.Permission = LootPermission.Roll;
                break;
            case LootMethod.MasterLoot when bag.Recipients.Contains(group.LooterGuid):
                bag.Permission = LootPermission.Master;
                bag.MasterLooter = group.LooterGuid;
                break;
            default:
                return;
        }

        foreach (LootItem item in bag.Items)
        {
            if (!item.IsQuestItem && !item.IsPerPlayer && items.Find(item.ItemId) is { } template && template.Quality >= group.LootThreshold)
            {
                item.IsUnderThreshold = false;
            }
        }
    }

    /// <summary>
    /// vmangos Player::SendLoot touches the round-robin pointer for game objects only when the object is a
    /// chest whose template sets chest.groupLootRules (data15; Player.cpp:7680-7698, GameObjectDefines.h:277).
    /// Herb and ore nodes, fishing nodes and ordinary chests leave Group::m_looterGuid alone.
    /// </summary>
    internal static bool UsesGroupLootRules(GameObject go)
        => go.Type == GameObjectType.Chest && go.Template.GetData(15) != 0;

    /// <summary>Apply a <see cref="LooterPlan"/>: set the bag's owner and move the group's pointer, telling its members.</summary>
    private void ApplyLooterPlan(LootBag bag, Group? group, in LooterPlan plan)
    {
        bag.Owner = plan.Owner;
        CommitLooter(group, plan);
    }

    private void CommitLooter(Group? group, in LooterPlan plan)
    {
        if (group is not null && plan.Changed)
        {
            group.LooterGuid = plan.Looter;
            Groups?.LooterChanged(group);
        }
    }

    /// <summary>
    /// Unit::Kill (Unit.cpp:1041-1063): a master looter who is offline hands the role to the first
    /// online leader or assistant; with none the group switches to group loot, threshold uncommon.
    /// </summary>
    private void EnsureMasterLooterAvailable(Group? group)
    {
        if (group is not { LootMethod: LootMethod.MasterLoot } || Groups is not { } groups
            || (!group.LooterGuid.IsEmpty && groups.IsMemberOnline(group.LooterGuid)))
        {
            return;
        }

        if (GroupLooterSelection.MasterLooterFallback(group, groups.IsMemberOnline) is { } replacement)
        {
            group.LooterGuid = replacement;
        }
        else
        {
            group.LootMethod = LootMethod.GroupLoot;
            group.LootThreshold = Group.DefaultLootThreshold;
        }

        groups.LooterChanged(group);
    }

    // --- creature corpses -----------------------------------------------------------------

    /// <summary>
    /// A creature died (subscribe to <see cref="MapCombat.UnitKilled"/>): generate its corpse loot
    /// for the killing player and their group, set UNIT_DYNFLAG_LOOTABLE (shown only to allowed
    /// looters), or mark it looted out at once when nothing dropped. Combat has no tap list yet,
    /// so the killer is the loot recipient.
    /// </summary>
    public LootBag? OnCreatureKilled(Unit? killer, Unit victim)
    {
        Prune();
        if (victim is not Creature creature || killer is not Player player || !creature.IsInWorld
            || !ReferenceEquals(player.Map, creature.Map))
        {
            return null;
        }

        CreatureLootInfo? info = Content.FindCreature(creature.Entry);
        List<Player> recipients = RecipientsFor(player, creature, out Group? group);
        LootBag bag = Generate(creature.Guid, LootSourceKind.Creature, LootType.Corpse, LootTableKind.Creature, info?.LootId ?? 0, recipients);
        if (info is not null && info.MaxGold > 0)
        {
            // vmangos Loot::GenerateMoneyLoot (LootMgr.cpp:735-746), including the 8-bit shifted boss range.
            bag.Gold = Math.Min(LootMoneyRules.Generate(info.MinGold, info.MaxGold, Options.MoneyRate, _random), MaxMoneyAmount);
        }

        EnsureMasterLooterAvailable(group);
        ApplyLooterPlan(bag, group, PlanLooter(group, recipients, bag.Recipients));
        ConfigureDistribution(bag, group, recipients);
        CloseReplacedBag(creature.Guid, bag);
        _bags[creature.Guid] = (creature, bag);
        creature.ViewerFieldFilter = this;
        if (HasSkinningLoot(creature))
        {
            // vmangos Creature::SetDeathState (Creature.cpp:2274-2277): skinnable from the moment it dies with a loot recipient and a skinning template.
            creature.UnitFlags |= UnitFlags.Skinnable;
        }

        if (bag.IsEmpty)
        {
            CreatureLootedOut(creature, bag);
        }
        else
        {
            creature.SetUInt32(UpdateFields.UnitDynamicFlags, creature.GetUInt32(UpdateFields.UnitDynamicFlags) | UnitDynFlagLootable);
        }

        return bag;
    }

    /// <summary>Per-viewer UNIT_DYNFLAG_LOOTABLE (vmangos Object::BuildValuesUpdate + Player::isAllowedToLoot).</summary>
    uint IViewerFieldFilter.Filter(WorldObject obj, int index, uint value, Player viewer)
    {
        if (index != UpdateFields.UnitDynamicFlags || (value & UnitDynFlagLootable) == 0)
        {
            return value;
        }

        bool allowed = obj is Creature { DeathState: CreatureDeathState.Corpse }
            && _bags.TryGetValue(obj.Guid, out var entry) && ReferenceEquals(entry.Source, obj)
            && !entry.Bag.IsClosed && entry.Bag.HasSomethingFor(viewer);
        return allowed ? value : value & ~UnitDynFlagLootable;
    }

    /// <summary>
    /// vmangos "all loot removed from corpse": the corpse stops being lootable, becomes skinnable
    /// when it has skinning loot, and decays sooner when it does not.
    /// </summary>
    private void CreatureLootedOut(Creature creature, LootBag bag)
    {
        bag.IsClosed = true;
        creature.SetUInt32(UpdateFields.UnitDynamicFlags, creature.GetUInt32(UpdateFields.UnitDynamicFlags) & ~UnitDynFlagLootable);
        AllLootRemovedFromCorpse(creature);
    }

    /// <summary>
    /// vmangos Creature::AllLootRemovedFromCorpse (Creature.cpp:3355-3395): the tapper's skinning head start restarts; a corpse that is not
    /// (or no longer) skinnable decays sooner: the decay arithmetic is <see cref="Creature.OnAllLootRemoved"/> (a skinned one at once, an unskinned one after a third of the respawn delay or <see cref="LootOptions.LootedCorpseDecayRate"/> of the corpse delay).
    /// </summary>
    private void AllLootRemovedFromCorpse(Creature creature)
    {
        creature.SkinningForOthersMs = Creature.SkinningForOthersDefaultMs;
        if ((creature.UnitFlags & UnitFlags.Skinnable) == 0 && creature.DeathState == CreatureDeathState.Corpse)
        {
            creature.OnAllLootRemoved(Options.LootedCorpseDecayRate);
        }
    }

    /// <summary>
    /// Skinning (called by the spells area once its skinning cast succeeded; the skill check is
    /// the spell's): a looted-out skinnable corpse yields its skinning_loot_template loot to the
    /// skinner, and stops being skinnable.
    /// </summary>
    public LootResult OpenSkinning(Player player, Creature creature)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(creature);
        if (creature.DeathState != CreatureDeathState.Corpse || (creature.UnitFlags & UnitFlags.Skinnable) == 0
            || Content.FindCreature(creature.Entry) is not { SkinningLootId: not 0 } info)
        {
            return LootResult.NotLootable;
        }

        if (CheckLooter(player, creature) is var check and not LootResult.Ok)
        {
            return check;
        }

        creature.UnitFlags &= ~UnitFlags.Skinnable;
        creature.LootedForSkin = true;
        LootBag bag = Generate(creature.Guid, LootSourceKind.Skinning, LootType.Skinning, LootTableKind.Skinning, info.SkinningLootId, [player]);
        _bags[creature.Guid] = (creature, bag);
        if (!bag.IsEmpty)
        {
            // Player.cpp:7914-7928: "let reopen skinning loot if will closed".
            creature.SetUInt32(UpdateFields.UnitDynamicFlags, creature.GetUInt32(UpdateFields.UnitDynamicFlags) | UnitDynFlagLootable);
        }

        return Show(player, bag);
    }

    // --- game objects ---------------------------------------------------------------------

    /// <summary>
    /// Open a chest-like object's loot (called by <see cref="GameObjectMapSystem"/> after its use
    /// checks). The loot is generated on first open from gameobject_loot_template (chest data1)
    /// for the opener and their group, with the template's mingold..maxgold as money
    /// (<see cref="LootMoneyRules.GenerateForGameObject"/>), and persists until the object despawns.
    /// </summary>
    internal LootResult OpenGameObject(Player player, GameObject go, uint lootId)
    {
        if (go.Loot is null || !_bags.ContainsKey(go.Guid))
        {
            List<Player> recipients = RecipientsFor(player, go, out Group? group);
            LootBag fresh = Generate(go.Guid, LootSourceKind.GameObject,
                go.Type == GameObjectType.FishingNode ? LootType.Fishing : LootType.Corpse,
                LootTableKind.GameObject, lootId, recipients);
            fresh.Gold = Math.Min(LootMoneyRules.GenerateForGameObject(go.Template, lootId, Options.MoneyRate, _random), MaxMoneyAmount);
            Group? looterGroup = UsesGroupLootRules(go) ? group : null;
            ApplyLooterPlan(fresh, looterGroup, PlanLooter(looterGroup, recipients, fresh.Recipients));
            ConfigureDistribution(fresh, looterGroup, recipients);
            go.Loot = fresh;
            _bags[go.Guid] = (go, fresh);
        }

        return ShowChest(player, go, go.Loot);
    }

    private LootResult ShowChest(Player player, GameObject go, LootBag bag)
    {
        if (!bag.IsRecipient(player) && bag.Owner.IsEmpty)
        {
            // vmangos lets anyone open a chest someone else left unfinished; they share what is left.
            bag.Recipients.Add(player.Guid);
        }

        if (!bag.HasSomethingFor(player) && !bag.IsEmpty)
        {
            return LootResult.NotAllowed;
        }

        go.LootState = GameObjectLootState.Activated;
        return Show(player, bag);
    }

    // --- durable chests (dungeon instances) ---------------------------------------------------

    /// <summary>
    /// Open a chest of a dungeon instance whose consumed and remaining loot is stored with the
    /// logical instance save. The window of a freshly generated chest appears when the generation
    /// committed (<see cref="LootResult.Ok"/> means "accepted"); a stored chest restores its exact
    /// remaining contents. <see cref="LootResult.Unsupported"/> when nothing can be persisted for
    /// the key, the stored chest does not match the object or an item template is missing;
    /// <see cref="LootResult.NotAllowed"/> while an operation for the chest is in flight.
    /// </summary>
    internal LootResult OpenDurableGameObject(Player player, GameObject go, uint lootId, LootStateKey key)
    {
        if (Durable is not { } durable || go.Spawn is null || Items is null)
        {
            return LootResult.Unsupported;
        }

        if (durable.IsBlocked(key))
        {
            return durable.IsPending(key) ? LootResult.NotAllowed : LootResult.Unsupported;
        }

        LootStateRecord? record = durable.Find(key);
        if (LiveBagOf(go) is { } live)
        {
            return ShowChest(player, go, live);
        }

        if (record is { Consumed: false })
        {
            // The stored contents are authoritative; they are never rebuilt while an operation is in flight (blocked above).
            IItemTemplateStore items = Items;
            LootBag? restored = record.SourceEntry == go.Entry
                ? LootBag.FromRecord(go.Guid, LootType.Corpse, record, item => items.Find(item)?.DisplayId) : null;
            if (restored is null)
            {
                return LootResult.Unsupported;
            }

            go.Loot = restored;
            _bags[go.Guid] = (go, restored);
            return ShowChest(player, go, restored);
        }

        // Nothing stored yet, or the chest was consumed and has respawned: a new generation. The
        // group's round-robin position only moves when the generation committed. No money: the durable record has no
        // place for it yet (LootBag.ToRecord refuses a bag with gold), so a stored chest pays nothing (docs/areas/loot-conditions-chest-gold.md).
        List<Player> recipients = RecipientsFor(player, go, out Group? group);
        LootBag fresh = Generate(go.Guid, LootSourceKind.GameObject, LootType.Corpse, LootTableKind.GameObject, lootId, recipients);
        fresh.DurableKey = key;
        Group? looterGroup = UsesGroupLootRules(go) ? group : null;
        LooterPlan plan = PlanLooter(looterGroup, recipients, fresh.Recipients, durable: true);
        fresh.Owner = plan.Owner;

        LootStateRecord updated = fresh.ToRecord(key, go.Entry, (record?.Generation ?? 0) + 1, RespawnAtUnix(go, durable.UnixNow));
        var operation = new LootOperation
        {
            Key = key,
            Expected = record,
            Updated = updated,
            Finished = (outcome, live, _) => FinishGeneration(outcome, live, player, key, fresh, looterGroup, plan),
        };
        return durable.TryStart(operation) ? LootResult.Ok : LootResult.NotAllowed;
    }

    /// <summary>The bag currently registered for the object (not a stale reference left on it), or null.</summary>
    private LootBag? LiveBagOf(GameObject go)
        => go.Loot is { } bag && _bags.TryGetValue(go.Guid, out var entry) && ReferenceEquals(entry.Bag, bag) ? bag : null;

    /// <summary>When a consumed chest is available again: Despawn's rule (a negative spawntimesecs never respawns by itself).</summary>
    private static long RespawnAtUnix(GameObject go, long now)
        => go.Spawn!.SpawnTimeSeconds >= 0 ? now + Math.Max(1L, go.Spawn.SpawnTimeSeconds) : long.MaxValue;

    private void FinishGeneration(LootOutcome outcome, bool live, Player opener, LootStateKey key, LootBag fresh, Group? group, LooterPlan plan)
    {
        if (!live)
        {
            return;
        }

        if (outcome != LootOutcome.After)
        {
            if (outcome != LootOutcome.Unknown && opener.IsInWorld)
            {
                Refuse(opener, fresh.Source);
            }

            return;
        }

        CommitLooter(group, plan);

        // The object that tracks the spawn now may be a new one (its grid reloaded meanwhile).
        if (Objects?.FindBySpawn(key.SpawnGuid) is not { IsSpawned: true } go || LiveBagOf(go) is not null)
        {
            return;
        }

        go.Loot = fresh;
        _bags[go.Guid] = (go, fresh);
        if (opener.IsInWorld && CheckLooter(opener, go) == LootResult.Ok)
        {
            ShowChest(opener, go, fresh);
        }

        if (fresh.Viewers.Count == 0)
        {
            go.System?.OnLootReleased(go, fresh);
        }
    }

    /// <summary>Plan an item take of a durable chest: the committed successor record and its award, computed from the committed record.</summary>
    private static bool TryPlanTake(LootBag live, LootStateRecord record, Player player, LootItem item, long respawnAt,
        out LootStateRecord updated, out LootAward award)
    {
        int id = LootBag.CharacterIdOf(player.Guid);
        int owner = LootBag.CharacterIdOf(live.Owner);
        award = new LootAward(id, item.Slot, item.ItemId, item.Count);
        updated = record;
        if (owner != 0 && owner != record.LootOwnerCharacterId)
        {
            return false; // the live round-robin owner can only have been released, never reassigned
        }

        // Like ShowChest, a late opener of an ownerless chest joins its recipients when taking.
        List<int> recipients = [.. record.Recipients];
        if (recipients.Count != 0 && !recipients.Contains(id) && owner == 0)
        {
            recipients.Add(id);
        }

        LootStateRecord? replayed = LootStateRules.Replay(record, recipients, owner, [award], respawnAt);
        if (replayed is null)
        {
            return false;
        }

        updated = replayed;
        return true;
    }

    private InventoryResult TakeDurableItem(Player player, LootBag bag, LootItem item, LootStateKey key, ILootStateCoordinator durable)
    {
        if (!_bags.TryGetValue(bag.Source, out var entry) || entry.Source is not GameObject { Spawn: not null } go
            || durable.IsBlocked(key) || !player.CanMutateQuestSettlementState || durable.Find(key) is not { } record
            || !TryPlanTake(bag, record, player, item, RespawnAtUnix(go, durable.UnixNow), out LootStateRecord updated, out LootAward award))
        {
            return RefuseTake(player, item);
        }

        InventoryResult staged = player.Inventory.TryStageQuestRewards([new InventoryRewardGrant(item.ItemId, item.Count)],
            out InventoryRewardStage? stage, out _);
        if (staged != InventoryResult.Ok || stage is null)
        {
            player.Inventory.SendEquipError(staged, null, null, 0, item.ItemId);
            return staged;
        }

        if (durable.CreateActor(player, stage.Before, stage.After) is not { } actor)
        {
            return RefuseTake(player, item);
        }

        var operation = new LootOperation
        {
            Key = key,
            Expected = record,
            Updated = updated,
            Awards = [award],
            Actor = actor,
            PublishActor = () => player.Inventory.ApplyQuestRewardInventory(stage),
            Finished = (outcome, live, current) => FinishTake(outcome, live, current, player, key, item, updated, stage),
        };
        return durable.TryStart(operation) ? InventoryResult.Ok : RefuseTake(player, item);
    }

    private static InventoryResult RefuseTake(Player player, LootItem item)
    {
        InventoryResult refused = item.IsLooted ? InventoryResult.AlreadyLooted : InventoryResult.LootCantLootThatNow;
        player.Inventory.SendEquipError(refused, null, null);
        return refused;
    }

    private void FinishTake(LootOutcome outcome, bool live, bool actorCurrent, Player player, LootStateKey key, LootItem item,
        LootStateRecord updated, InventoryRewardStage stage)
    {
        if (!live)
        {
            return;
        }

        GameObject? go = Objects?.FindBySpawn(key.SpawnGuid);
        LootBag? bag = go is not null && LiveBagOf(go) is { } registered && registered.DurableKey == key ? registered : null;
        if (outcome == LootOutcome.After)
        {
            if (actorCurrent)
            {
                player.Inventory.NotifyLootInventory(stage);
            }

            if (bag is not null)
            {
                // The registered bag, not the one captured when the take began: it may have been replaced meanwhile.
                bag.ApplyRecord(updated);
                PersistOwnerRelease(bag); // released while the take was in flight: store it now
                byte[] removed = LootPackets.Removed(item.Slot);
                if (item.IsQuestItem || item.IsPerPlayer)
                {
                    if (actorCurrent)
                    {
                        player.Session.Send(WorldOpcode.SmsgLootRemoved, removed);
                    }
                }
                else
                {
                    foreach (Player viewer in bag.Viewers)
                    {
                        viewer.Session.Send(WorldOpcode.SmsgLootRemoved, removed);
                    }
                }
            }
            else
            {
                Objects?.OnDurableRecordCommitted(updated);
            }

            if (actorCurrent)
            {
                Quests?.ItemLooted(player, item.ItemId, item.Count);
            }
        }
        else if (actorCurrent && outcome != LootOutcome.Unknown)
        {
            player.Inventory.SendEquipError(InventoryResult.LootCantLootThatNow, null, null);
        }

        if (bag is not null && go is not null && bag.Viewers.Count == 0)
        {
            go.System?.OnLootReleased(go, bag);
        }
    }

    /// <summary>
    /// A round-robin owner released leftovers: everybody may loot them now. Stored best effort
    /// (without an actor); a take that follows closely carries the cleared owner itself.
    /// </summary>
    private void PersistOwnerRelease(LootBag bag)
    {
        if (bag.DurableKey is not { } key || Durable is not { } durable || durable.IsBlocked(key) || durable.Find(key) is not { } record
            || record.Consumed || record.LootOwnerCharacterId == 0 || !bag.Owner.IsEmpty)
        {
            return;
        }

        LootStateRecord? released = LootStateRules.Replay(record, record.Recipients, 0, [], 0);
        if (released is not null)
        {
            durable.TryStart(new LootOperation { Key = key, Expected = record, Updated = released, Finished = static (_, _, _) => { } });
        }
    }

    /// <summary>Forget a despawned object's loot (vmangos clears it on respawn).</summary>
    internal void ForgetLoot(WorldObject source)
    {
        if (_bags.TryGetValue(source.Guid, out var entry) && ReferenceEquals(entry.Source, source))
        {
            CloseForViewers(entry.Bag);
            _bags.Remove(source.Guid);
        }
    }

    /// <summary>Rebind retained chest contents to the new object created when its grid loads.</summary>
    internal void RestoreGameObjectLoot(GameObject source, LootBag bag)
    {
        if (bag.Source != source.Guid || bag.Kind != LootSourceKind.GameObject
            || bag.Viewers.Count != 0 || _bags.ContainsKey(source.Guid))
        {
            throw new InvalidOperationException("retained chest loot does not match an unobserved source");
        }

        source.Loot = bag;
        _bags.Add(source.Guid, (source, bag));
    }

    // --- items -----------------------------------------------------------------------------

    /// <summary>
    /// CMSG_OPEN_ITEM (vmangos HandleOpenItemOpcode): with an <see cref="ItemLoot"/> collaborator (the item loot area, whose generated loot is saved
    /// with the inventory) the item opens into its loot window; without one an ITEM_FLAG_LOOTABLE item is refused (fail closed), because loot that
    /// cannot be kept across a login would be rerolled.
    /// </summary>
    public LootResult OpenItem(Player player, Item item)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(item);
        if (!player.IsAlive)
        {
            return LootResult.Dead;
        }

        if ((item.Template.Flags & ItemFlagLootable) == 0)
        {
            return LootResult.NotLootable;
        }

        if (ItemLoot is { } source)
        {
            return source.Open(player, item);
        }

        if (item.Template.LockId != 0)
        {
            player.Inventory.SendEquipError(InventoryResult.ItemLocked, item, null);
            return LootResult.Locked;
        }

        player.Inventory.SendEquipError(InventoryResult.LootCantLootThatNow, item, null);
        return LootResult.NotAllowed;
    }

    // --- CMSG_LOOT and the loot window -----------------------------------------------------

    /// <summary>
    /// CMSG_LOOT (vmangos HandleLootOpcode → Player::SendLoot for a corpse): the corpse must be
    /// in the looter's map, within loot distance, and hold something this player may take;
    /// otherwise the window is refused with SMSG_LOOT_RELEASE_RESPONSE.
    /// </summary>
    public LootResult Open(Player player, ObjectGuid guid)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (!_bags.TryGetValue(guid, out var entry) || entry.Source is not Creature creature
            || !ReferenceEquals(creature.Map, player.Map) || entry.Bag.Kind is not (LootSourceKind.Creature or LootSourceKind.Skinning)
            || creature.DeathState != CreatureDeathState.Corpse)
        {
            Refuse(player, guid);
            return LootResult.NotFound;
        }

        if (CheckLooter(player, creature) is var check and not LootResult.Ok)
        {
            Refuse(player, guid);
            return check;
        }

        if (entry.Bag.IsClosed || !entry.Bag.HasSomethingFor(player))
        {
            Refuse(player, guid);
            return LootResult.NotAllowed;
        }

        return Show(player, entry.Bag);
    }

    private LootResult CheckLooter(Player player, WorldObject source)
    {
        if (!player.IsAlive)
        {
            return LootResult.Dead;
        }

        if (!ReferenceEquals(player.Map, source.Map) || Distance3D(player, source) > Options.LootDistance)
        {
            return LootResult.TooFar;
        }

        return LootResult.Ok;
    }

    private LootResult Show(Player player, LootBag bag)
    {
        if (_open.TryGetValue(player, out LootBag? previous) && !ReferenceEquals(previous, bag))
        {
            Release(player, previous.Source);
        }

        _open[player] = bag;
        bag.Viewers.Add(player);
        player.UnitFlags |= UnitFlags.Looting;
        if (bag.Permission == LootPermission.Roll && !bag.RollsStarted)
        {
            Rolls.Start(player, bag); // vmangos Player::SendLoot starts the rolls before the window is sent
        }

        player.Session.Send(WorldOpcode.SmsgLootResponse, LootPackets.LootResponse(bag, player));
        if (bag.Permission == LootPermission.Master && bag.MasterLooter == player.Guid)
        {
            SendMasterList(player, bag);
        }

        return LootResult.Ok;
    }

    /// <summary>
    /// SMSG_LOOT_MASTER_LIST to the master looter: the group members who may be given items (the recipients of this loot that
    /// are within reward distance now). The reference cores also send it to every other member in range; only the master
    /// acts on it, so ArcaneCore sends it to the master alone.
    /// </summary>
    private void SendMasterList(Player master, LootBag bag)
    {
        if (!_bags.TryGetValue(bag.Source, out var entry))
        {
            return;
        }

        List<ObjectGuid> eligible = [];
        foreach (Player candidate in RecipientsFor(master, entry.Source, out _))
        {
            if (bag.Recipients.Contains(candidate.Guid))
            {
                eligible.Add(candidate.Guid);
            }
        }

        master.Session.Send(WorldOpcode.SmsgLootMasterList, GroupLootPackets.MasterList(eligible));
    }

    /// <summary>The object a registered bag belongs to, or null when the bag is no longer the one registered for its source.</summary>
    internal WorldObject? SourceOf(LootBag bag)
        => _bags.TryGetValue(bag.Source, out var entry) && ReferenceEquals(entry.Bag, bag) ? entry.Source : null;

    private static void Refuse(Player player, ObjectGuid guid)
        => player.Session.Send(WorldOpcode.SmsgLootReleaseResponse, LootPackets.ReleaseResponse(guid));

    /// <summary>
    /// CMSG_AUTOSTORE_LOOT_ITEM (vmangos HandleAutostoreLootItemOpcode): take the stack in
    /// <paramref name="slot"/> of the open loot. Refused when the source is gone or out of range,
    /// the slot is not this player's to take, or the bags are full (the item stays).
    /// </summary>
    public InventoryResult TakeItem(Player player, byte slot)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!_open.TryGetValue(player, out LootBag? bag))
        {
            return InventoryResult.LootCantLootThatNow;
        }

        if (!SourceStillValid(player, bag))
        {
            Release(player, bag.Source);
            return InventoryResult.LootCantLootThatNow;
        }

        LootItem? item = bag.FindSlot(slot);
        if (item is null || bag.SlotFor(player, item) != LootSlotType.AllowLoot)
        {
            InventoryResult refused = item is { IsLooted: true } ? InventoryResult.AlreadyLooted : InventoryResult.LootCantLootThatNow;
            player.Inventory.SendEquipError(refused, null, null);
            return refused;
        }

        if (bag.DurableKey is { } durableKey && Durable is { } durable)
        {
            return TakeDurableItem(player, bag, item, durableKey, durable);
        }

        InventoryResult result = AwardItem(player, bag, item);
        if (result != InventoryResult.Ok)
        {
            player.Inventory.SendEquipError(result, null, null, 0, item.ItemId);
        }

        return result;
    }

    /// <summary>
    /// Move one stack of an in-memory bag into <paramref name="recipient"/>'s bags: the shared tail of a plain take, a roll win
    /// and a master give. On a refusal nothing changes and the store result is returned for the caller's report; otherwise the
    /// stack is marked taken, the viewers' windows drop the slot (only the taker's for a quest or per-player copy) and the quest
    /// journal and the bag's change callback hear of it. World thread.
    /// </summary>
    internal InventoryResult AwardItem(Player recipient, LootBag bag, LootItem item)
    {
        InventoryResult result = recipient.Inventory.AddItem(item.ItemId, item.Count, out _, received: false, created: false, showInChat: true);
        if (result != InventoryResult.Ok)
        {
            return result;
        }

        bag.MarkTaken(item, recipient);
        item.RollActive = false;
        item.Winner = default;
        byte[] removed = LootPackets.Removed(item.Slot);
        if (item.IsQuestItem || item.IsPerPlayer)
        {
            recipient.Session.Send(WorldOpcode.SmsgLootRemoved, removed);
        }
        else
        {
            foreach (Player viewer in bag.Viewers)
            {
                viewer.Session.Send(WorldOpcode.SmsgLootRemoved, removed);
            }
        }

        Quests?.ItemLooted(recipient, item.ItemId, item.Count);
        RefreshLootable(bag);
        bag.Changed?.Invoke(bag);
        return InventoryResult.Ok;
    }

    /// <summary>
    /// An item left the bag while nobody has its window open (a roll resolved): when that emptied it, the corpse stops being
    /// lootable or the chest settles, exactly as the last viewer's release would have done.
    /// </summary>
    internal void SettleUnviewed(LootBag bag)
    {
        if (bag.Viewers.Count != 0 || !bag.IsEmpty || !_bags.TryGetValue(bag.Source, out var entry) || !ReferenceEquals(entry.Bag, bag))
        {
            return;
        }

        switch (entry.Source)
        {
            case Creature creature when bag.Kind == LootSourceKind.Creature:
                CreatureLootedOut(creature, bag);
                break;
            case GameObject go:
                go.System?.OnLootReleased(go, bag);
                break;
        }
    }

    /// <summary>
    /// CMSG_LOOT_MASTER_GIVE (vmangos HandleLootMasterGiveOpcode): the master looter of a master-loot group gives the item in
    /// <paramref name="slot"/> of the loot he has open to <paramref name="targetGuid"/>. The target must be a recipient of
    /// this loot within reward distance (the master list); a refusal of the target's bags tells the master with the error form
    /// of SMSG_LOOT_RESPONSE and leaves the item in the window (vmangos instead stamps the item with the target as winner).
    /// Not the master: the window is closed like vmangos does. Anything else that does not apply is ignored.
    /// </summary>
    public MasterGiveResult GiveMasterLoot(Player master, ObjectGuid lootGuid, byte slot, ObjectGuid targetGuid)
    {
        ArgumentNullException.ThrowIfNull(master);
        Group? group = Groups?.GroupOf(master);
        if (group is not { LootMethod: LootMethod.MasterLoot } || group.LooterGuid != master.Guid)
        {
            Release(master, lootGuid);
            return MasterGiveResult.NotMaster;
        }

        if (!_open.TryGetValue(master, out LootBag? bag) || bag.Source != lootGuid || bag.Permission != LootPermission.Master || bag.MasterLooter != master.Guid)
        {
            return MasterGiveResult.NotApplicable;
        }

        if (!SourceStillValid(master, bag))
        {
            Release(master, bag.Source);
            return MasterGiveResult.NotApplicable;
        }

        if (bag.FindSlot(slot) is not { IsLooted: false, IsQuestItem: false, IsPerPlayer: false } item || !item.Winner.IsEmpty)
        {
            return MasterGiveResult.NotApplicable;
        }

        WorldObject source = _bags[bag.Source].Source;
        Player? target = master.Map?.FindPlayer(targetGuid);
        if (target is null || !bag.Recipients.Contains(target.Guid) || !GroupRewardRange.IsAtGroupRewardDistance(target, source, Options.RewardRange))
        {
            master.Session.Send(WorldOpcode.SmsgLootResponse, GroupLootPackets.LootErrorResponse(lootGuid, LootError.PlayerNotFound));
            return MasterGiveResult.TargetNotEligible;
        }

        InventoryResult stored = AwardItem(target, bag, item);
        if (stored == InventoryResult.Ok)
        {
            return MasterGiveResult.Given;
        }

        (MasterGiveResult outcome, LootError error) = stored switch
        {
            InventoryResult.InventoryFull => (MasterGiveResult.TargetInventoryFull, LootError.MasterInventoryFull),
            InventoryResult.CantCarryMoreOfThis => (MasterGiveResult.TargetUnique, LootError.MasterUniqueItem),
            _ => (MasterGiveResult.TargetOther, LootError.MasterOther),
        };
        master.Session.Send(WorldOpcode.SmsgLootResponse, GroupLootPackets.LootErrorResponse(lootGuid, error));
        return outcome;
    }

    /// <summary>
    /// CMSG_LOOT_MONEY (vmangos HandleLootMoneyOpcode): corpse money is split evenly between the
    /// recipients still in the map within the group loot distance (each gets
    /// SMSG_LOOT_MONEY_NOTIFY with their share; the remainder is lost, as in vmangos); other loot
    /// pays the looter. Chest money (the template's mingold..maxgold) pays the looter too: the reference splits every non-item
    /// loot among the looter's group members in range (mangos WorldHandlers/LootHandler.cpp:344-385), but a chest's recipients
    /// also include ungrouped late openers, so that split waits for a group-membership check (docs/areas/loot-conditions-chest-gold.md).
    /// Every viewer then gets SMSG_LOOT_CLEAR_MONEY.
    /// </summary>
    public bool TakeMoney(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!_open.TryGetValue(player, out LootBag? bag) || bag.Gold == 0
            || !(bag.Owner.IsEmpty || bag.Owner == player.Guid) || !bag.IsRecipient(player))
        {
            return false;
        }

        if (!SourceStillValid(player, bag))
        {
            Release(player, bag.Source);
            return false;
        }

        if (!player.CanMutateQuestSettlementState)
        {
            return false;
        }

        _bags.TryGetValue(bag.Source, out var entry);
        var sharers = new List<Player>();
        if (bag.Kind == LootSourceKind.Creature && bag.ShareMoney && bag.Recipients.Count > 1 && player.Map is { } map && entry.Source is { } source)
        {
            foreach (ObjectGuid guid in bag.Recipients)
            {
                if (map.FindPlayer(guid) is { } member
                    && (ReferenceEquals(member, player) || Distance3D(member, source) <= Options.GroupLootDistance))
                {
                    // A temporary settlement hold cannot change this recipient's allocation.
                    // Defer the whole split before changing balances or consuming bag gold.
                    if (!member.CanMutateQuestSettlementState)
                    {
                        return false;
                    }

                    sharers.Add(member);
                }
            }
        }

        if (sharers.Count > 1)
        {
            uint share = bag.Gold / (uint)sharers.Count;
            foreach (Player member in sharers)
            {
                GiveMoney(member, share);
                member.Session.Send(WorldOpcode.SmsgLootMoneyNotify, LootPackets.MoneyNotify(share));
            }
        }
        else
        {
            GiveMoney(player, bag.Gold);
        }

        bag.Gold = 0;
        foreach (Player viewer in bag.Viewers)
        {
            viewer.Session.Send(WorldOpcode.SmsgLootClearMoney, []);
        }

        RefreshLootable(bag);
        bag.Changed?.Invoke(bag);
        return true;
    }

    private void GiveMoney(Player player, uint amount)
    {
        player.Money = (uint)Math.Min((ulong)player.Money + amount, MaxMoneyAmount);
        Quests?.MoneyLooted(player);
    }

    /// <summary>
    /// CMSG_LOOT_RELEASE (vmangos HandleLootReleaseOpcode → DoLootRelease): close the window,
    /// clear UNIT_FLAG_LOOTING and settle the source: a looted-out corpse stops being lootable; a
    /// round-robin looter releasing leftovers opens them to the whole group; a looted-out item is
    /// destroyed; a chest goes back to ready or despawns when empty.
    /// </summary>
    public void Release(Player player, ObjectGuid guid)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!_open.TryGetValue(player, out LootBag? bag) || bag.Source != guid)
        {
            // vmangos answers a release of a guid it does not track too: the client closes.
            player.Session.Send(WorldOpcode.SmsgLootReleaseResponse, LootPackets.ReleaseResponse(guid));
            return;
        }

        _open.Remove(player);
        bag.Viewers.Remove(player);
        player.UnitFlags &= ~UnitFlags.Looting;
        player.Session.Send(WorldOpcode.SmsgLootReleaseResponse, LootPackets.ReleaseResponse(guid));
        if (!_bags.TryGetValue(guid, out var entry) || !ReferenceEquals(entry.Bag, bag))
        {
            return;
        }

        if (bag.ReleaseHandler is { } handler)
        {
            handler.OnReleased(player, bag); // special source (fishing bobber/hole, pickpocket, disenchant): see LootService.Special.cs
            return;
        }

        switch (entry.Source)
        {
            case Creature creature when bag.Kind == LootSourceKind.Creature:
                if (bag.IsEmpty)
                {
                    CreatureLootedOut(creature, bag);
                }
                else if (bag.Owner == player.Guid)
                {
                    bag.Owner = default; // vmangos: the round robin player released; everyone may loot
                    creature.ForceFieldUpdate(UpdateFields.UnitDynamicFlags);
                }

                break;

            case Creature skinned when bag.Kind == LootSourceKind.Skinning:
                if (bag.IsEmpty)
                {
                    // DoLootRelease (LootHandler.cpp:574-580): nothing left, the corpse stops being lootable and, skinned, goes at once.
                    bag.IsClosed = true;
                    skinned.SetUInt32(UpdateFields.UnitDynamicFlags, skinned.GetUInt32(UpdateFields.UnitDynamicFlags) & ~UnitDynFlagLootable);
                    AllLootRemovedFromCorpse(skinned);
                    _bags.Remove(guid);
                }

                break;

            case GameObject go:
                if (bag.Owner == player.Guid && !bag.IsEmpty)
                {
                    bag.Owner = default;
                    PersistOwnerRelease(bag);
                }

                // A durable take in flight decides the chest's fate (empty or not) when it finishes.
                if (bag.Viewers.Count == 0 && !(bag.DurableKey is { } pending && Durable?.IsPending(pending) == true))
                {
                    go.System?.OnLootReleased(go, bag);
                }

                break;

            case Item item:
                if (bag.IsEmpty)
                {
                    _bags.Remove(guid);
                    if (player.Inventory.GetItemByGuid(item.Guid) is { } owned)
                    {
                        player.Inventory.DestroyItemCount(owned, owned.Count);
                    }
                }

                break;
        }
    }

    /// <summary>The player left the world or the map: close their window without a reply (vmangos logout DoLootRelease).</summary>
    public void OnPlayerLeft(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (_open.TryGetValue(player, out LootBag? bag))
        {
            Release(player, bag.Source);
        }
    }

    private bool SourceStillValid(Player player, LootBag bag)
    {
        if (!_bags.TryGetValue(bag.Source, out var entry) || !ReferenceEquals(entry.Bag, bag))
        {
            return false;
        }

        if (bag.SourceCheck is { } custom)
        {
            return custom(player);
        }

        return entry.Source switch
        {
            Creature creature => creature.DeathState == CreatureDeathState.Corpse && CheckLooter(player, creature) == LootResult.Ok,
            GameObject go => go.IsSpawned && (bag.IgnoreDistance ? IsAliveInSameMap(player, go) : CheckLooter(player, go) == LootResult.Ok),
            Item item => player.Inventory.GetItemByGuid(item.Guid) is not null,
            _ => false,
        };
    }

    /// <summary>Re-send UNIT_DYNAMIC_FLAGS so every viewer re-evaluates its lootable bit.</summary>
    private void RefreshLootable(LootBag bag)
    {
        if (bag.Kind == LootSourceKind.Creature && _bags.TryGetValue(bag.Source, out var entry) && entry.Source is Creature creature)
        {
            creature.ForceFieldUpdate(UpdateFields.UnitDynamicFlags);
        }
    }

    private void CloseForViewers(LootBag bag)
    {
        foreach (Player viewer in bag.Viewers.ToArray())
        {
            _open.Remove(viewer);
            viewer.UnitFlags &= ~UnitFlags.Looting;
            viewer.Session.Send(WorldOpcode.SmsgLootReleaseResponse, LootPackets.ReleaseResponse(bag.Source));
        }

        bag.Viewers.Clear();
    }

    /// <summary>Drop loot whose corpse respawned or left the world.</summary>
    private void Prune()
    {
        List<ObjectGuid>? stale = null;
        foreach ((ObjectGuid guid, (WorldObject source, LootBag bag)) in _bags)
        {
            bool gone = source switch
            {
                // A special bag (a pickpocketed live creature) is kept by its own area, never swept as a stale corpse.
                Creature c when bag.ReleaseHandler is null => !c.IsInWorld || c.DeathState != CreatureDeathState.Corpse,
                _ => false,
            };
            if (gone)
            {
                (stale ??= []).Add(guid);
                CloseForViewers(bag);
            }
        }

        if (stale is not null)
        {
            foreach (ObjectGuid guid in stale)
            {
                _bags.Remove(guid);
            }

            _logger.LogDebug("pruned {Count} stale loot entries", stale.Count);
        }
    }

    internal static float Distance3D(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        float d = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz)) - a.BoundingRadius - b.BoundingRadius;
        return d > 0 ? d : 0;
    }
}
