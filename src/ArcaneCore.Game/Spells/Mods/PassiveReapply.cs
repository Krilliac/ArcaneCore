using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Mods;

/// <summary>
/// vmangos <c>Aura::ReapplyAffectedPassiveAuras</c> (SpellAuras.cpp:1005-1075): after a modifier is added or removed, each
/// permanent passive aura the player cast on itself that the modifier affects is removed and cast again, so an amount that read
/// a modifier is recomputed. Skipped for the operations whose consumers read the engine live (duration, charges, casting time,
/// cooldown, cost, activation time, global cooldown, speed, haste, attack power; :1056-1070) and for a mod spell with proc
/// charges (:1051-1054). Pets and totems the player controls are not refreshed (limit, docs/areas/spell-mods.md).
/// </summary>
internal static class PassiveReapply
{
    [ThreadStatic]
    private static int _depth;

    /// <summary>A recast passive that is itself a modifier would re-enter. vmangos has no limit; this is a safety guard against a cycle, and that two levels cover the retail talent chains is unverified (docs/areas/spell-mods.md, Limits).</summary>
    private const int MaxDepth = 2;

    public static void Run(SpellSystem system, SpellModEngine engine, Player player, SpellMod mod, SpellInfo modSpell)
    {
        if (!engine.Options.ReapplyPassives || modSpell.ProcCharges != 0 || IsSkipped(mod.Op) || _depth >= MaxDepth)
        {
            return;
        }

        UnitSpellState? state = system.GetState(player.Guid);
        if (state is null)
        {
            return;
        }

        // Collect first: removing and casting edits the holder list. A map keeps one entry per spell (vmangos affectedSelf).
        var affected = new SortedSet<uint>();
        foreach (SpellAuraHolder holder in state.Auras)
        {
            if (holder.Spell.IsPassive && holder.IsPermanent && !holder.IsRemoved && holder.Spell.Id != mod.SpellId
                && holder.CasterGuid == player.Guid && mod.IsAffectedOnSpell(holder.Spell))
            {
                affected.Add(holder.Spell.Id);
            }
        }

        if (affected.Count == 0)
        {
            return;
        }

        _depth++;
        try
        {
            foreach (uint spellId in affected)
            {
                system.RemoveAuras(player, spellId);
                system.CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: true);
            }
        }
        finally
        {
            _depth--;
        }
    }

    private static bool IsSkipped(SpellModOp op) => op is SpellModOp.Duration or SpellModOp.Charges or SpellModOp.NotLoseCastingTime
        or SpellModOp.CastingTime or SpellModOp.Cooldown or SpellModOp.Cost or SpellModOp.ActivationTime
        or SpellModOp.GlobalCooldown or SpellModOp.Speed or SpellModOp.Haste or SpellModOp.AttackPower;
}
