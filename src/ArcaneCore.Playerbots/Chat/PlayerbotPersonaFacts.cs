using System.Globalization;
using System.Text.RegularExpressions;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Social;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Group = ArcaneCore.Game.Groups.Group;
using ArcaneCore.World.Bots.Progression;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// Reads a bot's real state into <see cref="BotChatFacts"/> for its chat persona (world thread only; the chat workers never touch the
/// world). Called when a line is answered, when the conversation director plans a conversation, and when a world channel line is
/// weighed: never per tick. The work is bounded: the equipped slots, the bags (at most 4 bags and the backpack), the quest log, and at
/// most <see cref="MaxNearbyLooks"/> of the objects the bot sees.
/// </summary>
internal static partial class PlayerbotPersonaFacts
{
    /// <summary>The most visible objects looked at for nearby players and creatures.</summary>
    internal const int MaxNearbyLooks = 64;

    /// <summary>Real players within this many yards are "nearby".</summary>
    internal const float NearbyYards = 40f;

    /// <summary>Food or drink beyond this many is spare.</summary>
    internal const int OwnConsumables = 20;

    /// <summary>Trade goods (cloth, herbs, ore, leather) in a stack of at least this many are spare.</summary>
    internal const int SpareTradeGoods = 5;

    private const uint ItemClassConsumable = 0, ItemClassWeapon = 2, ItemClassArmor = 4, ItemClassTradeGoods = 7, ItemClassQuest = 12;

    /// <summary>
    /// The facts of <paramref name="player"/>. <paramref name="questId"/> is the quest it works on (0: none); <paramref name="heard"/> is the
    /// line being answered, read for a place it asks about ("where is Goldshire?").
    /// </summary>
    internal static BotChatFacts Build(WorldSession session, Player player, uint questId, string? heard = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        SpellStore? spells = Try(() => session.Services.GetService<SpellFeature>()?.System.Store);
        SocialContext? social = Try(() => session.Services.GetService<SocialFeature>()?.Context);
        QuestNpcServices? quests = Try(() => session.Services.GetService<QuestNpcFeature>()?.Services);

        (int health, int? mana) = Vitals(player);
        (int averageLevel, IReadOnlyList<string> notable) = Gear(player);
        Bags bags = ReadBags(player, spells);
        PlayerbotTalentBuild? build = player.Level >= 10 ? PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low) : null;
        BotChatRole role = RoleOf(player.Class, build);
        (string? objective, bool ready, IReadOnlyList<string> questTitles) = QuestFacts(session, player, quests, questId);
        (IReadOnlyList<string> nearPlayers, string? nearCreature) = Nearby(session, player);
        MapTemplate? map = Try(() => player.Map?.Template);
        Group? group = social?.Groups.GetGroup(player.Guid);
        var members = new List<string>();
        if (group is not null)
            foreach (GroupMemberSlot slot in group.Members)
                if (slot.Guid != player.Guid && session.World.FindOnlinePlayer(slot.Guid) is { } member) members.Add(member.Name);

        var facts = new BotChatFacts
        {
            HealthPct = health,
            ManaPct = mana,
            Dead = !player.IsAlive,
            InCombat = player.Combat.IsInCombat,
            AverageItemLevel = averageLevel,
            NotableItems = notable,
            BagFree = bags.Free,
            BagTotal = bags.Total,
            Food = bags.Food,
            Drink = bags.Drink,
            Money = player.Money,
            Surplus = bags.Surplus,
            Objective = objective,
            QuestReady = ready,
            Quests = questTitles,
            Role = role,
            Spec = build is { Name: not "none" } b ? b.Name.Replace('-', ' ') : null,
            Guild = social?.Guilds.GetGuildOf(player)?.Name,
            NearbyPlayers = nearPlayers,
            NearbyCreature = nearCreature,
            Instance = map is { Instanceable: true } ? map.Name : null,
            InBattleground = map is { IsBattleground: true },
            GroupMembers = members,
            CanInvite = group is null || (group.IsLeader(player.Guid) && !group.IsFull),
            Place = heard is { Length: > 0 } ? PlaceAsked(session, player, heard) : null,
            SuitedDungeon = PlayerbotDungeons.SuitedFor(player.Level)?.Name,
        };
        return facts with { Needs = NeedsOf(player, facts, bags, questId, quests) };
    }

    /// <summary>Health and mana in percent (mana null for a class without it).</summary>
    internal static (int Health, int? Mana) Vitals(Player player)
    {
        int health = player.MaxHealth == 0 ? 0 : (int)((ulong)player.Health * 100 / player.MaxHealth);
        uint maxMana = player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana);
        int? mana = player.PowerType == PowerType.Mana && maxMana > 0 ? (int)(SpellSystem.GetPower(player, PowerType.Mana) * 100UL / maxMana) : null;
        return (Math.Clamp(health, 0, 100), mana is { } m ? Math.Clamp(m, 0, 100) : null);
    }

    /// <summary>The average item level of the worn gear (shirt and tabard left out) and the best three items' names.</summary>
    internal static (int Average, IReadOnlyList<string> Notable) Gear(Player player)
    {
        long sum = 0;
        int count = 0;
        var worn = new List<ItemTemplate>(19);
        foreach ((byte slot, Item item) in player.Inventory.Equipped)
        {
            if (slot is InventorySlots.Body or InventorySlots.Tabard) continue;
            ItemTemplate template = item.Template;
            sum += template.ItemLevel;
            count++;
            if (!string.IsNullOrWhiteSpace(template.Name)) worn.Add(template);
        }

        IReadOnlyList<string> notable = [.. worn.Where(t => t.Quality >= 2).OrderByDescending(t => t.Quality).ThenByDescending(t => t.ItemLevel)
            .Select(t => t.Name).Distinct(StringComparer.Ordinal).Take(3)];
        return (count == 0 ? 0 : (int)(sum / count), notable);
    }

    /// <summary>What the bags hold, as the chat needs it.</summary>
    internal sealed record Bags(int Free, int Total, int Food, int Drink, IReadOnlyList<BotChatItem> Surplus);

    internal static Bags ReadBags(Player player, SpellStore? spells)
    {
        int free = 0, total = 0, food = 0, drink = 0;
        var stacks = new Dictionary<uint, (ItemTemplate Template, int Count)>();
        void Look(Item? item)
        {
            total++;
            if (item is null)
            {
                free++;
                return;
            }

            ItemTemplate template = item.Template;
            int count = (int)Math.Max(1u, item.Count);
            if (template.Class == ItemClassConsumable && spells is not null && PlayerbotConsumables.TryClassify(template, spells, out PlayerbotConsumableKind kind))
            {
                if (kind == PlayerbotConsumableKind.Food) food += count;
                else if (kind == PlayerbotConsumableKind.Drink) drink += count;
            }

            if (item.IsSoulBound || template.Class == ItemClassQuest || string.IsNullOrWhiteSpace(template.Name)) return;
            stacks[template.Entry] = stacks.TryGetValue(template.Entry, out (ItemTemplate Template, int Count) seen) ? (template, seen.Count + count) : (template, count);
        }

        for (byte slot = InventorySlots.ItemStart; slot < InventorySlots.ItemEnd; slot++) Look(player.Inventory.GetItem(InventorySlots.Bag0, slot));
        for (byte bagSlot = InventorySlots.BagStart; bagSlot < InventorySlots.BagEnd; bagSlot++)
        {
            if (player.Inventory.GetItem(InventorySlots.Bag0, bagSlot) is not Container container) continue;
            for (byte s = 0; s < container.Size; s++) Look(container[s]);
        }

        var surplus = new List<BotChatItem>();
        foreach ((ItemTemplate template, int count) in stacks.Values)
        {
            bool spare = template.Class switch
            {
                ItemClassTradeGoods => count >= SpareTradeGoods,
                ItemClassWeapon or ItemClassArmor => template.Quality >= 2 && template.Bonding != 1,
                ItemClassConsumable => count > OwnConsumables,
                _ => false,
            };
            if (spare) surplus.Add(new BotChatItem(template.Entry, template.Name, count, template.Quality, template.SellPrice));
        }

        surplus.Sort((a, b) => b.Quality != a.Quality ? b.Quality.CompareTo(a.Quality) : b.Count.CompareTo(a.Count));
        return new Bags(free, total, food, drink, surplus.Count > 6 ? surplus.GetRange(0, 6) : surplus);
    }

    /// <summary>The bot's group role: a tank or healer build, or a druid's feral build (bear tank); everything else deals damage.</summary>
    internal static BotChatRole RoleOf(Class @class, PlayerbotTalentBuild? build)
    {
        if (build is null) return BotChatRole.Damage;
        if (ReferenceEquals(build.Weights, PlayerbotStatWeights.ShieldTank)) return BotChatRole.Tank;
        if (ReferenceEquals(build.Weights, PlayerbotStatWeights.Healer)) return BotChatRole.Healer;
        return ReferenceEquals(build.Weights, PlayerbotStatWeights.Feral) && @class == Class.Druid ? BotChatRole.Tank : BotChatRole.Damage;
    }

    /// <summary>What the bot lacks, most pressing first (<see cref="BotChatNeed"/>).</summary>
    internal static IReadOnlyList<BotChatNeed> NeedsOf(Player player, BotChatFacts facts, Bags bags, uint questId, QuestNpcServices? quests)
    {
        var needs = new List<BotChatNeed>(4);
        if (!facts.Dead && !facts.InCombat && facts.HealthPct < 33) needs.Add(BotChatNeed.Heal);
        if (!facts.Dead && facts.ManaPct is < 20) needs.Add(BotChatNeed.Mana);
        if (bags.Total > 0 && bags.Free <= 2) needs.Add(BotChatNeed.BagSpace);
        bool manaClass = facts.ManaPct is not null;
        if (manaClass && bags.Drink == 0 && player.Level >= 5) needs.Add(BotChatNeed.Water);
        if (bags.Food == 0 && player.Level >= 5 && player.Class is not Class.Mage) needs.Add(BotChatNeed.Food);
        if (player.Level >= 40 && player.Skills is { } skills && !skills.Has(SkillIds.Riding)) needs.Add(BotChatNeed.Mount);
        if (player.Level >= 10 && player.Money < (uint)player.Level * 300) needs.Add(BotChatNeed.Gold);
        if (questId != 0 && quests?.Quests.Get(questId) is { } quest && (quest.Template.SuggestedPlayers > 1 || quest.Template.Type is 1 or 81 or 62))
            needs.Add(BotChatNeed.Group);
        return needs;
    }

    /// <summary>The current quest's first open objective with its progress, whether every objective is done, and the other quests' titles.</summary>
    internal static (string? Objective, bool Ready, IReadOnlyList<string> Titles) QuestFacts(WorldSession session, Player player, QuestNpcServices? quests,
        uint questId)
    {
        if (quests is null || quests.StateOf(player) is not { } state) return (null, false, []);
        var titles = new List<string>(5);
        foreach ((uint id, QuestStatusData data) in state.Quests.Statuses)
        {
            if (data.Rewarded || data.Status is not (QuestStatus.Incomplete or QuestStatus.Complete)) continue;
            if (id != questId && quests.Quests.Get(id)?.Title is { Length: > 0 } title) titles.Add(title);
            if (titles.Count >= 5) break;
        }

        if (questId == 0 || quests.Quests.Get(questId) is not { } quest || state.Quests.Get(questId) is not { } status) return (null, false, titles);
        if (status.Status == QuestStatus.Complete) return (null, true, titles);
        CreatureContent? creatures = Try(() => session.Services.GetService<CreatureWorldFeature>()?.Content);
        IItemTemplateStore? items = Try(() => session.Services.GetService<IItemTemplateStore>());
        bool open = false;
        for (int i = 0; i < quest.ReqCreatureOrGOId.Count && i < status.CreatureOrGOCount.Length; i++)
        {
            uint need = i < quest.ReqCreatureOrGOCount.Count ? quest.ReqCreatureOrGOCount[i] : 0;
            if (need == 0) continue;
            uint have = status.CreatureOrGOCount[i];
            if (have >= need) continue;
            open = true;
            string? what = i < quest.ObjectiveText.Count && quest.ObjectiveText[i] is { Length: > 0 } text ? text
                : quest.ReqCreatureOrGOId[i] > 0 ? creatures?.FindTemplate((uint)quest.ReqCreatureOrGOId[i])?.Name : null;
            if (what is { Length: > 0 }) return (string.Create(CultureInfo.InvariantCulture, $"{what} {have}/{need}"), false, titles);
        }

        for (int i = 0; i < quest.ReqItemId.Count && i < status.ItemCount.Length; i++)
        {
            uint need = i < quest.ReqItemCount.Count ? quest.ReqItemCount[i] : 0;
            if (quest.ReqItemId[i] == 0 || need == 0) continue;
            uint have = status.ItemCount[i];
            if (have >= need) continue;
            open = true;
            if (items?.Find(quest.ReqItemId[i])?.Name is { Length: > 0 } name)
                return (string.Create(CultureInfo.InvariantCulture, $"{name} {have}/{need}"), false, titles);
        }

        return (null, !open && status.Status != QuestStatus.Incomplete, titles);
    }

    /// <summary>Real players near the bot (up to three names) and a notable creature it sees or targets (an elite, a rare, a boss).</summary>
    internal static (IReadOnlyList<string> Players, string? Creature) Nearby(WorldSession session, Player player)
    {
        if (player.Map is not { } map) return ([], null);
        var players = new List<string>(3);
        string? creature = null;
        if (map.FindObject(player.Selection) is Creature target && target.IsAlive && Notable(target)) creature = target.Template.Name;
        int looked = 0;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (++looked > MaxNearbyLooks || (players.Count >= 3 && creature is not null)) break;
            WorldObject? seen = map.FindObject(guid);
            if (seen is null || Distance(player, seen) > NearbyYards) continue;
            if (seen is Player other)
            {
                if (players.Count < 3 && other.Session is WorldSession { IsManaged: false }) players.Add(other.Name);
            }
            else if (creature is null && seen is Creature mob && mob.IsAlive && Notable(mob))
            {
                creature = mob.Template.Name;
            }
        }

        return (players, creature);
    }

    /// <summary>An elite, rare or boss creature (vmangos creature_template.rank 1..4).</summary>
    private static bool Notable(Creature creature) => creature.Template.Rank is >= 1 and <= 4 && !string.IsNullOrWhiteSpace(creature.Template.Name);

    /// <summary>
    /// The place a line asks about ("where is Goldshire", "wheres the deadmines", "how do i get to Booty Bay"), found by name in the
    /// server's area data (the bot's continent first), or null when the line asks about none or no area has that name.
    /// </summary>
    internal static BotChatPlace? PlaceAsked(WorldSession session, Player player, string heard)
    {
        if (AskedPlace(heard) is not { } wanted) return null;
        AreaTable? areas = Try(() => Game.Maps.Templates.WorldMaps.Of(session.World).Areas);
        if (areas is null || areas.Count == 0) return null;
        AreaTemplate? best = null;
        foreach (AreaTemplate area in areas.All)
        {
            if (string.IsNullOrWhiteSpace(area.Name)) continue;
            bool exact = string.Equals(Plain(area.Name), wanted, StringComparison.Ordinal);
            if (!exact) continue;
            if (best is null || (area.MapId == player.MapId && best.MapId != player.MapId) || (area.IsZone && !best.IsZone && area.MapId == best.MapId)) best = area;
        }

        if (best is null) return null;
        string? zone = best.IsZone ? null : areas.GetById(best.ZoneId)?.Name;
        string? mapName = best.MapId switch
        {
            0 => "the Eastern Kingdoms",
            1 => "Kalimdor",
            _ => Try(() => Game.Maps.Templates.WorldMaps.Of(session.World).Registry.Find(best.MapId)?.Name),
        };
        uint zoneId = best.IsZone ? best.Entry : best.ZoneId;
        return new BotChatPlace(best.Name, zone, mapName, zoneId != 0 && zoneId == player.ZoneId);
    }

    /// <summary>The place name a line asks about, plain (lower case, letters, digits and spaces; a leading "the" kept off), or null.</summary>
    internal static string? AskedPlace(string heard)
    {
        Match match = WhereIs().Match(heard.ToLowerInvariant());
        if (!match.Success) return null;
        string name = Plain(match.Groups["place"].Value);
        if (name.StartsWith("the ", StringComparison.Ordinal)) name = name[4..];
        return name.Length is >= 3 and <= 40 && name is not ("you" or "u" or "it" or "that" or "this" or "he" or "she" or "everyone") ? name : null;
    }

    private static string Plain(string text)
    {
        string lower = Punct().Replace(text.ToLowerInvariant().Replace("'", string.Empty, StringComparison.Ordinal), " ");
        lower = Spaces().Replace(lower, " ").Trim();
        return lower.StartsWith("the ", StringComparison.Ordinal) ? lower[4..] : lower;
    }

    /// <summary>A service read that fails soft (a minimal host without the feature, or one not attached yet).</summary>
    private static T? Try<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (InvalidOperationException error)
        {
            FaultLedger.Shared.Record("playerbot.chat-prompts", error);
            return null;
        }
    }

    [GeneratedRegex(@"\b(?:where(?:'?s| is| r| are)|how (?:do|can) (?:i|you|u|we) get to|how to get to|which way (?:is|to)|directions to)\s+(?<place>[a-z' ]{3,40}?)\s*(?:\?|$|,|\.|!| from| at| exactly)")]
    private static partial Regex WhereIs();

    [GeneratedRegex(@"[^a-z0-9 ]")] private static partial Regex Punct();

    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
}
