using System.Numerics;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>What the group stall watch asks the coordinator to do (<see cref="PlayerbotGroupStallWatch.Observe"/>).</summary>
internal enum PlayerbotGroupStallVerdict : byte
{
    /// <summary>The group made progress within the bound (or is in a state the watch leaves alone).</summary>
    None,

    /// <summary>A first bound without progress: the leader gives up its target (set aside for the group) and chooses again.</summary>
    Retarget,

    /// <summary>A further bound without progress after the re-target: the group gives its goal up and dissolves.</summary>
    Dissolve,
}

/// <summary>One bot member as the group stall watch sees it at one tick.</summary>
/// <param name="Busy">In combat, eating or drinking, or looting: the group's own work, progress whatever the position.</param>
/// <param name="Target">The creature the member's group AI fights (its guid), or 0.</param>
internal readonly record struct PlayerbotGroupStallMember(Guid BotId, uint MapId, Vector3 Position, bool Busy, ulong Target);

/// <summary>
/// Notices a bot-led group whose bot members make no progress at all (<see cref="PlayerbotGroupCoordinator.BotGroup.Stall"/>): none moved
/// <see cref="PlayerbotStallWatch.ProgressYards"/> or changed map, none was in combat, eating, drinking or looting, and none took a new
/// target, for <see cref="PlayerbotOptions.StallSeconds"/> of world time. While a group drives its bots their brains do not think, so the
/// brains' <see cref="PlayerbotStallWatch"/> never saw such a group (replays 2026-10-11 on the real terrain: two-bot groups of the risk
/// signal stood in <c>engaging</c> for 300-840 s with both members still; live 2026-10-11 05:43-06:13, group 9 sat until the 30-minute
/// goal timeout). The first bound without progress re-targets (<see cref="PlayerbotGroupStallVerdict.Retarget"/>), a second dissolves
/// (<see cref="PlayerbotGroupStallVerdict.Dissolve"/>); any progress starts over. Dropping a target is not progress, taking another one is:
/// a leader that chases a creature it cannot reach drops it and takes it again every think. Pure bookkeeping; world thread.
/// </summary>
internal sealed class PlayerbotGroupStallWatch
{
    /// <summary>
    /// How long after a re-target a member's new target is taken as the re-target's own doing, not as progress: the leader gave its
    /// target up and chose another, which says nothing about the group getting anywhere.
    /// </summary>
    internal const uint RetargetGraceMs = 5_000;

    private Dictionary<Guid, (uint Map, Vector3 Anchor, ulong Target)> _members = [];
    private Dictionary<Guid, (uint Map, Vector3 Anchor, ulong Target)> _next = [];
    private PlayerbotGroupState _state;
    private bool _started;
    private uint _progressMs;
    private uint _boundMs;

    /// <summary>Bounds without progress since the last progress (0: none; 1: re-targeted).</summary>
    internal int Strikes { get; private set; }

    /// <summary>World time since the last progress (valid after <see cref="Observe"/> returned a verdict).</summary>
    internal uint IdleMs { get; private set; }

    /// <summary>Forget everything: the next observation starts a new watch (a state the watch leaves alone, a group that waits on its own bound).</summary>
    internal void Reset() => _started = false;

    /// <summary>Look at the group once per coordinator tick.</summary>
    internal PlayerbotGroupStallVerdict Observe(PlayerbotGroupState state, IReadOnlyList<PlayerbotGroupStallMember> members, uint nowMs, uint stallMs)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (!_started || state != _state)
        {
            Start(state, members, nowMs);
            return PlayerbotGroupStallVerdict.None;
        }

        Progress progress = Progressed(members);
        if ((progress & Progress.Moved) != 0)
        {
            Start(state, members, nowMs);
            return PlayerbotGroupStallVerdict.None;
        }

        if ((progress & Progress.NewTarget) != 0)
        {
            // A new target right after a re-target is the re-target's own: the strike stands (with 2+ creatures the group cannot reach,
            // every re-target picks another and the group would never reach the dissolve).
            if (Strikes > 0 && unchecked(nowMs - _boundMs) is var since && since <= RetargetGraceMs) AdoptTargets(members);
            else
            {
                Start(state, members, nowMs);
                return PlayerbotGroupStallVerdict.None;
            }
        }

        IdleMs = unchecked(nowMs - _progressMs);
        if (unchecked(nowMs - _boundMs) is var bound && (bound > int.MaxValue || bound < stallMs)) return PlayerbotGroupStallVerdict.None;
        _boundMs = nowMs;
        Strikes++;
        return Strikes == 1 ? PlayerbotGroupStallVerdict.Retarget : PlayerbotGroupStallVerdict.Dissolve;
    }

    [Flags]
    private enum Progress : byte
    {
        None = 0,

        /// <summary>A member moved, changed map, is busy, or the set of members changed.</summary>
        Moved = 1,

        /// <summary>A member took a target other than the one it had.</summary>
        NewTarget = 2,
    }

    private Progress Progressed(IReadOnlyList<PlayerbotGroupStallMember> members)
    {
        Progress progress = members.Count != _members.Count ? Progress.Moved : Progress.None;
        foreach (PlayerbotGroupStallMember member in members)
        {
            if (!_members.TryGetValue(member.BotId, out var seen))
            {
                progress |= Progress.Moved;
                continue;
            }

            if (member.Busy || member.MapId != seen.Map || Vector3.Distance(member.Position, seen.Anchor) >= PlayerbotStallWatch.ProgressYards)
                progress |= Progress.Moved;
            else if (member.Target != 0 && member.Target != seen.Target)
                progress |= Progress.NewTarget;
        }

        return progress;
    }

    /// <summary>Remember the members' new targets without starting over (positions, time and strikes stay).</summary>
    private void AdoptTargets(IReadOnlyList<PlayerbotGroupStallMember> members)
    {
        foreach (PlayerbotGroupStallMember member in members)
            if (member.Target != 0 && _members.TryGetValue(member.BotId, out var seen))
                _members[member.BotId] = (seen.Map, seen.Anchor, member.Target);
    }

    private void Start(PlayerbotGroupState state, IReadOnlyList<PlayerbotGroupStallMember> members, uint nowMs)
    {
        // The target a member had is kept while it has none now: dropping a target and taking the same one again is no progress.
        _next.Clear();
        foreach (PlayerbotGroupStallMember member in members)
            _next[member.BotId] = (member.MapId, member.Position, member.Target != 0 ? member.Target : _members.TryGetValue(member.BotId, out var seen) ? seen.Target : 0);
        (_members, _next) = (_next, _members);
        _state = state;
        _started = true;
        _progressMs = nowMs;
        _boundMs = nowMs;
        IdleMs = 0;
        Strikes = 0;
    }
}
