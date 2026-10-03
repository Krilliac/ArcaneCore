using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.GameObjects;

namespace ArcaneCore.Game.Skills;

/// <summary>The outcome of <see cref="GatheringRules.CanOpenLock"/>.</summary>
/// <param name="Result">CAST_OK, LOW_CASTLEVEL or BAD_TARGETS (vmangos SpellCastResult).</param>
/// <param name="SkillId">The skill the matching lock case uses (0 for none).</param>
/// <param name="RequiredSkill">The lock case's required skill value.</param>
/// <param name="SkillValue">The caster's skill value including the spell's bonus (0 when a cast item is used).</param>
public readonly record struct OpenLockCheck(SpellCastResult Result, uint SkillId, int RequiredSkill, int SkillValue);

/// <summary>
/// The pure rules of lock opening and skinning, reimplemented from vmangos src/game/Spells (Spell.cpp:7869-7923
/// CanOpenLock, :5948-5964 and :6040-6059 the CheckCast cases, SpellEffects.cpp:5371-5390 EffectSkinning); no
/// reference code is copied.
/// </summary>
public static class GatheringRules
{
    /// <summary>vmangos LOCKTYPE_BLASTING (SharedDefines.h LockType): a skill-less lock type that still counts as a skill case (Seaforium charges).</summary>
    public const uint LockTypeBlasting = 16;

    /// <summary>
    /// vmangos <c>Spell::CanOpenLock</c>: a lock id of 0 opens for anyone; an unknown lock is a bad target; otherwise the
    /// cases are tried in file order. A key case opens when the casting item is the key; the FIRST skill case whose lock
    /// type equals the effect's MiscValue decides: the profession of that lock type (a skill-less type opens) must reach
    /// the case's required value, counting the effect's simple value as a bonus, and not at all when the cast comes
    /// from an item (the skill values are then not added). No case fitting means locked (BAD_TARGETS).
    /// </summary>
    public static OpenLockCheck CanOpenLock(
        uint lockId, LockEntry? lockInfo, uint effectLockType, uint castItemEntry, bool castFromItem, int spellSkillBonus, bool casterIsPlayer,
        Func<uint, uint> skillValueOf)
    {
        ArgumentNullException.ThrowIfNull(skillValueOf);
        if (lockId == 0)
        {
            return new OpenLockCheck(SpellCastResult.CastOk, 0, 0, 0);
        }

        if (lockInfo is null)
        {
            return new OpenLockCheck(SpellCastResult.BadTargets, 0, 0, 0);
        }

        for (int j = 0; j < LockEntry.Cases && j < lockInfo.Types.Count; j++)
        {
            switch ((LockKeyType)lockInfo.Types[j])
            {
                case LockKeyType.Item:
                    if (lockInfo.Indexes[j] != 0 && castItemEntry != 0 && castItemEntry == lockInfo.Indexes[j])
                    {
                        return new OpenLockCheck(SpellCastResult.CastOk, 0, 0, 0);
                    }

                    break;

                case LockKeyType.Skill:
                {
                    if (effectLockType != lockInfo.Indexes[j])
                    {
                        break;
                    }

                    uint skillId = LockSkills.ForLockType((LockType)lockInfo.Indexes[j]);
                    if (skillId == 0 && lockInfo.Indexes[j] != LockTypeBlasting)
                    {
                        return new OpenLockCheck(SpellCastResult.CastOk, 0, 0, 0);
                    }

                    int required = unchecked((int)lockInfo.Skills[j]);
                    int skill = castFromItem || !casterIsPlayer ? 0 : (int)skillValueOf(skillId);
                    skill += spellSkillBonus;
                    return new OpenLockCheck(skill < required ? SpellCastResult.LowCastlevel : SpellCastResult.CastOk, skillId, required, skill);
                }
            }
        }

        return new OpenLockCheck(SpellCastResult.BadTargets, 0, 0, 0);
    }

    /// <summary>
    /// The orange-gathering failure (Spell.cpp:6050-6057 for lock opening, :5960-5964 for skinning): only Lockpicking
    /// can fail at the world maximum skill (<c>canFailAtMax</c>, Spell.cpp:6054); Herbalism, Mining and Skinning
    /// require <c>skill &lt; max</c>. The roll is <c>required &gt; irand(skill - 25, skill + 37)</c>
    /// (inclusive). True when the attempt fails (TRY_AGAIN).
    /// </summary>
    public static bool OrangeGatherFails(uint skillId, int skillValue, int requiredSkill, ushort configMaxSkill, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        bool canFailAtMax = skillId == SkillIds.Lockpicking;
        return (canFailAtMax || skillValue < configMaxSkill) && requiredSkill > random.Next(skillValue - 25, skillValue + 37 + 1);
    }

    /// <summary>The skill the skinning cast needs against a target level (Spell.cpp:5955): <c>skill &lt; 100 ? (level - 10) * 10 : level * 5</c>.</summary>
    public static int SkinningRequiredSkill(int skillValue, int targetLevel) => skillValue < 100 ? (targetLevel - 10) * 10 : targetLevel * 5;

    /// <summary>The "red level" EffectSkinning feeds the skill-up (SpellEffects.cpp:5381): 0 below level 10, then 10 per level up to 20, then 5 per level.</summary>
    public static int SkinningSkillUpLevel(int targetLevel) => targetLevel < 10 ? 0 : targetLevel < 20 ? (targetLevel - 10) * 10 : targetLevel * 5;
}
