namespace ArcaneCore.Game.Social;

/// <summary>
/// Realm rules for cross-faction social interaction. Names and defaults follow the vmangos
/// mangosd.conf AllowTwoSide.* options (all off by default); bound from <see cref="SectionName"/>
/// and reloadable with <c>.reload config</c> (docs/areas/hot-reload.md).
/// </summary>
public sealed class SocialOptions
{
    /// <summary>The configuration section these options are bound from (live, via the config reload coordinator).</summary>
    public const string SectionName = "World:Social";

    /// <summary>
    /// Seconds before an offline group leader yields to an online member. Zero disables the
    /// handoff. vmangos@0e3ff01 src/game/World.cpp:811,2063-2072 defaults to 300 seconds.
    /// </summary>
    public int OfflineLeaderDelaySeconds { get; set; } = 300;

    /// <summary>
    /// Most channels one player may be in; 0 = unlimited. vmangos has no cap (ChannelMgr.cpp:52-69),
    /// so the retail default is 0; a positive value is opt-in hardening (World:Social:MaxJoinedChannels).
    /// </summary>
    public int MaxJoinedChannels { get; set; }

    /// <summary>
    /// ArcaneCore hardening: the longest channel name (characters) a join accepts; a longer one gets INVALID_NAME and no channel is
    /// created. 0 = no cap. vmangos has none and the stock client's limit is UNVERIFIED (not captured from a real client). The default
    /// (255) assumes a chat edit box of that length, which is unconfirmed; if the client allows longer names, raise it or set 0.
    /// </summary>
    public int MaxChannelNameLength { get; set; } = 255;

    /// <summary>
    /// ArcaneCore hardening: the longest channel password (characters) accepted on join or when set; over it a join gets WRONG_PASSWORD
    /// and a set is ignored. 0 = no cap. Default 255, as for <see cref="MaxChannelNameLength"/>.
    /// </summary>
    public int MaxChannelPassphraseLength { get; set; } = 255;

    /// <summary>
    /// ArcaneCore hardening: the shortest time (milliseconds) between two ready-check answers of one member that are forwarded to the
    /// leader; answers inside it are dropped (a dropped answer is not replayed, so the leader can keep a stale one), and a new ready check
    /// resets it. Off (0) by default, as in vmangos, which forwards every answer, pending check or not (GroupHandler.cpp
    /// HandleRaidReadyCheckOpcode): set it only to defend against a member spamming the leader.
    /// </summary>
    public int ReadyCheckAnswerIntervalMs { get; set; }

    /// <summary>AllowTwoSide.AddFriend.</summary>
    public bool AllowTwoSideAddFriend { get; set; }

    /// <summary>AllowTwoSide.Interaction.Group.</summary>
    public bool AllowTwoSideGroup { get; set; }

    /// <summary>AllowTwoSide.Interaction.Guild.</summary>
    public bool AllowTwoSideGuild { get; set; }

    /// <summary>AllowTwoSide.Interaction.Channel.</summary>
    public bool AllowTwoSideChannel { get; set; }

    /// <summary>
    /// The vmangos-only custom channel names: "World" becomes a General-flagged channel without
    /// join/leave announcements and "China" (or its Mandarin name) a Custom one without
    /// announcements (vmangos Channel.cpp:63-71). Retail 1.12 has neither, so they are ordinary
    /// custom channels unless this is on; it applies to channels created after the change.
    /// </summary>
    public bool VmangosChannelExtensions { get; set; }
}
