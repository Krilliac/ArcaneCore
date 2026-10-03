using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;

namespace ArcaneCore.Game.Spells.Utility.Targets;

/// <summary>
/// The pet-related implicit targets the warlock spells need and the built-in target switch does not serve:
/// <list type="bullet">
/// <item>5 TARGET_UNIT_CASTER_PET (Health Funnel, Dark Pact's pet side): the caster's pet, else its charm (vmangos Spell.cpp:2212-2222).</item>
/// <item>27 TARGET_UNIT_CASTER_MASTER (Sacrifice): the charmer or owner of the caster (Spell.cpp:2770-2772).</item>
/// <item>32 TARGET_LOCATION_UNIT_MINION_POSITION (every Summon Pet spell): the caster-relative point of the front-left family, +0.25 pi, radius 0
/// when the effect has no radius index (Spell.cpp:2975-3022, the comment there notes it is not known how it differs from the others).</item>
/// </list>
/// Install once per <see cref="SpellSystem"/>; a second install throws (two owners of one target id would disagree).
/// LIMITS: the destination of 32 is the unclamped offset at the caster's Z (vmangos uses GetFirstCollisionPosition, as the existing
/// caster-relative locations do, see <see cref="Spells.Targets.SpellTargetSelectors"/>).
/// </summary>
public static class PetTargets
{
    /// <summary>TARGET_UNIT_CASTER_MASTER (vmangos SpellDefines.h:82).</summary>
    public const SpellImplicitTarget CasterMaster = (SpellImplicitTarget)27;

    /// <summary>TARGET_LOCATION_UNIT_MINION_POSITION (vmangos SpellDefines.h:87).</summary>
    public const SpellImplicitTarget MinionPosition = (SpellImplicitTarget)32;

    /// <summary>Register the three selectors and the pet presence check on <paramref name="spells"/>.</summary>
    public static void Install(SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        spells.RegisterTargetSelector(SpellImplicitTarget.UnitCasterPet,
            (_, cast, _, _) => PetOf(cast.Caster) is { } pet ? [(pet, 1.0f)] : [], locationOnly: false);
        spells.RegisterTargetSelector(CasterMaster,
            (_, cast, _, _) => cast.Caster.GetCharmerOrOwner() is { } master ? [(master, 1.0f)] : [], locationOnly: false);
        spells.RegisterTargetSelector(MinionPosition,
            (_, cast, effect, _) => Spells.Targets.SpellTargetSelectors.SelectCasterRelativeLocation(cast, effect, MathF.PI * 0.25f), locationOnly: true);
        spells.RegisterCastCheck(new CasterPetCastCheck());
    }

    /// <summary>vmangos Spell.cpp:2215-2219 and :5545-5552: <c>GetPet()</c>, else the creature the caster charms (UNIT_FIELD_CHARM).</summary>
    public static Unit? PetOf(Unit caster)
    {
        ArgumentNullException.ThrowIfNull(caster);
        if (caster.GetPet() is { } pet)
        {
            return pet;
        }

        return caster.GetUInt64(UpdateFields.UnitFieldCharm) is not 0 and var charm
            ? caster.Map?.FindObject(new ObjectGuid(charm)) as Creature
            : null;
    }
}
