using System.Runtime.CompilerServices;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.MoltenCore;

/// <summary>mangos-classic Spells/SpellEffects.cpp Spell::EffectDummy, cases 19411,20474,21108,21908,23138.
/// Follow-up spells and their targets/positions remain content-owned. Gate uses vmangos boss_shazzrahAI::UpdateAI
/// (NearTeleportTo a random attacking player), with cmangos ReceiveAIEvent for the explosion.</summary>
[SpellScript(18947, 19411, 20474, 20538, 21108, 21908, 23138)]
public sealed class MoltenCoreSpellScripts : ISpellScript
{
    private readonly ConditionalWeakTable<SpellCast, object> _gates = new();
    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 0) return;
        uint spell = context.Spell.Id;
        if (spell == 20538)
        {
            // mangos-classic scripts/world/spell_scripts.cpp HateToZero::OnEffectExecute.
            context.Caster.Combat.Threat.ModifyThreatPercent(context.Target, -100);
            return;
        }
        if (spell == 18947)
        {
            SpellAuraHolder? holder = context.System.GetAuras(context.Caster).FirstOrDefault(h => h.Spell.Id == context.Cast.TriggeringSpell?.Id);
            int tick = holder?.Auras[0]?.TickCount ?? 0;
            int[] damage = [500, 500, 1000, 1000, 2000, 2000, 3000, 5000];
            if (tick is >= 1 and <= 8) context.System.CastCustomSpell(context.Caster, 19698, SpellCastTargets.ForSelf(), damage[tick - 1]);
            return;
        }
        if (spell is 19411 or 20474)
        {
            context.System.CastSpell(context.Target, spell == 19411 ? 20494u : 20495u, SpellCastTargets.ForSelf(), triggered: true);
            return;
        }
        if (spell == 21108)
        {
            for (uint summon = 21110; summon <= 21117; summon++)
                context.System.CastSpell(context.Caster, summon, SpellCastTargets.ForSelf(), triggered: true);
            return;
        }
        if (spell == 21908)
        {
            uint[] bursts = [21886, 21900, 21901, 21902, 21903, 21904, 21905, 21906, 21907];
            int choice = (context.Caster as Creature)?.System?.RandomInt(0, 8) ?? 0;
            context.System.CastSpell(context.Caster, bursts[choice], SpellCastTargets.ForSelf(), triggered: true);
            return;
        }
        if (spell == 23138 && context.Caster is Creature boss && !_gates.TryGetValue(context.Cast, out _))
        {
            _gates.Add(context.Cast, new object());
            Unit[] targets = boss.Combat.Threat.Entries.Select(e => e.Target).Where(u => u is Player && u.IsAlive).ToArray();
            if (targets.Length == 0) return;
            Unit selected = targets[boss.System?.RandomInt(0, targets.Length - 1) ?? 0];
            // The teleport's location is the selected player's position (the reverse-cast effect 23139).
            boss.System?.NearTeleport(boss, selected.X, selected.Y, selected.Z, boss.Orientation);
            boss.AI?.OnReceiveAiEvent(1000, boss, boss, 0);
            boss.AI?.AttackStart(selected);
        }
    }
}
