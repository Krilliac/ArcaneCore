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

    public SpellCastTargets Targets { get; }

    public bool IsTriggered { get; }

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

    public uint PowerCost { get; }

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

    /// <summary>The cast in progress (one generic/channeled slot; the auto-repeat slot belongs to combat).</summary>
    public SpellCast? CurrentCast { get; internal set; }

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

    internal bool IsIdle => CurrentCast is null && MeleeCast is null && Auras.Count == 0 && SpellCooldowns.Count == 0
        && CategoryCooldowns.Count == 0 && GlobalCooldowns.Count == 0 && SchoolLockouts.Count == 0;
}
