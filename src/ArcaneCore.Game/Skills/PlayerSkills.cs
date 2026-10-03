using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Skills;

namespace ArcaneCore.Game.Skills;

/// <summary>vmangos <c>SkillUpdateState</c> (Player.h:453-459): what the next save must do with a skill row.</summary>
public enum SkillState : byte
{
    Unchanged = 0,
    Changed = 1,
    New = 2,
    Deleted = 3,
}

/// <summary>
/// The skills of one player, owned by the world thread. As in vmangos the update fields are the source of
/// truth: slot <c>i</c> occupies the three words at <c>PLAYER_SKILL_INFO_1_1 + 3 * i</c> (Player.cpp:90-98):
/// word 0 = skill id (low 16 bits) and step (high), word 1 = value (low) and maximum (high), word 2 =
/// temporary bonus (low, signed) and permanent bonus (high, signed). A skill-id to slot map carries the save
/// state. The spellbook is reached through <see cref="ISkillSpellHost"/>.
/// </summary>
public sealed partial class PlayerSkills
{
    /// <summary>vmangos PLAYER_MAX_SKILLS (Player.h:69): the client's slot limit.</summary>
    public const int MaxSkills = 127;

    private sealed class Slot(byte pos, SkillState state)
    {
        public byte Pos { get; set; } = pos;

        public SkillState State { get; set; } = state;
    }

    private readonly Player _player;
    private readonly SkillCatalog _catalog;
    private readonly ISkillSpellHost _spells;
    private readonly ICombatRandom _random;
    private readonly Dictionary<uint, Slot> _slots = [];
    private readonly Dictionary<ushort, ushort> _forgotten = [];
    private bool _forgottenChanged;
    private bool _loaded;

    public PlayerSkills(Player player, SkillCatalog catalog, SkillOptions options, ISkillSpellHost spells, ICombatRandom? random = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(spells);
        _player = player;
        _catalog = catalog;
        Options = options.Validate();
        _spells = spells;
        _random = random ?? SharedCombatRandom.Instance;
    }

    public SkillOptions Options { get; }

    public SkillCatalog Catalog => _catalog;

    /// <summary>A skill gained its first value (after its bonus word was reset): the owner of MOD_SKILL auras re-applies them here (Player.cpp:5629-5645).</summary>
    public event Action<uint>? SkillAdded;

    /// <summary>A skill is about to be removed, its fields still set: MOD_SKILL auras unapply their bonuses here (Player.cpp:5547-5560).</summary>
    public event Action<uint>? SkillRemoving;

    /// <summary>A skill was removed: quests that require it leave the log here (Player.cpp:5575-5599).</summary>
    public event Action<uint>? SkillRemoved;

    /// <summary>The value, maximum, step or bonus of a skill changed (derived stats such as crit and defence recompute from it).</summary>
    public event Action<uint>? SkillChanged;

    /// <summary>vmangos Player::HasSpell, answered by the spellbook owner (the equip gates ask through <see cref="PlayerItemRequirements"/>).</summary>
    public bool HasSpell(uint spellId) => _spells.HasSpell(spellId);

    /// <summary>vmangos Player::HasSkill (Player.cpp:5658-5666).</summary>
    public bool Has(uint skillId) => TryGetSlot(skillId, out _);

    /// <summary>vmangos GetSkill (Player.cpp:5668-5701): the pure value (or maximum) plus the requested bonuses, never below zero.</summary>
    public ushort Get(uint skillId, bool bonusPerm, bool bonusTemp, bool max = false)
    {
        if (!TryGetSlot(skillId, out Slot? slot))
        {
            return 0;
        }

        uint field = _player.GetUInt32(ValueIndex(slot.Pos));
        int value = max ? (int)(field >> 16) : (int)(field & 0xFFFF);
        if (bonusPerm || bonusTemp)
        {
            uint bonus = _player.GetUInt32(BonusIndex(slot.Pos));
            if (bonusPerm)
            {
                value += unchecked((short)(bonus >> 16));
            }

            if (bonusTemp)
            {
                value += unchecked((short)(bonus & 0xFFFF));
            }
        }

        return (ushort)Math.Max(0, value);
    }

    /// <summary>Skill value plus permanent and temporary bonus (Player.h:1568 GetSkillValue).</summary>
    public ushort GetValue(uint skillId) => Get(skillId, true, true);

    /// <summary>Skill value plus permanent bonus (Player.h:1569 GetSkillValueBase).</summary>
    public ushort GetValueBase(uint skillId) => Get(skillId, true, false);

    /// <summary>The skill value alone (Player.h:1570 GetSkillValuePure).</summary>
    public ushort GetValuePure(uint skillId) => Get(skillId, false, false);

    /// <summary>Skill maximum plus both bonuses (Player.h:1571 GetSkillMax).</summary>
    public ushort GetMax(uint skillId) => Get(skillId, true, true, true);

    /// <summary>The skill maximum alone (Player.h:1572 GetSkillMaxPure).</summary>
    public ushort GetMaxPure(uint skillId) => Get(skillId, false, false, true);

    /// <summary>The recorded step (rank) of a skill: the high half of word 0, 0 when the skill is unknown.</summary>
    public ushort GetStep(uint skillId)
        => TryGetSlot(skillId, out Slot? slot) ? (ushort)(_player.GetUInt32(InfoIndex(slot.Pos)) >> 16) : (ushort)0;

    /// <summary>vmangos GetSkillBonus (Player.cpp:5743-5760).</summary>
    public short GetBonus(uint skillId, bool permanent = false)
    {
        if (!TryGetSlot(skillId, out Slot? slot))
        {
            return 0;
        }

        uint bonus = _player.GetUInt32(BonusIndex(slot.Pos));
        return unchecked((short)(permanent ? bonus >> 16 : bonus & 0xFFFF));
    }

    /// <summary>
    /// vmangos ModifySkillBonus (Player.cpp:5719-5742): add <paramref name="diff"/> to the permanent (high half) or
    /// temporary/item (low half) bonus. False for an unknown skill or a zero difference.
    /// </summary>
    public bool ModifyBonus(uint skillId, short diff, bool permanent = false)
    {
        if (skillId == 0 || diff == 0 || !TryGetSlot(skillId, out Slot? slot))
        {
            return false;
        }

        uint bonus = _player.GetUInt32(BonusIndex(slot.Pos));
        short temporary = unchecked((short)(bonus & 0xFFFF));
        short permanentBonus = unchecked((short)(bonus >> 16));
        if (permanent)
        {
            permanentBonus = unchecked((short)(permanentBonus + diff));
        }
        else
        {
            temporary = unchecked((short)(temporary + diff));
        }

        _player.SetUInt32(BonusIndex(slot.Pos), Pair(unchecked((ushort)temporary), unchecked((ushort)permanentBonus)));
        SkillChanged?.Invoke(skillId);
        return true;
    }

    /// <summary>
    /// vmangos SetSkill (Player.cpp:5504-5648): set a skill line's value and maximum, adding it in the first free
    /// slot when new, and remove it when <paramref name="currentValue"/> is zero. A non-zero
    /// <paramref name="step"/> replaces the stored step. Returns false when nothing could be applied (id 0, an
    /// unknown skill line, or no free slot).
    /// </summary>
    public bool Set(uint skillId, ushort currentValue, ushort maxValue, ushort step = 0)
    {
        if (skillId == 0 || skillId > ushort.MaxValue)
        {
            return false;
        }

        if (TryGetSlot(skillId, out Slot? slot))
        {
            if (currentValue != 0)
            {
                if (step != 0)
                {
                    _player.SetUInt32(InfoIndex(slot.Pos), Pair((ushort)skillId, step));
                }

                _player.SetUInt32(ValueIndex(slot.Pos), Pair(currentValue, maxValue));
                MarkChanged(slot);
                SkillChanged?.Invoke(skillId);
                UpdateSkillTrainedSpells((ushort)skillId, currentValue);
                return true;
            }

            Remove(skillId, slot);
            return true;
        }

        if (currentValue == 0)
        {
            return false;
        }

        for (int i = 0; i < MaxSkills; i++)
        {
            if (_player.GetUInt32(InfoIndex(i)) != 0)
            {
                continue;
            }

            if (_catalog.Line(skillId) is null)
            {
                return false;
            }

            _player.SetUInt32(InfoIndex(i), Pair((ushort)skillId, step));
            _player.SetUInt32(ValueIndex(i), Pair(currentValue, maxValue));

            // A deleted-but-unsaved entry is revived in place (Player.cpp:5618-5626).
            if (_slots.TryGetValue(skillId, out Slot? deleted))
            {
                deleted.Pos = (byte)i;
                deleted.State = SkillState.Changed;
            }
            else
            {
                _slots[skillId] = new Slot((byte)i, SkillState.New);
            }

            _player.SetUInt32(BonusIndex(i), 0);
            SkillAdded?.Invoke(skillId);
            SkillChanged?.Invoke(skillId);
            UpdateSkillTrainedSpells((ushort)skillId, currentValue);
            return true;
        }

        return false;
    }

    private void Remove(uint skillId, Slot slot)
    {
        SkillRemoving?.Invoke(skillId);

        _player.SetUInt32(InfoIndex(slot.Pos), 0);
        _player.SetUInt32(ValueIndex(slot.Pos), 0);
        _player.SetUInt32(BonusIndex(slot.Pos), 0);

        // Never saved: forget it; saved: keep a tombstone so the next snapshot deletes the row (Player.cpp:5563-5569).
        if (slot.State != SkillState.New)
        {
            slot.State = SkillState.Deleted;
        }
        else
        {
            _slots.Remove(skillId);
        }

        UpdateSkillTrainedSpells((ushort)skillId, 0);
        SkillRemoved?.Invoke(skillId);
        SkillChanged?.Invoke(skillId);
    }

    private bool TryGetSlot(uint skillId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Slot? slot)
    {
        slot = null;
        return skillId != 0 && _slots.TryGetValue(skillId, out slot) && slot.State != SkillState.Deleted;
    }

    private static void MarkChanged(Slot slot)
    {
        if (slot.State != SkillState.New)
        {
            slot.State = SkillState.Changed;
        }
    }

    private static int InfoIndex(int pos) => UpdateFields.PlayerSkillInfo11 + (pos * 3);

    private static int ValueIndex(int pos) => InfoIndex(pos) + 1;

    private static int BonusIndex(int pos) => InfoIndex(pos) + 2;

    private static uint Pair(ushort low, ushort high) => low | ((uint)high << 16);
}
