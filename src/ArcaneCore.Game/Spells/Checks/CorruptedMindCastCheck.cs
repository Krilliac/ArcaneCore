using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Procs;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Loatheb's Corrupted Mind (29184 priest, 29195 druid, 29197 shaman, 29199 paladin; each carries SPELL_AURA_OVERRIDE_CLASS_SCRIPTS misc value
/// 4327 and nothing else): a player under it cannot cast the healing of the four healer classes. vmangos Spell::CheckCast (Spell.cpp:5410-5430,
/// inside the player-only block) answers SPELL_FAILED_FIZZLE for a non-triggered cast that does not come from an item when the spell's family is
/// druid, priest, shaman or paladin and the spell is a heal, a dispel, Holy Nova's damage part (its heal is triggered, so the cast itself is
/// stopped) or Power Word: Shield.
/// <para>
/// Installed as a discovered <see cref="ISpellHandlerModule"/>. Reads the caster's auras on the world thread; allocates nothing on the path of a
/// caster with no matching aura. The retail 1.12 encounter applies the class-specific spell to every player of the class, so only that class's
/// family can be hit in practice; the family test is vmangos's, not the aura's.
/// </para>
/// </summary>
public sealed class CorruptedMindModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterCastCheck(new CorruptedMindCastCheck());
    }
}

/// <summary>The Corrupted Mind veto (see <see cref="CorruptedMindModule"/>).</summary>
public sealed class CorruptedMindCastCheck : ISpellCastCheck
{
    /// <summary>OVERRIDE_CLASS_SCRIPTS misc value of the four Corrupted Mind spells.</summary>
    public const int CorruptedMindClassScript = 4327;

    private const uint FamilyPriest = 6, FamilyDruid = 7, FamilyPaladin = 10, FamilyShaman = 11;

    /// <summary>CF_PRIEST_POWER_WORD_SHIELD (vmangos SpellClassMask.h:132).</summary>
    private const int PriestPowerWordShieldBit = 0;

    /// <summary>CF_PRIEST_HOLY_NOVA1 (vmangos SpellClassMask.h:154).</summary>
    private const int PriestHolyNova1Bit = 22;

    /// <summary>After the caster aura state check and before the target is resolved: the vmangos check sits among the player-only caster rules.</summary>
    public SpellCheckPhase Phase => SpellCheckPhase.Caster;

    public int Order => SpellCastCheckOrder.CasterAuraState + 50;

    public SpellCastResult Check(in SpellCastCheckContext context)
    {
        if (context.Caster is not Player || context.CastItem is not null || context.Triggered || !IsHealerFamily(context.Spell.SpellFamilyName))
        {
            return SpellCastResult.CastOk;
        }

        if (!IsBlocked(context.Spell) || !HasCorruptedMind(context.System, context.Caster))
        {
            return SpellCastResult.CastOk;
        }

        return SpellCastResult.Fizzle;
    }

    private static bool IsHealerFamily(uint family) => family is FamilyDruid or FamilyPriest or FamilyShaman or FamilyPaladin;

    /// <summary>A heal, a dispel, Holy Nova's damage part or Power Word: Shield.</summary>
    private static bool IsBlocked(SpellInfo spell)
        => ProcFlagRules.IsHealSpell(spell)
            || HasDispelEffect(spell)
            || spell.IsFitToFamily(FamilyPriest, PriestHolyNova1Bit)
            || spell.IsFitToFamily(FamilyPriest, PriestPowerWordShieldBit);

    private static bool HasDispelEffect(SpellInfo spell)
    {
        IReadOnlyList<SpellEffectInfo> effects = spell.Effects;
        for (int i = 0; i < effects.Count; i++)
        {
            if (effects[i].Effect == SpellEffectName.Dispel)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCorruptedMind(SpellSystem system, Unit caster)
    {
        foreach (SpellAuraHolder holder in system.GetAuras(caster))
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is { Type: AuraType.OverrideClassScripts, MiscValue: CorruptedMindClassScript })
                {
                    return true;
                }
            }
        }

        return false;
    }
}
