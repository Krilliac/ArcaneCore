using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Hunter;

/// <summary>
/// Improved Scorpid Sting (talent 1351: 19491, 19493, 19494; OVERRIDE_CLASS_SCRIPTS 2388, 2389, 2390), after vmangos
/// <c>Aura::HandleAuraModStat</c> (SpellAuras.cpp:4650-4677). When the strength aura of a hunter's Scorpid Sting
/// (SPELLFAMILY_HUNTER, CF_HUNTER_SCORPID_STING bit 15, MOD_STAT misc STAT_STRENGTH) lands, the caster's first class script of the talent
/// sets how much stamina goes with it: 10, 20 or 30 percent of the strength amount (integer division, as the C++), cast as a custom
/// 19486 (MOD_STAT stamina) on the stung target, triggered. When the sting's holder leaves, 19486 leaves the target too
/// (<c>RemoveAurasDueToSpell(19486)</c>).
/// <para>
/// Limit: vmangos runs this inside the stat handler, so a sting refreshed in place (no new holder) re-casts 19486 there; here it follows
/// <see cref="SpellSystem.HolderAdded"/>, which a refresh does not raise, so the first 19486 stays for the refreshed sting.
/// </para>
/// </summary>
public sealed class ImprovedScorpidStingModule : ISpellHandlerModule
{
    /// <summary>The stamina reduction the talent casts (Spell.dbc 19486, "Improved Scorpid Sting", MOD_STAT misc 2).</summary>
    public const uint StaminaSpell = 19486;

    /// <summary>SPELLFAMILY_HUNTER.</summary>
    public const uint HunterFamily = 9;

    /// <summary>CF_HUNTER_SCORPID_STING (vmangos SpellClassMask, 0x8000).</summary>
    public const int ScorpidStingFlagBit = 15;

    private const int StatStrength = 0;

    /// <summary>The talent's class script misc values and their stamina percent of the strength amount (SpellAuras.cpp:4665-4667).</summary>
    public static int? StaminaPercent(int classScript) => classScript switch
    {
        2388 => 10,
        2389 => 20,
        2390 => 30,
        _ => null,
    };

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.HolderAdded += holder => OnHolderAdded(system, holder);
        system.HolderRemoved += holder => OnHolderRemoved(system, holder);
    }

    private static SpellAura? StrengthAura(SpellAuraHolder holder)
    {
        if (!holder.Spell.IsFitToFamily(HunterFamily, ScorpidStingFlagBit))
        {
            return null;
        }

        foreach (SpellAura? aura in holder.Auras)
        {
            if (aura is not null && aura.Type == AuraType.ModStat && aura.MiscValue == StatStrength)
            {
                return aura;
            }
        }

        return null;
    }

    private static void OnHolderAdded(SpellSystem system, SpellAuraHolder holder)
    {
        if (StrengthAura(holder) is not { } strength || system.AuraCaster(holder) is not { } caster || system.Store.Get(StaminaSpell) is null)
        {
            return;
        }

        int staminaToRemove = 0;
        foreach (SpellAura script in system.AurasOfType(caster, AuraType.OverrideClassScripts))
        {
            if (StaminaPercent(script.MiscValue) is { } percent)
            {
                staminaToRemove = strength.Amount * percent / 100;
                break;
            }
        }

        if (staminaToRemove != 0)
        {
            system.CastCustomSpell(caster, StaminaSpell, SpellCastTargets.ForUnit(holder.Target.Guid), staminaToRemove);
        }
    }

    private static void OnHolderRemoved(SpellSystem system, SpellAuraHolder holder)
    {
        if (StrengthAura(holder) is not null && system.HasAura(holder.Target, StaminaSpell))
        {
            system.RemoveAuras(holder.Target, StaminaSpell);
        }
    }
}
