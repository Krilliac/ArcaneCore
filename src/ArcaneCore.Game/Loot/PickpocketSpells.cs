using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Loot;

/// <summary>
/// Registers Pick Pocket (<see cref="SpellEffectName.Pickpocket"/>, effect 71) on a spell system: the cast check of
/// <see cref="PickpocketLoot.CheckTarget"/> and the effect (vmangos Spell::EffectPickPocket, SpellEffects.cpp:2651-2661): a player caster, a living
/// creature target that is not friendly. The per-map state is <see cref="PickpocketLoot"/>; this class only routes by map. Install once per spell system.
/// </summary>
public sealed class PickpocketSpells(Func<Map, PickpocketLoot?> pickpocketOf)
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffectCheck(SpellEffectName.Pickpocket, CheckTarget);
        system.RegisterEffect(SpellEffectName.Pickpocket, context => Effect(system, context));
    }

    private SpellCastResult CheckTarget(SpellEffectCheckContext context)
        => context.Caster.Map is { } map && pickpocketOf(map) is { } pockets ? pockets.CheckTarget(context.UnitTarget) : SpellCastResult.BadTargets;

    private void Effect(SpellSystem system, SpellEffectContext context)
    {
        if (context.Caster is not Player player || context.Target is not Creature { IsAlive: true } creature
            || system.Relations.IsFriendly(player, creature) || player.Map is not { } map)
        {
            return; // victim must be a living creature the caster may attack (vmangos EffectPickPocket)
        }

        pickpocketOf(map)?.Pick(player, creature);
    }
}