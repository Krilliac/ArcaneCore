using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// vmangos Spell::CheckItems spell focus requirement (Spell.cpp:7230-7243): a non-passive spell whose
/// <see cref="SpellInfo.RequiresSpellFocus"/> is set needs a spawned GAMEOBJECT_TYPE_SPELL_FOCUS object of that focus id
/// whose radius reaches the caster, else <see cref="SpellCastResult.RequiresSpellFocus"/> (forges, anvils, cooking fires,
/// 695 classic-db spells). The check has no triggered-cast exemption (unlike the item checks next to it); the one retail
/// exemption is the GM no-check-cast cheat (Spell.cpp:5304), which ArcaneCore does not model.
/// Only a Player caster is checked: CheckItems returns SPELL_CAST_OK for any other caster before the focus block (Spell.cpp:7104-7106), so creatures and pets cast focus spells freely.
/// A caster whose map has no game object system finds no object and so fails, exactly as an empty map does in retail.
/// </summary>
/// <param name="systems">The game object system of a map, or null when the map has none.</param>
/// <param name="enabled">Configuration switch <c>Spells:RequireSpellFocus</c> (default true, retail); false disables the requirement.</param>
public sealed class SpellFocusCastCheck(Func<Map, GameObjects.GameObjectMapSystem?> systems, Func<bool>? enabled = null) : ISpellCastCheck
{
    /// <summary>Spell.cpp:7230 sits after the equipment checks of CheckItems (<see cref="SpellCastCheckOrder.Equipment"/>) and before the reagents.</summary>
    public const int FocusOrder = SpellCastCheckOrder.Equipment + 50;

    public SpellCheckPhase Phase => SpellCheckPhase.Items;

    public int Order => FocusOrder;

    public SpellCastResult Check(in SpellCastCheckContext context)
    {
        if (context.Caster is not Player)
        {
            return SpellCastResult.CastOk; // Spell.cpp:7104-7106: CheckItems returns SPELL_CAST_OK for a non-Player caster before the focus block
        }

        SpellInfo spell = context.Spell;
        if (spell.RequiresSpellFocus == 0 || spell.IsPassive || enabled?.Invoke() == false)
        {
            return SpellCastResult.CastOk;
        }

        return context.Caster.Map is { } map && systems(map)?.FindSpellFocus(context.Caster, spell.RequiresSpellFocus) is not null
            ? SpellCastResult.CastOk
            : SpellCastResult.RequiresSpellFocus;
    }
}
