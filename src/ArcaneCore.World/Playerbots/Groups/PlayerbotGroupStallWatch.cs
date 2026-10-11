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
    private readonly Dictionary<Guid, (uint Map, Vector3 Anchor, ulong Target)> _members = [];
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
        if (!_started || state != _state || Progressed(members))
        {
            Start(state, members, nowMs);
            return PlayerbotGroupStallVerdict.None;
        }

        IdleMs = unchecked(nowMs - _progressMs);
        if (unchecked(nowMs - _boundMs) is var bound && (bound > int.MaxValue || bound < stallMs)) return PlayerbotGroupStallVerdict.None;
        _boundMs = nowMs;
        Strikes++;
        return Strikes == 1 ? PlayerbotGroupStallVerdict.Retarget : PlayerbotGroupStallVerdict.Dissolve;
    }

    private bool Progressed(IReadOnlyList<PlayerbotGroupStallMember> members)
    {
        bool progressed = members.Count != _members.Count;
        foreach (PlayerbotGroupStallMember member in members)
        {
            if (!_members.TryGetValue(member.BotId, out var seen))
            {
                progressed = true;
                continue;
            }

            if (member.Busy || member.MapId != seen.Map || Vector3.Distance(member.Position, seen.Anchor) >= PlayerbotStallWatch.ProgressYards
                || (member.Target != 0 && member.Target != seen.Target))
                progressed = true;
        }

        return progressed;
    }

    private void Start(PlayerbotGroupState state, IReadOnlyList<PlayerbotGroupStallMember> members, uint nowMs)
    {
        // The target a member had is kept while it has none now: dropping a target and taking the same one again is no progress.
        var previous = new Dictionary<Guid, ulong>(_members.Select(m => KeyValuePair.Create(m.Key, m.Value.Target)));
        _members.Clear();
        foreach (PlayerbotGroupStallMember member in members)
            _members[member.BotId] = (member.MapId, member.Position, member.Target != 0 ? member.Target : previous.GetValueOrDefault(member.BotId));
        _state = state;
        _started = true;
        _progressMs = nowMs;
        _boundMs = nowMs;
        IdleMs = 0;
        Strikes = 0;
    }
}
