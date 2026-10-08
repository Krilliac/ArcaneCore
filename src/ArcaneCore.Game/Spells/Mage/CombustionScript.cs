using ArcaneCore.Game.Spells.Procs;

namespace ArcaneCore.Game.Spells.Mage;

/// <summary>
/// Combustion (vmangos scripts/spells/spell_mage.cpp:136-205, built for clients after 1.10.2). The spell in the book, 11129, puts an invisible DUMMY
/// proc aura with 3 charges on the mage (procced by a harmful fire spell: spell_proc_event school mask 4) and, through its TRIGGER_SPELL effect, the
/// first stack of the visible buff 28682 (+10% fire critical strike chance per stack, up to 10 stacks).
/// <list type="bullet">
/// <item>Proc aura (<c>MageCombustionProcScript::OnProc</c>, :144-170): a proc with no victim (an area spell) fails. When the buff is gone (dispelled or
/// clicked off) the proc aura goes too. On the last charge a critical hit ends the buff and spends the charge, which ends the proc aura. Otherwise every
/// fire spell hit adds a buff stack; only a critical hit spends a charge.</item>
/// <item>Buff (<c>MageCombustionBuffScript::OnAfterApply</c>, :177-190): cancelling the buff removes the proc aura (which starts the cooldown).</item>
/// </list>
/// </summary>
public sealed class CombustionScript : IProcScript
{
    /// <summary>Combustion, the invisible proc aura (the spell in the book).</summary>
    public const uint ProcAura = 11129;

    /// <summary>Combustion, the visible stacking critical strike buff.</summary>
    public const uint CritBuff = 28682;

    /// <summary>Install the proc script on <see cref="ProcAura"/> and the cancel hook of <see cref="CritBuff"/>.</summary>
    public static void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterProcScript(ProcAura, new CombustionScript());
        system.HolderRemoved += holder =>
        {
            // OnAfterApply(apply = false) of effect 0 with AURA_REMOVE_BY_CANCEL.
            if (holder.Spell.Id == CritBuff && holder.RemoveMode == AuraRemoveMode.Cancel)
            {
                system.RemoveAuras(holder.Target, ProcAura);
            }
        };
    }

    public AuraProcResult? OnProc(in AuraProcContext context)
    {
        if (context.Target is null)
        {
            return AuraProcResult.Failed;
        }

        SpellSystem system = context.System;
        if (!system.HasAura(context.Owner, CritBuff))
        {
            system.RemoveAuras(context.Owner, context.Holder.Spell.Id);
            return AuraProcResult.Failed;
        }

        bool crit = (context.ProcExtra & ProcFlagsEx.CriticalHit) != 0;
        if (context.Holder.Charges <= 1 && crit)
        {
            system.RemoveAuras(context.Owner, CritBuff);
            return AuraProcResult.Ok; // charge counting: the last charge goes and the proc aura with it
        }

        system.CastSpellTriggeredByAura(context.Owner, CritBuff, SpellCastTargets.ForSelf(), context.Holder.Spell);
        return crit ? AuraProcResult.Ok : AuraProcResult.Failed; // a charge only at a crit, no hidden cooldown
    }
}
