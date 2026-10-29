using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;

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
        => throw new NotImplementedException();
}
