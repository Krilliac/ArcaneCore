using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The aura-interrupt machinery (vmangos Unit::RemoveAurasWithInterruptFlags and its callers in Spell.cpp /
/// Unit.cpp). Data drives it: every aura breaks exactly when its Spell.dbc AuraInterruptFlags says so
/// (Stealth, Vanish and Shadowmeld carry 0x3C07, <see cref="AuraInterruptMask.StealthFamily"/>).
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>
    /// Roll Improved Sap separately at cast start and cast completion, as vmangos literally does (Spell.cpp:3455, 3713). Default false:
    /// one roll per cast, so the 30/60/90 percent of the talent text hold (docs/areas/rogue.md, open questions).
    /// </summary>
    public bool ImprovedSapRollPerPhase { get; set; }

    /// <summary>
    /// vmangos Unit::RemoveAurasWithInterruptFlags (Unit.cpp:3735-3751): remove every aura whose spell has any bit of
    /// <paramref name="flags"/> in its AuraInterruptFlags, except the spell <paramref name="exceptSpellId"/>,
    /// stealth auras (Dispel type 5) when <paramref name="skipStealth"/> and invisibility auras (Dispel type 6)
    /// when <paramref name="skipInvisibility"/>. Returns how many auras were removed.
    /// <para>
    /// <paramref name="checkProcFlags"/> (vmangos <c>checkProcFlags</c>): leave auras whose spell has procFlags alone. The damage
    /// break passes it, so Wyvern Sting, Prowl and the like survive the hit that their own proc causes.
    /// </para>
    /// </summary>
    /// <summary>
    /// True once a proc engine exists that breaks procFlags crowd control on damage (vmangos Unit.cpp:688-692). Default false: the
    /// engine has none (docs/areas/aura-engine.md), so the damage break keeps removing procFlags auras itself instead of leaving
    /// Polymorph, Sap, Gouge and Freezing Trap unbreakable. Known retail gap until the proc lane lands: Wyvern Sting and Prowl
    /// then break on their own hit.
    /// </summary>
    public bool ProcEngineBreaksDamageAuras { get; set; }

    public int RemoveAurasWithInterruptFlags(Unit unit, uint flags, uint exceptSpellId = 0, bool skipStealth = false, bool skipInvisibility = false,
        bool checkProcFlags = false)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (GetState(unit.Guid) is not { } state || !ReferenceEquals(state.Unit, unit))
        {
            return 0;
        }

        int removed = 0;

        // vmangos restarts from the first holder after every removal: a removal can cascade into other auras.
        while (state.Auras.FirstOrDefault(h => !h.IsRemoved
            && (!checkProcFlags || h.Spell.ProcFlags == ProcFlags.None)
            && (!skipStealth || h.Spell.Dispel != StealthBreakRules.DispelStealth)
            && (!skipInvisibility || h.Spell.Dispel != StealthBreakRules.DispelInvisibility)
            && ((uint)h.Spell.AuraInterruptFlags & flags) != 0
            && h.Spell.Id != exceptSpellId) is { } holder)
        {
            RemoveHolder(state, holder);
            removed++;
        }

        return removed;
    }

    /// <summary>vmangos Unit::RemoveSpellsCausingAura: remove every aura holder that carries an aura of <paramref name="type"/>.</summary>
    public int RemoveSpellsCausingAura(Unit unit, AuraType type)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (GetState(unit.Guid) is not { } state || !ReferenceEquals(state.Unit, unit))
        {
            return 0;
        }

        int removed = 0;
        foreach (SpellAuraHolder holder in state.Auras.Where(h => h.Auras.Any(a => a is not null && a.Type == type)).ToArray())
        {
            RemoveHolder(state, holder);
            removed++;
        }

        return removed;
    }

    private bool RollRemoveStealth(SpellCast cast)
        => StealthBreakRules.ShouldRemoveStealthAuras(cast.Spell, triggered: false, cast.Caster is Player,
            auraId => HasAura(cast.Caster, auraId), chance => Random.Next(100) < chance);

    /// <summary>
    /// An explicit (non-triggered) cast is prepared (vmangos Spell::prepare, Spell.cpp:3443-3456: "Stealth must be removed at
    /// cast starting"): remove ACTION auras (and LOOTING when the target is a game object) before the cast bar runs, so a
    /// cast that is later cancelled or interrupted has already dropped them. Stealth survives when
    /// <see cref="StealthBreakRules.ShouldRemoveStealthAuras"/> says so, invisibility when the spell has ALLOW_WHILE_INVISIBLE.
    /// </summary>
    private void InterruptAtCastStart(SpellCast cast)
    {
        if (cast.IsTriggered)
        {
            return;
        }

        Unit caster = cast.Caster;
        SpellInfo spell = cast.Spell;
        bool removeStealth = RollRemoveStealth(cast);
        cast.RemoveStealthRoll = removeStealth;
        bool skipInvisibility = ((uint)spell.AttributesEx2 & StealthBreakRules.AttributesEx2AllowWhileInvisible) != 0;

        uint early = AuraInterruptMask.Action;
        if ((cast.Targets.Mask & SpellCastTargetFlags.GameObject) != 0 && !cast.Targets.GameObject.IsEmpty)
        {
            early |= AuraInterruptMask.Looting;
        }

        RemoveAurasWithInterruptFlags(caster, early, spell.Id, skipStealth: !removeStealth, skipInvisibility);
    }

    /// <summary>
    /// The cast takes effect (vmangos Spell::cast, Spell.cpp:3697-3714): remove ACTION_LATE auras plus ATTACKING auras when
    /// the spell does not target a friend.
    /// </summary>
    private void InterruptAtCastCompletion(SpellCast cast)
    {
        if (cast.IsTriggered)
        {
            return;
        }

        Unit caster = cast.Caster;
        SpellInfo spell = cast.Spell;
        bool removeStealth = cast.RemoveStealthRoll ?? RollRemoveStealth(cast);
        bool skipInvisibility = ((uint)spell.AttributesEx2 & StealthBreakRules.AttributesEx2AllowWhileInvisible) != 0;

        uint late = AuraInterruptMask.ActionLate;
        SpellEffectInfo first = spell.Effects[0];
        if (!StealthBreakRules.IsPositiveTarget(first.TargetA, first.TargetB))
        {
            late |= AuraInterruptMask.Attacking;
        }

        // vmangos calls ShouldRemoveStealthAuras separately at the start and at the completion of the cast, so Improved Sap rolls
        // twice (a 30/60/90 percent talent keeps stealth 9/36/81 percent of the time). The talent text, and this lane by default,
        // use one roll per cast; ImprovedSapRollPerPhase reproduces the literal vmangos double roll.
        if (ImprovedSapRollPerPhase)
        {
            removeStealth = RollRemoveStealth(cast);
        }

        RemoveAurasWithInterruptFlags(caster, late, spell.Id, skipStealth: !removeStealth, skipInvisibility);
    }

    /// <summary>
    /// A spell reached a target (hit or miss) cast by a hostile unit (vmangos Spell.cpp:1622-1626, 1645-1650, 1893-1897):
    /// a hit that deals damage removes HOSTILE_ACTION_RECEIVED auras from the target; a miss removes them too;
    /// a hit that is an action (not EX2_NOT_AN_ACTION) also strips the target's stealth unless the spell has
    /// ALLOW_WHILE_STEALTHED and its non-passive invisibility unless it has ALLOW_WHILE_INVISIBLE.
    /// The caster-side half of Spell.cpp:1652-1668 needs the visibility model and is not modelled (docs/areas/rogue.md).
    /// </summary>
    private void InterruptTargetOfHostileSpell(SpellCast cast, Unit target, bool hit, bool dealsDamage)
    {
        Unit caster = cast.Caster;
        if (ReferenceEquals(caster, target) || Relations.IsFriendly(caster, target))
        {
            return;
        }

        if (!hit || dealsDamage)
        {
            RemoveAurasWithInterruptFlags(target, AuraInterruptMask.HostileActionReceived);
        }

        if (hit && ((uint)cast.Spell.AttributesEx2 & StealthBreakRules.AttributesEx2NotAnAction) == 0)
        {
            if (((uint)cast.Spell.AttributesEx & StealthBreakRules.AttributesExAllowWhileStealthed) == 0)
            {
                RemoveSpellsCausingAura(target, AuraType.ModStealth);
            }

            if (((uint)cast.Spell.AttributesEx2 & StealthBreakRules.AttributesEx2AllowWhileInvisible) == 0)
            {
                RemoveNonPassiveSpellsCausingAura(target, AuraType.ModInvisibility);
            }
        }
    }

    /// <summary>vmangos Unit::RemoveNonPassiveSpellsCausingAura.</summary>
    private int RemoveNonPassiveSpellsCausingAura(Unit unit, AuraType type)
    {
        if (GetState(unit.Guid) is not { } state || !ReferenceEquals(state.Unit, unit))
        {
            return 0;
        }

        int removed = 0;
        foreach (SpellAuraHolder holder in state.Auras.Where(h => !h.Spell.IsPassive && h.Auras.Any(a => a is not null && a.Type == type)).ToArray())
        {
            RemoveHolder(state, holder);
            removed++;
        }

        return removed;
    }

    /// <summary>
    /// Whether a spell's direct damage effect deals damage (the proxy for vmangos <c>m_damage != 0</c> at
    /// Spell.cpp:1622): a damage effect of the family SCHOOL_DAMAGE / HEALTH_LEECH / weapon damage with a positive value.
    /// </summary>
    private static bool IsDamageEffectWithValue(SpellEffectName effect, int value)
        => value > 0 && effect is SpellEffectName.SchoolDamage or SpellEffectName.HealthLeech or SpellEffectName.WeaponDamage
            or SpellEffectName.WeaponDamageNoschool or SpellEffectName.NormalizedWeaponDmg;
}
