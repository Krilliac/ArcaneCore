using System.Numerics;

namespace ArcaneCore.World.Playerbots.Party;

/// <summary>What a follower does with the route it planned to its leader (<see cref="PlayerbotFollowRules.Judge"/>).</summary>
internal enum PlayerbotFollowStep
{
    /// <summary>Walk the route.</summary>
    Walk,

    /// <summary>Walk to the edge above the leader and drop (<see cref="PlayerbotFollowDrop"/>).</summary>
    Drop,

    /// <summary>Teleport to the leader (the catch-up teleport).</summary>
    Teleport,

    /// <summary>No way and no teleport: stand (the follower keeps trying at each think).</summary>
    Wait,
}

/// <summary>Why a follower judged the way to its leader no way (logged with a catch-up teleport, and shown by inspection).</summary>
internal enum PlayerbotCatchUpReason
{
    None,

    /// <summary>vmangos PartyBotAI: the leader is farther than <see cref="PlayerbotFollowOptions.TeleportDistanceYards"/>.</summary>
    Distance,

    /// <summary>The leader is on another map.</summary>
    OtherMap,

    /// <summary>mangoszero playerbot <c>MovementAction::Follow</c>: within 50 yards across, 30 yards or more above or below.</summary>
    Vertical,

    /// <summary>The navigation mesh has no route to the leader.</summary>
    NoRoute,

    /// <summary>The route is longer than <see cref="PlayerbotFollowOptions.MaxPathRatio"/> times the straight distance.</summary>
    Detour,

    /// <summary>The route passes a hostile far above the follower's level (<see cref="PlayerbotFollowOptions.DangerLevelMargin"/>).</summary>
    Danger,

    /// <summary>No progress towards a leader standing still, twice (<see cref="PlayerbotFollowOptions.NoProgressSeconds"/>).</summary>
    Stalled,
}

/// <summary>
/// The facts of one route judgement: the follower's catch-up mode and whether it may teleport now (<see cref="PlayerbotParty.MayTeleport"/>),
/// the straight distance to the leader (3D and across), how far the follower stands above it, the route the planner found (or none),
/// the dangerous hostiles whose aggro reaches it, whether a survivable drop leads down to the leader, and the stalls counted so far.
/// </summary>
internal readonly record struct PlayerbotRouteFacts(
    PlayerbotCatchUpMode Mode, bool MayTeleport, float Straight, float Across, float Above, bool RouteFound, float RouteYards,
    int Dangers, bool DropAvailable, int Stalls);

/// <summary>
/// The catch-up rules of a follower, apart from the world so they can be tested alone (docs/areas/playbots.md, Following and
/// catch-up). The reference rules are vmangos PartyBotAI::UpdateAI (src/game/PlayerBots/PartyBotAI.cpp:807-817: out of combat,
/// <c>!me->IsWithinDistInMap(pLeader, 100.0f)</c>, <c>ChatHandler(me).HandleGonameCommand(name)</c>) and mangoszero's playerbot
/// <c>MovementAction::Follow</c> (modules/Bots/playerbot/strategy/actions/MovementActions.cpp: within
/// <c>sPlayerbotAIConfig.sightDistance</c> (50) across and <c>spellDistance</c> (30) or more above or below, TeleportTo the
/// leader). Neither plans a way first; ArcaneCore's follower does, and judges it.
/// </summary>
internal static class PlayerbotFollowRules
{
    /// <summary>mangoszero <c>AiPlayerbot.SightDistance</c> (default 50): the vertical teleport applies within this many yards across.</summary>
    internal const float VerticalAcrossYards = 50f;

    /// <summary>mangoszero <c>AiPlayerbot.SpellDistance</c> (default 30): the vertical teleport applies this many yards above or below.</summary>
    internal const float VerticalYards = 30f;

    /// <summary>Stalls after which a follower gives up walking: the first one re-plans, the second teleports (Teleport and Auto).</summary>
    internal const int StallsToTeleport = 2;

    /// <summary>
    /// Whether <paramref name="routeYards"/> is a detour: more than <see cref="PlayerbotFollowOptions.MaxPathRatio"/> times the straight
    /// distance and more than <see cref="PlayerbotFollowOptions.MinDetourYards"/> longer than it.
    /// </summary>
    internal static bool IsDetour(float straight, float routeYards, PlayerbotFollowOptions options)
        => float.IsFinite(routeYards) && float.IsFinite(straight)
            && routeYards > straight * options.MaxPathRatio && routeYards - straight > options.MinDetourYards;

    /// <summary>mangoszero's vertical rule: within <see cref="VerticalAcrossYards"/> across, <see cref="VerticalYards"/> or more above or below.</summary>
    internal static bool IsVertical(float across, float above) => across <= VerticalAcrossYards && MathF.Abs(above) >= VerticalYards;

    /// <summary>
    /// What to do with the planned route. A good route (found, no detour, no danger, not vertical, not stalled twice) is walked.
    /// Otherwise a survivable drop to the leader below is taken in every mode. Then <see cref="PlayerbotCatchUpMode.Teleport"/>
    /// teleports (when <see cref="PlayerbotRouteFacts.MayTeleport"/>), <see cref="PlayerbotCatchUpMode.Auto"/> teleports only when
    /// there is no route or the follower stalled twice, and <see cref="PlayerbotCatchUpMode.FightThrough"/> never does; a follower
    /// that does not teleport walks the route it has, or waits without one.
    /// </summary>
    internal static (PlayerbotFollowStep Step, PlayerbotCatchUpReason Reason) Judge(in PlayerbotRouteFacts facts, PlayerbotFollowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        PlayerbotCatchUpReason reason = facts.Stalls >= StallsToTeleport ? PlayerbotCatchUpReason.Stalled
            : !facts.RouteFound ? PlayerbotCatchUpReason.NoRoute
            : IsVertical(facts.Across, facts.Above) ? PlayerbotCatchUpReason.Vertical
            : facts.Dangers > 0 ? PlayerbotCatchUpReason.Danger
            : IsDetour(facts.Straight, facts.RouteYards, options) ? PlayerbotCatchUpReason.Detour
            : PlayerbotCatchUpReason.None;
        if (reason == PlayerbotCatchUpReason.None) return (PlayerbotFollowStep.Walk, reason);
        if (facts.DropAvailable) return (PlayerbotFollowStep.Drop, reason);
        PlayerbotFollowStep fallback = facts.RouteFound ? PlayerbotFollowStep.Walk : PlayerbotFollowStep.Wait;
        bool teleports = facts.Mode switch
        {
            PlayerbotCatchUpMode.Teleport => facts.MayTeleport,
            PlayerbotCatchUpMode.Auto => facts.MayTeleport && reason is PlayerbotCatchUpReason.NoRoute or PlayerbotCatchUpReason.Stalled,
            _ => false,
        };
        return teleports ? (PlayerbotFollowStep.Teleport, reason) : (fallback, reason);
    }
}

/// <summary>
/// Loop detection for a follower: no progress towards a leader who stands still over a window of
/// <see cref="PlayerbotFollowOptions.NoProgressSeconds"/> counts one stall (the follower re-plans; a second one gives up walking,
/// <see cref="PlayerbotFollowRules.StallsToTeleport"/>). Progress is the length of the way left (the route's remainder, or the
/// straight distance without one) shrinking by <see cref="ProgressYards"/>; a leader who moves more than
/// <see cref="LeaderMovedYards"/> starts a new window and forgets the stalls, so a follower trailing a running leader at a steady
/// distance never stalls. World thread.
/// </summary>
internal sealed class PlayerbotFollowProgress
{
    /// <summary>The way left must shrink by this many yards to count as progress.</summary>
    internal const float ProgressYards = 2f;

    /// <summary>A leader who moved this far started somewhere else: a new window.</summary>
    internal const float LeaderMovedYards = 5f;

    private long _windowStartMs = -1;
    private float _best;
    private Vector3 _leaderAt;

    /// <summary>Stalls counted in a row (reset by progress, a moving leader or <see cref="Reset"/>).</summary>
    internal int Stalls { get; private set; }

    internal void Reset()
    {
        _windowStartMs = -1;
        Stalls = 0;
    }

    /// <summary>
    /// One observation: at <paramref name="nowMs"/> the way left is <paramref name="remaining"/> yards and the leader stands at
    /// <paramref name="leader"/>. True when this observation counted a new stall.
    /// </summary>
    internal bool Observe(long nowMs, float remaining, Vector3 leader, int windowSeconds)
    {
        if (!float.IsFinite(remaining)) return false;
        if (_windowStartMs < 0 || Vector3.Distance(leader, _leaderAt) > LeaderMovedYards)
        {
            _windowStartMs = nowMs;
            _best = remaining;
            _leaderAt = leader;
            Stalls = 0;
            return false;
        }

        if (remaining < _best - ProgressYards)
        {
            _best = remaining;
            _windowStartMs = nowMs;
            Stalls = 0;
            return false;
        }

        if (nowMs - _windowStartMs < windowSeconds * 1000L) return false;
        Stalls++;
        _windowStartMs = nowMs;
        _best = remaining;
        return true;
    }
}
