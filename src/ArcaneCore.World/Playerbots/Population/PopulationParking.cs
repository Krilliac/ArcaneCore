using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>
/// The world-thread facts about one running managed bot that a pass decides on. <see cref="Busy"/> is "cannot be stopped now" (not in
/// the world, in combat, being teleported); <see cref="Dead"/> (dead or a ghost), <see cref="Sick"/> (resurrection sickness, spell
/// 15007) and <see cref="Stalled"/> (a stall or death loop is reported, <see cref="PlayerbotStallWatch"/>) mark a bot that earns
/// little or nothing right now, which is the first one to park.
/// </summary>
internal sealed record PopulationBotFacts(Guid BotId, byte Level, Team Team, uint ZoneId, bool Autonomous, bool Busy, bool PlayerInZone, bool Vetoed,
    bool Dead = false, bool Sick = false, bool Stalled = false, byte Race = 0, byte Class = 0, uint MapId = 0, float X = 0, float Y = 0);

/// <summary>A bot chosen to be parked and why: <c>dead</c>, <c>sick</c>, <c>stalled</c>, <c>surplus</c> (its band holds more than its share) or <c>rotation</c>.</summary>
internal sealed record PopulationParkPick(PopulationBotFacts Bot, string Reason);

/// <summary>Which running bots may be parked, in which order, and which parked bots come back first.</summary>
internal static class PopulationParking
{
    /// <summary>
    /// The bots to park, best first. Only bots the population owns that are autonomous (not scripted, in a real player's party or in a
    /// bot-led group) and not busy (in the world, out of combat, not teleporting) qualify; a dead bot or a ghost qualifies.
    /// <para>
    /// Order, least productive first: a dead bot or ghost, then a resurrection-sick one, then a stalled one, then the rest; within
    /// that the band with the largest surplus (a bot in no band counts as the largest), then a bot with no real player in its zone,
    /// then the bot that has run longest since the population last started it (one started before this process or by an operator
    /// counts as the longest), then the lower BotId. The surplus is recounted after each pick, so a band is not drained below its
    /// share while another still has a surplus; band fullness only ever breaks ties between equally productive bots.
    /// </para>
    /// <para>
    /// Anti-churn: a bot the population started less than <paramref name="minRunMs"/> ago is left out when
    /// <paramref name="keepRecent"/> (a tick-budget park), and goes last otherwise (an over-target park: the target is the operator's
    /// limit and is honoured).
    /// </para>
    /// </summary>
    public static IReadOnlyList<PopulationParkPick> Order(IReadOnlyCollection<PopulationBotFacts> running, IReadOnlySet<Guid> owned,
        IReadOnlyList<PopulationBand> bands, int[] desired, int[] have, IReadOnlyDictionary<Guid, long> startedAt, long nowMs = 0,
        long minRunMs = 0, bool keepRecent = false)
    {
        bool Recent(PopulationBotFacts f) => minRunMs > 0 && startedAt.TryGetValue(f.BotId, out long at) && nowMs - at < minRunMs;

        var remaining = running.Where(f => owned.Contains(f.BotId) && f.Autonomous && !f.Busy && !(keepRecent && Recent(f))).ToList();
        var counts = (int[])have.Clone();
        var ordered = new List<PopulationParkPick>(remaining.Count);
        while (remaining.Count > 0)
        {
            PopulationBotFacts best = remaining
                .OrderBy(Recent)
                .ThenBy(Tier)
                .ThenByDescending(f => Surplus(f, bands, desired, counts))
                .ThenBy(f => f.PlayerInZone)
                .ThenBy(f => startedAt.GetValueOrDefault(f.BotId))
                .ThenBy(f => f.BotId)
                .First();
            remaining.Remove(best);
            string reason = Tier(best) switch
            {
                0 => "dead",
                1 => "sick",
                2 => "stalled",
                _ => Surplus(best, bands, desired, counts) > 0 ? "surplus" : "rotation",
            };
            ordered.Add(new PopulationParkPick(best, reason));
            int band = PopulationBandPlanner.BandOf(bands, best.Level);
            if (band >= 0) counts[band]--;
        }

        return ordered;
    }

    /// <summary>
    /// The order parked bots are restarted in: a rotation, the one parked longest ago first, whatever its band, so no band's parked bots
    /// wait while another band's come and go. A bot not parked by this process (stopped before a restart, or by an operator) counts as
    /// parked longest; ties fall to the older <c>UpdatedUnix</c> of its membership row, then the lower BotId.
    /// </summary>
    public static IEnumerable<PlayerbotPopulationMember> RestartOrder(IEnumerable<PlayerbotPopulationMember> parked, IReadOnlyDictionary<Guid, long> parkedAt)
        => parked.OrderBy(m => parkedAt.TryGetValue(m.BotId, out long at) ? at : long.MinValue).ThenBy(m => m.UpdatedUnix).ThenBy(m => m.BotId);

    private static int Tier(PopulationBotFacts bot) => bot.Dead ? 0 : bot.Sick ? 1 : bot.Stalled ? 2 : 3;

    private static int Surplus(PopulationBotFacts bot, IReadOnlyList<PopulationBand> bands, int[] desired, int[] counts)
    {
        int band = PopulationBandPlanner.BandOf(bands, bot.Level);
        return band < 0 ? int.MaxValue : counts[band] - desired[band];
    }
}
