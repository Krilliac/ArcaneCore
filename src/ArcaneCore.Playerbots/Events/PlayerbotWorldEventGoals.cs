using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots.BotSocial;

namespace ArcaneCore.World.Playerbots.Events;

/// <summary>
/// One bot's side of the festival gathering (docs/areas/playbots-social.md, World events): while a festival runs and the bot is idle in a
/// town it stays in, it may take a place around an anchor NPC (<see cref="PlayerbotFestivalClaims"/>), and the brain then makes that spot its
/// idle centre instead of the inn. This type decides whether to go, where, and when to give the spot up; it sends nothing and walks
/// nothing: the brain's town idle walks and holds, and the events lane talks and emotes.
/// <para>
/// A field of the brain. Thread affinity: world thread. State ownership: the bot's claim lives on the board (<see cref="PlayerbotWorldEventBoard.Claims"/>);
/// this object keeps only the set-aside anchors, the decision clock and the spot area made for the claim. A throw in here is recorded, the claim is
/// released and the goals stay off for <see cref="FaultPauseMs"/>; the brain's think carries on down its ladder.
/// </para>
/// </summary>
internal sealed class PlayerbotWorldEventGoals(Guid botId, PlayerbotWorldEventBoard board)
{
    /// <summary>The world milliseconds between two looks for a place when the bot holds none (and the festivals did not change).</summary>
    internal const long DecideGapMs = 3_000;

    /// <summary>The wait after a look that found every place full.</summary>
    internal const long FullGapMs = 15_000;

    /// <summary>How long the goals stay off after a fault.</summary>
    internal const long FaultPauseMs = 60_000;

    private readonly Dictionary<ulong, long> _aside = [];
    private readonly List<(ulong Rank, PlayerbotFestivalAnchor Anchor)> _ranked = [];
    private PlayerbotHomeArea? _spotArea;
    private long _spotSerial = -1;
    private PlayerbotFestivalAnchor? _spotAnchor;
    private long _nextDecideMs;
    private int _decidedVersion = -1;
    private long _pausedUntilMs;
    private bool _paused;

    internal Guid BotId => botId;

    /// <summary>The claim the bot holds now, if it is alive on the board's clock.</summary>
    internal PlayerbotFestivalClaim? Claim => board.Claims.ClaimOf(botId, board.NowMs);

    /// <summary>Whether the bot holds a place at an anchor now.</summary>
    internal bool Holding => Claim is not null;

    /// <summary>
    /// The spot area the bot should treat as its idle centre (a home of radius 0 around the spot, in the town's map), or null for the inn.
    /// Keeps a claim the bot holds while its festival runs; otherwise looks for a place (at most every <see cref="DecideGapMs"/>, or when the
    /// festivals changed; never when <paramref name="mayDecide"/> is false, the town idle's explore window). Does not renew the claim: the brain renews it (<see cref="Renew"/>) once the town idle decided to stay.
    /// </summary>
    internal PlayerbotHomeArea? Spot(Player player, PlayerbotHomeArea town, bool mayDecide = true)
    {
        try
        {
            long now = board.NowMs;
            PlayerbotSocialEventsOptions events = board.Options.Social.Events;
            if (!events.Enabled || !events.Holidays.Enabled || (_paused && now < _pausedUntilMs))
            {
                Release(PlayerbotEventMeters.Done);
                return null;
            }

            _paused = false;
            if (board.Claims.ClaimOf(botId, now) is { } claim)
            {
                if (board.FestivalOf(claim.Anchor.Root) is not null && town.Place is { } place && !board.IsTownAside(place, claim.Team, now))
                    return AreaOf(claim, town);
                Release(PlayerbotEventMeters.Done);
            }
            else
            {
                _spotArea = null;
                _spotAnchor = null;
            }

            // A secondary town's explore window takes no place (it would be claimed and released every think, inflating festival_visit/done).
            return mayDecide ? Decide(player, town, now, events.Holidays) : null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.social.events", error);
            AnomalyLedger.Shared.Report("bot-social", "feature-fault", "events", $"{error.GetType().Name}: {error.Message}");
            board.Claims.Release(botId);
            _spotArea = null;
            _spotAnchor = null;
            (_paused, _pausedUntilMs) = (true, board.NowMs + FaultPauseMs);
            return null;
        }
    }

    /// <summary>A held think: the bot stays at its spot (or walks to it), so its place lasts another minute.</summary>
    internal void Renew() => board.Claims.Renew(botId, board.NowMs);

    /// <summary>The bot is no longer idle in the town (it hunts, quests, was attacked): its place is free at once.</summary>
    internal void Release(string outcome)
    {
        if (board.Claims.ClaimOf(botId, board.NowMs) is not null) PlayerbotEventMeters.Event(PlayerbotEventMeters.FestivalVisit, outcome);
        board.Claims.Release(botId);
        _spotArea = null;
        _spotAnchor = null;
    }

    /// <summary>
    /// The walk to the spot failed (the town idle gave it up, or the bot stands on the wrong floor): the anchor is set aside for this bot
    /// for <c>SpotBackoffMinutes</c> and the place is released. Reports <c>event/festival-spot-unreachable</c> once per anchor per hour.
    /// </summary>
    internal void GiveUp(Player player)
    {
        long now = board.NowMs;
        PlayerbotFestivalAnchor? held = _spotAnchor ?? board.Claims.ClaimOf(botId, now)?.Anchor;
        if (held is not { } anchor) return;
        if (_aside.Count >= 64) _aside.Clear();
        _aside[anchor.Guid] = now + (board.Options.Social.Events.Holidays.SpotBackoffMinutes * 60_000L);
        Release(PlayerbotEventMeters.Unreachable);
        if (board.TakeReportSlot(anchor.Guid, now))
            PlayerbotAnomalies.Report(player, "event", "festival-spot-unreachable",
                $"the walk to the spot at festival NPC {anchor.Entry} (spawn {anchor.Guid}) was given up; it is set aside for this bot", AnomalySeverity.Info);
    }

    /// <summary>
    /// The bot was attacked by a player where it stood: the whole town's festival is set aside for the bot's team for <c>SpotBackoffMinutes</c>
    /// (a crowd that draws attacks disperses) and the bot's place is released.
    /// </summary>
    internal void Attacked()
    {
        long now = board.NowMs;
        if (board.Claims.ClaimOf(botId, now) is not { } claim) return;
        board.SetTownAside(claim.Town, claim.Team, now + (board.Options.Social.Events.Holidays.SpotBackoffMinutes * 60_000L));
        Release(PlayerbotEventMeters.Abandoned);
    }

    /// <summary>The distance the town idle judges a walk to the spot by, and whether the bot is held there; see <see cref="PlayerbotFestivalRules"/>.</summary>
    internal static float SpotDistance(Player player, PlayerbotHomeArea spot)
        => PlayerbotFestivalRules.SpotDistance(spot.DistanceTo(player.X, player.Y), player.Z - spot.Centre.Z);

    /// <summary>
    /// Whether a bot at <paramref name="player"/>'s position is held at the spot (2D within the hold distance, on its floor and at least
    /// <see cref="PlayerbotFestivalRules.AnchorClearYards"/> from the anchor NPC: a stall next to the NPC is not a hold).
    /// </summary>
    internal bool IsHeld(Player player, PlayerbotHomeArea spot)
    {
        float anchorDistance = _spotAnchor is { } anchor
            ? MathF.Sqrt(((player.X - anchor.Position.X) * (player.X - anchor.Position.X)) + ((player.Y - anchor.Position.Y) * (player.Y - anchor.Position.Y)))
            : float.PositiveInfinity;
        return PlayerbotFestivalRules.IsHeldAtSpot(spot.DistanceTo(player.X, player.Y), player.Z - spot.Centre.Z, anchorDistance);
    }

    /// <summary>
    /// Whether the bot stands at the spot's ground position but on another floor (within the spot arrival distance in 2D, beyond the height
    /// tolerance): no walk will bring it closer, so the spot is given up at once instead of after the town idle's stall time.
    /// </summary>
    internal static bool IsOnWrongFloor(Player player, PlayerbotHomeArea spot)
        => spot.DistanceTo(player.X, player.Y) <= PlayerbotFestivalRules.SpotArriveYards
            && !(MathF.Abs(player.Z - spot.Centre.Z) <= PlayerbotFestivalRules.SpotZToleranceYards);

    private PlayerbotHomeArea AreaOf(PlayerbotFestivalClaim claim, PlayerbotHomeArea town)
    {
        if (_spotArea is { } known && _spotSerial == claim.Serial) return known;
        _spotSerial = claim.Serial;
        _spotAnchor = claim.Anchor;
        return _spotArea = new PlayerbotHomeArea(town.MapId, claim.Spot, town.Radius, town.Place) { Stay = town.Stay };
    }

    private PlayerbotHomeArea? Decide(Player player, PlayerbotHomeArea town, long now, PlayerbotHolidayOptions holidays)
    {
        int version = board.Version;
        if (now < _nextDecideMs && version == _decidedVersion) return null;
        _decidedVersion = version;
        _nextDecideMs = now + DecideGapMs;
        if (town.Place is not { } place || !board.AnyFestival() || board.IsTownAside(place, (uint)player.Team, now)) return null;
        IReadOnlyList<PlayerbotFestivalAnchor> anchors = board.AnchorsFor(town, player);
        if (anchors.Count == 0) return null;

        ulong guid = player.Guid.Value;
        double sociability = PlayerbotTraits.For(guid).Sociability;
        long day = PlayerbotFestivalRules.StaggeredDay(guid, board.UnixSeconds);
        _ranked.Clear();
        ushort lastRoot = 0;
        bool celebrates = false;
        foreach (PlayerbotFestivalAnchor anchor in anchors)
        {
            if (_aside.TryGetValue(anchor.Guid, out long until))
            {
                if (until > now) continue;
                _aside.Remove(anchor.Guid);
            }

            if (anchor.Root != lastRoot || !celebrates)
            {
                if (anchor.Root != lastRoot)
                {
                    lastRoot = anchor.Root;
                    celebrates = PlayerbotFestivalRules.Celebrates(guid, anchor.Root, day, holidays.CelebrateChance, sociability);
                }
            }

            if (!celebrates) continue;
            // Gossip anchors first, then service anchors; within a kind, the bot's own hash order.
            ulong rank = PlayerbotContention.Mix(guid, anchor.Guid);
            rank = (rank >> 1) | (anchor.Kind == PlayerbotAnchorKind.Service ? 1UL << 63 : 0UL);
            _ranked.Add((rank, anchor));
        }

        if (_ranked.Count == 0) return null;
        _ranked.Sort(static (a, b) => a.Rank.CompareTo(b.Rank));
        var caps = new PlayerbotFestivalCaps(holidays.MaxPerGossipAnchor, holidays.MaxPerServiceAnchor, holidays.MaxPerTown);
        uint team = (uint)player.Team;
        foreach ((_, PlayerbotFestivalAnchor anchor) in _ranked)
        {
            if (MathF.Abs(anchor.Position.Z - town.Centre.Z) > PlayerbotFestivalRules.SpotZToleranceYards
                && board.IsUnreachable(place, anchor.Guid, () => PlayerbotNavigation.IsUnreachableOnFoot(player, anchor.Position, out _, out _)))
            {
                _aside[anchor.Guid] = now + (holidays.SpotBackoffMinutes * 60_000L);
                continue;
            }

            Vector3 spot = PlayerbotFestivalRules.RingPoint(guid, anchor.Guid, anchor.Position);
            if (!board.Claims.TryClaim(anchor, spot, botId, place, team, caps, now)) continue;
            if (board.Claims.ClaimOf(botId, now) is { } claim) return AreaOf(claim, town);
        }

        _nextDecideMs = now + FullGapMs;
        PlayerbotEventMeters.Event(PlayerbotEventMeters.FestivalVisit, PlayerbotEventMeters.Capped);
        return null;
    }
}
