using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Gm;

/// <summary>
/// The GM spellbook: teaches staff accounts the <see cref="CustomSpellRegistry"/> spells and refuses their cast for anyone else. The handler
/// (an <see cref="ISpellCastCheck"/> at the start of the cast, for triggered casts too) checks the caster's account security against the entry
/// and, for a client cast, that the spell is known. A non-player caster (a script, a creature) is not restricted.
/// <para>
/// Candidates are filtered before they are taught (<see cref="Unsuitable"/>): a spell that needs a reagent, a spell focus, a shapeshift stance or an
/// equipped item would fail for a staff account with none of them, and a spell with no visual would be a silent cast.
/// </para>
/// Stateless; every member is safe from any thread (the world thread in practice).
/// </summary>
public sealed class GmSpellbook : ISpellHandlerModule
{
    /// <summary>Ahead of every other start check, so a refused account hears nothing else about the spell.</summary>
    public const int CheckOrder = 0;

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterCastCheck(new SecurityCheck());
    }

    /// <summary>Why <paramref name="spell"/> cannot be a staff spell, or null when it can: reagent, focus, stance, item requirement or no visual.</summary>
    public static string? Unsuitable(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        foreach (SpellReagent reagent in spell.Reagents)
        {
            if (reagent.Item != 0 && reagent.Count != 0)
            {
                return "needs a reagent";
            }
        }

        if (spell.RequiresSpellFocus != 0)
        {
            return "needs a spell focus";
        }

        if (spell.Stances != 0)
        {
            return "needs a shapeshift stance";
        }

        if (spell.EquippedItemClass != -1)
        {
            return "needs an equipped item";
        }

        return spell.SpellVisual == 0 ? "has no visual" : null;
    }

    /// <summary>The registry entries that are not in <paramref name="store"/> or that <see cref="Unsuitable"/> rejects, with the reason.</summary>
    public static IReadOnlyList<(uint SpellId, string Reason)> Audit(SpellStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var problems = new List<(uint SpellId, string Reason)>();
        foreach (CustomSpell entry in CustomSpellRegistry.All)
        {
            if (store.Get(entry.SpellId) is not { } spell)
            {
                problems.Add((entry.SpellId, "not in the spell store"));
            }
            else if (Unsuitable(spell) is { } reason)
            {
                problems.Add((entry.SpellId, reason));
            }
        }

        return problems;
    }

    /// <summary>
    /// Teach <paramref name="player"/> every custom spell its account security allows, that is in the store and passes <see cref="Unsuitable"/>.
    /// Returns the spells newly learned (already known ones are skipped). World thread only: it mutates the spellbook.
    /// </summary>
    public static IReadOnlyList<uint> Teach(SpellSystem system, Player player)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(player);
        var learned = new List<uint>();
        foreach (CustomSpell entry in CustomSpellRegistry.All)
        {
            if (player.Security >= entry.MinimumSecurity && system.Store.Get(entry.SpellId) is { } spell
                && Unsuitable(spell) is null && system.LearnSpell(player, entry.SpellId))
            {
                learned.Add(entry.SpellId);
            }
        }

        return learned;
    }

    private sealed class SecurityCheck : ISpellCastCheck
    {
        public SpellCheckPhase Phase => SpellCheckPhase.Start;

        public int Order => CheckOrder;

        public SpellCastResult Check(in SpellCastCheckContext context)
        {
            if (context.Caster is not Player player || !CustomSpellRegistry.TryGet(context.Spell.Id, out CustomSpell? entry))
            {
                return SpellCastResult.CastOk;
            }

            if (player.Security < entry.MinimumSecurity)
            {
                return SpellCastResult.NotKnown;
            }

            return !context.Triggered && context.System.Spellbook is { } book && !book.HasSpell(player, entry.SpellId)
                ? SpellCastResult.NotKnown
                : SpellCastResult.CastOk;
        }
    }
}
