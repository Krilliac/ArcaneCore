namespace ArcaneCore.Game.Stealth;

/// <summary>
/// Stealth detection limits. Every default reproduces vmangos (the primary reference):
/// <c>MaxPlayersStealthDetectRange</c> and <c>MaxCreaturesStealthDetectRange</c> are both 30 yards
/// (vmangos World.cpp:566-567, mangosd.conf.dist.in). The detection model itself is the vmangos one,
/// sniff-verified in the comment of Unit::CanDetectStealthOf (Unit.cpp:6543-6616); the mangos-classic
/// 0.3 x strength model (ObjectVisibility.cpp:152-178) is not implemented (docs/areas/rogue.md).
/// </summary>
public sealed class StealthOptions
{
    /// <summary>Configuration section (World:Stealth).</summary>
    public const string SectionName = "World:Stealth";

    /// <summary>Beyond this distance a player never detects a stealthed unit (vmangos CONFIG_FLOAT_MAX_PLAYERS_STEALTH_DETECT_RANGE).</summary>
    public float MaxPlayerDetectRange { get; set; } = 30.0f;

    /// <summary>Beyond this distance a creature never detects a stealthed unit (vmangos CONFIG_FLOAT_MAX_CREATURES_STEALTH_DETECT_RANGE).</summary>
    public float MaxCreatureDetectRange { get; set; } = 30.0f;

    /// <summary>
    /// Improved Sap rolls twice per cast, as the vmangos code does (once at cast start, once at completion), instead of once. Default false (the
    /// talent text: 30/60/90 percent chance to remain stealthed).
    /// </summary>
    public bool ImprovedSapRollPerPhase { get; set; }

    /// <summary>
    /// Who always sees a non-hostile stealthed player (vmangos Visibility.GroupMode, World.cpp:689; Player::IsGroupVisibleFor,
    /// Player.cpp:2924-2935). Default <see cref="StealthGroupVisibility.SameGroup"/>: only the same 5-man (sub)group.
    /// </summary>
    public StealthGroupVisibility GroupVisibilityMode { get; set; } = StealthGroupVisibility.SameGroup;

    /// <summary>The retail defaults.</summary>
    public static StealthOptions Default => new();
}

/// <summary>vmangos Visibility.GroupMode values (mangosd.conf.dist.in:2548-2552).</summary>
public enum StealthGroupVisibility
{
    /// <summary>0, the default: members of the same group (the same sub-group inside a raid) auto-detect.</summary>
    SameGroup = 0,

    /// <summary>1: members of the same raid auto-detect.</summary>
    SameRaid = 1,

    /// <summary>2: every player of the same team auto-detects.</summary>
    SameTeam = 2,
}
