using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Ranged;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    // Copy-on-write arrays: registration happens at startup, every cast reads them.
    private ISpellCastCheck[] _castChecks = [];
    private ISpellCastObserver[] _observers = [];
    private ISpellValueModifier[] _valueModifiers = [];
    private ISpellChainRangeProvider[] _chainRangeProviders = [];

    /// <summary>The registered cast checks in run order (phase, order, registration).</summary>
    public IReadOnlyList<ISpellCastCheck> CastChecks => _castChecks;

    /// <summary>The registered observers in registration order.</summary>
    public IReadOnlyList<ISpellCastObserver> Observers => _observers;

    /// <summary>The registered value modifiers in registration order.</summary>
    public IReadOnlyList<ISpellValueModifier> ValueModifiers => _valueModifiers;

    /// <summary>
    /// Interrupt a cast of the unit (vmangos InterruptSpell(type, false)): a preparing cast or a queued next-swing spell
    /// reports INTERRUPTED, a channel ends. A finished cast is ignored.
    /// </summary>
    public void Interrupt(SpellCast cast)
    {
        ArgumentNullException.ThrowIfNull(cast);
        Cancel(cast);
    }

    /// <summary>The target outcome being built by <see cref="ApplyEffects"/> (re-entrant: nested triggered casts save and restore it).</summary>
    private OutcomeBuilder? _outcome;

    /// <summary>
    /// Add a cast requirement. Checks run by (<see cref="ISpellCastCheck.Phase"/>, <see cref="ISpellCastCheck.Order"/>),
    /// then registration order; the first veto wins (<see cref="SpellCastCheckOrder"/>).
    /// </summary>
    public void RegisterCastCheck(ISpellCastCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (_castChecks.Contains(check))
        {
            throw new InvalidOperationException("this cast check is already registered");
        }

        _castChecks = [.. _castChecks.Append(check).OrderBy(c => c.Phase).ThenBy(c => c.Order)];
    }

    public void RegisterObserver(ISpellCastObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        if (_observers.Contains(observer))
        {
            throw new InvalidOperationException("this observer is already registered");
        }

        _observers = [.. _observers, observer];
    }

    public void RegisterValueModifier(ISpellValueModifier modifier)
    {
        ArgumentNullException.ThrowIfNull(modifier);
        if (_valueModifiers.Contains(modifier))
        {
            throw new InvalidOperationException("this value modifier is already registered");
        }

        _valueModifiers = [.. _valueModifiers, modifier];
    }

    public void RegisterChainRangeProvider(ISpellChainRangeProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (_chainRangeProviders.Contains(provider))
        {
            throw new InvalidOperationException("this chain range provider is already registered");
        }

        _chainRangeProviders = [.. _chainRangeProviders, provider];
    }

    private SpellCastResult RunCastChecks(SpellCheckPhase phase, Unit caster, SpellInfo spell, SpellCastTargets targets, Unit? target, bool triggered, bool strict)
    {
        foreach (ISpellCastCheck check in _castChecks)
        {
            if (check.Phase != phase)
            {
                continue;
            }

            SpellCastResult result = check.Check(new SpellCastCheckContext(this, caster, spell, targets, target, triggered, strict));
            if (result != SpellCastResult.CastOk)
            {
                return result;
            }
        }

        return SpellCastResult.CastOk;
    }

    // --- observers ----------------------------------------------------------------------------

    private void NotifyPrepared(SpellCast cast)
    {
        foreach (ISpellCastObserver observer in _observers)
        {
            observer.OnPrepared(cast);
        }
    }

    private void NotifyCast(SpellCast cast)
    {
        foreach (ISpellCastObserver observer in _observers)
        {
            observer.OnCast(cast);
        }
    }

    private void NotifyOutcome(SpellCast cast, SpellTargetOutcome outcome)
    {
        foreach (ISpellCastObserver observer in _observers)
        {
            observer.OnTargetOutcome(cast, outcome);
        }
    }

    private void NotifyFinished(SpellCast cast)
    {
        foreach (ISpellCastObserver observer in _observers)
        {
            observer.OnFinished(cast, cast.Completed);
        }
    }

    // --- value modifiers ----------------------------------------------------------------------

    private int ModifyValue(SpellValueKind kind, Unit caster, SpellInfo spell, int effectIndex, int value, Unit? target = null)
    {
        if (_valueModifiers.Length == 0)
        {
            return value;
        }

        var context = new SpellValueContext(caster, spell, effectIndex, target);
        foreach (ISpellValueModifier modifier in _valueModifiers)
        {
            value = modifier.Modify(kind, context, value);
        }

        return value;
    }

    /// <summary>The cast time in ms with the registered cast-time modifiers, the auto-repeat flag and the ranged haste (vmangos SpellEntry::GetCastTime).</summary>
    private int CastTimeFor(Unit caster, SpellInfo spell)
        => spell.GetCastTime(caster.Level, CastSpeed(caster), RangedSpellFacts.IsAutoRepeatRanged(spell), RangedAttackSpeedPct(caster),
            castTime => ModifyValue(SpellValueKind.CastTime, caster, spell, -1, castTime));

    /// <summary>
    /// The aura/channel duration in ms (vmangos SpellEntry::CalculateDuration, SpellEntry.cpp:723-751): a permanent
    /// duration (-1) is returned as is, anything else goes through the modifiers and is floored at 0.
    /// </summary>
    private int DurationFor(Unit caster, SpellInfo spell)
    {
        int duration = spell.GetDuration();
        return duration == -1 ? duration : Math.Max(ModifyValue(SpellValueKind.Duration, caster, spell, -1, duration), 0);
    }

    /// <summary>The power cost with the registered cost modifiers (vmangos Spell::CalculatePowerCost).</summary>
    private uint PowerCostFor(Unit caster, SpellInfo spell)
        => (uint)Math.Max(ModifyValue(SpellValueKind.PowerCost, caster, spell, -1,
            (int)CalculatePowerCost(caster, spell, cost => ModInt(caster, spell, SpellModOp.Cost, cost))), 0);

    // --- chain range --------------------------------------------------------------------------

    private float ChainJumpRadiusFor(SpellCast cast, SpellEffectInfo effect)
    {
        foreach (ISpellChainRangeProvider provider in _chainRangeProviders)
        {
            if (provider.TryGetChainRange(cast, effect, out float range))
            {
                return range;
            }
        }

        return SpellConstants.ChainJumpRadius;
    }

    // --- target outcomes ----------------------------------------------------------------------

    /// <summary>Accumulates what the effect handlers did to one target while <see cref="ApplyEffects"/> runs.</summary>
    private sealed class OutcomeBuilder(SpellCast cast, Unit target, int effectMask)
    {
        public SpellCast Cast { get; } = cast;

        public Unit Target { get; } = target;

        public int EffectMask { get; } = effectMask;

        public uint Damage { get; set; }

        public uint Healing { get; set; }

        public bool Critical { get; set; }

        public SpellTargetOutcome Build() => new(Target, SpellMissInfo.None, Damage, Healing, Critical, EffectMask);
    }

    /// <summary>Credit damage dealt by <paramref name="spell"/> to the outcome being built, if it is for that cast and target.</summary>
    private void RecordDamage(Unit caster, Unit target, SpellInfo spell, uint dealt, bool critical)
    {
        if (_outcome is { } outcome && ReferenceEquals(outcome.Target, target) && ReferenceEquals(outcome.Cast.Caster, caster)
            && outcome.Cast.Spell.Id == spell.Id)
        {
            outcome.Damage += dealt;
            outcome.Critical |= critical;
        }
    }

    private void RecordHealing(Unit caster, Unit target, SpellInfo spell, uint healed, bool critical)
    {
        if (_outcome is { } outcome && ReferenceEquals(outcome.Target, target) && ReferenceEquals(outcome.Cast.Caster, caster)
            && outcome.Cast.Spell.Id == spell.Id)
        {
            outcome.Healing += healed;
            outcome.Critical |= critical;
        }
    }
}
