namespace ArcaneCore.World.Playerbots.Battlegrounds;

/// <summary>
/// <c>World:Playerbots:Battlegrounds</c>: autonomous managed bots queue for Warsong Gulch, Arathi Basin and Alterac Valley through the real battlemaster
/// opcodes, accept the invitation, play the objective and leave at the end
/// (<c>PlayerbotBattlegroundCoordinator</c>, docs/areas/playbots-battlegrounds.md). Every key is live: <c>.reload config</c>
/// changes this object in place and the coordinator reads it at every decision.
/// </summary>
public sealed class PlayerbotBattlegroundOptions
{
    /// <summary>Let free bots queue for battlegrounds (on by default). Off: no bot starts a queue; bots already walking to a battlemaster, queued or in a match finish.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The chance, per minute, that a free bot of a fitting level starts to queue (0..1; 0 never, 1 at the next scan). The chance of
    /// one scan is <c>1 - (1 - p)^(scanSeconds / 60)</c>, so the rate does not depend on how often the coordinator looks.
    /// </summary>
    public float QueueChancePerMinute { get; set; } = 0.05f;

    /// <summary>
    /// The most bots in one battleground (0..80; 0 queues none), unless <see cref="RaiseCapToFillTemplate"/> raises it for a type whose
    /// template minimum needs more. Bots waiting for one battleground type and level bracket do not exceed the (effective) cap, a team
    /// never gets more than half of it (rounded up) and a bot declines an invitation into a match that already holds this many.
    /// </summary>
    public int MaxBotsPerBattleground { get; set; } = 20;

    /// <summary>
    /// Let bots fill the queues real players wait in, so their games start (on by default): a bracket with a real player waiting gets
    /// bots at once (no chance roll) up to the minimum both teams need, and they pick the type and bracket the player waits for.
    /// </summary>
    public bool PreferFillingRealPlayerQueues { get; set; } = true;

    /// <summary>
    /// Raise the cap of a battleground type whose template minimum needs more bots than <see cref="MaxBotsPerBattleground"/> allows (on by
    /// default) to 2 x <c>MaxPlayersPerTeam</c>, for example Alterac Valley (20 per team minimum, 40 maximum) against the default cap of 20
    /// gets 80, so bots alone can start it and fill it to 40 per team. A type whose minimum the cap covers keeps the cap. Off: such a type
    /// is refused for bots (<c>CapBelowMinimum</c>) instead of queues that can never pop. The retail template values are never changed.
    /// A raise is logged as a warning naming each raised type and its new cap. Upgrade note: this replaces the former RaiseCapToMatchMinimum,
    /// which raised the cap only to 2 x <c>MinPlayersPerTeam</c>; set it to false to keep a low cap.
    /// </summary>
    public bool RaiseCapToFillTemplate { get; set; } = true;

    /// <summary>
    /// The farthest, in yards, a battlemaster may stand from a bot for the bot to walk to it (100..20000): a straight line on the bot's own map,
    /// to a battlemaster whose faction is not hostile to the bot.
    /// </summary>
    public int MaxTravelYards { get; set; } = 1500;

    /// <summary>Minutes a bot waits in a queue before it leaves it (1..240); it then does not queue for that type again for 10 minutes.</summary>
    public int MaxQueueWaitMinutes { get; set; } = 30;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Battlegrounds";
        if (!float.IsFinite(QueueChancePerMinute) || QueueChancePerMinute is < 0f or > 1f)
            throw new InvalidOperationException($"{section}: QueueChancePerMinute must be 0..1.");
        if (MaxBotsPerBattleground is < 0 or > 80) throw new InvalidOperationException($"{section}: MaxBotsPerBattleground must be 0..80.");
        if (MaxTravelYards is < 100 or > 20_000) throw new InvalidOperationException($"{section}: MaxTravelYards must be 100..20000.");
        if (MaxQueueWaitMinutes is < 1 or > 240) throw new InvalidOperationException($"{section}: MaxQueueWaitMinutes must be 1..240.");
    }
}
