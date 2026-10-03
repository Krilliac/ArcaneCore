using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Stealth;

/// <summary>
/// The invisibility part of vmangos Unit::IsVisibleForOrDetect (Unit.cpp:6321-6461) with Unit::CanDetectInvisibilityOf (:6502-6540): a unit with
/// SPELL_AURA_MOD_INVISIBILITY auras (Lesser Invisibility, the Invisibility potions, Greater Invisibility) is hidden from every viewer except
/// <list type="bullet">
/// <item>a game master, the unit's owner or charmer, and the caster of a Hunter's Mark on it;</item>
/// <item>a viewer under the same invisibility type (the type masks share a bit);</item>
/// <item>a viewer whose SPELL_AURA_MOD_INVISIBILITY_DETECTION level for a type is at least the unit's invisibility level for it (amount of the
/// strongest aura of each; Detect Lesser Invisibility 100, Detect Invisibility 200, Detect Greater Invisibility 300);</item>
/// <item>for a player target, a non-hostile viewer the stealth group rule lets through (the same group, raid or team as
/// <see cref="StealthOptions.GroupVisibilityMode"/> says).</item>
/// </list>
/// Visibility rules are combined: a unit that is both stealthed and invisible must pass this rule and the stealth rule. Not modelled: the
/// drunk detection special case (type 6), world bosses detecting everything, invisibility seen by creatures (this rule is a player viewer
/// rule), and the ghost "invisible for alive" state.
/// </summary>
public sealed class InvisibilityVisibilityRule(SpellSystem spells, StealthOptions? options = null) : IVisibilityRule
{
    private readonly StealthOptions _options = options ?? StealthOptions.Default;

    public bool CanSee(Player viewer, WorldObject target, bool alreadyVisible, bool detect)
    {
        if (target is not Unit unit || ReferenceEquals(viewer, target))
        {
            return true;
        }

        uint mask = MaskOf(spells, unit, AuraType.ModInvisibility);
        if (mask == 0 || viewer.IsGameMaster)
        {
            return true;
        }

        // always seen by owner
        if (!unit.CharmerOrOwnerGuid.IsEmpty && unit.CharmerOrOwnerGuid == viewer.Guid)
        {
            return true;
        }

        // Hunter's Mark makes the target always visible to its caster.
        if (spells.GetAuras(unit).Any(h => !h.IsRemoved && h.CasterGuid == viewer.Guid
            && h.Auras.Any(a => a is not null && a.Type == AuraType.ModStalked)))
        {
            return true;
        }

        // invisible units are always visible for units under the same invisibility type
        if ((mask & MaskOf(spells, viewer, AuraType.ModInvisibility)) != 0 || CanDetect(spells, viewer, unit, mask))
        {
            return true;
        }

        // non-hostile case: a player sees another player with invisibility if they share a group (or raid or team by configuration)
        return unit is Player invisible && !spells.Relations.IsHostile(viewer, unit) && IsGroupVisibleFor(invisible, viewer);
    }

    /// <summary>vmangos Unit::CanDetectInvisibilityOf: for some invisibility type the detector's level reaches the target's level.</summary>
    private static bool CanDetect(SpellSystem spells, Unit detector, Unit target, uint targetMask)
    {
        uint detectMask = MaskOf(spells, detector, AuraType.ModInvisibilityDetection);
        for (int type = 0; type < 32; type++)
        {
            if ((targetMask & (1u << type)) == 0)
            {
                continue;
            }

            int invisibilityLevel = Level(spells, target, AuraType.ModInvisibility, type);
            int detectLevel = (detectMask & (1u << type)) != 0 ? Level(spells, detector, AuraType.ModInvisibilityDetection, type) : 0;
            if (invisibilityLevel <= detectLevel)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>vmangos <c>m_invisibilityMask</c> / <c>m_detectInvisibilityMask</c>: bit <c>misc</c> for every aura of <paramref name="type"/> on the unit.</summary>
    private static uint MaskOf(SpellSystem spells, Unit unit, AuraType type)
    {
        uint mask = 0;
        foreach (SpellAuraHolder holder in spells.GetAuras(unit))
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is not null && aura.Type == type && aura.MiscValue is >= 0 and < 32)
                {
                    mask |= 1u << aura.MiscValue;
                }
            }
        }

        return mask;
    }

    /// <summary>The largest amount of the unit's auras of <paramref name="type"/> with misc value <paramref name="misc"/> (0 when none).</summary>
    private static int Level(SpellSystem spells, Unit unit, AuraType type, int misc)
    {
        int level = 0;
        foreach (SpellAuraHolder holder in spells.GetAuras(unit))
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is not null && aura.Type == type && aura.MiscValue == misc && level < aura.Amount)
                {
                    level = aura.Amount;
                }
            }
        }

        return level;
    }

    /// <summary>vmangos Player::IsGroupVisibleFor (Player.cpp:2924-2935), as in <see cref="StealthVisibilityRule"/>.</summary>
    private bool IsGroupVisibleFor(Player invisible, Player viewer) => _options.GroupVisibilityMode switch
    {
        StealthGroupVisibility.SameRaid => spells.Groups.GetGroupMembers(invisible, raid: true).Contains(viewer.Guid),
        StealthGroupVisibility.SameTeam => invisible.Team == viewer.Team,
        _ => spells.Groups.GetGroupMembers(invisible, raid: false).Contains(viewer.Guid),
    };
}
