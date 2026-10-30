namespace ArcaneCore.World.Playerbots;

/// <summary>
/// <c>World:Playerbots:Groups</c>: managed bots form their own groups (and raids) for content they cannot do alone
/// (<c>Groups.PlayerbotGroupCoordinator</c>, docs/areas/playbots-groups.md). Elite, dungeon and raid quests, objectives inside
/// instances and quest targets the risk estimate rates too dangerous alone but feasible for a few players become a "needs a group
/// of N" goal; the coordinator matches bots with the same goal, one leads, the others accept its ordinary invitation, and they do
/// the content together. Every key is live: <c>.reload config</c> changes this object in place and the coordinator reads it at
/// every decision.
/// </summary>
public sealed class PlayerbotGroupOptions
{
    /// <summary>Let bots form groups for group content (on by default). Off: no new group forms; running ones finish.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The most bot-led groups at once (0..64; 0 forms none). A group the cap keeps from forming is logged.</summary>
    public int MaxGroups { get; set; } = 16;

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

    /// <summary>
    /// A bot whose level is outside a dungeon's level band (the catalogue band, else the 1.12 recommended levels with two levels of slack)
    /// waits for a group there only with a reason: a quest that is not grey for it, an attunement or key step, a gear upgrade the dungeon drops,
    /// a guildmate or friend already waiting, or a forming group of its level (on by default). Off: every dungeon holding the objective is
    /// allowed, still ranked in-band first, then nearest band.
    /// </summary>
    public bool DungeonOutOfBandNeedsReason { get; set; } = true;

    /// <summary>Convert a group to a raid when the goal needs more than five (raid instances and raid quests); on by default.</summary>
    public bool RaidsEnabled { get; set; } = true;

    /// <summary>
    /// A bot group short of a role or a member may invite a nearby real player on the same goal through the ordinary invitation (on by
    /// default), when the player opted in to AI chat or has affinity of at least <see cref="InvitePlayerAffinity"/> toward the bots; never
    /// a rival or a player the bot flagged NoWhispers, and a player at most once per <see cref="InvitePlayerCooldownMinutes"/>. A real
    /// player never becomes the group's master: the bots keep their goal.
    /// </summary>
    public bool InvitePlayers { get; set; } = true;

    /// <summary>The affinity toward a player that lets the bots invite it without an AI chat opt-in (-100..100).</summary>
    public int InvitePlayerAffinity { get; set; } = 20;

    /// <summary>Minutes before the bots invite the same player again (0..1440).</summary>
    public int InvitePlayerCooldownMinutes { get; set; } = 30;

    /// <summary>Recruiting, answering LFG calls, guild calls, mentoring and raid escalation (<c>World:Playerbots:Groups:Cooperation</c>; live).</summary>
    public PlayerbotCooperationOptions Cooperation { get; set; } = new();

    /// <summary>Dungeon and raid runs: free bots group up on their own for instances that fit them (<c>World:Playerbots:Groups:DungeonRuns</c>; live).</summary>
    public PlayerbotRunOptions DungeonRuns { get; set; } = new();

    /// <summary>Raids that muster for a world boss on a continent (<c>World:Playerbots:Groups:WorldBosses</c>; live).</summary>
    public PlayerbotWorldBossOptions WorldBosses { get; set; } = new();

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Groups";
        if (MaxGroups is < 0 or > 64) throw new InvalidOperationException($"{section}: MaxGroups must be 0..64.");
        if (LevelRange is < 0 or > 60) throw new InvalidOperationException($"{section}: LevelRange must be 0..60.");
        if (MinTank is < 0 or > 5 || MinHealer is < 0 or > 5) throw new InvalidOperationException($"{section}: MinTank and MinHealer must be 0..5.");
        if (FormationTimeoutSeconds is < 30 or > 86_400) throw new InvalidOperationException($"{section}: FormationTimeoutSeconds must be 30..86400.");
        if (InvitePlayerAffinity is < -100 or > 100) throw new InvalidOperationException($"{section}: InvitePlayerAffinity must be -100..100.");
        if (InvitePlayerCooldownMinutes is < 0 or > 1440) throw new InvalidOperationException($"{section}: InvitePlayerCooldownMinutes must be 0..1440.");
        if (Cooperation is null) throw new InvalidOperationException($"{section}:Cooperation is missing.");
        Cooperation.Validate();
        if (DungeonRuns is null) throw new InvalidOperationException($"{section}:DungeonRuns is missing.");
        DungeonRuns.Validate();
        if (WorldBosses is null) throw new InvalidOperationException($"{section}:WorldBosses is missing.");
        WorldBosses.Validate();
    }
}
