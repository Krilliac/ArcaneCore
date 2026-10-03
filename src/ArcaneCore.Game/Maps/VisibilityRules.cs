using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// A per-viewer visibility veto on top of the range check (the stealth and invisibility part of vmangos
/// <c>Unit::IsVisibleForOrDetect</c>, Unit.cpp:6321-6461). Every rule of the map must allow a viewer to see a
/// target for the target to be (or stay) in the viewer's visible list. A rule that has no opinion returns true.
/// Attach with <see cref="Map.AddVisibilityRule"/>. World thread.
/// </summary>
public interface IVisibilityRule
{
    /// <summary>
    /// Whether <paramref name="viewer"/> may see <paramref name="target"/>.
    /// </summary>
    /// <param name="alreadyVisible">The target is in the viewer's visible list (vmangos IsInVisibleList).</param>
    /// <param name="detect">
    /// Detect mode (vmangos <c>detect = true</c>): the periodic stealth detection pass, which may reveal a stealthed unit.
    /// Ordinary movement-driven updates run with false and only keep a unit the viewer already sees.
    /// </param>
    bool CanSee(Player viewer, WorldObject target, bool alreadyVisible, bool detect);
}
