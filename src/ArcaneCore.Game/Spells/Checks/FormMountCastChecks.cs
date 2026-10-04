using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells.Druid;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// A mount spell cannot be cast in a disallowed form: vmangos Spell::CheckCast, the SPELL_AURA_MOUNTED case of the effect loop
/// (Spell.cpp:6370-6391): <c>IsInDisallowedMountForm</c> answers SPELL_FAILED_NOT_SHAPESHIFT. Every form but none, the warrior
/// stances, Shadowform and Stealth is disallowed (<see cref="FormInterlocks"/>); the display half of the vmangos function (a
/// non-native display that cannot mount) needs CreatureDisplayInfo.dbc and is a documented limit. Triggered casts are checked
/// too, as in vmangos.
/// </summary>
public sealed class MountFormCastCheck : ISpellCastCheck
{
    public SpellCheckPhase Phase => SpellCheckPhase.Final;

    public int Order => 100;

    public SpellCastResult Check(in SpellCastCheckContext context)
        => context.Spell.HasAura(AuraType.Mounted) && FormInterlocks.IsInDisallowedMountForm(context.Caster)
            ? SpellCastResult.NotShapeshift
            : SpellCastResult.CastOk;
}

/// <summary>
/// A player casting while mounted gets off the mount first: vmangos Spell::CheckCast (Spell.cpp:5680-5692): a non-triggered cast
/// of a spell that is neither passive nor ALLOW_WHILE_MOUNTED dismounts the player and removes the mount auras. Runs once, at the
/// start of the cast (the strict check). The taxi branch (SPELL_FAILED_NOT_ON_TAXI while flying) has no flight state to read on
/// this base and is not implemented.
/// </summary>
public sealed class DismountOnCastCheck : ISpellCastCheck
{
    public SpellCheckPhase Phase => SpellCheckPhase.Items;

    public int Order => 0;

    public SpellCastResult Check(in SpellCastCheckContext context)
    {
        if (!context.Strict || context.Triggered || context.Caster is not Player player || context.Spell.IsPassive
            || context.Spell.HasAttribute(SpellAttributes.AllowWhileMounted) || !MountService.IsMounted(player))
        {
            return SpellCastResult.CastOk;
        }

        MountService.Unmount(context.System, player);
        context.System.RemoveSpellsCausingAura(player, AuraType.Mounted);
        return SpellCastResult.CastOk;
    }
}

/// <summary>
/// An aura spell that ends when its target shapeshifts or mounts cannot be cast on such a target: vmangos Spell::CheckCast
/// (Spell.cpp:5446-5451): a spell that applies auras (and is not an area spell) with AURA_INTERRUPT_SHAPESHIFTING_CANCELS on a
/// target that is shapeshifted (a form whose DBC row lacks the Stance flag, <see cref="FormQueries.IsShapeShifted(Unit, ShapeshiftFormCatalog?)"/>),
/// or with AURA_INTERRUPT_MOUNT_CANCELS on a mounted one, answers SPELL_FAILED_BAD_TARGETS (Water Walking on a cat).
/// </summary>
/// <param name="forms">The form table to read flags from (a null result means <see cref="ShapeshiftFormCatalog.Retail"/>).</param>
public sealed class ShiftedOrMountedTargetCastCheck(Func<ShapeshiftFormCatalog?> forms) : ISpellCastCheck
{
    private readonly Func<ShapeshiftFormCatalog?> _forms = forms ?? throw new ArgumentNullException(nameof(forms));

    public SpellCheckPhase Phase => SpellCheckPhase.Target;

    public int Order => 200;

    public SpellCastResult Check(in SpellCastCheckContext context)
    {
        SpellInfo spell = context.Spell;
        if (context.Target is not { } target || !AppliesAura(spell) || spell.IsAreaEffect())
        {
            return SpellCastResult.CastOk;
        }

        if (((uint)spell.AuraInterruptFlags & ShapeshiftService.ShapeshiftingCancelsFlag) != 0 && FormQueries.IsShapeShifted(target, _forms()))
        {
            return SpellCastResult.BadTargets;
        }

        return (spell.AuraInterruptFlags & SpellAuraInterruptFlags.MountCancels) != 0 && MountService.IsMounted(target)
            ? SpellCastResult.BadTargets
            : SpellCastResult.CastOk;
    }

    /// <summary>vmangos SpellEntry::IsSpellAppliesAura: an effect applies an aura (area auras count).</summary>
    private static bool AppliesAura(SpellInfo spell)
        => spell.Effects.Any(e => e.Effect is SpellEffectName.ApplyAura or SpellEffectName.ApplyAreaAuraParty);
}
