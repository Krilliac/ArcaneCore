using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.World.Spells;

/// <summary>
/// <see cref="ISpellGroupResolver"/> over the social area's <see cref="GroupManager"/> (vmangos
/// Player::GetGroup / Group::SameSubGroup). The manager is resolved lazily on the world thread
/// because the social feature may attach after this one; without it a unit is alone.
/// </summary>
public sealed class WorldSpellGroups(Func<GroupManager?> groups) : ISpellGroupResolver
{
    public IReadOnlyCollection<ObjectGuid> GetGroupMembers(Unit unit, bool raid)
    {
        ArgumentNullException.ThrowIfNull(unit);
        GroupManager? manager;
        try
        {
            manager = groups();
        }
        catch (InvalidOperationException)
        {
            manager = null;
        }

        if (manager?.GetGroup(unit.Guid) is not { } group || group.Find(unit.Guid) is not { } self)
        {
            return [unit.Guid];
        }

        // A raid's "party" is the caster's sub-group (vmangos Group::SameSubGroup).
        return [.. group.Members
            .Where(m => raid || !group.IsRaid || m.SubGroup == self.SubGroup)
            .Select(m => m.Guid)];
    }
}
