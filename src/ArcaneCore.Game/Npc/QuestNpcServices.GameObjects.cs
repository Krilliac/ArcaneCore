using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// Quest-giving game objects (GAMEOBJECT_TYPE_QUESTGIVER). The creature and game object relation tables
/// have separate entry spaces, so the starters and enders of a source depend on what it is (vmangos
/// GetCreatureQuestRelationsMapBounds / GetGOQuestRelationsMapBounds, Player::PrepareQuestMenu
/// Player.cpp:12349-12418).
/// </summary>
public sealed partial class QuestNpcServices
{
    /// <summary>The quests <paramref name="source"/> starts: its game object relations when it is a game object.</summary>
    private IReadOnlyList<uint> StartersOf(NpcInfo source)
        => source.IsGameObject ? Quests.GameObjectStartersOf(source.Entry) : Quests.StartersOf(source.Entry);

    /// <summary>The quests <paramref name="source"/> ends.</summary>
    private IReadOnlyList<uint> EndersOf(NpcInfo source)
        => source.IsGameObject ? Quests.GameObjectEndersOf(source.Entry) : Quests.EndersOf(source.Entry);

    /// <summary>
    /// CMSG_GAMEOBJ_USE on a quest giver (vmangos GameObject::Use, GameObject.cpp:1457-1471):
    /// PrepareGossipMenu(go, questgiver.gossipID) and SendPreparedGossip. Without a menu the quest list (or a
    /// single quest's window) opens; an object with nothing to say stays silent. False when the object is not
    /// an interactable quest giver for this player.
    /// </summary>
    public bool OpenGameObjectQuestMenu(Player player, ObjectGuid guid)
    {
        if (Ready(player) is not { } state || InteractableNpc(player, guid, NpcFlags.QuestGiver) is not { IsGameObject: true } source)
        {
            return false;
        }

        PrepareGossipMenu(state, source, source.GossipMenuId);
        SendPreparedGossip(state, source);
        Flush(state);
        return true;
    }

    /// <summary>
    /// The gossip of a goober without page text (vmangos GameObject::Use, GameObject.cpp:1555-1562): PrepareGossipMenu(go, goober.gossipID) and
    /// SendPreparedGossip, the menu being the object's default one. A game object shows only plain gossip lines (no quests: it has no quest-giver
    /// flag). False when the object is not interactable for this player or nothing was prepared.
    /// </summary>
    public bool OpenGameObjectGossip(Player player, ObjectGuid guid, uint menuId)
    {
        if (menuId == 0 || Ready(player) is not { } state || InteractableNpc(player, guid, NpcFlags.None) is not { IsGameObject: true } found)
        {
            return false;
        }

        NpcInfo source = found with { GossipMenuId = menuId };
        PrepareGossipMenu(state, source, menuId);
        SendPreparedGossip(state, source);
        Flush(state);
        return true;
    }
}
