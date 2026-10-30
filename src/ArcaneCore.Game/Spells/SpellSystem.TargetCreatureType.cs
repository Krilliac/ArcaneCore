using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells.Rules;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>Totem of Grounding's holder: it absorbs the spell, so it also bypasses the creature type restriction (vmangos Spell.cpp:7579-7583).</summary>
    private const uint GroundingTotemHolder = 8179;

    private const uint CurseOfDoomId = 603;
    private const uint CurseOfDoomTypeMask = 0x7FF;
    private const uint DismissPetId = 2641;
    private const uint TamingLessonId = 23356;

    /// <summary>vmangos Spell::CheckTarget (Spell.cpp:7639-7643): an area, chain or random candidate must fit the type mask, except for an effect whose target A is the caster.</summary>
    private bool PassesTargetCreatureType(SpellInfo spell, SpellEffectInfo effect, Unit candidate)
        => effect.TargetA == SpellImplicitTarget.UnitCaster || CheckTargetCreatureType(spell, candidate);

    /// <summary>
    /// vmangos Spell::CheckTargetCreatureType (Spell.cpp:7577-7605), re-expressed: does <paramref name="target"/>'s creature type
    /// fit the spell's TargetCreatureType mask (bit = CreatureType.dbc id - 1)? A target with no type, a spell with no mask, a target
    /// under the Grounding Totem holder (8179), and Dismiss Pet / Taming Lesson (mask ignored) pass. Curse of Doom (603) takes every
    /// type but never a player or a unit a player owns or charms. World thread; allocation free.
    /// </summary>
    public bool CheckTargetCreatureType(SpellInfo spell, Unit target)
    {
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(target);
        if (HasAura(target, GroundingTotemHolder))
        {
            return true;
        }

        uint mask = spell.TargetCreatureType;
        if (spell.Id == CurseOfDoomId)
        {
            if (target.IsCharmerOrOwnerPlayerOrPlayerItself)
            {
                return false;
            }

            mask = CurseOfDoomTypeMask;
        }

        if (spell.Id is DismissPetId or TamingLessonId)
        {
            mask = 0;
        }

        if (mask == 0)
        {
            return true;
        }

        uint targetMask = target.CreatureTypeMask();
        return targetMask == 0 || (mask & targetMask) != 0;
    }
}
