using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// Objects used by scripts rather than by a player's CMSG_GAMEOBJ_USE: the relay command ACTIVATE_OBJECT (13) uses an object for any unit,
/// or plays a custom animation on it (cmangos ScriptMgr.cpp:2115-2128; GameObject::Use(Unit*), Entities/GameObject.cpp:1461-1660).
/// </summary>
public sealed partial class GameObjectMapSystem
{
    /// <summary>
    /// GameObject::Use by a unit that need not be a player, for the types whose behaviour does not need one: a door toggles, a button toggles
    /// and fires its linked trap, a trap casts its spell at the user (its cooldown and charges counted), a spell focus fires its linked trap.
    /// None of these check a lock, as GameObject::Use itself does not. Other types are <see cref="GameObjectUseResult.Unsupported"/> (the
    /// player-only behaviours: loot, gossip, quests, chairs). The object must be spawned in this map.
    /// </summary>
    public GameObjectUseResult UseByUnit(Unit user, GameObject go)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(go);
        if (!go.IsSpawned || !Tracks(go))
        {
            return GameObjectUseResult.NotFound;
        }

        switch (go.Type)
        {
            case GameObjectType.Door:
                return ActivateDoorOrButton(go, go.Template.AutoCloseSeconds());
            case GameObjectType.Button:
            {
                GameObjectUseResult result = ActivateDoorOrButton(go, go.Template.AutoCloseSeconds());
                TriggerLinkedTrap(go, user);
                return result;
            }

            case GameObjectType.Trap:
                UseTrap(go, user);
                return GameObjectUseResult.Ok;
            case GameObjectType.SpellFocus:
                TriggerLinkedTrap(go, user);
                return GameObjectUseResult.Ok;
            default:
                return GameObjectUseResult.Unsupported;
        }
    }

    /// <summary>SMSG_GAMEOBJECT_CUSTOM_ANIM with <paramref name="animId"/> to everyone who sees the object (cmangos SendGameObjectCustomAnim).</summary>
    public void SendCustomAnim(GameObject go, uint animId)
    {
        ArgumentNullException.ThrowIfNull(go);
        if (go.IsSpawned && Tracks(go))
        {
            Map.BroadcastToObservers(go, WorldOpcode.SmsgGameobjectCustomAnim, GameObjectPackets.CustomAnim(go.Guid, animId));
        }
    }
}
