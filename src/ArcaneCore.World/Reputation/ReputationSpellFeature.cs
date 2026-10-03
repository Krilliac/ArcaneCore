using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Reputation;

/// <summary>
/// Connects reputation and the spell system: registers the live <see cref="ReputationService"/> for the spell handlers
/// (<see cref="ReputationSpellHandlers"/>: the reputation effect and forced reactions), feeds the reputation gain auras
/// (Diplomacy, SPELL_AURA_MOD_REPUTATION_GAIN; the kills-only faction aura) into <see cref="ReputationService.GainModifier"/>, and
/// lets a forced Friendly rank stop the player's fight with that faction. A gain modifier another area already set is kept.
/// </summary>
public sealed class ReputationSpellFeature(IServiceProvider services) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (services.GetService<SpellFeature>() is not { } spells)
        {
            return;
        }

        ReputationService service = services.GetRequiredService<ReputationFeature>().Service;
        SpellSystem system = spells.System;
        ReputationEnvironment.Register(system, service);
        service.GainModifier ??= ReputationSpellHandlers.GainModifier(system);
        service.StopAttackFaction ??= (player, faction) => StopAttackFaction(player, faction);
    }

    private void StopAttackFaction(Player player, uint faction)
    {
        ReputationReactionResolver? resolver = services.GetService<ReputationCombatFeature>()?.Resolver;
        if (resolver is null || player.Map is not { } map || player.Combat.Victim is not { } victim)
        {
            return;
        }

        // Unit::StopAttackFaction for the player's own victim (Unit.cpp:10008-10019).
        if (resolver.TemplateOf(victim)?.Faction == faction)
        {
            map.Combat.AttackStop(player);
        }
    }
}
