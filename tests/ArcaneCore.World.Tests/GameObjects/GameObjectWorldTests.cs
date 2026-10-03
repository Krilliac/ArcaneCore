using ArcaneCore.Game;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.GameObjects;

/// <summary>
/// Game objects and loot end to end over loopback: content from <see cref="IGameObjectDataStore"/>
/// and <see cref="ILootDataStore"/> at host start, the create block of a chest near the player,
/// CMSG_GAMEOBJECT_QUERY, and opening, looting and releasing the chest.
/// </summary>
public sealed class GameObjectWorldTests
{
    private const uint ChestEntry = 2843;
    private const uint ChestSpawn = 77001;
    private const uint ChestLootId = 2843;
    private const uint LinenCloth = 2589;

    [Fact]
    public async Task GameObjectQuery_ReturnsTheTemplate_OrTheUnknownMarker()
    {
        await using WorldTestHost host = StartWithChest(out _);
        await using WorldTestClient client = await host.EnterWorldAsync("GOWQUERY", "Goquery");

        var query = new PacketWriter(12);
        query.WriteUInt32(ChestEntry);
        query.WriteUInt64(ChestGuid());
        await client.SendAsync(WorldOpcode.CmsgGameobjectQuery, query.ToArray());
        var reader = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgGameobjectQueryResponse));
        Assert.Equal(ChestEntry, reader.ReadUInt32());
        Assert.Equal((uint)GameObjectType.Chest, reader.ReadUInt32());
        Assert.Equal(10u, reader.ReadUInt32());
        Assert.Equal("Battered Chest", reader.ReadCString());
        reader.Skip(4);                              // three empty names and the cast bar caption
        Assert.Equal(0u, reader.ReadUInt32());       // data0: no lock
        Assert.Equal(ChestLootId, reader.ReadUInt32()); // data1: loot id
        reader.Skip(22 * 4);
        Assert.Equal(0, reader.Remaining);

        var unknown = new PacketWriter(12);
        unknown.WriteUInt32(424242);
        unknown.WriteUInt64(0);
        await client.SendAsync(WorldOpcode.CmsgGameobjectQuery, unknown.ToArray());
        Assert.Equal([0x32, 0x79, 0x06, 0x80], await client.ReadUntilAsync(WorldOpcode.SmsgGameobjectQueryResponse));

        // A short payload is ignored; the session keeps working.
        await client.SendAsync(WorldOpcode.CmsgGameobjectQuery, [0x01]);
        await client.SendAsync(WorldOpcode.CmsgGameobjectQuery, query.ToArray());
        Assert.Equal(ChestEntry, new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgGameobjectQueryResponse)).ReadUInt32());
    }

    [Fact]
    public async Task Chest_IsCreatedNearThePlayer_AndLootGoesIntoTheBags()
    {
        await using WorldTestHost host = StartWithChest(out GameObjectTestContext context);
        await using WorldTestClient client = await host.EnterWorldAsync("GOWLOOT", "Golooter");
        ulong chest = ChestGuid();
        await ReadUntilGameObjectCreateAsync(client, chest);

        // CMSG_GAMEOBJ_USE on the chest → SMSG_LOOT_RESPONSE with the two linen cloth.
        await client.SendAsync(WorldOpcode.CmsgGameobjUse, BitConverter.GetBytes(chest));
        var loot = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgLootResponse));
        Assert.Equal(chest, loot.ReadUInt64());
        Assert.Equal((byte)1, loot.ReadByte());      // LOOT_CORPSE
        Assert.Equal(0u, loot.ReadUInt32());         // no gold
        Assert.Equal((byte)1, loot.ReadByte());
        Assert.Equal((byte)0, loot.ReadByte());      // slot
        Assert.Equal(LinenCloth, loot.ReadUInt32());
        Assert.Equal(2u, loot.ReadUInt32());
        Assert.Equal(3776u, loot.ReadUInt32());      // display
        loot.Skip(8);
        Assert.Equal((byte)0, loot.ReadByte());      // LOOT_SLOT_NORMAL
        Assert.Equal(0, loot.Remaining);

        // Take it: SMSG_LOOT_REMOVED, then the cloth is in the backpack.
        await client.SendAsync(WorldOpcode.CmsgAutostoreLootItem, [0]);
        Assert.Equal([0], await client.ReadUntilAsync(WorldOpcode.SmsgLootRemoved));
        await host.WaitForWorldAsync(
            () => host.World.FindOnlinePlayer("Golooter")!.Inventory.AllItems.Any(i => i.Entry == LinenCloth && i.Count == 2),
            "the cloth reaches the bags");

        // Taking the same slot again changes nothing.
        await client.SendAsync(WorldOpcode.CmsgAutostoreLootItem, [0]);

        // Release: the client is told, and the emptied chest despawns until its respawn time.
        await client.SendAsync(WorldOpcode.CmsgLootRelease, BitConverter.GetBytes(chest));
        var release = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgLootReleaseResponse));
        Assert.Equal(chest, release.ReadUInt64());
        GameObjectLootFeature feature = context.Feature!;
        await host.WaitForWorldAsync(
            () => feature.FindSystem(0)!.Find(new ObjectGuid(chest)) is { IsSpawned: false, LootState: GameObjectLootState.JustDeactivated },
            "the looted chest despawns");
        Assert.Equal(1, await host.OnWorldAsync(() =>
            host.World.FindOnlinePlayer("Golooter")!.Inventory.AllItems.Count(i => i.Entry == LinenCloth)));
    }

    private static ulong ChestGuid() => ObjectGuid.WithEntry(HighGuid.GameObject, ChestEntry, ChestSpawn).Value;

    private static WorldTestHost StartWithChest(out GameObjectTestContext context)
    {
        uint[] data = new uint[GameObjectTemplate.DataCount];
        data[1] = ChestLootId;
        var chest = new GameObjectTemplate { Entry = ChestEntry, Type = (uint)GameObjectType.Chest, DisplayId = 10, Name = "Battered Chest", Data = data };

        // Human start is (-8949.95, -132.49, 83.53): the chest is 2 yd away.
        var spawn = new GameObjectSpawn { Guid = ChestSpawn, Entry = ChestEntry, MapId = 0, X = -8948f, Y = -132.5f, Z = 83.5f };
        var goContent = new GameObjectContent([chest], [spawn], [], [], []);
        var lootContent = new LootContent([(LootTableKind.GameObject, new LootStoreRow(ChestLootId, LinenCloth, 100f, 0, 2, 2))], []);
        context = new GameObjectTestContext(goContent, lootContent);

        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = LinenCloth, Class = 7, Name = "Linen Cloth", DisplayId = 3776, Quality = 1, Stackable = 20 });
        GameObjectTestStore.Current.Value = context;
        try
        {
            using (items.Use())
            {
                return WorldTestHost.Start();
            }
        }
        finally
        {
            GameObjectTestStore.Current.Value = null;
        }
    }

    /// <summary>Read until an update packet carries the create block of a game object.</summary>
    private static async Task ReadUntilGameObjectCreateAsync(WorldTestClient client, ulong guid)
    {
        while (true)
        {
            (WorldOpcode op, byte[] payload) = await client.ReadAsync();
            byte[]? body = op switch
            {
                WorldOpcode.SmsgUpdateObject => payload,
                WorldOpcode.SmsgCompressedUpdateObject => WorldTestClient.Inflate(payload),
                _ => null,
            };
            if (body is not null && ContainsCreate(body, guid))
            {
                return;
            }
        }
    }

    /// <summary>A create block header: update type 2, packed GUID, TYPEID_GAMEOBJECT.</summary>
    private static bool ContainsCreate(byte[] body, ulong guid)
    {
        for (int i = 5; i < body.Length - 2; i++)
        {
            if (body[i] != 2)
            {
                continue;
            }

            var reader = new PacketReader(body.AsSpan(i + 1).ToArray());
            try
            {
                if (reader.ReadPackedGuid() == guid && reader.ReadByte() == TypeId.GameObject)
                {
                    return true;
                }
            }
            catch (ArgumentOutOfRangeException)
            {
            }
            catch (IndexOutOfRangeException)
            {
            }
        }

        return false;
    }
}

/// <summary>What a game object test hands its host: the content, and back the feature instance.</summary>
internal sealed class GameObjectTestContext(GameObjectContent content, LootContent loot)
{
    public GameObjectContent Content { get; } = content;

    public LootContent Loot { get; } = loot;

    public GameObjectLootFeature? Feature { get; set; }
}

/// <summary>Game object and loot stores whose content the starting test sets (async-local, so tests stay isolated).</summary>
internal sealed class GameObjectTestStore : IGameObjectDataStore, ILootDataStore
{
    public static readonly AsyncLocal<GameObjectTestContext?> Current = new();

    private readonly GameObjectTestContext? _context = Current.Value;

    Task<GameObjectContent> IGameObjectDataStore.LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(_context?.Content ?? GameObjectContent.Empty);

    Task<LootContent> ILootDataStore.LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(_context?.Loot ?? LootContent.Empty);
}

/// <summary>Registers <see cref="GameObjectTestStore"/> in every test host (empty unless a game object test set content).</summary>
internal sealed class GameObjectTestServices : IWorldTestServices
{
    public void Register(IServiceCollection services)
    {
        services.AddScoped<GameObjectTestStore>();
        services.AddScoped<IGameObjectDataStore>(sp => sp.GetRequiredService<GameObjectTestStore>());
        services.AddScoped<ILootDataStore>(sp => sp.GetRequiredService<GameObjectTestStore>());
        services.AddSingleton<Features.IWorldFeature>(sp => new GameObjectTestProbe(sp.GetRequiredService<GameObjectLootFeature>()));
    }

    /// <summary>Hands the host's feature to the test that started it (attached synchronously in WorldTestHost.Start).</summary>
    private sealed class GameObjectTestProbe(GameObjectLootFeature feature) : Features.IWorldFeature
    {
        public void Attach(Game.Maps.WorldRuntime world)
        {
            if (GameObjectTestStore.Current.Value is { } context)
            {
                context.Feature = feature;
            }
        }
    }
}
