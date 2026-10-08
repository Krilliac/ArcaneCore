using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Social;

/// <summary>
/// Battleground chat (vmangos HandleChatMessageOpcode, ChatHandler.cpp:579-615): CHAT_MSG_BATTLEGROUND goes to the speaker's
/// battleground raid group, CHAT_MSG_BATTLEGROUND_LEADER too but only from its leader; outside a battleground both are
/// dropped. The group is the speaker's team in its match (<see cref="IBattlegroundChatRoster"/>; a registered roster, else
/// one over a registered <see cref="BattlegroundManager"/>). The message keeps its language, as vmangos builds it with
/// <c>packet.lang</c> (the GM and two-side conversions of the chat handler have already run); every online member hears it,
/// the speaker included (<c>BroadcastPacket(&amp;data, false)</c>). Without a roster nothing is in a battleground and the
/// messages are dropped, as before.
/// </summary>
public sealed class BattlegroundChatFeature(IServiceProvider services) : IWorldFeature, IChatMessageHandler
{
    private IBattlegroundChatRoster? _roster;

    public void Attach(WorldRuntime world)
    {
        _roster = services.GetService<IBattlegroundChatRoster>()
            ?? (services.GetService<BattlegroundManager>() is { } manager ? new BattlegroundManagerChatRoster(manager) : null);
    }

    public bool TryHandle(WorldSession session, Player player, ClientChatMessage message)
    {
        if (message.Type is not (ChatType.Battleground or ChatType.BattlegroundLeader))
        {
            return false;
        }

        // Consumed either way: outside a battleground (or from a non-leader on the leader channel) vmangos just returns.
        if (_roster?.TeamOf(player.Guid) is not { } team
            || (message.Type == ChatType.BattlegroundLeader && team.Leader != player.Guid))
        {
            return true;
        }

        byte[] packet = ChatPackets.BuildMessage(message.Type, message.Language, player.Guid, message.Text, player.ChatTag);
        foreach (ObjectGuid member in team.Members)
        {
            session.World.FindOnlinePlayer(member)?.Session.Send(WorldOpcode.SmsgMessagechat, packet);
        }

        return true;
    }
}
