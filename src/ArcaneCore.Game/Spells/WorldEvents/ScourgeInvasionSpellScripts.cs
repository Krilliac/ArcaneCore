using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Kernel.WorldData.WorldState;

namespace ArcaneCore.Game.Spells.WorldEvents;

/// <summary>
/// spell_communique_trigger (mangos-classic scourge_invasion.cpp:1305-1312, bound at :1379): the camp timer's trigger 28345 makes its unit
/// target, the Necrotic Shard, cast the camp-to-relay communique 28281 (triggered, no explicit target, so the spell picks the nearest relay
/// by its spell_script_target row).
/// </summary>
[SpellScript(ScourgeInvasionCatalog.CommuniqueTrigger)]
public sealed class CommuniqueTriggerScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is not { IsAlive: true } target) return;
        context.System.CastSpell(target, ScourgeInvasionCatalog.CommuniqueCampToRelay, SpellCastTargets.ForSelf(), triggered: true);
    }
}

/// <summary>
/// spell_despawner_self (scourge_invasion.cpp:1295-1303, bound at :1378): Despawner, self 28091 makes a caster that is not in combat cast
/// Spirit Spawn-out 17680 on itself (triggered); the minion's own SpellHit then despawns it 3 s later.
/// </summary>
[SpellScript(ScourgeInvasionCatalog.DespawnerSelf)]
public sealed class DespawnerSelfScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Caster.Combat.IsInCombat) return;
        context.System.CastSpell(context.Caster, ScourgeInvasionCatalog.SpiritSpawnOut, SpellCastTargets.ForSelf(), triggered: true);
    }
}
