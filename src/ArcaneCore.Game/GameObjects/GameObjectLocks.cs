using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.WorldData.GameObjects;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// Lock.dbc evaluation (behaviour re-implemented from vmangos Spell::CanOpenLock and
/// GameObject::Use; no code copied). Each lock has up to eight cases; any satisfied case opens it.
/// </summary>
public static class GameObjectLocks
{
    /// <summary>The Lock.dbc id a template uses, by type (vmangos GameObjectInfo::GetLockId).</summary>
    public static uint LockIdOf(GameObjectTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return (GameObjectType)template.Type switch
        {
            GameObjectType.Door or GameObjectType.Button => template.GetData(1),
            GameObjectType.QuestGiver or GameObjectType.Chest or GameObjectType.Trap or GameObjectType.Goober
                or GameObjectType.AreaDamage or GameObjectType.Camera or GameObjectType.FlagStand => template.GetData(0),
            GameObjectType.FishingHole => template.GetData(4),
            _ => 0,
        };
    }

    /// <summary>
    /// A direct use (CMSG_GAMEOBJ_USE, no spell): satisfied by a key item in the bags or by a
    /// skill case that needs no profession (Open, Treasure, Quick open …). A profession lock
    /// (herbalism, mining, lockpicking, fishing) needs the open-lock spell:
    /// <see cref="GameObjectUseResult.Locked"/>; a missing key: <see cref="GameObjectUseResult.MissingKey"/>.
    /// Blasting has no profession either, but it is still a skill case (Seaforium charges,
    /// <c>Spell::CanOpenLock</c>): a click does not satisfy it.
    /// </summary>
    public static GameObjectUseResult CheckDirectUse(LockEntry? entry, Player player) => CheckDirectUse(entry, player, out _);

    /// <summary>
    /// <see cref="CheckDirectUse(LockEntry?, Player)"/>, naming the key that opened the lock: the first item in the bags (the bank does not
    /// count, vmangos Player::HasItemCount) of the first item case that matched, or null when no key case was the one satisfied.
    /// </summary>
    public static GameObjectUseResult CheckDirectUse(LockEntry? entry, Player player, out Item? key)
    {
        ArgumentNullException.ThrowIfNull(player);
        key = null;
        if (entry is null)
        {
            return GameObjectUseResult.Ok;
        }

        bool needsKey = false;
        bool needsProfession = false;
        for (int i = 0; i < LockEntry.Cases && i < entry.Types.Count; i++)
        {
            switch ((LockKeyType)entry.Types[i])
            {
                case LockKeyType.Item when entry.Indexes[i] != 0:
                    if (player.Inventory.GetItemCount(entry.Indexes[i]) > 0)
                    {
                        uint keyEntry = entry.Indexes[i];
                        key = player.Inventory.AllItems.FirstOrDefault(item => item.Entry == keyEntry && !PlayerInventory.IsInBank(item));
                        return GameObjectUseResult.Ok;
                    }

                    needsKey = true;
                    break;

                case LockKeyType.Skill:
                    // LOCKTYPE_BLASTING has no profession (SkillByLockType is SKILL_NONE) but CanOpenLock still
                    // requires the spell bonus. Treating it as a free click opens a Seaforium lock with no charge.
                    if (entry.Indexes[i] == (uint)LockType.Blasting)
                    {
                        needsProfession = true;
                        break;
                    }

                    if (LockSkills.ForLockType((LockType)entry.Indexes[i]) == 0)
                    {
                        return GameObjectUseResult.Ok;
                    }

                    needsProfession = true;
                    break;
            }
        }

        return needsProfession ? GameObjectUseResult.Locked
            : needsKey ? GameObjectUseResult.MissingKey
            : GameObjectUseResult.Ok;
    }

    /// <summary>
    /// The open-lock spell effect (vmangos Spell::CanOpenLock): a case of the spell's
    /// <paramref name="lockType"/> opens when the player's skill reaches the case's required
    /// value (professionless lock types always open); a key case opens with
    /// <paramref name="keyItemId"/>. Nothing matching: <see cref="GameObjectUseResult.Locked"/>.
    /// The first skill case of <paramref name="lockType"/> decides, including a failure
    /// (a later easier case is not tried). Blasting has no profession but still compares the
    /// spell bonus with the case's required skill.
    /// </summary>
    public static GameObjectUseResult CheckOpenLock(LockEntry? entry, Player player, LockType lockType, uint keyItemId, Func<Player, uint, uint> skillValue, uint skillBonus = 0)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(skillValue);
        if (entry is null)
        {
            return GameObjectUseResult.Ok;
        }

        for (int i = 0; i < LockEntry.Cases && i < entry.Types.Count; i++)
        {
            switch ((LockKeyType)entry.Types[i])
            {
                case LockKeyType.Item when keyItemId != 0 && entry.Indexes[i] == keyItemId:
                    return GameObjectUseResult.Ok;

                case LockKeyType.Skill when entry.Indexes[i] == (uint)lockType:
                    // Spell::CanOpenLock: the first skill case of this lock type decides. Blasting is
                    // skill-less but still checked (Seaforium); every other skill-less type opens.
                    uint skill = LockSkills.ForLockType(lockType);
                    if (skill != 0 || lockType == LockType.Blasting)
                    {
                        // CanOpenLock reads the skill only when the lock type has one (reqSkill ? GetSkillValue : 0).
                        uint have = skill == 0 ? 0 : skillValue(player, skill);
                        if (have + skillBonus < entry.Skills[i])
                        {
                            return GameObjectUseResult.SkillTooLow;
                        }
                    }

                    return GameObjectUseResult.Ok;
            }
        }

        return GameObjectUseResult.Locked;
    }
}
