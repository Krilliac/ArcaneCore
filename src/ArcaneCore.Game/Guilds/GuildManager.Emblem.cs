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

        if (Link is { IsHolder: false } link)
        {
            // The designer and the money are this worker's; the guild is the holder's. The holder re-checks the leader and answers the player itself
            // (result and query response); the 10 gold are taken here once it has stored the emblem.
            if (player.Money < EmblemCost)
            {
                SendEmblemResult(player, GuildEmblemResult.NotEnoughMoney);
                return;
            }

            QuestNpcServices npc = Npc;
            var request = new GuildRequest(GuildRequestKind.SetEmblem, player.Guid, Values: [emblemStyle, emblemColor, borderStyle, borderColor, backgroundColor]);
            if (!link.Forward(request, saved =>
            {
                if (saved)
                {
                    npc.TryCharge(player, EmblemCost);
                }
            }))
            {
                SendEmblemResult(player, GuildEmblemResult.NoMessage);
            }

            return;
        }

        if (player.Money < EmblemCost || !Npc.TryCharge(player, EmblemCost))
        {
            SendEmblemResult(player, GuildEmblemResult.NotEnoughMoney);
            return;
        }

        ApplyEmblem(guild, emblemStyle, emblemColor, borderStyle, borderColor, backgroundColor);
        SendEmblemResult(GuildActor.Of(player), GuildEmblemResult.Success);
        player.Session.Send(WorldOpcode.SmsgGuildQueryResponse, GuildPackets.BuildQueryResponse(guild));
    }

    /// <summary>
    /// The holder's half of <see cref="SaveEmblem"/> for a leader on another worker: the guild (2) and leader (3) checks again against the authoritative state, then the
    /// five values, result 0 and the query response to the leader. True when the emblem was stored.
    /// </summary>
    private bool ServeEmblem(GuildActor player, IReadOnlyList<int>? values)
    {
        if (values is not { Count: 5 })
        {
            return false;
        }

        if (GetGuildOf(player) is not { } guild)
        {
            SendEmblemResult(player, GuildEmblemResult.NoGuild);
            return false;
        }

        if (guild.LeaderId != player.Guid.Low)
        {
            SendEmblemResult(player, GuildEmblemResult.NotGuildMaster);
            return false;
        }

        ApplyEmblem(guild, values[0], values[1], values[2], values[3], values[4]);
        SendEmblemResult(player, GuildEmblemResult.Success);
        player.Send(WorldOpcode.SmsgGuildQueryResponse, GuildPackets.BuildQueryResponse(guild));
        return true;
    }

    /// <summary>Guild::SetEmblem (Guild.cpp:883-892).</summary>
    private void ApplyEmblem(Guild guild, int emblemStyle, int emblemColor, int borderStyle, int borderColor, int backgroundColor)
    {
        guild.EmblemStyle = emblemStyle;
        guild.EmblemColor = emblemColor;
        guild.BorderStyle = borderStyle;
        guild.BorderColor = borderColor;
        guild.BackgroundColor = backgroundColor;
        Save(guild);
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

        Npc.InteractingCancels?.Invoke(player); // vmangos HandleTabardVendorActivateOpcode, NPCHandler.cpp:58-59
        player.Session.Send(WorldOpcode.MsgTabardvendorActivate, GuildEmblemPackets.BuildTabardVendorActivate(npc));
    }

    private static void SendEmblemResult(Player player, GuildEmblemResult result)
        => player.Session.Send(WorldOpcode.MsgSaveGuildEmblem, GuildEmblemPackets.BuildSaveResult(result));

    private static void SendEmblemResult(GuildActor player, GuildEmblemResult result)
        => player.Send(WorldOpcode.MsgSaveGuildEmblem, GuildEmblemPackets.BuildSaveResult(result));
}
