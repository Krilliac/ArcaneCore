using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Packets;

namespace ArcaneCore.World.Gm.Core;

/// <summary>Replies to a player other than the invoker (vmangos <c>target-&gt;PSendSysMessage</c>).</summary>
public static class GmReplies
{
    /// <summary>Send CHAT_MSG_SYSTEM lines to <paramref name="player"/> ('\n' separates lines).</summary>
    public static void SendSystemMessage(this Player player, string text)
    {
        foreach (string line in text.Split('\n'))
        {
            player.Session.Send(WorldOpcode.SmsgMessagechat, ChatPackets.BuildSystemMessage(line));
        }
    }
}
