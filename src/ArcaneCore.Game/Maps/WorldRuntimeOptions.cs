using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.Game.Maps;

/// <summary>Tuning for the world simulation (bound from the "World" configuration section).</summary>
public sealed class WorldRuntimeOptions
{
    /// <summary>World tick length in milliseconds (vmangos WORLD_SLEEP_CONST = 50).</summary>
    public int TickIntervalMs { get; set; } = 50;

    /// <summary>
    /// Update packets larger than this many bytes are zlib-compressed into
    /// SMSG_COMPRESSED_UPDATE_OBJECT (vmangos Compression.Update.Size default 128). 0 disables.
    /// </summary>
    public int UpdateCompressionThreshold { get; set; } = 128;

    /// <summary>Periodic save of online characters, in milliseconds (vmangos PlayerSave.Interval default 900000). 0 disables.</summary>
    public int AutosaveIntervalMs { get; set; } = 15 * 60 * 1000;

    /// <summary>Characters an account may have on this realm (vmangos CharactersPerRealm: default 10, at most 10).</summary>
    public int CharactersPerRealm { get; set; } = 10;

    /// <summary>Message of the day sent as system lines at login; '@' separates lines (vmangos Motd).</summary>
    public string Motd { get; set; } = "Welcome to ArcaneCore.";

    /// <summary>
    /// Distance /say reaches. vmangos and cmangos-classic World.cpp default to 25 (cmangos'
    /// shipped mangosd.conf keeps 25; vmangos' raises it to 40). 0 = the whole map.
    /// </summary>
    public float ListenRangeSay { get; set; } = 25.0f;

    /// <summary>Distance /yell reaches (vmangos/cmangos ListenRange.Yell default 300). 0 = the whole map.</summary>
    public float ListenRangeYell { get; set; } = 300.0f;

    /// <summary>Distance /emote and text emotes reach (vmangos/cmangos ListenRange.TextEmote default 25). 0 = the whole map.</summary>
    public float ListenRangeTextEmote { get; set; } = 25.0f;

    /// <summary>
    /// Allow whispers and custom emotes across factions, and send Common/Orcish as Universal
    /// (vmangos AllowTwoSide.Interaction.Chat, default off).
    /// </summary>
    public bool AllowTwoSideChat { get; set; }

    /// <summary>Show the other faction in /who (vmangos AllowTwoSide.WhoList, default off).</summary>
    public bool AllowTwoSideWhoList { get; set; }

    /// <summary>
    /// How long a requested logout counts down before the player leaves the world (vmangos
    /// WorldSession::ShouldLogOut hard-codes 20 seconds; configurable here for tests and tuning).
    /// </summary>
    public uint LogoutDelayMs { get; set; } = Entities.Player.DefaultLogoutDelayMs;

    /// <summary>Lowest security level that logs out instantly (vmangos/cmangos InstantLogout default SEC_MODERATOR).</summary>
    public AccountSecurity InstantLogoutSecurity { get; set; } = AccountSecurity.Moderator;

    /// <summary>
    /// Highest staff level ordinary players see in /who (vmangos/cmangos GM.InWhoList.Level
    /// default SEC_ADMINISTRATOR, i.e. every account).
    /// </summary>
    public AccountSecurity GmLevelInWhoList { get; set; } = AccountSecurity.Administrator;

    /// <summary>
    /// Whether accounts without a GM level may use player commands such as .help and .save
    /// (vmangos PlayerCommands, default on).
    /// </summary>
    public bool PlayerCommands { get; set; } = true;

    /// <summary>
    /// Slow-update thresholds (the top-level "PerformanceLog" section, copied in by
    /// AddWorldDaemon; vmangos mangosd.conf.dist.in:898-906). 0 disables each.
    /// </summary>
    public PerformanceLogOptions Perf { get; set; } = new();

    /// <summary>Grid lifecycle and terrain data (the <c>World:Maps</c> section; docs/areas/grid-terrain.md).</summary>
    public MapOptions Maps { get; set; } = new();
}
