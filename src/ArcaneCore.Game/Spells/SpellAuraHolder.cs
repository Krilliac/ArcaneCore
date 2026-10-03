using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>One exact caster's aura ownership lifetime, without retaining the unit or its session.</summary>
internal sealed class AuraCasterOwner
{
    private readonly WeakReference<Unit>? _caster;
    private bool _revoked;

    internal AuraCasterOwner(Unit caster) => _caster = new WeakReference<Unit>(caster);

    private AuraCasterOwner()
    {
        _revoked = true;
    }

    internal Unit? Caster => !_revoked && _caster is not null && _caster.TryGetTarget(out Unit? caster) ? caster : null;

    internal bool IsRevoked => _revoked;

    /// <summary>
    /// A permanently revoked token with no caster: a restored foreign aura (character_aura) keeps
    /// its caster GUID as provenance only and always uses the target fallback (docs/integration/aura-caster-ownership.md).
    /// </summary>
    internal static AuraCasterOwner Orphaned() => new();

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

    /// <summary>
    /// vmangos Aura::m_positive = SpellEntry::IsPositiveEffect(effect index) (SpellAuras.cpp:276): polarity is decided per
    /// effect. Set by <see cref="SpellAuraHolder.ResolvePolarity"/> once every effect aura is attached.
    /// </summary>
    public bool IsPositive { get; internal set; } = true;

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
        : this(spell, target, caster.Guid, caster.Level, casterOwner, duration)
    {
    }

    internal SpellAuraHolder(SpellInfo spell, Unit target, ObjectGuid casterGuid, byte casterLevel, AuraCasterOwner casterOwner, int duration)
    {
        Spell = spell;
        Target = target;
        CasterGuid = casterGuid;
        CasterLevel = casterLevel;
        CasterOwner = casterOwner;
        Charges = (int)spell.ProcCharges;

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

    /// <summary>
    /// The unit in the caster's UNIT_FIELD_CHANNEL_OBJECT when this holder's channelled spell started (empty for anything
    /// else). A channel's final periodic tick runs in the same update that ends the channel, after the channel fields
    /// were cleared (vmangos keeps them for one more second), so periodic trigger auras read it from here.
    /// </summary>
    internal ObjectGuid ChannelTarget { get; set; }

    public byte CasterLevel { get; }

    internal AuraCasterOwner CasterOwner { get; }

    /// <summary>
    /// vmangos SpellAuraHolder::IsPositive (SpellAuras.cpp:7475-7482): positive only when EVERY aura effect of this holder is
    /// positive. Computed from the effect auras (<see cref="ResolvePolarity"/>), never from the spell as a whole: Stealth
    /// carries a -51 speed effect yet is a buff. A holder without aura effects falls back to the spell.
    /// </summary>
    public bool IsPositive => _positive ??= ComputePositive(null);

    private bool? _positive;

    private bool ComputePositive(Func<uint, SpellInfo?>? lookup)
    {
        bool any = false;
        bool positive = true;
        for (int i = 0; i < _auras.Length; i++)
        {
            if (_auras[i] is not { } aura)
            {
                continue;
            }

            any = true;
            aura.IsPositive = Spell.IsPositiveEffect(i, lookup);
            positive &= aura.IsPositive;
        }

        return any ? positive : Spell.IsPositiveSpell(lookup);
    }

    /// <summary>Recompute per-effect and holder polarity once all effect auras exist (the periodic-trigger check needs the spell store).</summary>
    internal void ResolvePolarity(Func<uint, SpellInfo?>? lookup) => _positive = ComputePositive(lookup);

    public bool IsPermanent { get; }

    /// <summary>Remaining ms (-1 when permanent).</summary>
    public int Duration { get; internal set; }

    public int MaxDuration { get; internal set; }

    /// <summary>
    /// When this holder was put on its target, in whole Unix seconds (vmangos SpellAuraHolder::m_applyTime, set when the
    /// holder is constructed: SpellAuras.cpp:6665, getter SpellAuras.h:473). A stack refresh keeps the original value; a
    /// replacement holder gets its own. Stamped by <see cref="SpellSystem.AddAuraHolder"/> from <see cref="SpellSystem.UnixSecondsClock"/>.
    /// </summary>
    public long AppliedAtUnixSeconds { get; internal set; }

    /// <summary>Visible aura slot 0-47, or <see cref="NoSlot"/>.</summary>
    public byte Slot { get; internal set; } = NoSlot;

    public byte StackAmount { get; internal set; } = 1;

    /// <summary>Remaining proc charges (Spell.dbc procCharges at application; 0 = unlimited). Persisted; consumption belongs to the proc system.</summary>
    public int Charges { get; internal set; }

    public bool IsRemoved { get; internal set; }

    /// <summary>For an aura a party area aura put on a group member: the caster's source holder (vmangos AreaAura owner).</summary>
    public SpellAuraHolder? AreaParent { get; internal set; }

    /// <summary>Whether this holder is a caster's own party area aura that spreads to its group.</summary>
    public bool IsAreaSource => AreaParent is null && CasterGuid == Target.Guid && Spell.HasEffect(SpellEffectName.ApplyAreaAuraParty);

    /// <summary>Members currently holding a child of this area aura source.</summary>
    internal Dictionary<ObjectGuid, SpellAuraHolder> AreaChildren { get; } = [];

    /// <summary>Whether <c>character_aura</c> keeps this holder across logout (see SpellSystem.CaptureState).</summary>
    public bool IsSaveable => !IsRemoved && !Spell.IsPassive && !Spell.IsChanneled && AreaParent is null && !IsEmpty;

    public IReadOnlyList<SpellAura?> Auras => _auras;

    public bool HasAura(AuraType type) => _auras.Any(a => a?.Type == type);

    internal void SetAura(SpellAura aura)
    {
        _auras[aura.EffectIndex] = aura;
        _positive = null;
    }

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
