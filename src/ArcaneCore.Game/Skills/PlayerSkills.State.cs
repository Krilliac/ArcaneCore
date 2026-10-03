using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Skills;

namespace ArcaneCore.Game.Skills;

public sealed partial class PlayerSkills
{
    private uint _weaponProficiency;
    private uint _armorProficiency;

    /// <summary>Weapon sub-class bitmask the player may use (vmangos m_WeaponProficiency), grown by PROFICIENCY spells.</summary>
    public uint WeaponProficiency => _weaponProficiency;

    /// <summary>Armor sub-class bitmask the player may use (vmangos m_ArmorProficiency).</summary>
    public uint ArmorProficiency => _armorProficiency;

    private ulong _knownLanguages;

    /// <summary>
    /// vmangos Player::LearnLanguage (Player.h:2154): the LANGUAGE spell effect sets the bit of its language id
    /// (MiscValue). Ids at or above 64 cannot be represented and are ignored.
    /// </summary>
    public void LearnLanguage(uint language)
    {
        if (language < 64)
        {
            _knownLanguages |= 1UL << (int)language;
        }
    }

    /// <summary>vmangos Player::KnowsLanguage (Player.h:2156).</summary>
    public bool KnowsLanguage(uint language) => language < 64 && (_knownLanguages & (1UL << (int)language)) != 0;

    /// <summary>vmangos Player::CanDualWield, set by the DUAL_WIELD spell effect.</summary>
    public bool CanDualWield { get; set; }

    /// <summary>vmangos Player::CanParry, set by the PARRY spell effect.</summary>
    public bool CanParry { get; set; }

    /// <summary>vmangos Player::CanBlock, set by the BLOCK spell effect.</summary>
    public bool CanBlock { get; set; }

    /// <summary>
    /// Add sub-classes to a proficiency mask (vmangos AddWeaponProficiency / AddArmorProficiency). Returns the
    /// new mask when it grew, null when the player already had every bit or the class has no proficiency.
    /// </summary>
    public uint? AddProficiency(ItemClass itemClass, uint subClassMask)
    {
        switch (itemClass)
        {
            case ItemClass.Weapon when (_weaponProficiency & subClassMask) != subClassMask:
                _weaponProficiency |= subClassMask;
                return _weaponProficiency;
            case ItemClass.Armor when (_armorProficiency & subClassMask) != subClassMask:
                _armorProficiency |= subClassMask;
                return _armorProficiency;
            default:
                return null;
        }
    }

    /// <summary>
    /// vmangos Player::_LoadSkills and _LoadForgottenSkills (Player.cpp:20540-20655). Rows for unknown skill
    /// lines, skills forbidden to the race and class, and skills with value 0 are dropped (the next snapshot
    /// no longer contains them); languages load as 300/300, mono skills as 1/1, level skills take the
    /// maximum for the level; at most <see cref="MaxSkills"/> load. Once all are in, the spells each skill
    /// grants are learned (so a skill is never granted from a spell before its stored value is in).
    /// Forgotten values for non-weapon or unknown skills are dropped.
    /// </summary>
    public void Load(IEnumerable<CharacterSkillRow> rows, IEnumerable<ForgottenSkillRow> forgotten)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(forgotten);
        if (_loaded || _slots.Count != 0)
        {
            throw new InvalidOperationException("the player's skills are already loaded");
        }

        _loaded = true;
        byte race = (byte)_player.Race;
        byte playerClass = (byte)_player.Class;
        int count = 0;
        var loaded = new List<(ushort Skill, ushort Value)>();
        bool dropped = false;
        foreach (CharacterSkillRow row in rows)
        {
            SkillLineRecord? line = _catalog.Line(row.Skill);
            SkillRaceClassInfoRecord? raceClass = line is null ? null : _catalog.RaceClassInfo(row.Skill, race, playerClass);
            if (line is null || raceClass is null || _slots.ContainsKey(row.Skill))
            {
                dropped = true;
                continue;
            }

            ushort value = row.Value;
            ushort max = row.Max;
            switch (_catalog.RangeType(row.Skill, raceClass))
            {
                case SkillRangeType.Language:
                    value = max = 300;
                    break;
                case SkillRangeType.Mono:
                    value = max = 1;
                    break;
                case SkillRangeType.Level:
                    max = MaxForLevel;
                    break;
                default:
                    break;
            }

            if (value == 0)
            {
                dropped = true;
                continue;
            }

            _player.SetUInt32(InfoIndex(count), row.Skill);
            _player.SetUInt32(ValueIndex(count), Pair(value, max));
            _player.SetUInt32(BonusIndex(count), 0);
            _slots[row.Skill] = new Slot((byte)count, SkillState.Unchanged);
            loaded.Add((row.Skill, value));
            count++;
            if (count >= MaxSkills)
            {
                dropped = true;
                break;
            }
        }

        foreach (ForgottenSkillRow row in forgotten)
        {
            if (_catalog.Line(row.Skill)?.Category == SkillCategories.Weapon)
            {
                _forgotten.TryAdd(row.Skill, row.Value);
            }
        }

        _forgottenChanged = dropped;
        foreach ((ushort skill, ushort value) in loaded)
        {
            UpdateSkillTrainedSpells(skill, value);
        }
    }

    /// <summary>Whether a snapshot would contain anything new since the last one (vmangos: any row not UNCHANGED).</summary>
    public bool IsDirty => _forgottenChanged || _slots.Values.Any(s => s.State != SkillState.Unchanged);

    /// <summary>
    /// The complete skill state for the store (vmangos Player::_SaveSkills, Player.cpp:16858-16920): the pure
    /// value and maximum of every live skill, in slot order, and every forgotten weapon value above 1. Taking
    /// it marks everything saved (new and changed rows become unchanged, deleted ones are forgotten), so a caller
    /// that fails to persist the snapshot must keep it and retry it.
    /// </summary>
    public CharacterSkillSnapshot TakeSnapshot()
    {
        var skills = new List<CharacterSkillRow>(_slots.Count);
        foreach ((uint skillId, Slot slot) in _slots.OrderBy(pair => pair.Value.Pos).ToArray())
        {
            if (slot.State == SkillState.Deleted)
            {
                _slots.Remove(skillId);
                continue;
            }

            uint data = _player.GetUInt32(ValueIndex(slot.Pos));
            skills.Add(new CharacterSkillRow((ushort)skillId, (ushort)(data & 0xFFFF), (ushort)(data >> 16)));
            slot.State = SkillState.Unchanged;
        }

        ForgottenSkillRow[] forgotten = _forgotten
            .Where(pair => pair.Value > 1)
            .OrderBy(pair => pair.Key)
            .Select(pair => new ForgottenSkillRow(pair.Key, pair.Value))
            .ToArray();
        _forgottenChanged = false;
        return new CharacterSkillSnapshot(skills, forgotten);
    }

    /// <summary>The snapshot when anything changed, otherwise null (nothing to write).</summary>
    public CharacterSkillSnapshot? TakeSnapshotIfChanged() => IsDirty ? TakeSnapshot() : null;

    /// <summary>Every live skill id with its slot, for diagnostics and commands (.skills).</summary>
    public IEnumerable<(uint SkillId, int Slot)> Known()
        => _slots.Where(pair => pair.Value.State != SkillState.Deleted).OrderBy(pair => pair.Value.Pos).Select(pair => (pair.Key, (int)pair.Value.Pos));
}
