using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The reputation spell handlers: SPELL_EFFECT_REPUTATION (103, vmangos Spell::EffectReputation, SpellEffects.cpp:5307-5323),
/// SPELL_AURA_FORCE_REACTION (139, Aura::HandleForceReaction, SpellAuras.cpp:2785-2805), and the two gain auras that
/// <see cref="ReputationService.GainModifier"/> reads, SPELL_AURA_MOD_REPUTATION_GAIN (156, Diplomacy) and
/// SPELL_AURA_MOD_FACTION_REPUTATION_GAIN (190, kills of one faction only), which only need to be applied so the spell
/// system keeps them (Player::CalculateReputationGain sums them, Player.cpp:6258-6262).
/// <para>
/// A module may only add handlers. Each registration is guarded, so the module coexists with another area that claims the same
/// effect or aura earlier: it skips what is already there. The type name sorts after the built-in aura and effect modules on
/// purpose. The handlers reach the live service through <see cref="ReputationEnvironment"/> and do nothing without one.
/// </para>
/// </summary>
public sealed class ReputationSpellHandlers : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (!system.HasEffectHandler(SpellEffectName.Reputation))
        {
            system.RegisterEffect(SpellEffectName.Reputation, ReputationEffect);
        }

        if (!system.HasAuraHandler(AuraType.ForceReaction))
        {
            system.RegisterAura(AuraType.ForceReaction, new AuraHandler(
                static (s, holder, aura, apply) => ForceReaction(s, holder, aura, apply), null));
        }

        foreach (AuraType gain in new[] { AuraType.ModReputationGain, AuraType.ModFactionReputationGain })
        {
            if (!system.HasAuraHandler(gain))
            {
                system.RegisterAura(gain, new AuraHandler(null, null));
            }
        }
    }

    /// <summary>
    /// The percentage added to a positive reputation gain (Player::CalculateReputationGain, Player.cpp:6258-6262): the sum of
    /// SPELL_AURA_MOD_REPUTATION_GAIN, plus the faction-specific aura for kills only ("faction specific auras only seem to apply to kills").
    /// </summary>
    public static Func<Player, ReputationSource, uint, float> GainModifier(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        return (player, source, faction) =>
            system.GetTotalAuraModifier(player, AuraType.ModReputationGain)
            + (source == ReputationSource.Kill
                ? system.GetTotalAuraModifier(player, AuraType.ModFactionReputationGain, aura => (uint)aura.MiscValue == faction)
                : 0);
    }

    private static void ReputationEffect(SpellEffectContext context)
    {
        if (context.Target is not Player player || ReputationEnvironment.For(context.System) is not { } service)
        {
            return;
        }

        uint faction = (uint)context.Effect.MiscValue;
        if (service.Factions.Find(faction) is null)
        {
            return;
        }

        // CalculateReputationGain(REPUTATION_SOURCE_SPELL, base points, faction): the Diplomacy aura and the faction rate apply.
        int gain = service.Gain(ReputationSource.Spell, player, context.Value, faction, player.Level);
        service.ModifyReputation(player, faction, gain);
    }

    private static void ForceReaction(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (holder.Target is not Player player || ReputationEnvironment.For(system) is not { } service)
        {
            return;
        }

        service.ApplyForcedReaction(player, (uint)aura.MiscValue, (ReputationRank)(uint)aura.Amount, apply);
    }
}
