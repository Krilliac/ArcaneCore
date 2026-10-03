using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>One exact caster's aura ownership lifetime, without retaining the unit or its session.</summary>
internal sealed class AuraCasterOwner(Unit caster)
{
    private readonly WeakReference<Unit> _caster = new(caster);
    private bool _revoked;

    internal Unit? Caster => !_revoked && _caster.TryGetTarget(out Unit? caster) ? caster : null;

    internal void Revoke() => _revoked = true;
}

/// <summary>One effect's aura inside a holder (vmangos Aura / Modifier).</summary>
public sealed class SpellAura
{
    internal SpellAura(int effectIndex, AuraType type, int amount, uint amplitude, int miscValue)
    {
        EffectIndex = effectIndex;
        Type = type;
        Amount = amount;
        Amplitude = amplitude;
        MiscValue = miscValue;
        PeriodicTimer = (int)amplitude;
    }

    public int EffectIndex { get; }

    public AuraType Type { get; }

    /// <summary>Modifier amount (vmangos Modifier::m_amount), from the effect value at application.</summary>
    public int Amount { get; internal set; }

    /// <summary>Periodic interval in ms (Spell.dbc EffectAmplitude; 0 = not periodic).</summary>
    public uint Amplitude { get; }

    /// <summary>Spell.dbc EffectMiscValue (power type for energize/mana auras).</summary>
    public int MiscValue { get; }

    public bool IsPeriodic => Amplitude > 0;

    /// <summary>Time to the next tick (vmangos Aura::m_periodicTimer, first tick one amplitude after application).</summary>
    internal int PeriodicTimer { get; set; }

    /// <summary>Ticks delivered so far.</summary>
    public int TickCount { get; internal set; }
}

/// <summary>
/// All auras one cast of one spell put on one target (vmangos SpellAuraHolder): the visible slot,
/// the duration, the stack count and up to three effect auras. World thread only.
/// </summary>
public sealed class SpellAuraHolder
{
    /// <summary>No visible slot (vmangos NULL_AURA_SLOT).</summary>
    public const byte NoSlot = 0xFF;

    private readonly SpellAura?[] _auras = new SpellAura?[SpellConstants.MaxEffects];

    internal SpellAuraHolder(SpellInfo spell, Unit target, Unit caster, AuraCasterOwner casterOwner, int duration)
    {
        Spell = spell;
        Target = target;
        CasterGuid = caster.Guid;
        CasterLevel = caster.Level;
        CasterOwner = casterOwner;
        IsPositive = spell.IsPositive;

        // vmangos SpellAuraHolder ctor: permanent for -1 durations and passive spells; durations
        // below 200 ms are raised to 300 ms ("some spells have 1 ms duration").
        IsPermanent = duration == -1 || spell.IsPassive;
        if (!IsPermanent && duration < 200)
        {
            duration = 300;
        }

        MaxDuration = IsPermanent ? -1 : duration;
        Duration = MaxDuration;
    }

    public SpellInfo Spell { get; }

    public Unit Target { get; }

    public ObjectGuid CasterGuid { get; }

    public byte CasterLevel { get; }

    internal AuraCasterOwner CasterOwner { get; }

    public bool IsPositive { get; }

    public bool IsPermanent { get; }

    /// <summary>Remaining ms (-1 when permanent).</summary>
    public int Duration { get; internal set; }

    public int MaxDuration { get; internal set; }

    /// <summary>Visible aura slot 0-47, or <see cref="NoSlot"/>.</summary>
    public byte Slot { get; internal set; } = NoSlot;

    public byte StackAmount { get; internal set; } = 1;

    public bool IsRemoved { get; internal set; }

    public IReadOnlyList<SpellAura?> Auras => _auras;

    public bool HasAura(AuraType type) => _auras.Any(a => a?.Type == type);

    internal void SetAura(SpellAura aura) => _auras[aura.EffectIndex] = aura;

    internal bool IsEmpty => _auras.All(a => a is null);

    /// <summary>
    /// vmangos SpellAuraHolder::IsNeedVisibleSlot (subset): spells with a SpellVisual always take
    /// a slot; otherwise passive auras do not.
    /// </summary>
    internal bool NeedsVisibleSlot => Spell.SpellVisual != 0 || !Spell.IsPassive;

    /// <summary>
    /// AURAFLAGS nibble for this holder (vmangos SpellAuraHolder::SetAuraFlag, build &gt; 1.6.1):
    /// CANCELABLE 0x01 for positive auras without NO_AURA_CANCEL, EFF_INDEX_0 0x08, EFF_INDEX_1
    /// 0x04, EFF_INDEX_2 0x02. cmangos-classic uses different bits (positive 0x04 / negative 0x08
    /// in SpellAuraHolder::SetAuraFlag): discrepancy recorded; vmangos is the 1.12-only server.
    /// </summary>
    internal uint AuraFlags
    {
        get
        {
            uint flags = 0;
            if (IsPositive && !Spell.HasAttribute(SpellAttributes.NoAuraCancel))
            {
                flags |= 0x01;
            }

            if (_auras[0] is not null)
            {
                flags |= 0x08;
            }

            if (_auras[1] is not null)
            {
                flags |= 0x04;
            }

            if (_auras[2] is not null)
            {
                flags |= 0x02;
            }

            return flags;
        }
    }
}
