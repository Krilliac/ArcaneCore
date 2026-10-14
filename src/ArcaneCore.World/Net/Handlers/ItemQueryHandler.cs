using ArcaneCore.Game.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Items;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Net.Handlers;

/// <summary>
/// Multi-item query handler (<c>CMSG_ITEM_QUERY_MULTIPLE</c>, 0x0057; vmangos ItemHandler.cpp).
/// </summary>
public sealed class ItemQueryHandler : IOpcodeHandlerGroup
{
    private const uint MaxBatchCount = 64;

    public void Register(OpcodeTable table)
    {
        table.OnSession(WorldOpcode.CmsgItemQueryMultiple, SessionStates.LoggedIn, HandleItemQueryMultipleAsync);
    }

    private static async Task HandleItemQueryMultipleAsync(WorldSession session, byte[] payload)
    {
        if (payload.Length < 4)
        {
            return;
        }

        var reader = new PacketReader(payload);
        uint count = Math.Min(reader.ReadUInt32(), MaxBatchCount);
        uint available = (uint)(reader.Remaining / 4);
        if (count > available)
        {
            count = available;
        }

        var entries = new uint[count];
        for (uint i = 0; i < count; i++)
        {
            entries[i] = reader.ReadUInt32();
        }

        ItemsFeature items = session.Services.GetRequiredService<ItemsFeature>();
        IItemTemplateStore templates = await items.EnsureLoadedAsync().ConfigureAwait(false);
        foreach (uint entry in entries)
        {
            session.Send(WorldOpcode.SmsgItemQuerySingleResponse,
                items.QueryCache.Get(entry, templates.Find(entry), static (id, template) => template is null
                    ? ItemPackets.ItemQueryUnknown(id)
                    : ItemPackets.ItemQueryResponse(template)));
        }
    }
}
