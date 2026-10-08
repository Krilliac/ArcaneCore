using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Procs;

namespace ArcaneCore.Game.Spells.Mage;

/// <summary>
/// Ignite (11119, 11120, 12846, 12847, 12848; vmangos scripts/spells/spell_mage.cpp:50-130, <c>MageIgniteScript::OnProc</c>). The talent is a passive
/// DUMMY aura procced by a fire spell's critical hit (spell_proc_event: school mask 4, procEx CRITICAL_HIT). 4, 8, 12, 16 or 20 percent of the crit's
/// original amount (before absorbs and resists) is the per-tick damage of the Ignite damage over time (12654: PERIODIC_DAMAGE every 2 s for 4 s, up to
/// 5 stacks) on the victim:
/// <list type="bullet">
/// <item>No Ignite on the victim: the mage casts 12654 at it with the share as its base points.</item>
/// <item>An Ignite (anyone's, vmangos <c>GetAura(SPELL_DOT, EFFECT_INDEX_0)</c>) that still has ticks to deal: under 5 stacks the share is added to its
/// tick damage and it gains a stack; at 5 stacks only the duration is refreshed. Either way its ticks start over, so the accumulated tick damage rolls
/// into a new 4 seconds.</item>
/// <item>An Ignite whose ticks are all dealt (it has not been removed yet): it is removed and a fresh one is cast.</item>
/// </list>
/// </summary>
public sealed class IgniteScript : IProcScript
{
    /// <summary>The Ignite damage over time.</summary>
    public const uint IgniteDot = 12654;

    /// <summary>The most stacks the damage over time takes (12654 StackAmount; the script's <c>&lt; 5</c>).</summary>
    public const int MaxStacks = 5;

    /// <summary>The talent ranks, 1 to 5.</summary>
    public static readonly uint[] Ranks = [11119, 11120, 12846, 12847, 12848];

    /// <summary>The share of the crit one rank turns into tick damage (vmangos <c>0.04f * totalDamage</c> ... <c>0.20f * totalDamage</c>).</summary>
    public static float ShareOf(uint rank) => Array.IndexOf(Ranks, rank) switch
    {
        0 => 0.04f,
        1 => 0.08f,
        2 => 0.12f,
        3 => 0.16f,
        4 => 0.20f,
        _ => 0f,
    };

    public AuraProcResult? OnProc(in AuraProcContext context)
    {
        float share = ShareOf(context.Holder.Spell.Id);
        if (share == 0f || context.Target is not { } victim)
        {
            return AuraProcResult.Failed; // "non handled spell id"
        }

        int basePoints = (int)(share * context.OriginalAmount);
        SpellSystem system = context.System;
        if (system.GetAuras(victim).FirstOrDefault(h => !h.IsRemoved && h.Spell.Id == IgniteDot && h.Auras[0] is not null) is { } ignite)
        {
            SpellAura dot = ignite.Auras[0]!;
            int maxTicks = ignite.MaxDuration > 0 && dot.Period > 0 ? ignite.MaxDuration / (int)dot.Period : 0;
            if (dot.TickCount < maxTicks)
            {
                if (ignite.StackAmount < MaxStacks)
                {
                    int tickDamage = dot.Amount + basePoints;
                    system.ModAuraStackAmount(ignite, 1);
                    system.SetAuraAmount(ignite, dot, tickDamage);
                }
                else
                {
                    system.ModAuraStackAmount(ignite, 0); // SetStackAmount(5): no change, the duration is refreshed
                }

                system.RestartHolderTicks(ignite);
                return AuraProcResult.Ok;
            }

            // All damage done: remove it and apply a fresh one.
            system.RemoveAuras(victim, IgniteDot);
        }

        return system.CastCustomSpell(context.Owner, IgniteDot, SpellCastTargets.ForUnit(victim.Guid), basePoints) == SpellCastResult.CastOk
            ? AuraProcResult.Ok
            : AuraProcResult.Failed;
    }
}

/// <summary>Registers the mage talent proc scripts (discovered <see cref="ISpellHandlerModule"/>): Ignite's five ranks, Combustion's proc aura and buff.</summary>
public sealed class MageTalentScriptsModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        var ignite = new IgniteScript();
        foreach (uint rank in IgniteScript.Ranks)
        {
            system.RegisterProcScript(rank, ignite);
        }

        CombustionScript.Register(system);
    }
}
