namespace ArcaneCore.Game.Stealth;

/// <summary>
/// Stealth detection limits. Every default reproduces vmangos (the primary reference):
/// <c>MaxPlayersStealthDetectRange</c> and <c>MaxCreaturesStealthDetectRange</c> are both 30 yards
/// (vmangos World.cpp:566-567, mangosd.conf.dist.in). The detection model itself is the vmangos one,
/// sniff-verified in the comment of Unit::CanDetectStealthOf (Unit.cpp:6543-6616); the mangos-classic
/// 0.3 x strength model (ObjectVisibility.cpp:152-178) is not implemented (docs/areas/rogue.md).
/// </summary>
public sealed record StealthOptions
{
    /// <summary>Beyond this distance a player never detects a stealthed unit (vmangos CONFIG_FLOAT_MAX_PLAYERS_STEALTH_DETECT_RANGE).</summary>
    public float MaxPlayerDetectRange { get; init; } = 30.0f;

    /// <summary>Beyond this distance a creature never detects a stealthed unit (vmangos CONFIG_FLOAT_MAX_CREATURES_STEALTH_DETECT_RANGE).</summary>
    public float MaxCreatureDetectRange { get; init; } = 30.0f;

    /// <summary>The retail defaults.</summary>
    public static StealthOptions Default { get; } = new();
}
