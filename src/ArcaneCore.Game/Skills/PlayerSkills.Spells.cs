using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;

namespace ArcaneCore.Game.Skills;

public sealed partial class PlayerSkills
{
    /// <summary>vmangos <c>ABILITY_LEARNED_ON_GET_RACE_OR_CLASS_SKILL</c> (DBCEnums.h:150-154).</summary>
    private const uint LearnedOnGetRaceOrClassSkill = 2;

    /// <summary>
    /// Free primary profession slots (PLAYER_CHARACTER_POINTS2; vmangos Player.h:1551-1552). A new character
    /// and every login start at <see cref="SkillOptions.MaxPrimaryTradeSkill"/> before any spell is added
    /// (Player.cpp:509, 14730, 19027-19030); learning a profession's first rank spends one.
    /// </summary>
    public uint FreePrimaryProfessionPoints
    {
        get => _player.GetUInt32(UpdateFields.PlayerCharacterPoints2);
        set => _player.SetUInt32(UpdateFields.PlayerCharacterPoints2, (ushort)value);
    }

    /// <summary>vmangos InitPrimaryProfessions (Player.cpp:19027-19030).</summary>
    public void InitPrimaryProfessions() => FreePrimaryProfessionPoints = (uint)Options.MaxPrimaryTradeSkill;

    /// <summary>
    /// The spellbook owner calls this after a spell was added (vmangos Player::AddSpell, Player.cpp:3702-3709 and
    /// :3739): a primary profession's first rank spends a free slot (when any is left; a GM may learn more), then
    /// the skills the spell grants are set up.
    /// </summary>
    public void OnSpellLearned(uint spellId)
    {
        uint free = FreePrimaryProfessionPoints;
        if (free != 0 && _catalog.IsPrimaryProfessionFirstRankSpell(spellId))
        {
            FreePrimaryProfessionPoints = free - 1;
        }

        UpdateSpellTrainedSkills(spellId, true);
    }

    /// <summary>
    /// The spellbook owner calls this after a spell was removed (vmangos Player::RemoveSpell, Player.cpp:3868-3880):
    /// a primary profession's first rank returns its free slot (never above the configured maximum), then the
    /// skills the spell granted are lowered or removed.
    /// </summary>
    public void OnSpellForgotten(uint spellId)
    {
        if (_catalog.IsPrimaryProfessionFirstRankSpell(spellId))
        {
            uint free = FreePrimaryProfessionPoints + 1;
            if (free <= (uint)Options.MaxPrimaryTradeSkill)
            {
                FreePrimaryProfessionPoints = free;
            }
        }

        UpdateSpellTrainedSkills(spellId, false);
    }

    /// <summary>
    /// vmangos UpdateSkillTrainedSpells (Player.cpp:5710-5768): teach (or take) the spells a skill grants.
    /// With a value of zero every spell of the skill is removed whatever taught it; otherwise a spell flagged
    /// "learn on get skill" fitting the race and class is removed while the value is below its requirement
    /// and learned from there on.
    /// </summary>
    public void UpdateSkillTrainedSpells(ushort skillId, ushort currentValue)
    {
        uint raceMask = RaceMask;
        uint classMask = ClassMask;
        foreach (SkillLineAbilityRecord ability in _catalog.AbilitiesOfSkill(skillId))
        {
            if (currentValue == 0)
            {
                _spells.RemoveSpell(ability.SpellId);
                continue;
            }

            if (ability.LearnOnGetSkill == 0)
            {
                continue;
            }

            if (ability.RaceMask != 0 && (ability.RaceMask & raceMask) == 0)
            {
                continue;
            }

            if (ability.ClassMask != 0 && (ability.ClassMask & classMask) == 0)
            {
                continue;
            }

            if (currentValue < ability.ReqSkillValue)
            {
                _spells.RemoveSpell(ability.SpellId);
            }
            else
            {
                _spells.LearnSpell(ability.SpellId);
            }
        }
    }

    /// <summary>
    /// vmangos UpdateSpellTrainedSkills (Player.cpp:5770-5896): add or lower the skills a spell grants. A spell
    /// with a SKILL effect sets its skill to the effect's values (never lowering what the player has); on
    /// removal it falls back to the previous rank's grant, or removes the skill for a first rank. Any other spell
    /// that is an ability of a skill the player lacks grants the skill when it is a race or class skill, a class
    /// specialisation (ALWAYS_MAX | MONO flags exactly) or an unrestricted Poisons or Lockpicking row; removal
    /// forgets weapon values (client builds above 1.10.2: the value is remembered and restored on relearn).
    /// </summary>
    public void UpdateSpellTrainedSkills(uint spellId, bool apply)
    {
        if (_catalog.LearnSkills.TryGet(spellId, out SpellLearnSkillNode node))
        {
            if (apply)
            {
                ushort value = Math.Max(node.Value, GetValuePure(node.SkillId));
                ushort max = Math.Max(node.MaxValue == 0 ? MaxForLevel : node.MaxValue, GetMaxPure(node.SkillId));
                Set(node.SkillId, value, max, node.Step);
                return;
            }

            uint previous = _catalog.Ranks.Previous(spellId);
            if (previous == 0)
            {
                Set(node.SkillId, 0, 0);
                return;
            }

            // Search the previous grant along the chain (the reference looks up the first rank of each earlier spell).
            SpellLearnSkillNode previousNode = default;
            bool found = _catalog.LearnSkills.TryGet(previous, out previousNode);
            while (!found && previous != 0)
            {
                previous = _catalog.Ranks.Previous(previous);
                found = _catalog.LearnSkills.TryGet(_catalog.Ranks.First(previous), out previousNode);
            }

            if (!found)
            {
                Set(node.SkillId, 0, 0);
                return;
            }

            ushort previousValue = Math.Min(previousNode.Value, GetValuePure(previousNode.SkillId));
            ushort previousMax = Math.Min(previousNode.MaxValue == 0 ? MaxForLevel : previousNode.MaxValue, GetMaxPure(previousNode.SkillId));
            Set(previousNode.SkillId, previousValue, previousMax, previousNode.Step);
            return;
        }

        byte race = (byte)_player.Race;
        byte playerClass = (byte)_player.Class;
        foreach (SkillLineAbilityRecord ability in _catalog.AbilitiesOfSpell(spellId).ToArray())
        {
            SkillLineRecord? line = _catalog.Line(ability.SkillId);
            if (line is null)
            {
                continue;
            }

            if (apply)
            {
                if (Has(line.Id))
                {
                    continue;
                }

                SkillRaceClassInfoRecord? raceClass = _catalog.RaceClassInfo(line.Id, race, playerClass);
                if (raceClass is null)
                {
                    continue;
                }

                bool grants = ability.LearnOnGetSkill == LearnedOnGetRaceOrClassSkill
                    || raceClass.Flags == (SkillRaceClassFlags.AlwaysMaxValue | SkillRaceClassFlags.MonoValue)
                    || (line.Id == SkillIds.Poisons && ability.MaxValue == 0)
                    || (line.Id == SkillIds.Lockpicking && ability.MaxValue == 0);
                if (!grants)
                {
                    continue;
                }

                switch (_catalog.RangeType(line.Id, raceClass))
                {
                    case SkillRangeType.Language:
                        Set(line.Id, 300, 300);
                        break;
                    case SkillRangeType.Level:
                    {
                        ushort newValue = Options.AlwaysMaxSkillForLevel || (raceClass.Flags & SkillRaceClassFlags.AlwaysMaxValue) != 0
                            ? MaxForLevel
                            : (ushort)1;

                        // Client patch 1.11.0: weapon skill levels survive unspending the talent that granted them.
                        if (line.Category == SkillCategories.Weapon
                            && _forgotten.TryGetValue((ushort)line.Id, out ushort saved)
                            && saved <= MaxForLevel)
                        {
                            newValue = saved;
                        }

                        Set(line.Id, newValue, MaxForLevel);
                        break;
                    }

                    case SkillRangeType.Mono:
                        Set(line.Id, 1, 1);
                        break;
                    default:
                        break;
                }
            }
            else
            {
                bool raceClassSkill = ability.LearnOnGetSkill == LearnedOnGetRaceOrClassSkill && line.Category != SkillCategories.Class;
                bool unrestricted = (line.Id is SkillIds.Poisons or SkillIds.Lockpicking) && ability.MaxValue == 0;
                if (!raceClassSkill && !unrestricted)
                {
                    continue;
                }

                // Professions and racial abilities keep their skill.
                if ((line.Category is SkillCategories.Secondary or SkillCategories.Profession)
                    && (_catalog.IsProfessionSkill(line.Id) || ability.RaceMask != 0))
                {
                    continue;
                }

                if (line.Category == SkillCategories.Weapon)
                {
                    ushort pure = GetValuePure(line.Id);
                    if (pure > _forgotten.GetValueOrDefault((ushort)line.Id))
                    {
                        _forgotten[(ushort)line.Id] = pure;
                        _forgottenChanged = true;
                    }
                }

                Set(line.Id, 0, 0);
                if (line.Category == SkillCategories.Weapon)
                {
                    _spells.WeaponSkillRemoved();
                }
            }
        }
    }

    /// <summary>The weapon skill values kept after unlearning (vmangos m_forgottenSkills).</summary>
    public IReadOnlyDictionary<ushort, ushort> ForgottenSkills => _forgotten;

    private ushort MaxForLevel => SkillRules.MaxForLevel(_player.Level);

    private uint RaceMask => 1u << (((byte)_player.Race) - 1);

    private uint ClassMask => 1u << (((byte)_player.Class) - 1);
}
