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
