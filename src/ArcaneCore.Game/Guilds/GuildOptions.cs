namespace ArcaneCore.Game.Guilds;

/// <summary>
/// Guild, charter and petition rules, bound from <see cref="SectionName"/> at startup (restart-only:
/// these are deliberately not <c>SocialOptions</c> members, so the live <c>.reload config</c> key
/// registry is untouched). Every default is the retail/vmangos value; a changed value is a
/// deliberate deviation switched on by the operator.
/// </summary>
public sealed class GuildOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "World:Guild";

    /// <summary>vmangos MAX_CHARTER_NAME (ObjectMgr.h:405).</summary>
    public const int MaxCharterNameLength = 24;

    /// <summary>vmangos caps MinPetitionSigns at 9 (World.cpp:666) and the client hard cap is 9 signatures (PetitionsHandler.cpp:270).</summary>
    public const int MaxPetitionSigns = 9;

    /// <summary>
    /// Honour CMSG_GUILD_CREATE. vmangos honours it (GuildHandler.cpp:47-72) but the retail client
    /// only founds guilds through charters, so the default is off (deliberate, documented).
    /// </summary>
    public bool AllowClientGuildCreate { get; set; }

    /// <summary>MinPetitionSigns (mangosd.conf.dist.in:1341, default 9, World.cpp:666 clamps to 0..9).</summary>
    public int MinPetitionSigns { get; set; } = MaxPetitionSigns;

    /// <summary>MinCharterName (mangosd.conf.dist.in:1299, default 2, World.cpp:625 clamps to 2..24).</summary>
    public int MinCharterNameLength { get; set; } = 2;

    /// <summary>
    /// StrictCharterNames (mangosd.conf.dist.in:1296, default 0 = any single script). Bit 0x1 accepts
    /// basic Latin only. Bit 0x2 (realm-zone language) is not supported: this server has no realm
    /// zone, so only the 0x1 bit is evaluated when the mask is non-zero (documented limit).
    /// </summary>
    public int StrictCharterNames { get; set; }

    /// <summary>
    /// Deleting a rank moves its members to the new lowest rank. Default false: vmangos
    /// Guild::DelRank (Guild.cpp:696-707) leaves them on the dead rank id (name "&lt;unknown&gt;",
    /// no rights) until the next load clamps them (Guild.cpp:458-460).
    /// </summary>
    public bool DeleteRankMovesMembers { get; set; }

    /// <summary>
    /// Disconnect a client that sends over-long guild text (name, MOTD, info, notes, rank names),
    /// as vmangos does through ProcessAnticheatAction (GuildHandler.cpp:58-62,470-474,518-522,
    /// 554-558,580-584,600-604). The retail client never sends such text.
    /// </summary>
    public bool KickOnOversizedText { get; set; } = true;

    /// <summary><see cref="MinPetitionSigns"/> clamped like vmangos.</summary>
    public int EffectiveMinPetitionSigns => Math.Clamp(MinPetitionSigns, 0, MaxPetitionSigns);

    /// <summary><see cref="MinCharterNameLength"/> clamped like vmangos.</summary>
    public int EffectiveMinCharterNameLength => Math.Clamp(MinCharterNameLength, 2, MaxCharterNameLength);
}
