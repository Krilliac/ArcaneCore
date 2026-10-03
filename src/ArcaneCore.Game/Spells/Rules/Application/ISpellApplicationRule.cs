using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Rules.Application;

/// <summary>
/// One spell landing on one target while its effects are applied (vmangos Spell::DoSpellHitOnUnit /
/// DoAllEffectOnTarget). The rules narrow <see cref="EffectMask"/> before the effects run and may veto the
/// aura holder the effects built.
/// </summary>
public sealed class SpellApplication
{
    private Dictionary<ISpellApplicationRule, object>? _state;

    internal SpellApplication(SpellSystem system, SpellCast cast, Unit target, int effectMask)
    {
        System = system;
        Cast = cast;
        Target = target;
        EffectMask = effectMask;
    }

    public SpellSystem System { get; }

    public SpellCast Cast { get; }

    public Unit Target { get; }

    /// <summary>The effects that still apply (bit per effect index); a rule may clear bits.</summary>
    public int EffectMask { get; set; }

    /// <summary>State a rule keeps between <see cref="ISpellApplicationRule.Begin"/> and <see cref="ISpellApplicationRule.AcceptHolder"/>.</summary>
    public object? GetState(ISpellApplicationRule rule) => _state?.GetValueOrDefault(rule);

    public void SetState(ISpellApplicationRule rule, object state) => (_state ??= [])[rule] = state;
}

/// <summary>
/// A rule of spell application, kept in <see cref="SpellSystem.ApplicationRules"/> and run in list order:
/// per-effect mechanic resistance, then diminishing returns, then (later) immunities.
/// </summary>
public interface ISpellApplicationRule
{
    /// <summary>Before any effect runs: narrow <see cref="SpellApplication.EffectMask"/>, note per-hit state.</summary>
    void Begin(SpellApplication application);

    /// <summary>The effects built an aura holder: false discards it (the effects that already ran stay).</summary>
    bool AcceptHolder(SpellApplication application, SpellAuraHolder holder);
}
