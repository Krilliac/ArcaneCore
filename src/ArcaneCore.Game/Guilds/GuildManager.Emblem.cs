using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Guilds;

public sealed partial class GuildManager
{
    /// <summary>The emblem design costs 10 gold (GuildHandler.cpp:712-719, "10 * GOLD").</summary>
    public const uint EmblemCost = 100_000;

    /// <summary>The NPC and money services (set by the world daemon); without them no tabard designer is reachable.</summary>
    public QuestNpcServices? Npc { get; set; }

    /// <summary>
    /// MSG_SAVE_GUILD_EMBLEM (vmangos HandleSaveGuildEmblemOpcode, GuildHandler.cpp:684-735), in its order: a
    /// reachable tabard designer (else result 5, which the client prints as nothing), a guild (2), the leader (3),
    /// 10 gold (4); then the charge, the five raw values, result 0 and SMSG_GUILD_QUERY_RESPONSE to the leader only.
    /// Like vmangos and cmangos there is no range validation, no GE_TABARDCHANGE broadcast and result 1 is never sent.
    /// </summary>
    public void SaveEmblem(Player player, ObjectGuid vendor, int emblemStyle, int emblemColor, int borderStyle, int borderColor, int backgroundColor)
    {
        if (Npc?.InteractableNpc(player, vendor, NpcFlags.TabardDesigner) is null)
        {
            SendEmblemResult(player, GuildEmblemResult.NoMessage);
            return;
        }

        if (GetGuildOf(player) is not { } guild)
        {
            SendEmblemResult(player, GuildEmblemResult.NoGuild);
            return;
        }

        if (guild.LeaderId != player.Guid.Low)
        {
            SendEmblemResult(player, GuildEmblemResult.NotGuildMaster);
            return;
        }

        if (player.Money < EmblemCost || !Npc.TryCharge(player, EmblemCost))
        {
            SendEmblemResult(player, GuildEmblemResult.NotEnoughMoney);
            return;
        }

        // Guild::SetEmblem (Guild.cpp:883-892).
        guild.EmblemStyle = emblemStyle;
        guild.EmblemColor = emblemColor;
        guild.BorderStyle = borderStyle;
        guild.BorderColor = borderColor;
        guild.BackgroundColor = backgroundColor;
        Save(guild);

        SendEmblemResult(player, GuildEmblemResult.Success);
        player.Session.Send(WorldOpcode.SmsgGuildQueryResponse, GuildPackets.BuildQueryResponse(guild));
    }

    /// <summary>
    /// MSG_TABARDVENDOR_ACTIVATE from the client and the TabardDesigner gossip option (vmangos
    /// HandleTabardVendorActivateOpcode, NPCHandler.cpp:49-69; Player::OnGossipSelect, Player.cpp:12268-12271): a
    /// reachable tabard designer is answered with its own guid and the client opens the designer.
    /// </summary>
    public void ActivateTabardVendor(Player player, ObjectGuid npc)
    {
        if (Npc?.InteractableNpc(player, npc, NpcFlags.TabardDesigner) is null)
        {
            return;
        }

        player.Session.Send(WorldOpcode.MsgTabardvendorActivate, GuildEmblemPackets.BuildTabardVendorActivate(npc));
    }

    private static void SendEmblemResult(Player player, GuildEmblemResult result)
        => player.Session.Send(WorldOpcode.MsgSaveGuildEmblem, GuildEmblemPackets.BuildSaveResult(result));
}
