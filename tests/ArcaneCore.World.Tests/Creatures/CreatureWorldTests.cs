using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Creatures;

/// <summary>
/// Creatures end to end: content loaded through <see cref="ICreatureDataStore"/> at host start,
/// create blocks for clients near a spawn, CMSG_CREATURE_QUERY, and the GM commands.
/// </summary>
public sealed class CreatureWorldTests
{
    private const uint WolfEntry = 299;
    private const uint SpawnGuid = 4242;

    [Fact]
    public void CreatureQueryResponse_MatchesTheGtkerVector()
    {
        // gtker/wow_messages smsg_creature_query_response.wowm test (1.12): entry 69 "Thing".
        byte[] expected =
        [
            0x45, 0x00, 0x00, 0x00,
            0x54, 0x68, 0x69, 0x6E, 0x67, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0x00, 0x00,
        ];
        var thing = new CreatureTemplate { Entry = 69, Name = "Thing", DisplayIds = [0] };
        Assert.Equal(expected, CreaturePackets.BuildCreatureQueryResponse(69, thing));

        // Not found: the entry with the high bit set, nothing else.
        Assert.Equal([0x45, 0x00, 0x00, 0x80], CreaturePackets.BuildCreatureQueryResponse(69, null));
    }

    [Fact]
    public async Task ClientNearASpawn_SeesTheCreature_AndCanQueryIt()
    {
        await using WorldTestHost host = StartWithWolf(out _);
        await using WorldTestClient client = await host.EnterWorldAsync("CRWALICE", "Crwalice");

        // Human start (-8949.95, -132.49): the wolf 10 yd away is created after login.
        ulong guid = WolfGuid();
        await ReadUntilCreateAsync(client, guid);

        var query = new PacketWriter(12);
        query.WriteUInt32(WolfEntry);
        query.WriteUInt64(guid);
        await client.SendAsync(WorldOpcode.CmsgCreatureQuery, query.ToArray());
        var reader = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgCreatureQueryResponse));
        Assert.Equal(WolfEntry, reader.ReadUInt32());
        Assert.Equal("Young Wolf", reader.ReadCString());
        Assert.Equal(string.Empty, reader.ReadCString());
        Assert.Equal(string.Empty, reader.ReadCString());
        Assert.Equal(string.Empty, reader.ReadCString());
        Assert.Equal(string.Empty, reader.ReadCString());
        reader.Skip(4);                       // type flags
        Assert.Equal(1u, reader.ReadUInt32()); // beast
        Assert.Equal(1u, reader.ReadUInt32()); // wolf family
        reader.Skip(12);                      // rank, reserved, pet spells
        Assert.Equal(903u, reader.ReadUInt32());
        reader.Skip(2);
        Assert.Equal(0, reader.Remaining);

        var unknown = new PacketWriter(12);
        unknown.WriteUInt32(123456);
        unknown.WriteUInt64(0);
        await client.SendAsync(WorldOpcode.CmsgCreatureQuery, unknown.ToArray());
        Assert.Equal(123456u | 0x80000000u, new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgCreatureQueryResponse)).ReadUInt32());
    }

    [Fact]
    public async Task GmCommands_SpawnKillAndRespawn()
    {
        await using WorldTestHost host = StartWithWolf(out CreatureTestContext context);
        await using WorldTestClient gm = await host.EnterWorldAsync("CRWGM", "Crwgm", AccountSecurity.GameMaster);
        await ReadUntilCreateAsync(gm, WolfGuid());

        // .creature add → CREATE_OBJECT2 (vmangos Map::Add) of a temporary wolf.
        await gm.SendChatAsync(ChatType.Say, Language.Common, $".creature add {WolfEntry}");
        Assert.StartsWith("Spawned Young Wolf", (await gm.ReadChatAsync()).Text, StringComparison.Ordinal);
        await ReadUntilCreateAsync(gm, guid: null, createObject2: true);

        // Select the database wolf, kill it, respawn it.
        ulong wolf = WolfGuid();
        await host.OnWorldAsync(() =>
        {
            host.World.FindOnlinePlayer("Crwgm")!.Selection = new Game.ObjectGuid(wolf);
            return true;
        });
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".creature kill");
        CreatureWorldFeature feature = context.Feature!;
        await host.WaitForWorldAsync(
            () => feature.FindSystem(0)!.FindCreature(new Game.ObjectGuid(wolf))!.DeathState == Game.Creatures.CreatureDeathState.Corpse,
            "the wolf dies");

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".creature respawn");
        await host.WaitForWorldAsync(
            () => feature.FindSystem(0)!.FindCreature(new Game.ObjectGuid(wolf))!.DeathState == Game.Creatures.CreatureDeathState.Alive,
            "the wolf respawns");
    }

    private static ulong WolfGuid() => Game.ObjectGuid.WithEntry(Game.HighGuid.Unit, WolfEntry, SpawnGuid).Value;

    private static WorldTestHost StartWithWolf(out CreatureTestContext context)
    {
        var wolf = new CreatureTemplate
        {
            Entry = WolfEntry,
            Name = "Young Wolf",
            MinLevel = 2,
            MaxLevel = 2,
            DisplayIds = [903],
            Faction = 32,
            CreatureType = 1,
            Family = 1,
            MinLevelHealth = 55,
            MaxLevelHealth = 55,
        };
        var spawn = new CreatureSpawn { Guid = SpawnGuid, Entry = WolfEntry, MapId = 0, X = -8940f, Y = -132f, Z = 83.5f };
        context = new CreatureTestContext(new CreatureContent([wolf], [spawn], [], [], []));
        CreatureTestStore.Current.Value = context;
        try
        {
            return WorldTestHost.Start();
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }
    }

    /// <summary>Read until an update packet carries a create block (of <paramref name="guid"/> when given).</summary>
    private static async Task ReadUntilCreateAsync(WorldTestClient client, ulong? guid, bool createObject2 = false)
    {
        byte type = createObject2 ? (byte)3 : (byte)2;
        while (true)
        {
            (WorldOpcode op, byte[] payload) = await client.ReadAsync();
            byte[]? body = op switch
            {
                WorldOpcode.SmsgUpdateObject => payload,
                WorldOpcode.SmsgCompressedUpdateObject => WorldTestClient.Inflate(payload),
                _ => null,
            };
            if (body is not null && ContainsCreate(body, type, guid))
            {
                return;
            }
        }
    }

    /// <summary>A create block header: update type, packed GUID (high part 0xF130…), TYPEID_UNIT.</summary>
    private static bool ContainsCreate(byte[] body, byte type, ulong? guid)
    {
        for (int i = 5; i < body.Length - 2; i++)
        {
            if (body[i] != type)
            {
                continue;
            }

            var reader = new PacketReader(body.AsSpan(i + 1).ToArray());
            try
            {
                ulong packed = reader.ReadPackedGuid();
                if (reader.ReadByte() == 3 && (packed >> 48) == (ulong)Game.HighGuid.Unit && (guid is null || packed == guid))
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

/// <summary>What a creature test hands its host: the content, and back the feature instance.</summary>
internal sealed class CreatureTestContext(CreatureContent content)
{
    public CreatureContent Content { get; } = content;

    public CreatureWorldFeature? Feature { get; set; }
}

/// <summary>An <see cref="ICreatureDataStore"/> whose content the starting test sets (async-local, so tests stay isolated).</summary>
internal sealed class CreatureTestStore : ICreatureDataStore
{
    public static readonly AsyncLocal<CreatureTestContext?> Current = new();

    private readonly CreatureContent _content = Current.Value?.Content ?? CreatureContent.Empty;

    public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_content);
}

/// <summary>Registers <see cref="CreatureTestStore"/> in every test host (empty unless a creature test set content).</summary>
internal sealed class CreatureTestServices : IWorldTestServices
{
    public void Register(IServiceCollection services)
    {
        services.AddScoped<ICreatureDataStore, CreatureTestStore>();
        services.AddSingleton<Features.IWorldFeature>(sp => new CreatureTestProbe(sp.GetRequiredService<CreatureWorldFeature>()));
    }

    /// <summary>Hands the host's feature to the test that started it (attached synchronously in WorldTestHost.Start).</summary>
    private sealed class CreatureTestProbe(CreatureWorldFeature feature) : Features.IWorldFeature
    {
        public void Attach(Game.Maps.WorldRuntime world)
        {
            if (CreatureTestStore.Current.Value is { } context)
            {
                context.Feature = feature;
            }
        }
    }
}
