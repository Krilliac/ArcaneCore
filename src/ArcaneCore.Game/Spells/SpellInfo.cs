namespace ArcaneCore.Game.Spells;

/// <summary>SpellCastTimes.dbc row (cmangos-classic DBCStructure.h SpellCastTimesEntry: id, base, perLevel, min).</summary>
public readonly record struct SpellCastTime(int Base, int PerLevel, int Minimum);

/// <summary>SpellDuration.dbc row (cmangos-classic SpellDurationEntry: id, base, perLevel, max). -1 = permanent.</summary>
public readonly record struct SpellDuration(int Base, int PerLevel, int Maximum);

/// <summary>SpellRange.dbc row (cmangos-classic SpellRangeEntry: minRange, maxRange in yards).</summary>
public readonly record struct SpellRange(float Min, float Max);

/// <summary>One of a spell's three effects (Spell.dbc fields 61-114, one column per effect index).</summary>
public sealed record SpellEffectInfo
{
    public SpellEffectName Effect { get; init; }

    public int DieSides { get; init; }

    public int BaseDice { get; init; }

    public float DicePerLevel { get; init; }

    public float RealPointsPerLevel { get; init; }

    public int BasePoints { get; init; }

    public uint Mechanic { get; init; }

    public SpellImplicitTarget TargetA { get; init; }

    public SpellImplicitTarget TargetB { get; init; }

    /// <summary>SpellRadius.dbc radius resolved from EffectRadiusIndex (yards; 0 = none).</summary>
    public float Radius { get; init; }

    public AuraType AuraType { get; init; }

    /// <summary>Periodic interval in ms for periodic auras.</summary>
    public uint Amplitude { get; init; }

    public float MultipleValue { get; init; }

    public uint ChainTarget { get; init; }

    public uint ItemType { get; init; }

    public int MiscValue { get; init; }

    public uint TriggerSpell { get; init; }

    public float PointsPerComboPoint { get; init; }

    /// <summary>Spell.dbc DmgMultiplier: the value multiplier applied per chain jump (vmangos m_damageMultipliers; 0 is read as 1).</summary>
    public float DamageMultiplier { get; init; } = 1.0f;

    public bool IsEmpty => Effect == SpellEffectName.None;
}

/// <summary>
/// An immutable, resolved Spell.dbc entry: the DB row (<c>spell_template</c>, cmangos column
/// names) with its cast-time, duration, range and radius indices already looked up. Built once
/// at startup and shared by every map (read-only, so thread-safe).
/// </summary>
public sealed partial record SpellInfo
{
    private SpellEffectInfo[] _effects = [new(), new(), new()];

    public uint Id { get; init; }

    public SpellSchool School { get; init; }

    public uint Category { get; init; }

    public uint Dispel { get; init; }

    public uint Mechanic { get; init; }

    public SpellAttributes Attributes { get; init; }

    public SpellAttributesEx AttributesEx { get; init; }

    public SpellAttributesEx2 AttributesEx2 { get; init; }

    public uint AttributesEx3 { get; init; }

    public uint AttributesEx4 { get; init; }

    public uint Targets { get; init; }

    public uint TargetCreatureType { get; init; }

    public uint RequiresSpellFocus { get; init; }

    public SpellCastTime CastTime { get; init; }

    public uint RecoveryTime { get; init; }

    public uint CategoryRecoveryTime { get; init; }

    public SpellInterruptFlags InterruptFlags { get; init; }

    public SpellAuraInterruptFlags AuraInterruptFlags { get; init; }

    public SpellAuraInterruptFlags ChannelInterruptFlags { get; init; }

    public uint MaxLevel { get; init; }

    public uint BaseLevel { get; init; }

    public uint SpellLevel { get; init; }

    public SpellDuration Duration { get; init; }

    /// <summary>Power type (Spell.dbc powerType; -2 = health, as vmangos POWER_HEALTH).</summary>
    public int PowerType { get; init; }

    public uint ManaCost { get; init; }

    public uint ManaCostPerLevel { get; init; }

    public uint ManaPerSecond { get; init; }

    public uint ManaPerSecondPerLevel { get; init; }

    public uint ManaCostPercentage { get; init; }

    public uint RangeIndex { get; init; }

    public SpellRange Range { get; init; }

    public float Speed { get; init; }

    public uint StackAmount { get; init; }

    /// <summary>Spell.dbc procCharges: charges a new aura holder starts with (0 = unlimited).</summary>
    public uint ProcCharges { get; init; }

    /// <summary>Spell.dbc Stances: bit (form - 1) set = castable in that <see cref="ShapeshiftForm"/> (vmangos SpellEntry::Stances).</summary>
    public uint Stances { get; init; }

    /// <summary>Spell.dbc StancesNot: bit (form - 1) set = NOT castable in that form (vmangos SpellEntry::StancesNot).</summary>
    public uint StancesNot { get; init; }

    /// <summary>Spell.dbc CasterAuraState: the caster must be in this <see cref="AuraState"/> (vmangos SpellEntry::CasterAuraState).</summary>
    public AuraState CasterAuraState { get; init; }

    /// <summary>Spell.dbc TargetAuraState: the target must be in this <see cref="AuraState"/> (vmangos SpellEntry::TargetAuraState).</summary>
    public AuraState TargetAuraState { get; init; }

    /// <summary>Spell.dbc procFlags: the events this (aura) spell can proc on (vmangos SpellEntry::procFlags).</summary>
    public ProcFlags ProcFlags { get; init; }

    /// <summary>Spell.dbc procChance in percent (vmangos SpellEntry::procChance).</summary>
    public uint ProcChance { get; init; }

    /// <summary>Spell.dbc EquippedItemClass: required equipped item class, -1 = none (vmangos SpellEntry::EquippedItemClass).</summary>
    public int EquippedItemClass { get; init; } = -1;

    /// <summary>Spell.dbc EquippedItemSubClassMask: required item subclasses, as a bit mask over the class (vmangos SpellEntry::EquippedItemSubClassMask).</summary>
    public int EquippedItemSubClassMask { get; init; }

    /// <summary>Spell.dbc EquippedItemInventoryTypeMask: required inventory types, as a bit mask (vmangos SpellEntry::EquippedItemInventoryTypeMask).</summary>
    public int EquippedItemInventoryTypeMask { get; init; }

    public uint StartRecoveryCategory { get; init; }

    public uint StartRecoveryTime { get; init; }

    public SpellDamageClass DamageClass { get; init; }

    public uint PreventionType { get; init; }

    public uint SpellFamilyName { get; init; }

    public ulong SpellFamilyFlags { get; init; }

    public uint MaxTargetLevel { get; init; }

    public uint MaxAffectedTargets { get; init; }

    public uint SpellVisual { get; init; }

    public uint SpellIconId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Rank { get; init; } = string.Empty;

    /// <summary>The three effects (always exactly <see cref="SpellConstants.MaxEffects"/> entries).</summary>
    public IReadOnlyList<SpellEffectInfo> Effects
    {
        get => _effects;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Count > SpellConstants.MaxEffects)
            {
                throw new ArgumentException($"a spell has at most {SpellConstants.MaxEffects} effects", nameof(value));
            }

            var effects = new SpellEffectInfo[SpellConstants.MaxEffects];
            for (int i = 0; i < effects.Length; i++)
            {
                effects[i] = i < value.Count ? value[i] : new SpellEffectInfo();
            }

            _effects = effects;
        }
    }

    public bool HasAttribute(SpellAttributes flag) => (Attributes & flag) != 0;

    public bool HasAttribute(SpellAttributesEx flag) => (AttributesEx & flag) != 0;

    public bool HasAttribute(SpellAttributesEx2 flag) => (AttributesEx2 & flag) != 0;

    public bool IsPassive => HasAttribute(SpellAttributes.Passive);

    /// <summary>vmangos SPELL_ATTR_EX3_ONLY_ON_GHOSTS (SpellDefines.h, AttributesEx3 bit 12).</summary>
    private const uint Ex3OnlyOnGhosts = 0x00001000;

    /// <summary>vmangos SPELL_ATTR_EX3_ALLOW_AURA_WHILE_DEAD (SpellDefines.h:967, AttributesEx3 bit 20).</summary>
    private const uint Ex3AllowAuraWhileDead = 0x00100000;

    /// <summary>
    /// vmangos SpellEntry::IsDeathPersistentSpell (SpellEntry.h:952-959): for a 1.12.1 build
    /// (SUPPORTED_CLIENT_BUILD &gt; CLIENT_BUILD_1_8_4) that is AttributesEx3 ALLOW_AURA_WHILE_DEAD; the
    /// aura survives its holder dying.
    /// </summary>
    public bool IsDeathPersistent => (AttributesEx3 & Ex3AllowAuraWhileDead) != 0;

    /// <summary>vmangos SpellEntry::IsDeathOnlySpell (SpellEntry.h:931-936), including its hard-coded spell id 2584.</summary>
    public bool IsDeathOnly => (AttributesEx3 & Ex3OnlyOnGhosts) != 0
        || (Targets & (uint)(SpellCastTargetFlags.CorpseEnemy | SpellCastTargetFlags.UnitDead | SpellCastTargetFlags.CorpseAlly)) != 0
        || Id == 2584;

    /// <summary>vmangos SpellEntry::CanTargetDeadTarget (SpellEntry.h:938-942).</summary>
    public bool CanTargetDead => HasAttribute(SpellAttributesEx2.AllowDeadTarget) || IsDeathOnly;

    /// <summary>vmangos SpellEntry::IsChanneledSpell: AttributesEx IS_CHANNELED or IS_SELF_CHANNELED.</summary>
    public bool IsChanneled => HasAttribute(SpellAttributesEx.IsChanneled | SpellAttributesEx.IsSelfChanneled);

    public bool HasEffect(SpellEffectName effect) => _effects.Any(e => e.Effect == effect);

    public bool HasAura(AuraType aura) => _effects.Any(e => e.Effect == SpellEffectName.ApplyAura && e.AuraType == aura);

    /// <summary>
    /// Whether the spell is beneficial. Simplified from vmangos SpellEntry::IsPositiveSpell /
    /// IsPositiveEffect: negative when AURA_IS_DEBUFF is set, when any effect targets an enemy,
    /// or when it deals school damage or applies a known harmful aura. Recorded as a deviation in
    /// docs/areas/spells.md (the full vmangos table also inspects triggered spells).
    /// </summary>
    public bool IsPositive
    {
        get
        {
            if (HasAttribute(SpellAttributes.AuraIsDebuff))
            {
                return false;
            }

            foreach (SpellEffectInfo effect in _effects)
            {
                if (effect.IsEmpty)
                {
                    continue;
                }

                if (effect.TargetA is SpellImplicitTarget.UnitEnemy or SpellImplicitTarget.UnitEnemyNearCaster
                    or SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc or SpellImplicitTarget.EnumUnitsEnemyAoeAtDestLoc
                    or SpellImplicitTarget.EnumUnitsEnemyInCone24 or SpellImplicitTarget.EnumUnitsEnemyInCone54
                    or SpellImplicitTarget.EnumUnitsEnemyWithinCasterRange)
                {
                    return false;
                }

                if (effect.Effect is SpellEffectName.SchoolDamage or SpellEffectName.HealthLeech or SpellEffectName.WeaponDamage
                    or SpellEffectName.WeaponDamageNoschool or SpellEffectName.NormalizedWeaponDmg or SpellEffectName.WeaponPercentDamage
                    or SpellEffectName.InterruptCast or SpellEffectName.EnvironmentalDamage)
                {
                    return false;
                }

                if (effect.Effect == SpellEffectName.ApplyAura && effect.AuraType is AuraType.PeriodicDamage
                    or AuraType.ModStun or AuraType.ModRoot or AuraType.ModDecreaseSpeed or AuraType.PeriodicDamagePercent
                    or AuraType.PeriodicLeech or AuraType.ModFear or AuraType.ModConfuse or AuraType.ModSilence)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// vmangos Unit::GetSpellRank: the caster level, capped at maxLevel * 5 when maxLevel is set
    /// (the formulas below divide it by 5 again — kept verbatim).
    /// </summary>
    public int GetSpellRank(byte casterLevel)
        => MaxLevel > 0 && casterLevel >= MaxLevel * 5 ? (int)(MaxLevel * 5) : casterLevel;

    /// <summary>
    /// Cast time in ms (vmangos SpellEntry::GetCastTime): castTime + perLevel * (rank / 5 -
    /// baseLevel), floored at the minimum; spells that are neither abilities nor tradeskills are
    /// scaled by UNIT_MOD_CAST_SPEED (build &gt; 1.11.2 branch); USES_RANGED_SLOT adds 500 ms.
    /// A spell without a SpellCastTimes entry is instant.
    /// </summary>
    public int GetCastTime(byte casterLevel, float castSpeed = 1.0f)
    {
        if (CastTime == default)
        {
            return 0;
        }

        int castTime = CastTime.Base + (CastTime.PerLevel * ((GetSpellRank(casterLevel) / 5) - (int)BaseLevel));
        castTime = Math.Max(castTime, CastTime.Minimum);
        if (!HasAttribute(SpellAttributes.IsAbility | SpellAttributes.IsTradeskill))
        {
            castTime = (int)(castTime * castSpeed);
        }

        if (HasAttribute(SpellAttributes.UsesRangedSlot))
        {
            castTime += 500;
        }

        return Math.Max(castTime, 0);
    }

    /// <summary>
    /// Aura/channel duration in ms (vmangos SpellEntry::GetDuration / CalculateDuration: the
    /// SpellDuration base value, -1 = permanent; combo points stretch it toward the maximum,
    /// which needs combo points and is left to the combat area). 0 = no duration entry.
    /// </summary>
    public int GetDuration() => Duration == default ? 0 : Duration.Base == -1 ? -1 : Math.Abs(Duration.Base);

    /// <summary>vmangos SpellEntry::GetMaxDuration: the SpellDuration maximum (-1 = permanent).</summary>
    public int GetMaxDuration() => Duration == default ? 0 : Duration.Maximum == -1 ? -1 : Math.Abs(Duration.Maximum);

    /// <summary>
    /// Power cost (vmangos Spell::CalculatePowerCost without spell mods and item casts):
    /// manaCost + manaCostPerlevel * (rank / 5 - baseLevel), plus ManaCostPercentage of the base
    /// mana/health (mana, health) or of the maximum power (rage, focus, energy). USE_ALL_MANA
    /// drains the whole current power. Power cost modifiers by school are added by the caller.
    /// </summary>
    public int CalculatePowerCost(byte casterLevel, uint currentPower, uint maxPower, uint createHealth, uint createMana, uint currentHealth)
    {
        if (HasAttribute(SpellAttributesEx.UseAllMana))
        {
            return (int)(PowerType == SpellMath.PowerHealth ? currentHealth : currentPower);
        }

        int cost = (int)ManaCost + ((int)ManaCostPerLevel * ((GetSpellRank(casterLevel) / 5) - (int)BaseLevel));
        if (ManaCostPercentage > 0)
        {
            cost += PowerType switch
            {
                SpellMath.PowerHealth => (int)(ManaCostPercentage * createHealth / 100),
                0 => (int)(ManaCostPercentage * createMana / 100),
                _ => (int)(ManaCostPercentage * maxPower / 100),
            };
        }

        return Math.Max(cost, 0);
    }

    /// <summary>Effect value for a caster level; see <see cref="SpellMath.CalculateEffectValue"/>.</summary>
    public int CalculateEffectValue(int effectIndex, byte casterLevel, Random random)
        => SpellMath.CalculateEffectValue(this, _effects[effectIndex], casterLevel, random);
}
