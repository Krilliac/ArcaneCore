using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.World.Playerbots.Battlegrounds;

/// <summary>
/// What the queues hold now, and the rules that decide how many more bots may join them (unit tested; the coordinator fills the snapshot
/// from the online players and its own bots every few seconds). Counts are per battleground type, level bracket and team.
/// <para>
/// <b>Caps.</b> <c>World:Playerbots:Battlegrounds:MaxBotsPerBattleground</c> bounds the bots committed to one (type, bracket) at once
/// (queued, invited or on their way into a match, plus the bots of a running match that still has room, which a new queuer would join),
/// and one team never holds more than the template's <c>MaxPlayersPerTeam</c> nor more than half of the cap rounded up. A bot also
/// declines an invitation into a match that holds the cap already (<see cref="MustDecline"/>). With <c>RaiseCapToMatchMinimum</c> (on by
/// default) a type whose two teams need more than the cap, such as Alterac Valley's 20 per team, uses 2 x <c>MinPlayersPerTeam</c> as its
/// cap instead (never above 2 x <c>MaxPlayersPerTeam</c>; <see cref="EffectiveCap"/>), so bots alone can fill it.
/// </para>
/// <para>
/// <b>Real players.</b> With <c>PreferFillingRealPlayerQueues</c>, a (type, bracket) a real player waits in needs bots until both teams
/// have the template's minimum (<see cref="RealNeed"/>); those bots are chosen first and skip the chance roll. The real player's own
/// team counts what it already holds.
/// </para>
/// </summary>
internal sealed class PlayerbotBattlegroundDemand
{
    private readonly Dictionary<(BattlegroundType Type, int Bracket, int Team), int> _real = [];
    private readonly Dictionary<(BattlegroundType Type, int Bracket, int Team), int> _bots = [];
    private readonly HashSet<(BattlegroundType Type, int Bracket)> _blocked = [];
    private readonly Dictionary<(BattlegroundType Type, int Bracket), int> _matches = [];

    /// <summary>Forget the snapshot (the coordinator rebuilds it).</summary>
    internal void Clear()
    {
        _real.Clear();
        _bots.Clear();
        _blocked.Clear();
        _matches.Clear();
    }

    /// <summary>Add a real player waiting in a queue (not yet invited).</summary>
    internal void AddRealWaiting(BattlegroundType type, int bracket, Team team) => Bump(_real, type, bracket, team);

    /// <summary>Add a bot committed to a queue or on its way into a match.</summary>
    internal void AddBotCommitted(BattlegroundType type, int bracket, Team team) => Bump(_bots, type, bracket, team);

    /// <summary>A running match of this (type, bracket) already holds the cap in bots and still has room: a new queuer would be put into it.</summary>
    internal void BlockBracket(BattlegroundType type, int bracket) => _blocked.Add((type, bracket));

    internal void AddMatchInBracket(BattlegroundType type, int bracket) => _matches[(type, bracket)] = _matches.GetValueOrDefault((type, bracket)) + 1;

    internal int MatchesInBracket(BattlegroundType type, int bracket) => _matches.GetValueOrDefault((type, bracket));

    internal int RealWaiting(BattlegroundType type, int bracket, Team team) => _real.GetValueOrDefault((type, bracket, Slot(team)));

    internal int RealWaiting(BattlegroundType type, int bracket) => RealWaiting(type, bracket, Team.Alliance) + RealWaiting(type, bracket, Team.Horde);

    internal int BotsCommitted(BattlegroundType type, int bracket, Team team) => _bots.GetValueOrDefault((type, bracket, Slot(team)));

    internal int BotsCommitted(BattlegroundType type, int bracket) => BotsCommitted(type, bracket, Team.Alliance) + BotsCommitted(type, bracket, Team.Horde);

    /// <summary>Whether any real player waits in any queue.</summary>
    internal bool AnyRealWaiting => _real.Count > 0;

    internal bool AnyBotWaiting => _bots.Count > 0;

    private static int Slot(Team team) => team.Index();

    private static void Bump(Dictionary<(BattlegroundType, int, int), int> map, BattlegroundType type, int bracket, Team team)
    {
        var key = (type, bracket, Slot(team));
        map[key] = map.GetValueOrDefault(key) + 1;
    }

    // --- the rules ------------------------------------------------------------------------------------------------------

    /// <summary>The most bots one team of a (type, bracket) may commit: <c>min(MaxPlayersPerTeam, ceil(cap / 2))</c>.</summary>
    internal static int TeamAllowance(uint maxPlayersPerTeam, int maxBots)
        => maxBots <= 0 ? 0 : (int)Math.Min(maxPlayersPerTeam, (uint)((maxBots + 1) / 2));

    /// <summary>
    /// Whether one more bot of <paramref name="team"/> may queue for the (type, bracket): the bracket holds fewer than
    /// <paramref name="maxBots"/> committed bots, the team fewer than <see cref="TeamAllowance"/>, and no running match of the bracket is
    /// already full of bots.
    /// </summary>
    internal bool CanQueue(BattlegroundType type, int bracket, Team team, BattlegroundTemplate template, int maxBots, bool raiseToMinimum = true)
    {
        ArgumentNullException.ThrowIfNull(template);
        int cap = EffectiveCap(template, maxBots, raiseToMinimum);
        if (cap <= 0 || _blocked.Contains((type, bracket))) return false;
        return BotsCommitted(type, bracket) < cap && BotsCommitted(type, bracket, team) < TeamAllowance(template.MaxPlayersPerTeam, cap);
    }

    /// <summary>
    /// The bot cap that applies to a template: <paramref name="maxBots"/> (0 or less: 0). With <paramref name="raiseToMinimum"/> it is raised
    /// to 2 x <c>MinPlayersPerTeam</c> when that is larger, but never above 2 x <c>MaxPlayersPerTeam</c>; a cap that already covers the
    /// minimum is never lowered.
    /// </summary>
    internal static int EffectiveCap(BattlegroundTemplate template, int maxBots, bool raiseToMinimum)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (maxBots <= 0) return 0;
        if (!raiseToMinimum) return maxBots;
        long floor = 2L * Math.Min(template.MinPlayersPerTeam, template.MaxPlayersPerTeam);
        return (int)Math.Max(maxBots, floor);
    }

    /// <summary>
    /// The bots <paramref name="team"/> still needs so a real player's game starts: <c>max(0, MinPlayersPerTeam - realWaiting(team) -
    /// committed(team))</c>, zero when the option is off or no real player waits in the (type, bracket).
    /// </summary>
    internal int RealNeed(bool preferFilling, BattlegroundType type, int bracket, Team team, BattlegroundTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (!preferFilling) return 0;
        bool realWaiting = RealWaiting(type, bracket) > 0;
        bool botWaitingWithoutMatch = BotsCommitted(type, bracket) > 0 && MatchesInBracket(type, bracket) == 0;
        if (!realWaiting && !botWaitingWithoutMatch) return 0;
        return Math.Max(0, (int)template.MinPlayersPerTeam - RealWaiting(type, bracket, team) - BotsCommitted(type, bracket, team));
    }

    /// <summary>
    /// The chance that a free bot starts to queue within <paramref name="elapsedMs"/> when it does so with probability
    /// <paramref name="perMinute"/> a minute: <c>1 - (1 - p)^(dt / 60000)</c>.
    /// </summary>
    internal static float ChancePerThink(float perMinute, uint elapsedMs)
    {
        if (!float.IsFinite(perMinute) || perMinute <= 0f || elapsedMs == 0) return 0f;
        if (perMinute >= 1f) return 1f;
        return (float)(1d - Math.Pow(1d - perMinute, elapsedMs / 60_000d));
    }

    /// <summary>An invitation into a match that holds <paramref name="botsInMatch"/> bots (including those porting in) is declined at the cap.</summary>
    internal static bool MustDecline(int botsInMatch, int cap) => botsInMatch >= cap;

    /// <summary>
    /// The (type, bracket) a bot of <paramref name="team"/> queues for, or null. Choices where a real player waits and <paramref name="team"/>
    /// is short come first (no roll); otherwise, when <paramref name="chanceHit"/> (the bot's roll for this scan), a random one of the
    /// choices that <see cref="CanQueue"/> admits (<paramref name="pick"/> is a uniform number in 0..1).
    /// </summary>
    internal PlayerbotBattlegroundChoice? Pick(IReadOnlyList<PlayerbotBattlegroundChoice> eligible, Team team,
        Func<BattlegroundType, BattlegroundTemplate?> templates, PlayerbotBattlegroundOptions options, bool chanceHit, double pick)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled) return null;
        var open = new List<PlayerbotBattlegroundChoice>(eligible.Count);
        foreach (PlayerbotBattlegroundChoice choice in eligible)
        {
            if (templates(choice.Type) is not { } template || !CanQueue(choice.Type, choice.Bracket, team, template, options.MaxBotsPerBattleground, options.RaiseCapToMatchMinimum)) continue;
            if (RealNeed(options.PreferFillingRealPlayerQueues, choice.Type, choice.Bracket, team, template) > 0
                && (RealWaiting(choice.Type, choice.Bracket) > 0 || options.QueueChancePerMinute > 0f))
                return choice;
            open.Add(choice);
        }

        if (!chanceHit || open.Count == 0) return null;
        int index = (int)Math.Clamp(pick * open.Count, 0d, open.Count - 1);
        return open[index];
    }
}
