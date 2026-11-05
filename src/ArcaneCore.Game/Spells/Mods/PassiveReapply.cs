using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Mods;

/// <summary>
/// vmangos <c>Aura::ReapplyAffectedPassiveAuras</c> (SpellAuras.cpp:1005-1075): after a modifier is added or removed, each
/// permanent passive aura the player cast on itself that the modifier affects is removed and cast again, so an amount that read
/// a modifier is recomputed. Skipped for the operations whose consumers read the engine live (duration, charges, casting time,
/// cooldown, cost, activation time, global cooldown, speed, haste, attack power; :1056-1070) and for a mod spell with proc
/// charges (:1051-1054). The pets and totems the player controls are refreshed too (:1067-1073).
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

        ReapplyOn(system, player, mod);

        // Aura::ReapplyAffectedPassiveAuras (SpellAuras.cpp:1067-1073): the passives, area auras and talents on the player's pet and
        // totems read the owner's mods too (GetSpellModOwner), so they are refreshed as well. Only when the owner-mod resolution is on.
        if (engine.Options.OwnerModsForPetsAndTotems)
        {
            foreach (Unit controlled in system.ControlledPetsAndTotems(player))
            {
                ReapplyOn(system, controlled, mod);
            }
        }
    }

    /// <summary>One unit's own permanent passives that <paramref name="mod"/> affects: removed and cast again (vmangos Aura::ReapplyAffectedPassiveAuras(Unit*)).</summary>
    private static void ReapplyOn(SpellSystem system, Unit unit, SpellMod mod)
    {
        UnitSpellState? state = system.GetState(unit.Guid);
        if (state is null)
        {
            return;
        }

        // Collect first: removing and casting edits the holder list. A map keeps one entry per spell (vmangos affectedSelf).
        var affected = new SortedSet<uint>();
        foreach (SpellAuraHolder holder in state.Auras)
        {
            if (holder.Spell.IsPassive && holder.IsPermanent && !holder.IsRemoved && holder.Spell.Id != mod.SpellId
                && holder.CasterGuid == unit.Guid && mod.IsAffectedOnSpell(holder.Spell))
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
                system.RemoveAuras(unit, spellId);
                system.CastSpell(unit, spellId, SpellCastTargets.ForSelf(), triggered: true);
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
