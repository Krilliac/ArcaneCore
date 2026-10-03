using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;

namespace ArcaneCore.World.Handlers;

/// <summary>
/// The packets a client gets when a character enters the world, in vmangos' three stages so a
/// far teleport can repeat the last two (vmangos HandleMoveWorldportAckOpcode calls
/// <c>SendInitialPacketsBeforeAddToMap</c>, adds the player to the new map, then calls
/// <c>SendInitialPacketsAfterAddToMap</c>). World thread.
/// </summary>
public static class LoginSequence
{
    /// <summary>
    /// Login only (vmangos WorldSession::HandlePlayerLogin, before
    /// SendInitialPacketsBeforeAddToMap): verify world, account data hashes, friend and ignore
    /// lists, MOTD lines.
    /// </summary>
    public static void SendLoginPackets(WorldSession session, WorldRuntime world, CharacterRecord character, byte[] accountDataMd5)
    {
        session.Send(WorldOpcode.SmsgLoginVerifyWorld, CharacterPackets.BuildLoginVerifyWorld(character));
        session.Send(WorldOpcode.SmsgAccountDataMd5, accountDataMd5);
        session.Send(WorldOpcode.SmsgFriendList, LoginPackets.BuildEmptyFriendList());
        session.Send(WorldOpcode.SmsgIgnoreList, LoginPackets.BuildEmptyIgnoreList());
        foreach (string line in world.Options.Motd.Split('@', StringSplitOptions.RemoveEmptyEntries))
        {
            session.Send(WorldOpcode.SmsgMessagechat, ChatPackets.BuildSystemMessage(line));
        }
    }

    /// <summary>
    /// vmangos Player::SendInitialPacketsBeforeAddToMap: rest start, bind point, tutorials,
    /// spells, action bar, reputations, time speed.
    /// </summary>
    public static void SendInitialPacketsBeforeAddToMap(WorldSession session, Player player, byte[] tutorialFlags)
    {
        session.Send(WorldOpcode.SmsgSetRestStart, LoginPackets.BuildSetRestStart());
        session.Send(WorldOpcode.SmsgBindpointupdate, LoginPackets.BuildBindPointUpdate(player.Home));
        session.Send(WorldOpcode.SmsgTutorialFlags, tutorialFlags);
        session.Send(WorldOpcode.SmsgInitialSpells, CharacterPackets.BuildInitialSpells());
        session.Send(WorldOpcode.SmsgActionButtons, LoginPackets.BuildActionButtons(player.ActionButtons));
        session.Send(WorldOpcode.SmsgInitializeFactions, LoginPackets.BuildInitializeFactions());
        session.Send(WorldOpcode.SmsgLoginSettimespeed, CharacterPackets.BuildTimeSpeed(DateTime.UtcNow));
    }

    /// <summary>
    /// vmangos Player::SendInitialPacketsAfterAddToMap: the zone update, which sends the zone's
    /// world states (the map add itself has already sent the self create).
    /// </summary>
    public static void SendInitialPacketsAfterAddToMap(WorldSession session, Player player)
        => session.Send(WorldOpcode.SmsgInitWorldStates, LoginPackets.BuildInitWorldStates(player.MapId, player.ZoneId));
}
