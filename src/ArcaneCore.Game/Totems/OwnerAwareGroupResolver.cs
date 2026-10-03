using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Totems;

/// <summary>
/// Makes a totem resolve its OWNER's party, so a totem's party area aura and party AoE reach the shaman's
/// sub-group (vmangos Spells/SpellAuras.cpp:597-640: the area aura of a totem caster uses
/// <c>caster->GetCharmerOrOwner()</c> and that owner's sub-group; an ungrouped owner gets the aura itself).
/// The stock resolver only knows units that are in a group, so a totem would otherwise reach nobody.
/// Every other unit is answered by the wrapped resolver unchanged.
/// </summary>
public sealed class OwnerAwareGroupResolver(ISpellGroupResolver inner) : ISpellGroupResolver
{
    public IReadOnlyCollection<ObjectGuid> GetGroupMembers(Unit unit, bool raid)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return TotemQuery.TryGet(unit, out TotemInfo totem)
            ? inner.GetGroupMembers(totem.Owner, raid)
            : inner.GetGroupMembers(unit, raid);
    }
}
