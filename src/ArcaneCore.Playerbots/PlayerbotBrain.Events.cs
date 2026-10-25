using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Playerbots.Events;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The brain's side of the festival gathering (docs/areas/playbots-social.md, World events): while a festival runs, an idle bot of a town home
/// may stand at a place around a festival NPC instead of at its inn. The place is only the idle centre: the town idle rule
/// (<see cref="PlayerbotTownIdle"/>) walks to it and holds there exactly as it does at the inn. World thread.
/// </summary>
internal sealed partial class PlayerbotBrain
{
    private PlayerbotWorldEventGoals? _eventGoals;
    private Vector3? _idleCentrePoint;

    /// <summary>The world's festival board (set by the feature; null in a host or a test without it).</summary>
    internal PlayerbotWorldEventBoard? EventBoard { get; set; }

    /// <summary>The bot's identity on the board's claims (set by the feature).</summary>
    internal Guid BotId { get; set; }

    /// <summary>The spot area the bot holds a place at now, or null (inspection, tests).</summary>
    internal bool HoldsEventSpot => _eventGoals?.Holding == true;

    /// <summary>The claim the bot holds now (inspection, tests).</summary>
    internal PlayerbotFestivalClaim? EventClaim => _eventGoals?.Claim;

    private void ReleaseEventSpot(string outcome) => _eventGoals?.Release(outcome);

    /// <summary>The centre the town idle works against this think: the bot's festival spot, or the town's own centre (the inn).</summary>
    private PlayerbotHomeArea IdleCentre(Player player, PlayerbotHomeArea town)
    {
        PlayerbotHomeArea centre = town;
        if (EventBoard is { } board)
        {
            _eventGoals ??= new PlayerbotWorldEventGoals(BotId, board);
            centre = _eventGoals.Spot(player, town, mayDecide: !_townIdle.Exploring) ?? town;
        }

        NoteIdleCentre(centre);
        return centre;
    }

    // The walk state of the town idle belongs to one centre: a changed centre clears it (PlayerbotTownIdle.Recentre).
    private void NoteIdleCentre(PlayerbotHomeArea centre)
    {
        if (_idleCentrePoint is { } last && last != centre.Centre) _townIdle.Recentre();
        _idleCentrePoint = centre.Centre;
    }

    /// <summary>
    /// One think of an idle bot with a town home that is on its town's map. True when the think is used (the bot lingers or walks to its
    /// centre); false falls through to the explore below (a secondary town's explore window, or a walk given up far from the centre).
    /// </summary>
    private bool TownIdleThink(Player player, PlayerbotHomeArea town, uint elapsedMs)
    {
        PlayerbotHomeArea centre = IdleCentre(player, town);
        bool spot = !ReferenceEquals(centre, town);
        TownIdleAction idle = DecideIdle(player, town, centre, spot);
        if (spot)
        {
            if (idle == TownIdleAction.Explore) ReleaseEventSpot(PlayerbotEventMeters.Done);
            else if (PlayerbotWorldEventGoals.IsOnWrongFloor(player, centre)
                || (idle == TownIdleAction.Linger && !_eventGoals!.IsHeld(player, centre)))
            {
                // The walk to the spot came to nothing (stairs, a counter, a wall): the anchor is set aside and the bot goes back to the inn.
                _eventGoals!.GiveUp(player);
                centre = town;
                spot = false;
                NoteIdleCentre(centre);
                _townIdle.Recentre();
                idle = DecideIdle(player, town, centre, spot);
            }
            else _eventGoals!.Renew();
        }

        if (idle == TownIdleAction.Linger && (spot ? _eventGoals!.IsHeld(player, centre) : PlayerbotTownIdle.IsHeld(town.DistanceTo(player.X, player.Y))))
        {
            // Standing at the inn is what the idle rule asks for, not a stall: the watch starts afresh at every such think. Before, a bot
            // lingering at its inn was reported stalled every stall bound, gave up its goal (which freed nothing: it was not stuck) and, at
            // the second report within 10 yards, was sent home to where it stood (live 2026-10-17: 1 253 stalls of 186 bots at the
            // Stormwind inn alone). A festival spot is held the same way.
            _stall.Reset();
            PlayerbotMovementControl.Stop(_session, player);
            if (!spot) TryBindAtInn(player, town);
            return true;
        }

        // A walk to the centre given up far from it (a Linger past HoldYards) falls through to the explore: the bot hunts, or wanders
        // inward (WanderLegEnd), for the give-up instead of standing in the open until the stall watch reports it.
        if (idle == TownIdleAction.WalkToTown)
        {
            WalkTowardTown(player, centre, elapsedMs);
            return true;
        }

        return false;
    }

    private TownIdleAction DecideIdle(Player player, PlayerbotHomeArea town, PlayerbotHomeArea centre, bool spot)
    {
        float distance = spot ? PlayerbotWorldEventGoals.SpotDistance(player, centre) : town.DistanceTo(player.X, player.Y);
        return _townIdle.Decide(town.Stay, distance, _session.World.NowMs);
    }
}
