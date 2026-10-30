namespace ArcaneCore.World.GameObjects;

/// <summary>
/// Realm rules for the meeting stone queue (<see cref="MeetingStoneFeature"/>), bound from <see cref="SectionName"/> when the feature
/// installs its queue. Every default is the retail (vmangos) behaviour. Read once at start (restart to change).
/// </summary>
public sealed class MeetingStoneOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "World:MeetingStone";

    /// <summary>
    /// ArcaneCore deviation, off by default (retail): a player queued alone at a meeting stone who joins a party by an ordinary invitation
    /// leaves the solo queue (the client is told SETQUEUE LEAVE_QUEUE). vmangos keeps the stale entry (Group.cpp AddMember only updates the
    /// LFG state of a party that is itself in the queue), so that player keeps blocking the roles of everyone queued after them until they log
    /// out, and is lost from a pair the queue forms. This also applies to the inviter of a new party who had queued alone.
    /// </summary>
    public bool LeaveSoloQueueOnPartyJoin { get; set; }
}
