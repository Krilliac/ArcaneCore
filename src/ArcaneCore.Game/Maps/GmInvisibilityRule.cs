using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// GM invisibility (vmangos Player::SetGMVisible / Unit::IsVisibleForOrDetect, Unit.cpp:6321-6336): a player with
/// <see cref="Player.GmInvisible"/> is seen only by itself and by players whose account security is at least its own.
/// </summary>
public sealed class GmInvisibilityRule : IVisibilityRule
{
    public static GmInvisibilityRule Instance { get; } = new();

    /// <inheritdoc />
    public bool CanSee(Player viewer, WorldObject target, bool alreadyVisible, bool detect)
        => target is not Player { GmInvisible: true } gm || ReferenceEquals(viewer, gm) || viewer.Security >= gm.Security;
}