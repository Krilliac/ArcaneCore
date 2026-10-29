using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>
/// What a zone's value is measured against: the bot's level, team, race, class, money, place and what it has done and owns. The
/// callbacks are filled by the world thread from the live player; <see cref="Fresh"/> is the profile of a bot that has done nothing.
/// </summary>
internal sealed record PopulationBotProfile(int Level, Team Team, byte Race, byte Class, uint Money, uint MapId, float X, float Y)
{
    /// <summary>Whether the quest is rewarded (non-repeatable) or in the bot's log.</summary>
    public Func<uint, bool> QuestDone { get; init; } = _ => false;

    /// <summary>The bot's reputation rank with a faction.</summary>
    public Func<uint, ReputationRank> RankOf { get; init; } = _ => ReputationRank.Neutral;

    /// <summary>The lowest equipped item level among the slots an inventory type fits (0 = a slot is empty).</summary>
    public Func<uint, int> EquippedItemLevel { get; init; } = _ => 0;

    /// <summary>A bot of the level and team that has done nothing, is neutral with everyone, wears nothing and has no money. Race and class 0 mean "any".</summary>
    public static PopulationBotProfile Fresh(int level, Team team, byte race = 0, byte cls = 0) => new(level, team, race, cls, 0, 0, 0f, 0f);
}

/// <summary>The weights and thresholds of the zone value, from the population options.</summary>
internal sealed record PopulationValueWeights(int Quest, int Reputation, int Gear, int Farm, int Elite, int Trouble, int Distance,
    int QuestGrayMargin, int EliteSoloPct, int TopZones, int ZoneSoftCap, int CapReach)
{
    public static PopulationValueWeights From(PlayerbotPopulationOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        return new(o.ValueQuestWeight, o.ValueReputationWeight, o.ValueGearWeight, o.ValueFarmWeight, o.ValueEliteWeight, o.ValueTroubleWeight,
            o.ValueDistanceWeight, o.ValueQuestGrayMargin, o.ValueEliteSoloPct, o.ValueTopZones, o.ValueZoneSoftCap, o.ValueCapReach);
    }

    public static PopulationValueWeights Default { get; } = From(new PlayerbotPopulationOptions());
}

/// <summary>The value of one zone for one bot, split into its terms.</summary>
internal readonly record struct ZoneScore(uint ZoneId, double Quest, double Reputation, double Gear, double Farm, double Elite, double Trouble,
    double Distance)
{
    public double Total => Quest + Reputation + Gear + Farm - Elite - Trouble - Distance;

    /// <summary>Quests, reputation or gear make the zone worth going to; mobs alone do not.</summary>
    public bool Valued => Quest + Reputation + Gear > 0;
}

/// <summary>
/// Scores a zone for a bot. Pure.
/// <para>Thread affinity: any thread, over immutable facts and the profile's callbacks.</para>
/// </summary>
internal static class PopulationZoneScoring
{
    public static ZoneScore Score(PopulationZone zone, ZoneContentFacts? facts, PopulationBotProfile bot, PopulationValueWeights w,
        int recentTrouble = 0, int assigned = 0)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(w);
        double trouble = w.Trouble * Saturate(Math.Max(0, recentTrouble), Math.Max(4, assigned));
        double distance = w.Distance * DistanceFraction(zone, bot);
        if (facts is null) return new ZoneScore(zone.ZoneId, 0, 0, 0, 0, 0, trouble, distance);

        // Quests and the factions their rewards raise.
        int available = 0;
        HashSet<uint>? factions = null;
        uint raceBit = bot.Race is >= 1 and <= 32 ? 1u << (bot.Race - 1) : 0;
        uint classBit = bot.Class is >= 1 and <= 32 ? 1u << (bot.Class - 1) : 0;
        foreach (ZoneQuestFact quest in facts.Quests)
        {
            if (quest.MinLevel > bot.Level || quest.QuestLevel < bot.Level - w.QuestGrayMargin) continue;
            if (quest.RequiredRaces != 0 && raceBit != 0 && (quest.RequiredRaces & raceBit) == 0) continue;
            if (quest.RequiredClasses != 0 && classBit != 0 && (quest.RequiredClasses & classBit) == 0) continue;
            if (bot.QuestDone(quest.QuestId)) continue;
            available++;
            foreach (uint faction in quest.RepFactions)
                if (bot.RankOf(faction) < ReputationRank.Exalted) (factions ??= []).Add(faction);
        }

        foreach (ZoneRepSource source in facts.RepSources)
            if (bot.RankOf(source.FactionId) < source.MaxRank) (factions ??= []).Add(source.FactionId);

        // Gear: the inventory types (one bit per type) the zone offers an upgrade for.
        uint upgrades = 0;
        foreach (ZoneGearFact gear in facts.Gear)
        {
            if (gear.InventoryType >= 32 || (upgrades & (1u << (int)gear.InventoryType)) != 0 || gear.RequiredLevel > bot.Level) continue;
            if (gear.AllowableClass is not (0 or uint.MaxValue) && classBit != 0 && (gear.AllowableClass & classBit) == 0) continue;
            if (gear.FromVendor && (bot.RankOf(gear.RequiredFaction) < gear.RequiredRank || gear.BuyPrice > bot.Money)) continue;
            if (gear.ItemLevel > bot.EquippedItemLevel(gear.InventoryType)) upgrades |= 1u << (int)gear.InventoryType;
        }

        // Mobs the bot can fight, and how many of them are elite.
        int lowFit = bot.Level - 5, highFit = bot.Level + 3;
        int hostile = facts.HostileLevels.TryGetValue(bot.Team, out int[]? hostileLevels) ? CountInRange(hostileLevels, lowFit, highFit) : 0;
        int elite = facts.EliteLevels.TryGetValue(bot.Team, out int[]? eliteLevels) ? CountInRange(eliteLevels, lowFit, highFit) : 0;
        double solo = w.EliteSoloPct / 100.0;
        double eliteTerm = hostile == 0 || solo >= 1.0 ? 0 : w.Elite * Math.Max(0, (double)elite / hostile - solo) / (1.0 - solo);

        return new ZoneScore(zone.ZoneId,
            w.Quest * Saturate(available, 8),
            w.Reputation * Saturate(factions?.Count ?? 0, 2),
            w.Gear * Saturate(System.Numerics.BitOperations.PopCount(upgrades), 3),
            w.Farm * Saturate(hostile + facts.Skinnable / 4.0, 150),
            eliteTerm, trouble, distance);
    }

    private static double Saturate(double n, double k) => n / (n + k);

    private static double DistanceFraction(PopulationZone zone, PopulationBotProfile bot)
    {
        if (bot.MapId != zone.MapId) return 1.0;
        IReadOnlyList<WorldSafeLoc> locations = zone.LocationsFor(bot.Team);
        if (locations.Count == 0) return 1.0;
        double nearest = double.MaxValue;
        foreach (WorldSafeLoc location in locations)
        {
            double dx = location.X - bot.X, dy = location.Y - bot.Y;
            nearest = Math.Min(nearest, Math.Sqrt(dx * dx + dy * dy));
        }

        return Math.Min(1.0, nearest / 8000.0);
    }

    // The count of values in [low, high] of an ascending array.
    private static int CountInRange(int[] sorted, int low, int high) => LowerBound(sorted, high + 1) - LowerBound(sorted, low);

    private static int LowerBound(int[] sorted, int value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (sorted[mid] < value) lo = mid + 1; else hi = mid;
        }

        return lo;
    }
}
