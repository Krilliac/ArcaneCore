using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.RuinsOfAhnQiraj;

/// <summary>mangos-classic boss_moam.cpp SummonManaFiendsMoam::OnEffectExecute.</summary>
[SpellScript(25684)]
public sealed class MoamManaFiendsScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 0) return;
        foreach (uint spell in new uint[] { 25681, 25682, 25683 })
            context.System.CastSpell(context.Caster, spell, SpellCastTargets.ForSelf(), triggered: true);
    }
}

/// <summary>mangos-classic boss_buru.cpp npc_buru_eggAI::JustDied:
/// explode, summon a hatchling and force Buru to choose a new target.</summary>
public sealed class BuruEggAI(Creature creature) : CreatureAI(creature)
{
    private void Explode()
    {
        if (Me.Map is not { } map) return;
        // boss_buru.cpp npc_buru_eggAI::SpellHitTarget supplies the custom
        // explosion value before casting 5255. This host excludes a dead
        // spell caster from SpellHitTarget, so apply those values at death.
        foreach (Player player in map.Players.Where(p => p.IsAlive))
        {
            float dx = Me.X - player.X, dy = Me.Y - player.Y, dz = Me.Z - player.Z;
            float distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance > 25) continue;
            uint damage = (uint)(100 + (25 - distance) / 25 * 400);
            map.Combat.DealDamage(Me, player, damage, direct: false, meleeDamage: false, startsCombat: false);
        }
        if (map.FindUpdater<CreatureMapSystem>()?.Creatures.FirstOrDefault(c => c.Entry == 15370 && c.IsAlive)
            is { } boss)
        {
            float dx = Me.X - boss.X, dy = Me.Y - boss.Y, dz = Me.Z - boss.Z;
            if (dx * dx + dy * dy + dz * dz <= 625)
            {
                map.Combat.DealDamage(Me, boss, boss.MaxHealth * 15 / 100,
                    direct: false, meleeDamage: false, startsCombat: false);
                if (boss.AI is BuruAI ai) ai.OnEggDestroyed();
            }
        }
    }

    public override void OnDeath(Unit? killer)
    {
        Explode();
        DoCast(Me, 19593, triggered: true);
        DoCast(Me, 1881, triggered: true);
    }
    public override void OnUpdate(uint diffMs) { }
}
