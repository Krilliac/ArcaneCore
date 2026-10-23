namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// <c>World:Playerbots:Social:Lists</c>: a bot's friend and ignore lists follow what it feels about others (docs/areas/playbots-social.md,
/// Friends, ignore and guild invites). A bot adds a character it likes and knows to its friends (CMSG_ADD_FRIEND), removes one it no longer
/// likes (CMSG_DEL_FRIEND), ignores one it holds a grudge against (CMSG_ADD_IGNORE) and stops ignoring it when the grudge has faded
/// (CMSG_DEL_IGNORE), all through its own session, so the server's ordinary rules (list sizes, factions) apply. The affinities are the
/// bot's, as <c>PlayerbotSocialMemory.EffectiveAffinity</c> reads them (War Mode's layer while it is on). Every key is live.
/// </summary>
public sealed class PlayerbotSocialListsOptions
{
    /// <summary>Let bots manage their friend and ignore lists by affinity (on by default).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The affinity at or above which a bot befriends a character (with <see cref="FriendMinFamiliarity"/>; -100..100).</summary>
    public int FriendAffinity { get; set; } = 50;

    /// <summary>The familiarity a bot needs with a character before it befriends it (0..1000).</summary>
    public int FriendMinFamiliarity { get; set; } = 5;

    /// <summary>A friend whose affinity has fallen below this is removed (-100..100; keep it below <see cref="FriendAffinity"/>).</summary>
    public int UnfriendAffinity { get; set; } = 20;

    /// <summary>The affinity at or below which a bot ignores a character (-100..0). A character the bot treats as a rival and that earned a chat strike is ignored too.</summary>
    public int IgnoreAffinity { get; set; } = -70;

    /// <summary>An ignored character whose affinity has risen above this is no longer ignored (-100..0; keep it above <see cref="IgnoreAffinity"/>).</summary>
    public int UnignoreAffinity { get; set; } = -30;

    /// <summary>The least seconds between two list changes of one bot (1..3600).</summary>
    public int ChangeSpacingSeconds { get; set; } = 60;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Social:Lists";
        Check(FriendAffinity, -100, 100, nameof(FriendAffinity));
        Check(FriendMinFamiliarity, 0, 1000, nameof(FriendMinFamiliarity));
        Check(UnfriendAffinity, -100, 100, nameof(UnfriendAffinity));
        Check(IgnoreAffinity, -100, 0, nameof(IgnoreAffinity));
        Check(UnignoreAffinity, -100, 0, nameof(UnignoreAffinity));
        Check(ChangeSpacingSeconds, 1, 3600, nameof(ChangeSpacingSeconds));
        if (UnfriendAffinity > FriendAffinity) throw new InvalidOperationException($"{section}: UnfriendAffinity must not exceed FriendAffinity.");
        if (UnignoreAffinity < IgnoreAffinity) throw new InvalidOperationException($"{section}: UnignoreAffinity must not be below IgnoreAffinity.");

        static void Check(int value, int min, int max, string name)
        {
            if (value < min || value > max) throw new InvalidOperationException($"{section}: {name} must be {min}..{max}.");
        }
    }
}
