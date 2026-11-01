using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Spells.Rules.Immunity;

/// <summary>
/// The creature immunities the world database defines: the template masks <c>MechanicImmuneMask</c> / <c>SchoolImmuneMask</c> (vmangos
/// Creature::IsImmuneToSpell, Creature.cpp:2438-2452) unioned with the rows of the creature's active <c>creature_immunities</c> set
/// (mangos-classic Unit::IsImmuneToSpell and IsImmuneToSpellEffect, Entities/Unit.cpp:7343-7418). A dump that carries both therefore gives
/// the creature both; neither source overrides the other. Whatever provider was installed before is consulted too, so script providers
/// (<c>TwinEmperorSpellModule</c>) compose in either order. The creature type adds the vmangos type immunities (Creature::Create, Creature.cpp:425-441,
/// re-expressed): Mechanical creatures refuse the Heal and HealMaxHealth effects and the PeriodicHeal and PeriodicLeech aura states, and
/// Mechanical and Elemental creatures are immune to the Disease and Poison dispel types.
/// <para>
/// Ownership: stateless apart from the wrapped provider; the creature data comes from <see cref="Creature.Template"/>,
/// <see cref="Creature.Content"/> and <see cref="Creature.ImmunitySetId"/>. Thread affinity: world thread (the creature's own state).
/// Hot path: every spell check asks it; a creature without masks or content sets costs a type test, a template read and one flag read, with
/// no allocation.
/// </para>
/// </summary>
public sealed class CreatureDataImmunities(ICreatureImmunityProvider? previous) : ICreatureImmunityProvider
{
    // CreatureType.dbc ids (vmangos CreatureType enum: CREATURE_TYPE_ELEMENTAL 4, CREATURE_TYPE_MECHANICAL 9).
    private const uint ElementalType = 4;
    private const uint MechanicalType = 9;
    private const uint TypeDispelMask = (1u << (int)DispelType.Disease) | (1u << (int)DispelType.Poison);

    public uint MechanicImmuneMask(Unit unit)
    {
        uint mask = previous?.MechanicImmuneMask(unit) ?? 0;
        if (unit is Creature creature)
        {
            mask |= creature.Template.MechanicImmuneMask | (ActiveSet(creature)?.MechanicMask ?? 0);
        }

        return mask;
    }

    public uint SchoolImmuneMask(Unit unit)
    {
        uint mask = previous?.SchoolImmuneMask(unit) ?? 0;
        if (unit is Creature creature)
        {
            mask |= creature.Template.SchoolImmuneMask | (ActiveSet(creature)?.SchoolMask ?? 0);
        }

        return mask;
    }

    public uint DamageImmuneMask(Unit unit)
    {
        uint mask = previous?.DamageImmuneMask(unit) ?? 0;
        return unit is Creature creature ? mask | (ActiveSet(creature)?.DamageMask ?? 0) : mask;
    }

    public uint DispelImmuneMask(Unit unit)
    {
        uint mask = previous?.DispelImmuneMask(unit) ?? 0;
        if (unit is not Creature creature)
        {
            return mask;
        }

        mask |= ActiveSet(creature)?.DispelMask ?? 0;
        return creature.Template.CreatureType is ElementalType or MechanicalType ? mask | TypeDispelMask : mask;
    }

    public bool IsImmuneToEffect(Unit unit, SpellEffectName effect)
        => (previous?.IsImmuneToEffect(unit, effect) ?? false)
            || (unit is Creature creature
                && ((creature.Template.CreatureType == MechanicalType && effect is SpellEffectName.Heal or SpellEffectName.HealMaxHealth)
                    || ActiveSet(creature)?.ImmuneToEffect((uint)effect) == true));

    public bool IsImmuneToAuraState(Unit unit, AuraType aura)
        => (previous?.IsImmuneToAuraState(unit, aura) ?? false)
            || (unit is Creature creature
                && ((creature.Template.CreatureType == MechanicalType && aura is AuraType.PeriodicHeal or AuraType.PeriodicLeech)
                    || ActiveSet(creature)?.ImmuneToState((uint)aura) == true));

    private static CreatureImmunitySet? ActiveSet(Creature creature)
        => creature.Content.HasImmunitySets ? creature.Content.FindImmunitySet(creature.Template.Entry, creature.ImmunitySetId) : null;
}

/// <summary>
/// Puts <see cref="CreatureDataImmunities"/> in front of the spell system's creature immunity provider (discovered like every
/// <see cref="ISpellHandlerModule"/>, so a bare <see cref="SpellSystem"/> has it). It wraps whatever provider is installed when it runs and is
/// itself wrapped by modules that run later, so module order does not matter.
/// </summary>
public sealed class CreatureDataImmunityModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.CreatureImmunities = new CreatureDataImmunities(system.CreatureImmunities);
    }
}
