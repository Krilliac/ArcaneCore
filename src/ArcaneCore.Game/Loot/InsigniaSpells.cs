using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Loot;

/// <summary>
/// Registers SPELL_EFFECT_SKIN_PLAYER_CORPSE (116, Remove Insignia 22027; vmangos Spell::EffectSkinPlayerCorpse, SpellEffects.cpp:5849-5875) on a
/// spell system: a player caster; the target is the dead player (the explicit unit, or the owner of the targeted corpse), or the targeted
/// corpse alone when its owner is not here. The per-map state is <see cref="InsigniaLoot"/>; this class only routes by map. Install once per
/// spell system.
/// </summary>
public sealed class InsigniaSpells(Func<Map, InsigniaLoot?> insigniaOf)
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (system.HasEffectHandler(SpellEffectName.SkinPlayerCorpse))
        {
            throw new InvalidOperationException("the SkinPlayerCorpse effect already has a handler; installing a second one would replace it");
        }

        system.RegisterEffect(SpellEffectName.SkinPlayerCorpse, context => Effect(system, context));
    }

    private void Effect(SpellSystem system, SpellEffectContext context)
    {
        if (context.Caster is not Player looter || looter.Map is not { } map || insigniaOf(map) is not { } insignia)
        {
            return; // "Player* playerCaster = m_caster->ToPlayer(); if (!playerCaster) return;"
        }

        Corpse? corpse = system.ResolveCorpseTarget(looter, context.Cast.Targets);
        Player? victim = context.Target is Player target && !ReferenceEquals(target, looter) ? target : null;
        if (victim is null && corpse is not null && map.FindPlayer(corpse.Owner) is { } owner)
        {
            victim = owner;
        }

        insignia.Remove(looter, victim, corpse);
    }
}
