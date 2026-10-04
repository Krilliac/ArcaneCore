using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>Cast states (vmangos SpellState).</summary>
public enum SpellCastState : byte
{
    Preparing = 1,
    Casting = 2,
    Finished = 3,
}

/// <summary>One cast in flight (vmangos Spell): preparing (cast bar) → casting (channel) → finished.</summary>
public sealed class SpellCast
{
    internal SpellCast(SpellInfo spell, Unit caster, SpellCastTargets targets, bool triggered, int castTime, uint powerCost, int duration)
    {
        Spell = spell;
        Caster = caster;
        Targets = targets;
        IsTriggered = triggered;
        CastTime = castTime;
        Timer = castTime;
        PowerCost = powerCost;
        Duration = duration;
        CastX = caster.X;
        CastY = caster.Y;
        CastZ = caster.Z;
    }

    public SpellInfo Spell { get; }

    public Unit Caster { get; }

    public SpellCastTargets Targets { get; internal set; }

    public bool IsTriggered { get; }

    /// <summary>
    /// One shot of the auto-repeat spell (ranged (autorepeat lane)): a triggered copy of Auto Shot / Shoot cast by
    /// <see cref="SpellSystem"/> every weapon period. It sends no SMSG_SPELL_COOLDOWN (vmangos Player::AddCooldown only
    /// tells the client for COOLDOWN_ON_EVENT spells, Player.cpp:22139-22250), so the client's own timer keeps running.
    /// </summary>
    internal bool AutoRepeatShot { get; set; }
    /// <summary>The item the spell is cast from (vmangos Spell::m_CastItem; CMSG_USE_ITEM, recipes, bandages, poisons), or null. Set once at prepare.</summary>
    public Items.Item? CastItem { get; internal set; }

    public SpellCastState State { get; internal set; } = SpellCastState.Preparing;

    /// <summary>
    /// The ShouldRemoveStealthAuras roll taken at cast start (vmangos Spell.cpp:3455), reused at completion unless
    /// <see cref="SpellSystem.ImprovedSapRollPerPhase"/>; null until the start phase ran.
    /// </summary>
    internal bool? RemoveStealthRoll { get; set; }

    /// <summary>Full cast time in ms.</summary>
    public int CastTime { get; }

    /// <summary>Time left in the current state (cast bar, then channel).</summary>
    public int Timer { get; internal set; }

    /// <summary>
    /// The power cost: computed at prepare without spending mod charges, and again at the end of the cast bar with them (vmangos
    /// Spell.cpp:3395 and :3646-3658, "in case of mana reduction buff proc while casting").
    /// </summary>
    public uint PowerCost { get; internal set; }

    /// <summary>The charged spell mods this cast spent (vmangos Spell::m_appliedMods); null when the caster holds no modifiers.</summary>
    internal Mods.SpellModScope? ModScope { get; set; }

    /// <summary>The aura/channel duration in ms, computed once at prepare (vmangos Spell::m_duration; -1 = permanent).</summary>
    public int Duration { get; }

    /// <summary>Whether the cast got through its checks and reached its effects (false when cancelled or failed).</summary>
    public bool Completed { get; internal set; }

    /// <summary>Damage pushbacks taken (cast bar or channel; vmangos m_delayAtDamageCount).</summary>
    public int PushbackCount { get; internal set; }

    internal float CastX { get; set; }

    internal float CastY { get; set; }

    internal float CastZ { get; set; }

    /// <summary>Whether the caster moved more than 0.5 yd on any axis since the cast started (vmangos Spell::update).</summary>
    internal bool HasMoved
        => Math.Abs(Caster.X - CastX) > SpellConstants.MovementCancelThreshold
            || Math.Abs(Caster.Y - CastY) > SpellConstants.MovementCancelThreshold
            || Math.Abs(Caster.Z - CastZ) > SpellConstants.MovementCancelThreshold;
}

/// <summary>Per-unit spell state kept by <see cref="SpellSystem"/> (world thread).</summary>
public sealed class UnitSpellState
{
    internal UnitSpellState(Unit unit) => Unit = unit;

    public Unit Unit { get; }

    /// <summary>The cast in progress (one generic/channeled slot; the auto-repeat spell has its own, <see cref="AutoRepeatCast"/>).</summary>
    public SpellCast? CurrentCast { get; internal set; }

    /// <summary>
    /// The auto-repeat spell toggled on (vmangos CURRENT_AUTOREPEAT_SPELL; Auto Shot, wand Shoot). It never casts itself: it stays in
    /// the Preparing state until cancelled and <see cref="SpellSystem"/> fires a triggered copy of it each time the ranged swing
    /// timer is ready (vmangos Unit::_UpdateAutoRepeatSpell). Ranged (autorepeat lane).
    /// </summary>
    public SpellCast? AutoRepeatCast { get; internal set; }

    /// <summary>vmangos Unit::m_autoRepeatFirstCast: the next shot waits at least 500 ms (the wind-up) after a toggle, a cast or movement.</summary>
    internal bool AutoRepeatFirstCast { get; set; }

    /// <summary>The queued next-swing spell (vmangos CURRENT_MELEE_SPELL); it casts when the melee swing fires, see <see cref="SpellSystem.CastQueuedMeleeSpell"/>.</summary>
    public SpellCast? MeleeCast { get; internal set; }

    /// <summary>spell id → absolute expiry (WorldRuntime ms clock).</summary>
    internal Dictionary<uint, uint> SpellCooldowns { get; } = [];

    /// <summary>Spell.dbc Category → absolute expiry.</summary>
    internal Dictionary<uint, uint> CategoryCooldowns { get; } = [];

    /// <summary>StartRecoveryCategory → absolute expiry of the global cooldown.</summary>
    internal Dictionary<uint, uint> GlobalCooldowns { get; } = [];

    /// <summary>School → absolute end of an interrupt lockout (vmangos Unit::ProhibitSpellSchool).</summary>
    internal Dictionary<SpellSchool, uint> SchoolLockouts { get; } = [];

    internal List<SpellAuraHolder> Auras { get; } = [];

    public IReadOnlyList<SpellAuraHolder> AuraHolders => Auras;

    internal bool IsIdle => CurrentCast is null && MeleeCast is null && AutoRepeatCast is null && Auras.Count == 0 && SpellCooldowns.Count == 0
        && CategoryCooldowns.Count == 0 && GlobalCooldowns.Count == 0 && SchoolLockouts.Count == 0;
}
