using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.World.Playerbots.Combat;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>
/// What a bot-led group adopted through a leader handoff carries (<see cref="PlayerbotGroupCoordinator.BotGroup.Handoff"/>): who handed the
/// lead over, the instance, and (without a catalogue route for the map) the cached clear list. World thread.
/// </summary>
/// <param name="ServerGroupId">The server group (<c>Group.Id</c>) the bots lead: the player's own party or raid, kept as it is.</param>
/// <param name="HandedBy">The real player who passed the lead (it is handed back to them when the clear ends).</param>
/// <param name="HandedByName">Their name (notes and chat).</param>
/// <param name="InstanceName">The instance's name (<c>map_template</c>), for the chat lines.</param>
/// <param name="Sweep">The clear list when the catalogue has no route for the map (<see cref="PlayerbotClearList"/>), else null.</param>
internal sealed record PlayerbotHandoff(uint ServerGroupId, ObjectGuid HandedBy, string HandedByName, string InstanceName, PlayerbotClearList? Sweep);

/// <summary>A creature on the clear list: its guid, entry and where it stood when the list was built.</summary>
internal readonly record struct PlayerbotClearTarget(ulong Guid, uint Entry, Vector3 Position);

/// <summary>
/// The clear list of an instance the catalogue has no route for: the hostile creatures alive in it when the lead was handed over, visited
/// nearest first from where the leader stands (straight line; the walk itself is path-planned and pulls whatever stands in the way). Built
/// once and cached: per tick it only looks up the current target; a dead, despawned or given-up one is dropped as it is met. Pure apart from
/// the lookup it is given; world thread.
/// </summary>
internal sealed class PlayerbotClearList
{
    private readonly List<PlayerbotClearTarget> _left;
    private readonly HashSet<ulong> _givenUp = [];
    private ulong _current;

    internal PlayerbotClearList(IEnumerable<PlayerbotClearTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        _left = [.. targets.DistinctBy(t => t.Guid).OrderBy(t => t.Guid)];
        Initial = _left.Count;
    }

    /// <summary>How many creatures the list held when it was built (status).</summary>
    internal int Initial { get; private set; }

    /// <summary>The creatures still on the list (alive when last looked at, not given up).</summary>
    internal int Remaining => _left.Count;

    /// <summary>The creatures given up as unreachable (status).</summary>
    internal int GivenUp => _givenUp.Count;

    /// <summary>Whether the list was built a second time after it ran empty (<see cref="Rebuild"/>; once only).</summary>
    internal bool Rebuilt { get; private set; }

    /// <summary>
    /// The creature to clear next: the current one while <paramref name="alive"/> still says so, otherwise the nearest remaining to
    /// <paramref name="from"/> that is alive and not <paramref name="skip"/>ped (a creature another group fights, or one the group
    /// shunned for now). Dead and despawned ones (<paramref name="alive"/> null or false) leave the list. Null when nothing is left.
    /// </summary>
    /// <param name="alive">The creature's state now: null when it is gone from the map, else whether it lives and where it stands.</param>
    internal PlayerbotClearTarget? Next(Vector3 from, Func<ulong, (bool Alive, Vector3 Position)?> alive, Func<ulong, bool>? skip = null)
    {
        ArgumentNullException.ThrowIfNull(alive);
        if (_current != 0)
        {
            int index = _left.FindIndex(t => t.Guid == _current);
            if (index >= 0 && alive(_current) is { Alive: true } state && skip?.Invoke(_current) != true)
                return _left[index] with { Position = state.Position };
            if (index >= 0 && alive(_current) is not { Alive: true }) _left.RemoveAt(index);
            _current = 0;
        }

        PlayerbotClearTarget? best = null;
        float nearest = float.MaxValue;
        for (int i = _left.Count - 1; i >= 0; i--)
        {
            PlayerbotClearTarget target = _left[i];
            if (alive(target.Guid) is not { Alive: true } state)
            {
                _left.RemoveAt(i);
                continue;
            }

            if (skip?.Invoke(target.Guid) == true) continue;
            float distance = Vector3.DistanceSquared(from, state.Position);
            if (distance < nearest || distance == nearest && best is { } tie && target.Guid < tie.Guid)
            {
                nearest = distance;
                best = target with { Position = state.Position };
            }
        }

        _current = best?.Guid ?? 0;
        return best;
    }

    /// <summary>The group cannot reach <paramref name="guid"/> (no way planned to it for a while): it leaves the list for good.</summary>
    internal void GiveUp(ulong guid)
    {
        if (_left.RemoveAll(t => t.Guid == guid) > 0) _givenUp.Add(guid);
        if (_current == guid) _current = 0;
    }

    /// <summary>
    /// The list ran empty: take the hostile creatures alive now (a grid loaded since the handoff may hold more). Only once; true when the
    /// new list holds anything that was not given up.
    /// </summary>
    internal bool Rebuild(IEnumerable<PlayerbotClearTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (Rebuilt) return false;
        Rebuilt = true;
        _left.Clear();
        _left.AddRange(targets.Where(t => !_givenUp.Contains(t.Guid)).DistinctBy(t => t.Guid).OrderBy(t => t.Guid));
        _current = 0;
        Initial += _left.Count;
        return _left.Count > 0;
    }
}

/// <summary>A bot of the handed-over group as the role assignment sees it.</summary>
internal readonly record struct PlayerbotHandoffBot(Guid BotId, Class Class, PlayerbotRole TalentRole, byte Level);

/// <summary>
/// The pure rules of a leader handoff (docs/areas/playbots-groups.md, "Leader handoff"): which bots tank and heal, which wing of a
/// many-winged instance the group stands in, and the chat lines.
/// </summary>
internal static class PlayerbotLeaderHandoff
{
    /// <summary>The source a handed-over group's goal carries (<see cref="PlayerbotGroupGoal.Source"/>).</summary>
    internal const string Source = "handoff";

    /// <summary>A clear-list creature this close to the leader is pulled; farther, the leader walks towards it first.</summary>
    internal const float PullYards = 30f;

    /// <summary>
    /// The roles of the bots: the tanks and healers a run of this many bots needs (<see cref="PlayerbotGroupContent.RunRoleNeeds"/>),
    /// taken as the run matching takes them (the bots that prefer the role, then any that can, highest level first), the rest damage.
    /// Never refuses: a group short of a tank or a healer still clears, with what it has. Indexed like <paramref name="bots"/>.
    /// </summary>
    internal static PlayerbotGroupRole[] AssignRoles(IReadOnlyList<PlayerbotHandoffBot> bots, bool raid, PlayerbotGroupOptions options)
    {
        ArgumentNullException.ThrowIfNull(bots);
        ArgumentNullException.ThrowIfNull(options);
        var roles = new PlayerbotGroupRole[bots.Count];
        Array.Fill(roles, PlayerbotGroupRole.Damage);
        (int tanks, int healers) = PlayerbotGroupContent.RunRoleNeeds(bots.Count, raid, options);
        var free = new List<int>(Enumerable.Range(0, bots.Count));
        foreach (int index in free.Where(i => PlayerbotGroupContent.CanTank(bots[i].Class))
                     .OrderByDescending(i => PlayerbotGroupContent.Preferred(bots[i].Class, bots[i].TalentRole) == PlayerbotGroupRole.Tank)
                     .ThenByDescending(i => bots[i].Level).ThenBy(i => bots[i].BotId).Take(tanks).ToArray())
        {
            roles[index] = PlayerbotGroupRole.Tank;
            free.Remove(index);
        }

        foreach (int index in free.Where(i => PlayerbotGroupContent.CanHeal(bots[i].Class))
                     .OrderByDescending(i => PlayerbotGroupContent.Preferred(bots[i].Class, bots[i].TalentRole) == PlayerbotGroupRole.Healer)
                     .ThenByDescending(i => bots[i].Level).ThenBy(i => bots[i].BotId).Take(healers).ToArray())
        {
            roles[index] = PlayerbotGroupRole.Healer;
            free.Remove(index);
        }

        return roles;
    }

    /// <summary>
    /// The wing of <paramref name="mapId"/> the leader stands in: the catalogue plan one of whose places (where its entrance lands, or a
    /// boss) is nearest <paramref name="leader"/>. Null when the catalogue has no plan for the map.
    /// </summary>
    internal static PlayerbotRunPlan? ChooseWing(IReadOnlyList<PlayerbotRunPlan> plans, uint mapId, Vector3 leader)
    {
        ArgumentNullException.ThrowIfNull(plans);
        PlayerbotRunPlan? best = null;
        float nearest = float.MaxValue;
        foreach (PlayerbotRunPlan plan in plans)
        {
            if (plan.MapId != mapId) continue;
            float distance = Vector3.DistanceSquared(plan.InsideStart, leader);
            foreach (PlayerbotRunBoss boss in plan.Bosses) distance = Math.Min(distance, Vector3.DistanceSquared(boss.Position, leader));
            if (distance >= nearest) continue;
            nearest = distance;
            best = plan;
        }

        return best;
    }

    /// <summary>What the new leader says when it takes the lead.</summary>
    internal static string TakeOverLine(string instance) => $"I'll lead the clear of {instance}.";

    /// <summary>What the leader says when the clear is done.</summary>
    internal static string ClearedLine(string instance) => $"{instance} is clear. The lead is yours again.";

    /// <summary>What the leader says when the clear ends early, with the reason in words.</summary>
    internal static string StoppedLine(string instance, string reason) => $"Stopping the clear of {instance}: {ReasonText(reason)}.";

    /// <summary>What the new leader says when it will not lead the clear, with the reason in words (the bots keep following the player).</summary>
    internal static string RefusedLine(string instance, string reason) => $"I can't lead the clear of {instance}: {ReasonText(reason)}.";

    /// <summary>The reason codes of an ended or refused clear, in words for the chat.</summary>
    internal static string ReasonText(string reason) => reason switch
    {
        "called-back" => "you called us back, the lead is yours",
        "leader-taken-back" => "you have the lead again",
        "leader-passed" => "the lead went to someone else",
        "left-instance" => "we left the instance",
        "player-left" => "nobody we follow is in the group any more",
        "wiped" => "we wiped too often",
        "stalled" => "we are stuck",
        "too-few" => "too few of us are left",
        "no-path-to-boss" => "I can't find a way to the next boss",
        "no-entrance" => "I don't know the way in and out of here",
        "groups-disabled" => "bot groups are switched off",
        "map-not-allowed" => "bots may not go here",
        "no-bots-inside" => "none of us is in here with me",
        "scripted" => "I am being controlled by a script",
        "shutdown" => "the server is stopping",
        _ when reason.StartsWith("leader-", StringComparison.Ordinal) => "I can't lead any more",
        _ when reason.EndsWith("timeout", StringComparison.Ordinal) => "this is taking too long",
        _ => reason,
    };

    /// <summary>The master's orders that take the lead back from the bots (<c>follow</c>, <c>stay</c>, <c>come</c>, <c>attack</c>, <c>stop</c>, <c>leave</c>).</summary>
    internal static bool TakesTheLeadBack(Party.PlayerbotPartyCommand command)
        => command is Party.PlayerbotPartyCommand.Follow or Party.PlayerbotPartyCommand.Stay or Party.PlayerbotPartyCommand.Come
            or Party.PlayerbotPartyCommand.Attack or Party.PlayerbotPartyCommand.Stop or Party.PlayerbotPartyCommand.Leave;
}
