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

    /// <summary>The retail defaults.</summary>
    public static StealthOptions Default => new();
}
