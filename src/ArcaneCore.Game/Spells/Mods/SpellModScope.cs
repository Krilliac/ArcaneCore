using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Mods;

/// <summary>
/// The charged mods one cast has used (vmangos <c>Spell::m_appliedMods</c>). A cast gets one when it is prepared; while it is
/// the current scope (the consume windows <see cref="ISpellModEngine.Begin"/> opens around the cast-time and cast phases) the
/// engine spends charges on it, and a mod with no charge left (-1) keeps applying to exactly the casts whose scope holds it.
/// </summary>
public sealed class SpellModScope
{
    private readonly List<SpellMod> _applied = [];

    internal SpellModScope(Player owner, SpellInfo spell)
    {
        Owner = owner;
        Spell = spell;
    }

    /// <summary>The player whose mods this cast reads.</summary>
    public Player Owner { get; }

    public SpellInfo Spell { get; }

    /// <summary>The mods this cast spent a charge of, in the order they were spent.</summary>
    public IReadOnlyList<SpellMod> Applied => _applied;

    /// <summary>Whether the cast already ended its mods (<see cref="ISpellModEngine.Seal"/> or <see cref="ISpellModEngine.Restore"/> ran).</summary>
    public bool IsClosed { get; internal set; }

    /// <summary>vmangos Spell::HasModifierApplied (Spell.cpp:8282-8289).</summary>
    public bool HasModifierApplied(SpellMod mod) => _applied.Contains(mod);

    internal void Add(SpellMod mod) => _applied.Add(mod);

    internal bool Remove(SpellMod mod) => _applied.Remove(mod);

    internal void Clear() => _applied.Clear();
}

/// <summary>A consume window (see <see cref="ISpellModEngine.Begin"/>): disposing it closes the window. A default value does nothing.</summary>
public readonly struct SpellModWindow : IDisposable
{
    private readonly SpellModEngine? _engine;
    private readonly SpellModScope? _scope;

    internal SpellModWindow(SpellModEngine engine, SpellModScope scope)
    {
        _engine = engine;
        _scope = scope;
    }

    public void Dispose() => _engine?.End(_scope!);
}
