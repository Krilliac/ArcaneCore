using ArcaneCore.Game.Entities;
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
    /// </summary>
    public static GameObjectUseResult CheckDirectUse(LockEntry? entry, Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
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
                        return GameObjectUseResult.Ok;
                    }

                    needsKey = true;
                    break;

                case LockKeyType.Skill:
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
    /// </summary>
    public static GameObjectUseResult CheckOpenLock(LockEntry? entry, Player player, LockType lockType, uint keyItemId, Func<Player, uint, uint> skillValue)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(skillValue);
        if (entry is null)
        {
            return GameObjectUseResult.Ok;
        }

        GameObjectUseResult result = GameObjectUseResult.Locked;
        for (int i = 0; i < LockEntry.Cases && i < entry.Types.Count; i++)
        {
            switch ((LockKeyType)entry.Types[i])
            {
                case LockKeyType.Item when keyItemId != 0 && entry.Indexes[i] == keyItemId:
                    return GameObjectUseResult.Ok;

                case LockKeyType.Skill when entry.Indexes[i] == (uint)lockType:
                    uint skill = LockSkills.ForLockType(lockType);
                    if (skill == 0 || skillValue(player, skill) >= entry.Skills[i])
                    {
                        return GameObjectUseResult.Ok;
                    }

                    result = GameObjectUseResult.SkillTooLow;
                    break;
            }
        }

        return result;
    }
}
