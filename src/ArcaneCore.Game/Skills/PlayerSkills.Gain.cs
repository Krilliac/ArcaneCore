using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.Npc;

namespace ArcaneCore.Game.Skills;

/// <summary>Which hand a combat skill-up belongs to (vmangos WeaponAttackType: BASE_ATTACK 0, OFF_ATTACK 1, RANGED_ATTACK 2).</summary>
public enum SkillAttack : byte
{
    Base = 0,
    Off = 1,
    Ranged = 2,
}

/// <summary>
/// The facts <see cref="PlayerSkills.UpdateCombatSkills"/> needs about one resolved attack (vmangos
/// Player::UpdateCombatSkills(pVictim, attType, defence), Player.cpp:5341-5410). The combat area fills it in:
/// <see cref="WeaponSkillId"/> is the proficiency skill of the usable, unbroken weapon in the attacking slot
/// (<see cref="SkillIds.Unarmed"/> for an empty main hand, 0 for an empty off hand or ranged slot) and
/// <see cref="CanGainSkill"/> is false when that weapon is a fishing pole.
/// </summary>
public readonly record struct CombatSkillContext(
    bool Defence,
    SkillAttack Attack,
    uint VictimLevel,
    bool VictimIsPlayerControlled,
    bool ShapeShifted,
    uint WeaponSkillId,
    bool CanGainSkill,
    float Intellect);

public sealed partial class PlayerSkills
{
    /// <summary>vmangos UpdateSkill (Player.cpp:5162-5201): add <paramref name="step"/> to a known skill, capped at its maximum. False for an unknown skill or one at its maximum.</summary>
    public bool Update(uint skillId, uint step)
    {
        if (!TryGetSlot(skillId, out Slot? slot))
        {
            return false;
        }

        uint data = _player.GetUInt32(ValueIndex(slot.Pos));
        uint value = data & 0xFFFF;
        uint max = data >> 16;
        if (max == 0 || value == 0 || value >= max)
        {
            return false;
        }

        uint newValue = Math.Min(value + step, max);
        _player.SetUInt32(ValueIndex(slot.Pos), Pair((ushort)newValue, (ushort)max));
        MarkChanged(slot);
        SkillChanged?.Invoke(skillId);
        return true;
    }

    /// <summary>
    /// vmangos UpdateSkillPro (Player.cpp:5291-5339): roll <c>irand(1, 1000)</c> against
    /// <paramref name="chance"/> (per mille) and add <paramref name="step"/> on success. Like the reference it
    /// returns true when the skill could have risen (a known skill below its maximum) whether or not the roll
    /// succeeded, and false for a non-positive chance, an unknown skill, a zero value or maximum, or a skill at
    /// its maximum.
    /// </summary>
    public bool UpdatePro(uint skillId, int chance, uint step)
    {
        if (skillId == 0 || chance <= 0 || !TryGetSlot(skillId, out Slot? slot))
        {
            return false;
        }

        uint data = _player.GetUInt32(ValueIndex(slot.Pos));
        uint value = data & 0xFFFF;
        uint max = data >> 16;
        if (max == 0 || value == 0 || value >= max)
        {
            return false;
        }

        if (_random.Next(1, 1000) <= chance)
        {
            uint newValue = Math.Min(value + step, max);
            _player.SetUInt32(ValueIndex(slot.Pos), Pair((ushort)newValue, (ushort)max));
            MarkChanged(slot);
            SkillChanged?.Invoke(skillId);
        }

        return true;
    }

    /// <summary>
    /// vmangos UpdateCraftSkill (Player.cpp:5214-5243): the first SkillLineAbility row of the spell with a
    /// skill decides (its trivial high/low rank set the colour). Account trial restrictions (Player.cpp:5224-5232)
    /// are not modelled.
    /// </summary>
    public bool UpdateCraft(uint spellId)
    {
        foreach (SkillLineAbilityRecord ability in _catalog.AbilitiesOfSpell(spellId))
        {
            if (ability.SkillId == 0)
            {
                continue;
            }

            uint skillValue = GetValuePure(ability.SkillId);
            return UpdatePro(
                ability.SkillId,
                SkillRules.CraftChance(skillValue, ability.MaxValue, ability.MinValue, Options),
                Options.GainCrafting);
        }

        return false;
    }

    /// <summary>
    /// vmangos UpdateGatherSkill (Player.cpp:5245-5275). Only Herbalism, Lockpicking, Skinning and Mining are
    /// gathering skills; any other id returns false. Account trial restrictions are not modelled.
    /// </summary>
    public bool UpdateGather(uint skillId, uint skillValue, uint redLevel, uint multiplicator = 1)
    {
        int? chance = SkillRules.GatherChance(skillId, skillValue, redLevel, multiplicator, Options);
        return chance is { } value && UpdatePro(skillId, value, Options.GainGathering);
    }

    /// <summary>vmangos UpdateFishingSkill (Player.cpp:5277-5289).</summary>
    public bool UpdateFishing()
        => UpdatePro(SkillIds.Fishing, SkillRules.FishingChance(GetValuePure(SkillIds.Fishing)), Options.GainGathering);

    /// <summary>
    /// vmangos UpdateCombatSkills (Player.cpp:5341-5410). No gain against player-controlled victims, and no
    /// weapon gain in a shapeshift form. Returns true when a skill value rose (the caller then recomputes the
    /// derived defence or crit values; <see cref="SkillChanged"/> also fires).
    /// </summary>
    public bool UpdateCombatSkills(in CombatSkillContext context)
    {
        if (context.VictimIsPlayerControlled || (!context.Defence && context.ShapeShifted))
        {
            return false;
        }

        uint playerLevel = _player.Level;
        // GetBaseWeaponSkillValue: an empty off hand or ranged slot is 0, an empty main hand is Unarmed (Player.cpp:20052-20063).
        uint current = GetValuePure(context.Defence ? SkillIds.Defense : context.WeaponSkillId);
        if (SkillRules.CombatGainChance(context.Defence, playerLevel, context.VictimLevel, current, context.Intellect) is not { } chance)
        {
            return false;
        }

        // roll_chance_f: chance > rand_chance(), a double in [0, 100) (vmangos Random.h:49-58).
        if (!(chance > _random.NextFloat(0f, 100f)))
        {
            return false;
        }

        if (context.Defence)
        {
            return Update(SkillIds.Defense, Options.GainDefense);
        }

        return context.CanGainSkill && context.WeaponSkillId != 0 && Update(context.WeaponSkillId, Options.GainWeapon);
    }

    /// <summary>
    /// vmangos UpdateSkillsForLevel (Player.cpp:5438-5496): skills of range type "level" whose maximum is not 1 take
    /// the new level's maximum (or sit at it with AlwaysMaxSkillForLevel or the skill's ALWAYS_MAX flag); a
    /// maximum already at the configured world maximum is left alone.
    /// </summary>
    public void UpdateSkillsForLevel()
    {
        ushort configMax = SkillRules.ConfigMaxSkillValue(Options.MaxPlayerLevel);
        ushort maxSkill = SkillRules.MaxForLevel(_player.Level);
        byte race = (byte)_player.Race;
        byte playerClass = (byte)_player.Class;
        foreach ((uint skillId, Slot slot) in _slots.ToArray())
        {
            if (slot.State == SkillState.Deleted || _catalog.Line(skillId) is null)
            {
                continue;
            }

            SkillRaceClassInfoRecord? raceClass = _catalog.RaceClassInfo(skillId, race, playerClass);
            if (raceClass is null || _catalog.RangeType(skillId, raceClass) != SkillRangeType.Level)
            {
                continue;
            }

            uint data = _player.GetUInt32(ValueIndex(slot.Pos));
            uint max = data >> 16;
            uint value = data & 0xFFFF;
            if (max == 1)
            {
                continue;
            }

            if (Options.AlwaysMaxSkillForLevel || (raceClass.Flags & SkillRaceClassFlags.AlwaysMaxValue) != 0)
            {
                _player.SetUInt32(ValueIndex(slot.Pos), Pair(maxSkill, maxSkill));
                MarkChanged(slot);
                SkillChanged?.Invoke(skillId);
            }
            else if (max != configMax)
            {
                _player.SetUInt32(ValueIndex(slot.Pos), Pair((ushort)value, maxSkill));
                MarkChanged(slot);
                SkillChanged?.Invoke(skillId);
            }
        }
    }

    /// <summary>
    /// vmangos UpdateSkillsToMaxSkillsForLevel (Player.cpp:5498-5527), the GM ".maxskill": every non-profession,
    /// non-riding skill with a maximum above 1 is set to that maximum.
    /// </summary>
    public void UpdateSkillsToMax()
    {
        foreach ((uint skillId, Slot slot) in _slots.ToArray())
        {
            if (slot.State == SkillState.Deleted || _catalog.IsProfessionOrRidingSkill(skillId))
            {
                continue;
            }

            uint max = _player.GetUInt32(ValueIndex(slot.Pos)) >> 16;
            if (max > 1)
            {
                _player.SetUInt32(ValueIndex(slot.Pos), Pair((ushort)max, (ushort)max));
                MarkChanged(slot);
                SkillChanged?.Invoke(skillId);
            }
        }
    }
}
