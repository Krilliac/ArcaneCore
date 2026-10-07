using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Reputation;

/// <summary>
/// Live reputation modifiers from aura 156 (MOD_REPUTATION_GAIN) and the kill-only faction aura 190, read when a reward is calculated: the
/// one formula of <see cref="ReputationSpellHandlers.GainModifier"/> bound to a reputation service.
/// </summary>
public static class ReputationAuras
{
    public static void Bind(SpellSystem spells, ReputationService reputation)
    {
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(reputation);
        // These data auras are consumed when a reward is calculated, not on apply/remove.
        foreach (AuraType gain in new[] { AuraType.ModReputationGain, AuraType.ModFactionReputationGain })
        {
            if (!spells.HasAuraHandler(gain))
            {
                spells.RegisterAura(gain, new AuraHandler(null, null));
            }
        }

        reputation.GainModifier = ReputationSpellHandlers.GainModifier(spells);
    }
}
