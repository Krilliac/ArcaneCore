using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Pets.Control;

/// <summary>
/// vmangos Spell::CheckCast, the aura block for SPELL_AURA_MOD_POSSESS, SPELL_AURA_MOD_CHARM and SPELL_AURA_MOD_POSSESS_PET
/// (Spell.cpp:6292-6363), at build 5875 (&gt;= 1.11.2, so "charmed" answers SPELL_FAILED_CHARMED rather than FIZZLE):
/// <list type="bullet">
/// <item>possess: only a player casts it (BAD_TARGETS); then the charm rules;</item>
/// <item>charm and possess: an explicit unit target (unless the effect's target is a script target) must not already be charmed
/// (CHARMED) and must not be above the effect's value in level (HIGHLEVEL); a caster with a pet dismisses it first when the spell has
/// SPELL_ATTR_EX_DISMISS_PET_FIRST, else ALREADY_HAVE_SUMMON; a caster that already charms something uncharms first with that attribute,
/// else ALREADY_HAVE_CHARM; a charmed caster cannot (CHARMED);</item>
/// <item>possess pet: a player caster that is not charmed, has a pet (NO_PET), charms nothing else (ALREADY_HAVE_CHARM), and whose pet no
/// one else charms (CHARMED).</item>
/// </list>
/// Runs in the final phase, like vmangos (also for triggered casts). Script targets (vmangos <c>IsScriptTarget</c>: the implicit targets
/// resolved from <c>spell_script_target</c>) are not modelled, so every charm effect with a unit target is checked.
/// </summary>
internal sealed class CharmCastCheck(CharmService service) : ISpellCastCheck
{
    /// <summary>vmangos SPELL_ATTR_EX_DISMISS_PET_FIRST (SpellDefines.h: AttributesEx 0x00000001).</summary>
    private const uint DismissPetFirst = 0x00000001;

    public SpellCheckPhase Phase => SpellCheckPhase.Final;

    public int Order => 0;

    public SpellCastResult Check(in SpellCastCheckContext context)
    {
        SpellInfo spell = context.Spell;
        Unit caster = context.Caster;
        for (int i = 0; i < spell.Effects.Count; i++)
        {
            SpellEffectInfo effect = spell.Effects[i];
            if (effect.Effect != SpellEffectName.ApplyAura)
            {
                continue;
            }

            SpellCastResult result = effect.AuraType switch
            {
                AuraType.ModPossess when caster is not Player => SpellCastResult.BadTargets,
                AuraType.ModPossess or AuraType.ModCharm => CheckCharm(context, effect),
                AuraType.ModPossessPet => CheckPossessPet(caster),
                _ => SpellCastResult.CastOk,
            };

            if (result != SpellCastResult.CastOk)
            {
                return result;
            }
        }

        return SpellCastResult.CastOk;
    }

    private SpellCastResult CheckCharm(in SpellCastCheckContext context, SpellEffectInfo effect)
    {
        Unit caster = context.Caster;
        if (context.Target is not { } target)
        {
            return SpellCastResult.BadImplicitTargets;
        }

        if (!target.CharmerGuid.IsEmpty)
        {
            return SpellCastResult.Charmed;
        }

        int maxLevel = SpellMath.CalculateEffectValue(context.Spell, effect, caster.Level, context.System.Random);
        if (target.Level > maxLevel)
        {
            return SpellCastResult.Highlevel;
        }

        bool dismissFirst = ((uint)context.Spell.AttributesEx & DismissPetFirst) != 0;
        if (caster.GetPet() is { } pet)
        {
            if (!dismissFirst)
            {
                return SpellCastResult.AlreadyHaveSummon;
            }

            if (context.Strict && caster.Map?.Pets is not null)
            {
                DismissPet(pet);
            }
        }

        if (!caster.CharmGuid.IsEmpty)
        {
            if (!dismissFirst)
            {
                return SpellCastResult.AlreadyHaveCharm;
            }

            if (context.Strict)
            {
                service.Uncharm(caster);
            }
        }

        return caster.CharmerGuid.IsEmpty ? SpellCastResult.CastOk : SpellCastResult.Charmed;
    }

    private static SpellCastResult CheckPossessPet(Unit caster)
    {
        if (caster is not Player)
        {
            return SpellCastResult.BadTargets;
        }

        if (!caster.CharmerGuid.IsEmpty)
        {
            return SpellCastResult.Charmed;
        }

        if (caster.GetPet() is not { } pet)
        {
            return SpellCastResult.NoPet;
        }

        if (!caster.CharmGuid.IsEmpty && caster.CharmGuid != pet.Guid)
        {
            return SpellCastResult.AlreadyHaveCharm;
        }

        return !pet.CharmerGuid.IsEmpty && pet.CharmerGuid != caster.Guid ? SpellCastResult.Charmed : SpellCastResult.CastOk;
    }

    /// <summary>vmangos Pet::Unsummon(PET_SAVE_NOT_IN_SLOT) of the caster's pet before the charm (SPELL_ATTR_EX_DISMISS_PET_FIRST).</summary>
    private void DismissPet(Creature pet) => service.Summons?.Unsummon(pet);
}
