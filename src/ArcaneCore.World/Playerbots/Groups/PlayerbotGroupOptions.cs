namespace ArcaneCore.World.Playerbots;

/// <summary>
/// <c>World:Playerbots:Groups</c>: managed bots form their own groups (and raids) for content they cannot do alone
/// (<see cref="Groups.PlayerbotGroupCoordinator"/>, docs/areas/playbots-groups.md). Elite, dungeon and raid quests, objectives inside
/// instances and quest targets the risk estimate rates too dangerous alone but feasible for a few players become a "needs a group
/// of N" goal; the coordinator matches bots with the same goal, one leads, the others accept its ordinary invitation, and they do
/// the content together. Every key is live: <c>.reload config</c> changes this object in place and the coordinator reads it at
/// every decision.
/// </summary>
public sealed class PlayerbotGroupOptions
{
    /// <summary>Let bots form groups for group content (on by default). Off: no new group forms; running ones finish.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The most bot-led groups at once (0..64; 0 forms none).</summary>
    public int MaxGroups { get; set; } = 4;

    /// <summary>The most levels between the lowest and the highest member of a group (0..60).</summary>
    public int LevelRange { get; set; } = 5;

    /// <summary>Tanks a group of three or more needs (0..5); warriors, druids and paladins can tank.</summary>
    public int MinTank { get; set; } = 1;

    /// <summary>Healers a group of three or more needs (0..5); priests, druids, paladins and shamans can heal.</summary>
    public int MinHealer { get; set; } = 1;

    /// <summary>
    /// Seconds a bot waits for partners for one group goal (30..86400). With none by then, the goal is set aside and the bot goes on
    /// with what it can do alone; invitations not answered within this are given up too.
    /// </summary>
    public int FormationTimeoutSeconds { get; set; } = 300;

    /// <summary>Convert a group to a raid when the goal needs more than five (raid instances and raid quests); on by default.</summary>
    public bool RaidsEnabled { get; set; } = true;

    /// <summary>
    /// A bot group short of a role or a member may invite a nearby real player of a fitting level and class through the ordinary
    /// invitation (off by default). A real player never becomes the group's master: the bots keep their goal.
    /// </summary>
    public bool InvitePlayers { get; set; }

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Groups";
        if (MaxGroups is < 0 or > 64) throw new InvalidOperationException($"{section}: MaxGroups must be 0..64.");
        if (LevelRange is < 0 or > 60) throw new InvalidOperationException($"{section}: LevelRange must be 0..60.");
        if (MinTank is < 0 or > 5 || MinHealer is < 0 or > 5) throw new InvalidOperationException($"{section}: MinTank and MinHealer must be 0..5.");
        if (FormationTimeoutSeconds is < 30 or > 86_400) throw new InvalidOperationException($"{section}: FormationTimeoutSeconds must be 30..86400.");
    }
}
