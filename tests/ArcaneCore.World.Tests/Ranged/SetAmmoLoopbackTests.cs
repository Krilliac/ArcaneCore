using System.Buffers.Binary;
using System.Collections.Concurrent;
using ArcaneCore.Data.Characters.Ranged;
using ArcaneCore.Game;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Ranged;

/// <summary>
/// CMSG_SET_AMMO, the starting ammo, PLAYER_AMMO_ID restore at login and its persistence, end to
/// end over loopback (vmangos ItemHandler.cpp:988-1008, Player.cpp:560-575, 14703, 16501).
/// </summary>
public sealed class SetAmmoLoopbackTests
{
    private const string Account = "AMMO1";
    private const string Name = "Quiverfull";
    private const byte Hunter = 3;
    private const uint Bow = 2504;
    private const uint Arrow = 2512;
    private const uint Bullet = 2516;
    private const uint Jerky = 117;
    private const uint HeavyBolt = 90040;

    [Fact]
    public async Task NewHunter_StartsWithTheLastStartingAmmo_AndLoginRestoresTheField()
    {
        (ItemTestContent content, AmmoTestContent ammo) = Content();
        await using WorldTestHost host = Start(content, ammo);
        byte[] key = await host.AddAccountAsync(Account);
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(Account, key);
        await client.CreateCharacterAsync(Name, 3, Hunter);

        await WorldTestHost.WaitForAsync(() => ammo.Store.Get(1) == Bullet, "starting ammo saved");
        await client.LoginAsync(1);
        Assert.Equal(Bullet, await host.PlayerStateAsync(Name, p => p.GetUInt32(UpdateFields.PlayerAmmoId)));
    }

    [Fact]
    public async Task SetAmmo_ChangesTheField_ReachesTheOwner_Persists_AndSurvivesRelog()
    {
        (ItemTestContent content, AmmoTestContent ammo) = Content();
        await using WorldTestHost host = Start(content, ammo);
        byte[] key = await host.AddAccountAsync(Account);
        await using (WorldTestClient client = await host.ConnectAsync())
        {
            await client.AuthenticateAsync(Account, key);
            await client.CreateCharacterAsync(Name, 3, Hunter);
            await client.LoginAsync(1);

            await client.SendAsync(WorldOpcode.CmsgSetAmmo, U32(Arrow));
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(Name)!.GetUInt32(UpdateFields.PlayerAmmoId) == Arrow, "ammo set");
            await WorldTestHost.WaitForAsync(() => ammo.Store.Get(1) == Arrow, "ammo saved");
            await ReadValuesUpdateWithFieldAsync(client, UpdateFields.PlayerAmmoId);
        }

        await WorldTestHost.WaitForAsync(() => host.World.OnlinePlayerCount == 0, "player removed");
        await using (WorldTestClient client = await host.ConnectAsync())
        {
            await client.AuthenticateAsync(Account, key);
            await client.LoginAsync(1);
            Assert.Equal(Arrow, await host.PlayerStateAsync(Name, p => p.GetUInt32(UpdateFields.PlayerAmmoId)));
        }
    }

    [Fact]
    public async Task SetAmmoZero_RemovesTheAmmoAndItsRow()
    {
        (ItemTestContent content, AmmoTestContent ammo) = Content();
        await using WorldTestHost host = Start(content, ammo);
        await using WorldTestClient client = await EnterAsync(host);
        await WorldTestHost.WaitForAsync(() => ammo.Store.Get(1) == Bullet, "starting ammo saved");

        await client.SendAsync(WorldOpcode.CmsgSetAmmo, U32(0));

        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(Name)!.GetUInt32(UpdateFields.PlayerAmmoId) == 0, "ammo removed");
        await WorldTestHost.WaitForAsync(() => ammo.Store.Get(1) == 0, "row removed");
    }

    [Fact]
    public async Task SetAmmo_AnswersTheEquipErrors_AndKeepsTheField()
    {
        (ItemTestContent content, AmmoTestContent ammo) = Content();
        await using WorldTestHost host = Start(content, ammo);
        await using WorldTestClient client = await EnterAsync(host);

        await client.SendAsync(WorldOpcode.CmsgSetAmmo, U32(HeavyBolt)); // not carried
        Assert.Equal((byte)InventoryResult.ItemNotFound, (await client.ReadUntilAsync(WorldOpcode.SmsgInventoryChangeFailure))[0]);

        await client.SendAsync(WorldOpcode.CmsgSetAmmo, U32(Jerky)); // carried, but not ammo
        Assert.Equal((byte)InventoryResult.OnlyAmmoCanGoHere, (await client.ReadUntilAsync(WorldOpcode.SmsgInventoryChangeFailure))[0]);

        Assert.Equal(Bullet, await host.PlayerStateAsync(Name, p => p.GetUInt32(UpdateFields.PlayerAmmoId)));
    }

    [Fact]
    public async Task DeletingTheCharacter_RemovesItsAmmoRow()
    {
        (ItemTestContent content, AmmoTestContent ammo) = Content();
        await using WorldTestHost host = Start(content, ammo);
        byte[] key = await host.AddAccountAsync(Account);
        await using (WorldTestClient client = await host.ConnectAsync())
        {
            await client.AuthenticateAsync(Account, key);
            await client.CreateCharacterAsync(Name, 3, Hunter);
        }

        await WorldTestHost.WaitForAsync(() => ammo.Store.Get(1) == Bullet, "starting ammo saved");
        await using (WorldTestClient client = await host.ConnectAsync())
        {
            await client.AuthenticateAsync(Account, key);
            byte[] guid = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(guid, 1);
            await client.SendAsync(WorldOpcode.CmsgCharDelete, guid);
            (WorldOpcode op, byte[] payload) = await client.ReadAsync();
            Assert.Equal(WorldOpcode.SmsgCharDelete, op);
            Assert.Equal((byte)CharResult.CharDeleteSuccess, payload[0]);
        }

        Assert.Equal(0u, ammo.Store.Get(1));
    }

    private static async Task<WorldTestClient> EnterAsync(WorldTestHost host)
    {
        byte[] key = await host.AddAccountAsync(Account);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(Account, key);
        await client.CreateCharacterAsync(Name, 3, Hunter);
        await client.LoginAsync(1);
        return client;
    }

    private static byte[] U32(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    /// <summary>
    /// Read SMSG_UPDATE_OBJECT packets until a values block of the player carries <paramref name="field"/>
    /// in its update mask (the mask is count byte + u32 words; field f is bit f % 32 of word f / 32).
    /// </summary>
    private static async Task ReadValuesUpdateWithFieldAsync(WorldTestClient client, int field)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            byte[] update = await client.ReadUntilAsync(WorldOpcode.SmsgUpdateObject);
            if (update.Length > 6 && update[5] == (byte)ObjectUpdateType.Values)
            {
                int offset = 6;
                byte guidMask = update[offset++];
                offset += System.Numerics.BitOperations.PopCount(guidMask);
                byte words = update[offset++];
                int word = field / 32;
                if (word < words && (BinaryPrimitives.ReadUInt32LittleEndian(update.AsSpan(offset + (word * 4))) & (1u << (field % 32))) != 0)
                {
                    return;
                }
            }
        }

        Assert.Fail($"no values update carried update field {field}");
    }

    private static WorldTestHost Start(ItemTestContent content, AmmoTestContent ammo)
    {
        using (content.Use())
        using (ammo.Use())
        {
            return WorldTestHost.Start();
        }
    }

    private static (ItemTestContent, AmmoTestContent) Content()
    {
        var content = new ItemTestContent();
        content.Templates.Templates.AddRange(
        [
            new ItemTemplate { Entry = Bow, Class = 2, SubClass = 2, Name = "Crude Bow", DisplayId = 200, Quality = 1, InventoryType = 15, Delay = 2500, MaxDurability = 40, Damages = [new ItemDamage(3, 5, 0)] },
            new ItemTemplate { Entry = Arrow, Class = 6, SubClass = 2, Name = "Rough Arrow", DisplayId = 5996, InventoryType = 24, Stackable = 200, BagFamily = 1, Damages = [new ItemDamage(1, 2, 0)] },
            new ItemTemplate { Entry = Bullet, Class = 6, SubClass = 3, Name = "Light Shot", DisplayId = 5998, InventoryType = 24, Stackable = 200, BagFamily = 2, Damages = [new ItemDamage(1, 2, 0)] },
            new ItemTemplate { Entry = HeavyBolt, Class = 6, SubClass = 2, Name = "Not carried", DisplayId = 5997, InventoryType = 24, Stackable = 200 },
            new ItemTemplate { Entry = Jerky, Class = 0, SubClass = 0, Name = "Tough Jerky", DisplayId = 2473, Quality = 1, Stackable = 20 },
        ]);
        content.Templates.StartingItems.AddRange(
        [
            new StartingItem(3, Hunter, Bow, 1), new StartingItem(3, Hunter, Arrow, 100), new StartingItem(3, Hunter, Bullet, 100), new StartingItem(3, Hunter, Jerky, 4),
        ]);
        return (content, new AmmoTestContent());
    }
}

/// <summary>An in-memory <see cref="ICharacterAmmoStore"/> for the hosts started inside <see cref="Use"/>.</summary>
internal sealed class AmmoTestContent
{
    private static readonly AsyncLocal<AmmoTestContent?> Ambient = new();

    public static AmmoTestContent? Current => Ambient.Value;

    public InMemoryAmmoStore Store { get; } = new();

    public IDisposable Use()
    {
        Ambient.Value = this;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => Ambient.Value = null;
    }
}

internal sealed class InMemoryAmmoStore : ICharacterAmmoStore
{
    private readonly ConcurrentDictionary<int, uint> _ammo = new();

    public uint Get(int characterId) => _ammo.GetValueOrDefault(characterId);

    public Task<uint> GetAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult(Get(characterId));

    public Task SetAsync(int characterId, uint itemId, CancellationToken cancellationToken = default)
    {
        if (itemId == 0)
        {
            _ammo.TryRemove(characterId, out _);
        }
        else
        {
            _ammo[characterId] = itemId;
        }

        return Task.CompletedTask;
    }

    public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        _ammo.TryRemove(characterId, out _);
        return Task.CompletedTask;
    }
}

internal sealed class AmmoWorldTestServices : IWorldTestServices
{
    public void Register(IServiceCollection services)
    {
        if (AmmoTestContent.Current is { } content)
        {
            services.AddSingleton<ICharacterAmmoStore>(content.Store);
            var inner = (IWorldDataStore)services.Last(d => d.ServiceType == typeof(IWorldDataStore)).ImplementationInstance!;
            services.AddSingleton<IWorldDataStore>(new HunterWorldDataStore(inner));
        }
    }
}

/// <summary>The in-memory world data allows human/orc warriors only; a dwarf hunter is added for the ammo tests.</summary>
internal sealed class HunterWorldDataStore(IWorldDataStore inner) : IWorldDataStore
{
    public Task<StartPosition?> GetStartPositionAsync(byte race, byte cls, CancellationToken cancellationToken = default)
        => race == 3 && cls == 3 ? inner.GetStartPositionAsync(1, 1, cancellationToken) : inner.GetStartPositionAsync(race, cls, cancellationToken);

    public Task<RaceInfo?> GetRaceInfoAsync(byte race, byte gender, CancellationToken cancellationToken = default)
        => inner.GetRaceInfoAsync(race == 3 ? (byte)1 : race, gender, cancellationToken);

    public Task<ClassInfo?> GetClassInfoAsync(byte cls, CancellationToken cancellationToken = default)
        => inner.GetClassInfoAsync(cls == 3 ? (byte)1 : cls, cancellationToken);

    public async Task<bool> IsValidRaceClassAsync(byte race, byte cls, CancellationToken cancellationToken = default)
        => (race == 3 && cls == 3) || await inner.IsValidRaceClassAsync(race, cls, cancellationToken);
}
