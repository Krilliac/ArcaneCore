namespace ArcaneCore.Game.Reputation;

/// <summary>vmangos ReputationMgr.h FactionFlags (the u8 flag byte of SMSG_INITIALIZE_FACTIONS; gtker FactionFlag).</summary>
[Flags]
public enum FactionStateFlags : uint
{
    None = 0x00,

    /// <summary>Shown in the client's reputation pane.</summary>
    Visible = 0x01,

    /// <summary>Player-controlled at-war state; makes reputation factions hostile to the player.</summary>
    AtWar = 0x02,

    /// <summary>Hidden from the pane; reputation still accrues.</summary>
    Hidden = 0x04,

    /// <summary>Never visible (opposite team factions).</summary>
    InvisibleForced = 0x08,

    /// <summary>Cannot be set at war (own team core factions) unless already Hated.</summary>
    PeaceForced = 0x10,

    /// <summary>Player-controlled "inactive" grouping in the pane.</summary>
    Inactive = 0x20,

    /// <summary>Present in vmangos for vanilla, unused by 1.12 content.</summary>
    Rival = 0x40,
}
