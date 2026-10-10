using System.Runtime.CompilerServices;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.Naxxramas;

/// <summary>
/// mangos-classic naxxramas/boss_grobbulus.cpp MutatingInjection::OnApply:
/// expiry causes Embalming Cloud, dispel causes Mutagen Explosion, either leaves a Poison Cloud.
/// The 1.12 Spell.dbc aura type for 28169 is DUMMY.
/// </summary>
public sealed class NaxxramasAuraModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        system.RegisterDummyAuraHandler(28169, (spells, holder, aura, apply) =>
        {
            if (apply || holder.Target.Map?.MapId != 533 || !holder.Target.IsAlive || aura.EffectIndex != 0) return;
            uint result = holder.RemoveMode switch
            {
                AuraRemoveMode.Expire => 28322,
                AuraRemoveMode.Dispel => 28206,
                _ => 0,
            };
            if (result != 0)
                spells.CastSpell(holder.Target, result, SpellCastTargets.ForSelf(), triggered: true);
            if (result != 0)
                spells.CastSpell(holder.Target, 28240, SpellCastTargets.ForSelf(), triggered: true);
        });
        // mangos-classic boss_kelthuzad.cpp FrostBlast::OnPeriodicTrigger.
        system.RegisterPeriodicTriggerScript(27808, (spells, holder, _) =>
        {
            Unit target = holder.Target;
            if (target.Map?.MapId != 533 || !target.IsAlive
                || target.Map.FindObject(holder.CasterGuid) is not Unit caster) return;
            int damage = (int)((ulong)target.MaxHealth * 26 / 100);
            spells.CastCustomSpell(caster, 29879, SpellCastTargets.ForUnit(target.Guid), damage);
        });
        // mangos-classic boss_thaddius.cpp ThaddiusCharge::OnPeriodicTrigger
        // and ThaddiusChargeDamage::OnCheckTarget. Charges pulse over the 13-yard
        // DBC radius; same polarity buffs, opposite polarity takes the pulse.
        system.RegisterPeriodicTriggerScript(28059, ChargeTick);
        system.RegisterPeriodicTriggerScript(28084, ChargeTick);
        system.RegisterObserver(new HorsemenMarkObserver(system));
    }

    /// <summary>
    /// mangos-classic naxxramas/boss_four_horsemen.cpp HorsemenMark::OnApply (vmangos boss_four_horsemen_shared::SpellHitTarget):
    /// each new stack of a horseman's mark past the first deals 28836 from that horseman, 250/1000/3000 for stacks 2/3/4 and
    /// 1000 per stack after that.
    /// </summary>
    private sealed class HorsemenMarkObserver(SpellSystem system) : ISpellCastObserver
    {
        public void OnTargetOutcome(SpellCast cast, SpellTargetOutcome outcome)
        {
            if (outcome.Miss != SpellMissInfo.None || cast.Spell.Id is not (28832 or 28833 or 28834 or 28835)
                || outcome.Target.Map?.MapId != 533 || !outcome.Target.IsAlive) return;
            int stacks = system.GetAuras(outcome.Target)
                .FirstOrDefault(h => !h.IsRemoved && h.Spell.Id == cast.Spell.Id && h.CasterGuid == cast.Caster.Guid)?.StackAmount ?? 0;
            int damage = MarkDamage(stacks);
            if (damage > 0)
                system.CastCustomSpell(cast.Caster, 28836, SpellCastTargets.ForUnit(outcome.Target.Guid), damage);
        }
    }

    /// <summary>HorsemenMark::OnApply's stack table (no damage for the first mark).</summary>
    public static int MarkDamage(int stacks) => stacks switch
    {
        <= 1 => 0,
        2 => 250,
        3 => 1000,
        4 => 3000,
        _ => 1000 * stacks,
    };

    private static void ChargeTick(SpellSystem spells, SpellAuraHolder holder, SpellAura _)
    {
        Unit source = holder.Target;
        if (source.Map?.MapId != 533 || !source.IsAlive) return;
        uint charge = holder.Spell.Id;
        uint buff = charge == 28059 ? 29659u : 29660u;
        uint pulse = charge == 28059 ? 28062u : 28085u;
        spells.RemoveAuras(source, buff);
        foreach (Player other in source.Map.Players.Where(p => p != source && p.IsAlive))
        {
            float dx = source.X - other.X, dy = source.Y - other.Y;
            if (dx * dx + dy * dy > 169) continue;
            if (spells.HasAura(other, charge))
                spells.CastSpell(source, buff, SpellCastTargets.ForSelf(), triggered: true);
            else
                spells.CastSpell(source, pulse, SpellCastTargets.ForUnit(other.Guid), triggered: true);
        }
    }
}

/// <summary>
/// mangos-classic naxxramas/boss_thaddius.cpp PolarityShift::OnEffectExecute:
/// remove the previous charge and assign a fresh positive or negative charge to each hit player.
/// </summary>
[SpellScript(28089)]
public sealed class ThaddiusPolarityScript : ISpellScript
{
    private sealed class ShiftState { public int Targets; public uint First; }
    private readonly ConditionalWeakTable<SpellCast, ShiftState> _states = new();

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 0 || context.Caster is not Creature { AI: ThaddiusAI } || context.Target is not Player target)
            return;
        context.System.RemoveAuras(target, 28059);
        context.System.RemoveAuras(target, 28084);
        ShiftState state = _states.GetValue(context.Cast, static _ => new ShiftState());
        uint charge = state.Targets switch
        {
            0 => context.System.Random.Next(2) == 0 ? 28059u : 28084u,
            1 => state.First == 28059 ? 28084u : 28059u,
            _ => context.System.Random.Next(2) == 0 ? 28059u : 28084u,
        };
        if (state.Targets == 0) state.First = charge;
        state.Targets++;
        context.System.CastSpell(context.Caster, charge, SpellCastTargets.ForUnit(target.Guid), triggered: true);
    }
}

/// <summary>
/// mangos-classic naxxramas/boss_kelthuzad.cpp ChainsKelThuzad::OnEffectExecute:
/// four random non-tanks and the current tank are controlled, but only with at least
/// five eligible non-tanks so the spell does not control the whole remaining raid.
/// </summary>
[SpellScript(28408)]
public sealed class KelThuzadChainsScript : ISpellScript
{
    private readonly ConditionalWeakTable<SpellCast, object> _processed = new();

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 0 || context.Caster is not Creature { AI: KelThuzadAI } boss
            || _processed.TryGetValue(context.Cast, out _)) return;
        _processed.Add(context.Cast, new object());
        Player? tank = boss.Combat.Victim as Player;
        if (tank is null) return;
        Player[] candidates = [.. boss.Combat.Threat.Entries.Select(e => e.Target).OfType<Player>()
            .Where(p => p != tank && p.IsAlive && p.IsInWorld && p.Map == boss.Map)
            .Where(p => { float x = p.X - boss.X, y = p.Y - boss.Y; return x * x + y * y <= 10000; })];
        if (candidates.Length <= 4) return;
        for (int i = candidates.Length - 1; i > 0; i--)
        {
            int j = context.System.Random.Next(i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }
        foreach (Player target in candidates.Take(4).Append(tank))
        {
            context.System.CastSpell(boss, 28410, SpellCastTargets.ForUnit(target.Guid), triggered: true);
            context.System.CastSpell(target, 28409, SpellCastTargets.ForSelf(), triggered: true);
        }
    }
}
