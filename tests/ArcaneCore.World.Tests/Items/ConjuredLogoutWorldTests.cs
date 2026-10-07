using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Game.Death;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Tests.Progression;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Items;

/// <summary>
/// "Conjured items disappear if you are logged out for more than 15 minutes" (vmangos Player::_LoadInventory, Player.cpp:15533-15540): at login,
/// an item whose template has ITEM_FLAG_CONJURED is dropped when more than 900 seconds passed since the stored logout second, whatever its
/// class; other items stay, and the next save no longer has it. The logout second is the one the rested state keeps (character_rest).
/// </summary>
public sealed class ConjuredLogoutWorldTests
{
    private const long Start = 1_700_000_000;
    private const uint ConjuredWater = 5350;
    private const uint ConjuredGem = 5512;
    private const uint Linen = 2589;

    private sealed class FixedClock : DeathClock
    {
        private long _now = Start;

        public override long UnixSeconds => Volatile.Read(ref _now);

        public void Advance(long seconds) => Interlocked.Add(ref _now, seconds);
    }

    [Theory]
    [InlineData(901, false)]
    [InlineData(900, true)]
    public async Task ConjuredItems_AreDroppedAfterMoreThanFifteenMinutesOffline(long offlineSeconds, bool kept)
    {
        var content = new ItemTestContent();
        content.Templates.Templates.Add(new ItemTemplate { Entry = ConjuredWater, Class = 0, SubClass = 0, Name = "Conjured Water", DisplayId = 1, Stackable = 20, Flags = 0x2 });
        content.Templates.Templates.Add(new ItemTemplate { Entry = ConjuredGem, Class = 12, SubClass = 0, Name = "Healthstone", DisplayId = 1, Flags = 0x2 });
        content.Templates.Templates.Add(new ItemTemplate { Entry = Linen, Class = 7, SubClass = 0, Name = "Linen Cloth", DisplayId = 1, Stackable = 20 });
        var clock = new FixedClock();
        WorldTestHost host;
        using (content.Use())
        {
            host = WorldTestHost.Start();
        }

        await using (host)
        {
            DeathHooks.Register(host.World, new DeathHooks(new DeathOptions(), clock));
            byte[] key = await host.AddAccountAsync("CONJURE");
            await using WorldTestClient client = await host.ConnectAsync();
            await client.AuthenticateAsync("CONJURE", key);
            await client.CreateCharacterAsync("Conjurer");
            await content.Items.SaveInventoryAsync(1, new InventorySnapshot(
            [
                new InventoryItemData(0, 23, new ItemInstanceData { Guid = 900_001, Entry = ConjuredWater, Count = 20 }),
                new InventoryItemData(0, 24, new ItemInstanceData { Guid = 900_002, Entry = ConjuredGem }),
                new InventoryItemData(0, 25, new ItemInstanceData { Guid = 900_003, Entry = Linen, Count = 5 }),
            ]));
            host.WorldServices.GetRequiredService<InMemoryRestStore>().Set(1, new CharacterRestState(0f, Start, WasResting: false));
            clock.Advance(offlineSeconds);

            await client.LoginAsync(1);

            Assert.Equal(kept ? 20u : 0u, await host.PlayerStateAsync("Conjurer", p => p.Inventory.GetItemCount(ConjuredWater)));
            Assert.Equal(kept ? 1u : 0u, await host.PlayerStateAsync("Conjurer", p => p.Inventory.GetItemCount(ConjuredGem)));
            Assert.Equal(5u, await host.PlayerStateAsync("Conjurer", p => p.Inventory.GetItemCount(Linen)));
            Assert.Equal(kept ? 3 : 1, (await host.PlayerStateAsync("Conjurer", p => p.Inventory.CreateSnapshot())).Items.Count);
        }
    }

    [Fact]
    public async Task WithoutAStoredLogoutSecond_ConjuredItemsStay()
    {
        var content = new ItemTestContent();
        content.Templates.Templates.Add(new ItemTemplate { Entry = ConjuredWater, Class = 0, SubClass = 0, Name = "Conjured Water", DisplayId = 1, Stackable = 20, Flags = 0x2 });
        WorldTestHost host;
        using (content.Use())
        {
            host = WorldTestHost.Start();
        }

        await using (host)
        {
            byte[] key = await host.AddAccountAsync("CONJURE2");
            await using WorldTestClient client = await host.ConnectAsync();
            await client.AuthenticateAsync("CONJURE2", key);
            await client.CreateCharacterAsync("Conjurtwo");
            await content.Items.SaveInventoryAsync(1, new InventorySnapshot(
                [new InventoryItemData(0, 23, new ItemInstanceData { Guid = 900_011, Entry = ConjuredWater, Count = 3 })]));

            await client.LoginAsync(1);

            Assert.Equal(3u, await host.PlayerStateAsync("Conjurtwo", p => p.Inventory.GetItemCount(ConjuredWater)));
        }
    }
}
