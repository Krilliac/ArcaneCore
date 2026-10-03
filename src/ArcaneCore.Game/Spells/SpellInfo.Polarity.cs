namespace ArcaneCore.Game.Spells;

/// <summary>
/// Exact port of vmangos SpellEntry::IsPositiveSpell / IsPositiveEffect / IsPositiveTarget (SpellEntry.cpp:795-1018,
/// SpellEntry.h:218-240). Polarity is decided per EFFECT; a spell is positive when none of its effects is negative.
/// </summary>
public sealed partial record SpellInfo
{
    /// <summary>vmangos SPELL_CUSTOM_NEGATIVE (SpellDefines.h:1003): spell_template.custom data, zero unless a data source supplies it.</summary>
    public const uint CustomNegative = 0x002;

    /// <summary>vmangos SPELL_CUSTOM_POSITIVE (SpellDefines.h:1004).</summary>
    public const uint CustomPositive = 0x004;

    private const uint FamilyGeneric = 0;
    private const uint FamilyWarlock = 5;
    private const int WarlockHellfireBit = 6;
    private const int WarlockVoidwalkerSpellsBit = 25;
    private const int MechanicBandage = 16;
    private const int MechanicShield = 19;
    private const int MechanicMount = 21;
    private const int MechanicInvulnerability = 25;
    private const int SpellModCost = 14;

    // Target ids that SpellImplicitTarget does not name (vmangos SpellDefines.h:61-108).
    private const uint TargetScriptAoeAtSrcLoc = 7;
    private const uint TargetEnemyAoeAtDynObjLoc = 28;

    /// <summary>Spell.dbc ActiveIconID (vmangos activeIconID): CMSG_CANCEL_AURA needs it for NO_AURA_ICON spells.</summary>
    public uint ActiveIconId { get; init; }

    /// <summary>spell_template.custom flag bits (SPELL_CUSTOM_*); only POSITIVE/NEGATIVE are read here.</summary>
    public uint CustomFlags { get; init; }

    /// <summary>vmangos SpellEntry::CalculateSimpleValue (SpellEntry.h:1232).</summary>
    public int SimpleValue(int effectIndex) => _effects[effectIndex].BasePoints + _effects[effectIndex].BaseDice;

    /// <summary>
    /// vmangos IsPositiveTarget (SpellEntry.h:218-240). Ids are plain numbers because the shared enum does not name them all.
    /// </summary>
    public static bool IsPositiveTarget(uint targetA, uint targetB)
    {
        switch (targetA)
        {
            case (uint)SpellImplicitTarget.UnitEnemy:
            case (uint)SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc:
            case (uint)SpellImplicitTarget.EnumUnitsEnemyAoeAtDestLoc:
            case (uint)SpellImplicitTarget.EnumUnitsEnemyInCone24:
            case TargetEnemyAoeAtDynObjLoc:
            case (uint)SpellImplicitTarget.LocationCasterTargetPosition:
                return false;
            case (uint)SpellImplicitTarget.LocationCasterSrc:
                return targetB is (uint)SpellImplicitTarget.EnumUnitsPartyAoeAtSrcLoc or (uint)SpellImplicitTarget.EnumUnitsFriendAoeAtSrcLoc
                    or TargetScriptAoeAtSrcLoc;
        }

        return targetB != 0 ? IsPositiveTarget(targetB, 0) : true;
    }

    /// <summary>
    /// vmangos IsPositiveSpell (SpellEntry.cpp:795-806). <paramref name="lookup"/> resolves a periodic-trigger spell
    /// (null = skip that check). <paramref name="friendlyDispel"/> is the caster/victim friendliness for DISPEL effects
    /// (null = the aura-less fall-through of the null caster/victim call, as the Aura constructor makes it).
    /// </summary>
    public bool IsPositiveSpell(Func<uint, SpellInfo?>? lookup = null, bool? friendlyDispel = null)
    {
        if (HasAttribute(SpellAttributes.AuraIsDebuff))
        {
            return false;
        }

        for (int i = 0; i < SpellConstants.MaxEffects; i++)
        {
            if (!_effects[i].IsEmpty && !IsPositiveEffect(i, lookup, friendlyDispel))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>vmangos IsPositiveEffect (SpellEntry.cpp:808-1007).</summary>
    public bool IsPositiveEffect(int effIndex, Func<uint, SpellInfo?>? lookup = null, bool? friendlyDispel = null)
    {
        SpellEffectInfo e = _effects[effIndex];
        if ((CustomFlags & CustomPositive) != 0)
        {
            return true;
        }

        if ((CustomFlags & CustomNegative) != 0)
        {
            return false;
        }

        // Hellfire damages the caster but is positive (SpellEntry.cpp:817-823).
        if (IsFitToFamily(FamilyWarlock, WarlockHellfireBit) && SpellIconId == 937 && SpellVisual == 5423)
        {
            return true;
        }

        switch (e.Effect)
        {
            case SpellEffectName.Dummy:
                if (Id is 10258 or 18153)
                {
                    return true;
                }

                break;
            case SpellEffectName.Heal:
            case SpellEffectName.LearnSpell:
            case SpellEffectName.SkillStep:
                return true;
            case SpellEffectName.Instakill:
                // Suicide (caster-only) is positive; Sacrifice is positive for the warlock (SpellEntry.cpp:837-842).
                if (e.TargetA == SpellImplicitTarget.UnitCaster && e.TargetB == SpellImplicitTarget.None)
                {
                    return true;
                }

                return IsFitToFamily(FamilyWarlock, WarlockVoidwalkerSpellsBit);
            case SpellEffectName.Dispel:
                if (friendlyDispel is { } friendly)
                {
                    return friendly;
                }

                // vmangos falls through into the aura switch with no aura name, which matches no case.
                break;
            case SpellEffectName.ApplyAura:
                switch (e.AuraType)
                {
                    case AuraType.Dummy:
                        switch (Id)
                        {
                            case 13139:
                            case 23445:
                                return false;
                            case 27184:
                            case 27190:
                            case 27191:
                            case 27201:
                            case 27202:
                            case 27203:
                                return true;
                        }

                        break;
                    case AuraType.ModDamageDone:
                    case AuraType.ModResistance:
                    case AuraType.ModStat:
                    case AuraType.ModSkill:
                    case AuraType.ModDodgePercent:
                    case AuraType.ModHealingPct:
                    case AuraType.ModHealingDone:
                        if (SimpleValue(effIndex) < 0)
                        {
                            return false;
                        }

                        break;
                    case AuraType.ModDamageTaken:
                        if (SimpleValue(effIndex) < 0)
                        {
                            return true;
                        }

                        break;
                    case AuraType.ModSpellCritChance:
                    case AuraType.ModIncreaseHealthPercent:
                    case AuraType.ModDamagePercentDone:
                        if (SimpleValue(effIndex) > 0)
                        {
                            return true;
                        }

                        break;
                    case AuraType.ModIncreaseHealth:
                    case AuraType.AddTargetTrigger:
                        return true;
                    case AuraType.PeriodicTriggerSpell:
                        if (Id != e.TriggerSpell && lookup?.Invoke(e.TriggerSpell) is { } triggered)
                        {
                            for (int i = 0; i < SpellConstants.MaxEffects; i++)
                            {
                                SpellEffectInfo t = triggered._effects[i];
                                if (!t.IsEmpty && IsPositiveTarget((uint)t.TargetA, (uint)t.TargetB) && !triggered.IsPositiveEffect(i))
                                {
                                    return false;
                                }
                            }
                        }

                        break;
                    case AuraType.ModStun:
                        if (effIndex == 0 && _effects[1].IsEmpty && _effects[2].IsEmpty)
                        {
                            return false;
                        }

                        break;
                    case AuraType.ModPacifySilence:
                        return Id == 24740;
                    case AuraType.ModSilence:
                        return Id == 24732;
                    case AuraType.ModRoot:
                    case AuraType.Ghost:
                    case AuraType.PeriodicLeech:
                    case AuraType.ModStalked:
                    case AuraType.PeriodicDamagePercent:
                    case AuraType.ModDetectRange:
                        return false;
                    case AuraType.PeriodicDamage:
                        if (e.TargetA == SpellImplicitTarget.UnitCaster)
                        {
                            return false;
                        }

                        break;
                    case AuraType.ModDecreaseSpeed:
                        if (e.TargetA == SpellImplicitTarget.UnitCaster && SpellFamilyName == FamilyGeneric)
                        {
                            return false;
                        }

                        if (HasAttribute(SpellAttributes.AuraIsDebuff) && effIndex == 0)
                        {
                            return false;
                        }

                        break;
                    case AuraType.ModScale:
                        if (Id == 802)
                        {
                            return true;
                        }

                        break;
                    case AuraType.MechanicImmunity:
                        if (e.MiscValue is MechanicBandage or MechanicShield or MechanicMount or MechanicInvulnerability)
                        {
                            return false;
                        }

                        break;
                    case AuraType.AddFlatModifier:
                    case AuraType.AddPctModifier:
                        if (e.MiscValue == SpellModCost && SimpleValue(effIndex) > 0)
                        {
                            return false;
                        }

                        break;
                }

                break;
        }

        if (!IsPositiveTarget((uint)e.TargetA, (uint)e.TargetB))
        {
            return false;
        }

        return !HasAttribute(SpellAttributes.AuraIsDebuff);
    }
}
