using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Druid;

/// <summary>
/// Swiftmend (18562; vmangos scripts/spells/spell_druid.cpp:101-160, built for clients after 1.10.2). The spell is a HEAL of 1 at a friendly target.
/// <list type="bullet">
/// <item>Cast check (<c>OnCheckCast</c>): the target needs a druid PERIODIC_HEAL aura of Rejuvenation (family flag 0x10) or Regrowth (0x40), anyone's;
/// without one the cast fails with TARGET_AURASTATE.</item>
/// <item>Effect (<c>OnEffectExecute</c>, effect 0): of the target's Rejuvenation and Regrowth periodic heals the one with the shortest remaining
/// duration is chosen (the first in aura order on a tie), consumed, and its tick amount times 4 (Rejuvenation, "12 sec") or 6 (Regrowth, "18 sec") is
/// added to the heal. The heal bonus then applies to the whole amount, as vmangos adds it to <c>damage</c> before EffectHeal.</item>
/// </list>
/// The addition is a value modifier on effect 0 (<see cref="SwiftmendHealModifier"/>, it reads the aura before the script consumes it); the script
/// removes the aura before the HEAL effect runs.
/// </summary>
[SpellScript(Swiftmend, ExecuteEffects = [SpellEffectName.Heal])]
public sealed class SwiftmendScript : ISpellScript
{
    public const uint Swiftmend = 18562;

    /// <summary>SPELLFAMILY_DRUID.</summary>
    public const uint DruidFamily = 7;

    /// <summary>CF_DRUID_REJUVENATION.</summary>
    public const ulong RejuvenationFlag = 0x10;

    /// <summary>CF_DRUID_REGROWTH.</summary>
    public const ulong RegrowthFlag = 0x40;

    /// <summary>"12 sec of Rejuvenation": four ticks.</summary>
    public const int RejuvenationTicks = 4;

    /// <summary>"18 sec of Regrowth": six ticks.</summary>
    public const int RegrowthTicks = 6;

    public SpellCastResult OnCheckCast(in SpellCastCheckContext context)
    {
        if (context.Target is { } target && Consumable(context.System, target) is null)
        {
            return SpellCastResult.TargetAurastate;
        }

        return SpellCastResult.CastOk;
    }

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex == 0 && context.Effect.Effect == SpellEffectName.Heal && Pick(context.System, context.Target) is { } chosen)
        {
            context.System.RemoveAuras(context.Target, chosen.Holder.Spell.Id); // consumes Regrowth or Rejuvenation
        }
    }

    /// <summary>The heal Swiftmend adds for the aura it would consume on <paramref name="target"/> (0 when there is none).</summary>
    public static int BonusHeal(SpellSystem system, Unit target)
    {
        if (Pick(system, target) is not { } chosen)
        {
            return 0;
        }

        // vmangos tests Regrowth, then Rejuvenation, and the later test wins.
        int ticks = (chosen.Holder.Spell.SpellFamilyFlags & RejuvenationFlag) != 0 ? RejuvenationTicks
            : (chosen.Holder.Spell.SpellFamilyFlags & RegrowthFlag) != 0 ? RegrowthTicks
            : 0;
        return chosen.Aura.Amount * ticks;
    }

    /// <summary>
    /// The aura the effect consumes: of the PERIODIC_HEAL auras of Rejuvenation or Regrowth, the one with the shortest remaining duration (vmangos keeps
    /// the first on a tie: <c>GetAuraDuration() &lt; targetAura-&gt;GetAuraDuration()</c>).
    /// </summary>
    public static (SpellAuraHolder Holder, SpellAura Aura)? Pick(SpellSystem system, Unit target)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(target);
        (SpellAuraHolder Holder, SpellAura Aura)? chosen = null;
        foreach (SpellAuraHolder holder in system.GetAuras(target))
        {
            if (holder.IsRemoved || holder.Spell.SpellFamilyName != DruidFamily
                || (holder.Spell.SpellFamilyFlags & (RejuvenationFlag | RegrowthFlag)) == 0)
            {
                continue;
            }

            foreach (SpellAura aura in holder.Auras.OfType<SpellAura>())
            {
                if (aura.Type == AuraType.PeriodicHeal && (chosen is null || holder.Duration < chosen.Value.Holder.Duration))
                {
                    chosen = (holder, aura);
                }
            }
        }

        return chosen;
    }

    /// <summary>The cast check's aura (vmangos <c>GetAura(SPELL_AURA_PERIODIC_HEAL, SPELLFAMILY_DRUID, 0x50)</c>).</summary>
    private static SpellAura? Consumable(SpellSystem system, Unit target) => Pick(system, target)?.Aura;
}

/// <summary>Swiftmend's heal: effect 0 plus the chosen periodic heal's remaining ticks (vmangos <c>spell->damage += tickheal * tickcount</c>).</summary>
public sealed class SwiftmendHealModifier(SpellSystem system) : ISpellValueModifier
{
    public int Modify(SpellValueKind kind, in SpellValueContext context, int value)
        => kind == SpellValueKind.EffectValue && context.EffectIndex == 0 && context.Spell.Id == SwiftmendScript.Swiftmend && context.Target is { } target
            ? value + SwiftmendScript.BonusHeal(system, target)
            : value;
}

/// <summary>Registers <see cref="SwiftmendHealModifier"/> (discovered <see cref="ISpellHandlerModule"/>).</summary>
public sealed class SwiftmendModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterValueModifier(new SwiftmendHealModifier(system));
    }
}
