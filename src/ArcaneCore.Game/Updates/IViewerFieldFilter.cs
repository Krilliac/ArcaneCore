using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Updates;

/// <summary>
/// Per-viewer field values for one object (vmangos Object::BuildValuesUpdate rewrites
/// UNIT_DYNAMIC_FLAGS and GAMEOBJECT_DYN_FLAGS per target player). Attach with
/// <see cref="WorldObject.ViewerFieldFilter"/>; when the answer for a viewer changes without the
/// stored value changing, call <see cref="WorldObject.ForceFieldUpdate"/>. World thread; must be
/// cheap and side-effect free.
/// </summary>
public interface IViewerFieldFilter
{
    uint Filter(WorldObject obj, int index, uint value, Player viewer);
}
