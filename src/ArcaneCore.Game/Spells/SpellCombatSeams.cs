using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

// Registration seams of the spell system for the combat-mechanics features (stances, equipment, aura
// states, combo points, spell modifiers, procs). Each is registered on SpellSystem; none is discovered by
// reflection. Callbacks run on the world thread and, like SpellSystem.SpellHit, a throwing callback is not
// caught here.

/// <summary>
/// Where in <c>Spell::CheckCast</c> a cast check runs relative to the checks the spell system owns
/// (vmangos Spell.cpp:5302-5760). The order of the phases is the order of the vmangos source.
/// </summary>
public enum SpellCheckPhase
{
    /// <summary>First: before the cooldown and death checks (vmangos not-standing :5309).</summary>
    Start = 0,

    /// <summary>After the caster is alive and the spell is off cooldown, before the target is resolved (vmangos shapeshift :5349, caster aura state :5392).</summary>
    Caster = 1,

    /// <summary>After the explicit unit target exists and is alive, before range and line of sight (vmangos :5572-5640 target checks).</summary>
    Target = 2,

    /// <summary>Before the range check (vmangos <c>CheckItems</c> :5698).</summary>
    Items = 3,

    /// <summary>After range, line of sight and the target rules, before the power amounts (vmangos CheckPower :5721, combo points :7035-7038).</summary>
    Power = 4,

    /// <summary>After power and the caster auras (vmangos target aura state :5733-5742); also run for triggered casts.</summary>
    Final = 5,
}

/// <summary>
/// The stable ordering inside a <see cref="SpellCheckPhase"/>: lower runs first. The values follow the
/// line order of the vmangos source (Spell.cpp: shapeshift 5349, caster aura state 5392, items 5698, combo points 7035,
/// target aura state 5733), so the result a client sees when two checks would fail at once is the retail one.
/// </summary>
public static class SpellCastCheckOrder
{
    /// <summary>vmangos Spell.cpp:5309 the caster must stand (<see cref="SpellCheckPhase.Start"/>).</summary>
    public const int Standing = 50;

    /// <summary>vmangos Spell.cpp:5343-5344 a combat-forbidden spell in combat (strict, non-triggered; before the shapeshift check).</summary>
    public const int AffectingCombat = 50;

    /// <summary>vmangos Spell.cpp:5353-5354 the stealth requirement (strict, non-triggered; after the shapeshift check).</summary>
    public const int Stealth = 150;

    /// <summary>vmangos Spell.cpp:5640-5649 behind/in-front facing of the target (<see cref="SpellCheckPhase.Target"/>).</summary>
    public const int Facing = 100;

    /// <summary>vmangos Spell.cpp:5349 <c>GetErrorAtShapeshiftedCast</c> (strict, non-triggered casts only).</summary>
    public const int Shapeshift = 100;

    /// <summary>vmangos Spell.cpp:5392 caster aura state requirement.</summary>
    public const int CasterAuraState = 200;

    /// <summary>vmangos Spell.cpp:5698 <c>CheckItems</c> (equipped item class and weapon requirements).</summary>
    public const int Equipment = 300;

    /// <summary>vmangos Spell.cpp:7035-7038 (inside CheckPower): a finishing move needs combo points on the target.</summary>
    public const int ComboPoints = 400;

    /// <summary>vmangos Spell.cpp:5733-5742: a spell that needs a target below 20% health.</summary>
    public const int TargetAuraState = 500;
}

/// <summary>What a cast check is asked about.</summary>
/// <param name="System">The spell system running the check.</param>
/// <param name="Caster">The caster.</param>
/// <param name="Spell">The spell being checked.</param>
/// <param name="Targets">The cast's target block.</param>
/// <param name="Target">The resolved explicit unit target (the caster for a self cast of a unit-target spell), or null.</param>
/// <param name="Triggered">True for server-initiated casts.</param>
/// <param name="Strict">True at cast start (vmangos CheckCast(true)), false when the cast lands (CheckCast(false)).</param>
public readonly record struct SpellCastCheckContext(
    SpellSystem System, Unit Caster, SpellInfo Spell, SpellCastTargets Targets, Unit? Target, bool Triggered, bool Strict);

/// <summary>
/// An extra requirement of a cast. Returning anything but <see cref="SpellCastResult.CastOk"/> vetoes the
/// cast with that result; the first veto in (<see cref="Phase"/>, <see cref="Order"/>, registration) order wins.
/// </summary>
public interface ISpellCastCheck
{
    SpellCheckPhase Phase { get; }

    /// <summary>Order inside the phase; use <see cref="SpellCastCheckOrder"/>.</summary>
    int Order { get; }

    SpellCastResult Check(in SpellCastCheckContext context);
}

/// <summary>One target's outcome of a cast, handed to <see cref="ISpellCastObserver.OnTargetOutcome"/>.</summary>
/// <param name="Target">The unit the spell was aimed at.</param>
/// <param name="Miss">The hit-table result (<see cref="SpellMissInfo.None"/> = landed).</param>
/// <param name="Damage">Direct damage the spell actually dealt to the target (0 on a miss).</param>
/// <param name="Healing">Health the spell actually restored on the target.</param>
/// <param name="Critical">Whether a direct damage or healing effect critically struck.</param>
/// <param name="EffectMask">The effects selected for this target (bit i = effect i).</param>
public readonly record struct SpellTargetOutcome(Unit Target, SpellMissInfo Miss, uint Damage, uint Healing, bool Critical, int EffectMask);

/// <summary>
/// Observes casts: the hook point for combo points, aura-state reactives, procs and threat. All members have
/// empty default bodies; implement what is needed.
/// </summary>
public interface ISpellCastObserver
{
    /// <summary>A cast passed its checks and was created (queued for a next-swing spell; triggered casts included).</summary>
    void OnPrepared(SpellCast cast)
    {
    }

    /// <summary>The cast went through: power taken, before targets and effects (vmangos Spell::cast after TakePower, :3750).</summary>
    void OnCast(SpellCast cast)
    {
    }

    /// <summary>One target was resolved (missed, or its effects applied).</summary>
    void OnTargetOutcome(SpellCast cast, SpellTargetOutcome outcome)
    {
    }

    /// <summary>The cast ended. <paramref name="completed"/> is true when it reached its effects, false when it was cancelled or failed.</summary>
    void OnFinished(SpellCast cast, bool completed)
    {
    }
}

/// <summary>Which number an <see cref="ISpellValueModifier"/> is asked to adjust.</summary>
public enum SpellValueKind
{
    /// <summary>One effect's value (vmangos CalculateSpellEffectValue, before chain multipliers).</summary>
    EffectValue = 1,

    /// <summary>Aura or channel duration in ms (vmangos SpellEntry::CalculateDuration, never called for a permanent duration of -1).</summary>
    Duration = 2,

    /// <summary>The power cost (vmangos Spell::CalculatePowerCost).</summary>
    PowerCost = 3,

    /// <summary>The cast time in ms, after the minimum and before haste (vmangos SpellEntry::GetCastTime :492-494; not called for 0).</summary>
    CastTime = 4,
}

/// <summary>What a value modifier is asked about.</summary>
/// <param name="Caster">The caster.</param>
/// <param name="Spell">The spell.</param>
/// <param name="EffectIndex">The effect for <see cref="SpellValueKind.EffectValue"/>, otherwise -1.</param>
/// <param name="Target">The unit the effect value is for (<see cref="SpellValueKind.EffectValue"/> only), otherwise null.</param>
public readonly record struct SpellValueContext(Unit Caster, SpellInfo Spell, int EffectIndex, Unit? Target = null);

/// <summary>
/// Adjusts a computed spell number (spell modifiers, talents, set bonuses). Modifiers run in registration
/// order, each receiving the previous result. A modifier must be pure: the spell system may ask for the same
/// number more than once per cast (the power cost is re-read by every check); consume charges from an
/// <see cref="ISpellCastObserver"/> instead.
/// </summary>
public interface ISpellValueModifier
{
    int Modify(SpellValueKind kind, in SpellValueContext context, int value);
}

/// <summary>
/// Overrides how far a chain spell jumps (vmangos Spell.cpp:2256-2265: melee chains use the effect radius
/// instead of the fixed chain jump distance). The first provider that answers true wins.
/// </summary>
public interface ISpellChainRangeProvider
{
    bool TryGetChainRange(SpellCast cast, SpellEffectInfo effect, out float range);
}
