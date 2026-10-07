using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Reputation;

/// <summary>Live reputation modifiers from aura156 and kill-only faction aura190.</summary>
public static class ReputationAuras
{
    public static void Bind(SpellSystem spells, ReputationService reputation)
    {
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(reputation);
        // These data auras are consumed when a reward is calculated, not on apply/remove.
        spells.RegisterAura(AuraType.ModReputationGain, new AuraHandler(null, null));
        spells.RegisterAura(AuraType.ModFactionReputationGain, new AuraHandler(null, null));
        reputation.GainModifier = (player, source, faction) =>
            spells.GetTotalAuraModifier(player, AuraType.ModReputationGain)
            + (source == ReputationSource.Kill
                ? spells.GetTotalAuraModifier(player, AuraType.ModFactionReputationGain,
                    aura => aura.MiscValue >= 0 && (uint)aura.MiscValue == faction)
                : 0);
    }
}
