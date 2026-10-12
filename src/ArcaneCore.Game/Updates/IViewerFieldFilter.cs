using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Updates;

/// <summary>
/// Per-viewer field values for one object (vmangos Object::BuildValuesUpdate rewrites
/// UNIT_DYNAMIC_FLAGS and GAMEOBJECT_DYN_FLAGS per target player). Attach with
/// <see cref="WorldObject.ViewerFieldFilter"/>; when the answer for a viewer changes without the
/// stored value changing, call <see cref="WorldObject.ForceFieldUpdate"/>. World thread; must be
/// cheap. An implementation may record what it answered per viewer (the game object quest flags do, as the baseline of their refresh), so a
/// caller asks for a viewer's value only to send it to that viewer.
/// </summary>
public interface IViewerFieldFilter
{
    uint Filter(WorldObject obj, int index, uint value, Player viewer);
}
