using System.Globalization;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.Bots.Progression;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>
/// The dungeon choice of a quest goal (<see cref="PlayerbotDungeonFit"/>, docs/areas/playbots-groups.md, dungeon goals): the dungeons that
/// hold an objective creature are collected together and the best is taken (in the bot's level band first, else the nearest band with a
/// reason); a bot outside every band without a reason waits for no dungeon. Live logs showed level 58-60 bots queueing ~1300 Wailing
/// Caverns groups for a level-21 quest because the first dungeon by map id was always taken.
/// </summary>
public sealed partial class PlayerbotGroupCoordinator
{
    /// <summary>How long a bot's answer to "does this dungeon drop an upgrade for it" is kept.</summary>
    internal const uint GearFitMemoryMs = 600_000;

    /// <summary>One dungeon that holds an objective creature, the way into it and where the creature stands.</summary>
    private readonly record struct DungeonCandidate(uint MapId, MapTemplate Instance, AreaTriggerTemplate Entrance, AreaTriggerTeleport Teleport,
        CreatureSpawn Spawn, uint Entry, PlayerbotDungeonBand Band);

    private readonly List<DungeonCandidate> _candidatesScratch = [];
    private readonly List<PlayerbotDungeonBand> _bandsScratch = [];
    private readonly uint[] _singleEntry = new uint[1];

    // A bot that passed over a dungeon for a quest, so its refusal is noted once per SetAsideMs (pruned in Scan).
    private readonly Dictionary<(Guid Bot, uint Quest, uint Map), uint> _passedOver = [];

    // Attunement: the quests and items that gate an instance entrance (areatrigger_teleport required_quest_done, required_item, required_item2).
    private IReadOnlyCollection<AreaTriggerTemplate>? _attuneTriggers;
    private readonly HashSet<uint> _attuneQuests = [];
    private readonly HashSet<uint> _attuneItems = [];

    // Gear upgrade: the items the creatures of a dungeon map drop (per loot and creature content), and each bot's answer per map.
    private LootContent? _gearLoot;
    private CreatureContent? _gearCreatures;
    private readonly Dictionary<uint, uint[]> _gearItemsByMap = [];
    private readonly Dictionary<(Guid Bot, uint Map), (uint Until, bool Fits)> _gearFits = [];

    /// <summary>Forget the per-bot fit memory that has run out (called from <see cref="Scan"/>).</summary>
    private void PruneFit(uint now)
    {
        foreach (KeyValuePair<(Guid Bot, uint Quest, uint Map), uint> passed in _passedOver)
            if (unchecked((int)(passed.Value - now)) <= 0) _passedOver.Remove(passed.Key);
        foreach (KeyValuePair<(Guid Bot, uint Map), (uint Until, bool Fits)> gear in _gearFits)
            if (unchecked((int)(gear.Value.Until - now)) <= 0) _gearFits.Remove(gear.Key);
    }

    private void ClearFit()
    {
        _candidatesScratch.Clear();
        _bandsScratch.Clear();
        _passedOver.Clear();
        _attuneTriggers = null;
        _attuneQuests.Clear();
        _attuneItems.Clear();
        _gearLoot = null;
        _gearCreatures = null;
        _gearItemsByMap.Clear();
        _gearFits.Clear();
    }

    /// <summary>
    /// The instance goal for the objective creatures <paramref name="entries"/> of <paramref name="quest"/>: every dungeon that holds one of
    /// them and can be entered from the bot's map is a candidate, and <see cref="PlayerbotDungeonFit.Choose"/> takes the best (in the bot's band
    /// first, else the nearest band with a reason for the bot). Null when none can be entered, none is allowed, or the dungeon is set aside for the bot;
    /// a refusal is noted once per <see cref="SetAsideMs"/> per bot, quest and dungeon. World thread; not reentrant (it uses scratch lists).
    /// </summary>
    private PlayerbotGroupGoal? InstanceGoalFor(Guid botId, Player player, WorldMaps maps, CreatureContent creatures, Quest quest, IReadOnlyList<uint> entries, int flagged)
    {
        (uint MapId, MapTemplate Instance)[] dungeonMaps = GetDungeonMapsWithSpawns(creatures, maps.Registry);
        List<DungeonCandidate> found = _candidatesScratch;
        found.Clear();
        var playerAt = new Vector3(player.X, player.Y, player.Z);
        for (int e = 0; e < entries.Count; e++)
        {
            uint entry = entries[e];
            for (int i = 0; i < dungeonMaps.Length; i++)
            {
                (uint mapId, MapTemplate instance) = dungeonMaps[i];
                IReadOnlyList<CreatureSpawn> inside = creatures.GetSpawns(mapId, entry);
                if (inside.Count == 0) continue;
                if (instance.IsRaid && !_options.Groups.RaidsEnabled) continue;
                if (IsSetAside(botId, (instance.IsRaid ? PlayerbotGroupGoalKind.Raid : PlayerbotGroupGoalKind.Dungeon, mapId))) continue;
                if (EntranceTo(maps, player.MapId, mapId, playerAt) is not { } entrance) continue;
                if (maps.FindAreaTriggerTeleport(entrance.Id) is not { } teleport) continue;
                found.Add(new DungeonCandidate(mapId, instance, entrance, teleport, NearestSpawn(inside, teleport.TargetX, teleport.TargetY), entry, default));
            }
        }

        if (found.Count == 0) return null;

        IReadOnlyList<PlayerbotRunPlan> plans = Catalogue().Plans;
        PlayerbotRunOptions runs = _options.Groups.DungeonRuns;
        int level = player.Level;
        bool reasonRequired = _options.Groups.DungeonOutOfBandNeedsReason;
        var options = new (PlayerbotDungeonBand Band, PlayerbotDungeonReasons Reasons)[found.Count];
        for (int i = 0; i < found.Count; i++)
        {
            DungeonCandidate candidate = found[i];
            _bandsScratch.Clear();
            for (int p = 0; p < plans.Count; p++)
            {
                if (plans[p].MapId != candidate.MapId) continue;
                (byte min, byte max) = plans[p].Band(runs);
                _bandsScratch.Add(new PlayerbotDungeonBand(min, max));
            }

            PlayerbotDungeonBand band = PlayerbotDungeonFit.BandOf(candidate.MapId, candidate.Teleport.RequiredLevel, _bandsScratch);
            found[i] = candidate with { Band = band };
            // The reasons cost scans over the waiting bots and the loot: only a bot outside the band needs them.
            options[i] = (band, band.Holds(level) ? PlayerbotDungeonReasons.None : ReasonsFor(botId, player, quest, found[i], maps, creatures));
        }

        int? pick = PlayerbotDungeonFit.Choose(level, options, reasonRequired);
        if (pick is null)
        {
            uint now = Now;
            for (int i = 0; i < found.Count; i++)
            {
                (Guid, uint, uint) key = (botId, quest.Id, found[i].MapId);
                if (_passedOver.TryGetValue(key, out uint until) && unchecked((int)(until - now)) > 0) continue;
                _passedOver[key] = unchecked(now + SetAsideMs);
                Note($"{player.Name} passes over {found[i].Instance.Name} for quest {quest.Id}: {PlayerbotDungeonFit.Judge(level, options[i].Band, options[i].Reasons, reasonRequired).Trace}");
            }

            return null;
        }

        DungeonCandidate chosen = found[pick.Value];
        PlayerbotDungeonVerdict verdict = PlayerbotDungeonFit.Judge(level, chosen.Band, options[pick.Value].Reasons, reasonRequired);
        MapTemplate chosenInstance = chosen.Instance;
        int size = chosenInstance.IsRaid ? Math.Max(PlayerbotGroupContent.InstanceGroupSize(chosenInstance), flagged)
            : Math.Max(flagged >= 2 ? Math.Min(flagged, PlayerbotGroupContent.PartySize) : PlayerbotGroupContent.InstanceGroupSize(chosenInstance), 2);
        return new PlayerbotGroupGoal(chosenInstance.IsRaid ? PlayerbotGroupGoalKind.Raid : PlayerbotGroupGoalKind.Dungeon, quest.Id, chosen.Entry,
            chosen.MapId, player.MapId, new Vector3(chosen.Entrance.X, chosen.Entrance.Y, chosen.Entrance.Z), chosen.Entrance.Id,
            new Vector3(chosen.Spawn.X, chosen.Spawn.Y, chosen.Spawn.Z), size, Math.Max(chosen.Teleport.RequiredLevel, quest.MinLevel), "instance",
            Fit: verdict.InBand ? null : verdict.Trace);
    }

    /// <summary>
    /// Why the bot would run the dungeon of <paramref name="candidate"/> although its level is outside the band. The cheap reasons come first
    /// and end the search (the quest is not grey for the bot, the quest is an attunement step); then a companion or a forming group waiting for
    /// the dungeon, then a gear upgrade it drops. <see cref="PlayerbotDungeonReasons.Farming"/> is never produced: bots have no reputation or
    /// farming goal to ask.
    /// </summary>
    private PlayerbotDungeonReasons ReasonsFor(Guid botId, Player player, Quest quest, in DungeonCandidate candidate, WorldMaps maps, CreatureContent creatures)
    {
        var reasons = PlayerbotDungeonReasons.None;
        if (PlayerbotDungeonFit.QuestQualifies(player.Level, quest.QuestLevel)) reasons |= PlayerbotDungeonReasons.Quest;
        if (IsAttunementQuest(quest, maps)) reasons |= PlayerbotDungeonReasons.Attunement;
        if (reasons != PlayerbotDungeonReasons.None) return reasons;

        (PlayerbotGroupGoalKind, uint) key = (candidate.Instance.IsRaid ? PlayerbotGroupGoalKind.Raid : PlayerbotGroupGoalKind.Dungeon, candidate.MapId);
        if (HasCompanion(botId, player, key)) reasons |= PlayerbotDungeonReasons.Companion;
        if (HasFormingGroup(botId, player, key)) reasons |= PlayerbotDungeonReasons.FormingGroup;
        if (DropsUpgrade(botId, player, candidate.MapId, creatures)) reasons |= PlayerbotDungeonReasons.GearUpgrade;
        return reasons;
    }

    /// <summary>
    /// Whether <paramref name="quest"/> is a key or attunement step: its id is the <c>required_quest_done</c> of an area trigger teleport, or
    /// an item it asks for or rewards is such a teleport's <c>required_item</c> or <c>required_item2</c>. The sets are built from the teleports
    /// once per trigger table (rebuilt on a reload).
    /// </summary>
    private bool IsAttunementQuest(Quest quest, WorldMaps maps)
    {
        IReadOnlyCollection<AreaTriggerTemplate> triggers = maps.AreaTriggers;
        if (!ReferenceEquals(_attuneTriggers, triggers))
        {
            _attuneQuests.Clear();
            _attuneItems.Clear();
            foreach (AreaTriggerTemplate trigger in triggers)
            {
                if (maps.FindAreaTriggerTeleport(trigger.Id) is not { } teleport) continue;
                if (teleport.RequiredQuestDone != 0) _attuneQuests.Add(teleport.RequiredQuestDone);
                if (teleport.RequiredItem != 0) _attuneItems.Add(teleport.RequiredItem);
                if (teleport.RequiredItem2 != 0) _attuneItems.Add(teleport.RequiredItem2);
            }

            _attuneTriggers = triggers;
        }

        if (_attuneQuests.Contains(quest.Id)) return true;
        if (_attuneItems.Count == 0) return false;
        for (int i = 0; i < quest.ReqItemId.Count; i++)
            if (quest.ReqItemId[i] != 0 && _attuneItems.Contains(quest.ReqItemId[i])) return true;
        for (int i = 0; i < quest.RewItemId.Count; i++)
            if (quest.RewItemId[i] != 0 && _attuneItems.Contains(quest.RewItemId[i])) return true;
        return false;
    }

    private SocialContext? SocialOrNull()
    {
        try
        {
            return services.GetService<SocialFeature>()?.Context;
        }
        catch (InvalidOperationException ex)
        {
            FaultLedger.Shared.Record("playerbot.groups", ex);
            return null;
        }
    }

    /// <summary>The two share a guild, or <paramref name="other"/> is on the bot's own friend list (the invite policy's social grounds).</summary>
    private static bool IsCompanion(SocialContext social, Player player, Player other)
        => PlayerbotGroupInvites.SameGuild(social, player, other) || PlayerbotGroupInvites.OnBotsFriendList(social, player, other);

    /// <summary>
    /// A guildmate or friend of the same team already waits for the goal <paramref name="key"/>, or is a member of a bot group whose goal is
    /// that dungeon.
    /// </summary>
    private bool HasCompanion(Guid botId, Player player, (PlayerbotGroupGoalKind, uint) key)
    {
        if (_waiting.Count == 0 && _groups.Count == 0) return false;
        if (SocialOrNull() is not { } social) return false;
        foreach ((Guid otherId, Waiting waiting) in _waiting)
        {
            if (otherId == botId || waiting.Team != player.Team || waiting.Goal.Key != key) continue;
            if (TryPlayer(otherId, out Player? other) && IsCompanion(social, player, other)) return true;
        }

        if (_world is not { } world) return false;
        foreach (BotGroup group in _groups)
        {
            if (group.Goal.MapId != key.Item2 || group.Goal.Kind != key.Item1) continue;
            foreach (Member member in group.Members)
                if (world.FindOnlinePlayer(member.Guid) is { } other && other.Team == player.Team && !ReferenceEquals(other, player) && IsCompanion(social, player, other))
                    return true;
        }

        return false;
    }

    /// <summary>A bot of the same team already waits for the goal <paramref name="key"/>, within <see cref="PlayerbotGroupOptions.LevelRange"/> of this bot.</summary>
    private bool HasFormingGroup(Guid botId, Player player, (PlayerbotGroupGoalKind, uint) key)
    {
        foreach ((Guid otherId, Waiting waiting) in _waiting)
        {
            if (otherId == botId || waiting.Team != player.Team || waiting.Goal.Key != key) continue;
            if (TryPlayer(otherId, out Player? other) && Math.Abs(other.Level - player.Level) <= _options.Groups.LevelRange) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a creature that spawns on the dungeon map <paramref name="mapId"/> drops an item of at least uncommon quality that the bot could
    /// wear for a gain (<see cref="PlayerbotGearScoreSeam.Gain"/> above <see cref="PlayerbotItemScore.MinimumGain"/>). The item set of a map is
    /// built once per loot and creature content (the drop index inverted through the map's spawns); the bot's answer is kept for
    /// <see cref="GearFitMemoryMs"/>, and the scan stops at the first upgrade, so a bot costs at most one pass over a map's items per ten minutes.
    /// </summary>
    private bool DropsUpgrade(Guid botId, Player player, uint mapId, CreatureContent creatures)
    {
        if (services.GetService<GameObjects.GameObjectLootFeature>()?.LootContent is not { } loot) return false;
        if (!ReferenceEquals(_gearLoot, loot) || !ReferenceEquals(_gearCreatures, creatures))
        {
            _gearItemsByMap.Clear();
            _gearFits.Clear();
            _gearLoot = loot;
            _gearCreatures = creatures;
        }

        uint now = Now;
        if (_gearFits.TryGetValue((botId, mapId), out (uint Until, bool Fits) known) && unchecked((int)(known.Until - now)) > 0) return known.Fits;

        if (!_gearItemsByMap.TryGetValue(mapId, out uint[]? items))
        {
            var onMap = new HashSet<uint>();
            foreach (CreatureSpawn spawn in creatures.GetSpawns(mapId)) onMap.Add(spawn.Entry);
            var dropped = new List<uint>();
            foreach ((uint item, uint[] droppers) in PlayerbotDropIndex.Index(loot))
            {
                for (int i = 0; i < droppers.Length; i++)
                {
                    if (!onMap.Contains(droppers[i])) continue;
                    dropped.Add(item);
                    break;
                }
            }

            dropped.Sort();
            _gearItemsByMap[mapId] = items = [.. dropped];
        }

        bool fits = false;
        for (int i = 0; i < items.Length && !fits; i++)
        {
            if (player.Inventory.Templates.Find(items[i]) is not { Quality: >= 2 } template || template.RequiredLevel > player.Level) continue;
            fits = PlayerbotGearScoreSeam.Gain(player, template) is { } gain && gain > PlayerbotItemScore.MinimumGain;
        }

        _gearFits[(botId, mapId)] = (unchecked(now + GearFitMemoryMs), fits);
        return fits;
    }
}
