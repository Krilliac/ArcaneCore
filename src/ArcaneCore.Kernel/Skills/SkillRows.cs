namespace ArcaneCore.Kernel.Skills;

/// <summary>
/// One SkillLine.dbc row (build 5875, 22 fields; vmangos Database/DBCStructure.h:528-538
/// <c>SkillLineEntry</c>, DBCfmt.h:67 <c>"nixssssssssxxxxxxxxxxi"</c>): id, category, the enUS display
/// name (field 3) and the spell icon (field 21).
/// </summary>
public sealed record SkillLineRecord(uint Id, int Category, string Name, uint SpellIconId);

/// <summary>
/// One SkillRaceClassInfo.dbc row (build 5875, 8 fields; vmangos DBCStructure.h:507-517
/// <c>SkillRaceClassInfoEntry</c>, DBCfmt.h:69 <c>"diiiiiix"</c>; field 0 is the unused row id and field 7
/// the unused cost index). A mask of zero means "any" (vmangos DBCStores.cpp:589-602).
/// </summary>
public sealed record SkillRaceClassInfoRecord(uint SkillId, uint RaceMask, uint ClassMask, uint Flags, uint MinLevel, uint TierId);

/// <summary>
/// One SkillTiers.dbc row (build 5875, 33 fields; vmangos DBCStructure.h:519-526 <c>SkillTiersEntry</c>,
/// DBCfmt.h:70): <see cref="Cost"/> is fields 1-16 and <see cref="MaxValue"/> fields 17-32, one entry per
/// skill step (<see cref="StepCount"/>).
/// </summary>
public sealed class SkillTierRecord
{
    /// <summary>vmangos DBCStructure.h:519 <c>MAX_SKILL_STEP 16</c>.</summary>
    public const int StepCount = 16;

    public SkillTierRecord(uint id, IReadOnlyList<uint> cost, IReadOnlyList<uint> maxValue)
    {
        ArgumentNullException.ThrowIfNull(cost);
        ArgumentNullException.ThrowIfNull(maxValue);
        if (cost.Count != StepCount || maxValue.Count != StepCount)
        {
            throw new ArgumentException($"a skill tier has exactly {StepCount} steps");
        }

        Id = id;
        Cost = cost.ToArray();
        MaxValue = maxValue.ToArray();
    }

    public uint Id { get; }

    /// <summary>Training cost per step (m_cost).</summary>
    public IReadOnlyList<uint> Cost { get; }

    /// <summary>Skill maximum per step (m_valueMax).</summary>
    public IReadOnlyList<uint> MaxValue { get; }
}

/// <summary>
/// What a spell with a SKILL effect grants (vmangos Spells/SpellMgr.h:230-236 <c>SpellLearnSkillNode</c>).
/// All four are <c>uint16</c> in the reference and are truncated the same way.
/// </summary>
public readonly record struct SpellLearnSkillNode(ushort SkillId, ushort Step, ushort Value, ushort MaxValue);

/// <summary>
/// One SPELL_EFFECT_SKILL (118) effect of a spell, as the caller extracts it from Spell.dbc: the effect
/// index, MiscValue (the skill), BasePoints and BaseDice (vmangos <c>CalculateSimpleValue</c> =
/// BasePoints + BaseDice, SpellEntry.h:1232).
/// </summary>
public readonly record struct SpellSkillEffect(uint SpellId, int EffectIndex, int MiscValue, int BasePoints, int BaseDice);

/// <summary>
/// One persisted skill of a character (vmangos characters.sql <c>character_skills</c>: guid, skill, value,
/// max; Player::_LoadSkills reads <c>skill, value, max</c>, Player.cpp:20540-20660). The pure value and
/// maximum only: bonuses and the step are derived again at login.
/// </summary>
public readonly record struct CharacterSkillRow(ushort Skill, ushort Value, ushort Max);

/// <summary>
/// A weapon skill value a character kept after unlearning it (vmangos <c>character_forgotten_skills</c>:
/// guid, skill, value; Player::_LoadForgottenSkills Player.cpp:20628-20655, client builds above 1.10.2).
/// </summary>
public readonly record struct ForgottenSkillRow(ushort Skill, ushort Value);

/// <summary>Everything the skill store keeps for one character: the skills and the forgotten weapon skill values.</summary>
public sealed record CharacterSkillSnapshot(IReadOnlyList<CharacterSkillRow> Skills, IReadOnlyList<ForgottenSkillRow> Forgotten);
