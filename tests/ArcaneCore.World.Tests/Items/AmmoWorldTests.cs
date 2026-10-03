using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Items;

/// <summary>CMSG_SET_AMMO end to end: starting ammo, the opcode, the persisted selection (vmangos ItemHandler.cpp:988-1008, Player.cpp:570-575, 14703, 16501).</summary>
public sealed class AmmoWorldTests
{
    private const string Account = "AMMO1";
    private const string Name = "Quiverman";
    private const uint Arrow = 2512;
    private const uint Bullet = 2516;

    [Fact]
    public async Task StartingAmmoIsSelected_SetAmmoSwitchesIt_AndItSurvivesRelog()
    {
        var content = new ItemTestContent();
        content.Templates.Templates.AddRange(
        [
            new ItemTemplate { Entry = Arrow, Class = 6, SubClass = 2, Name = "Rough Arrow", DisplayId = 5996, InventoryType = 24, Stackable = 200 },
            new ItemTemplate { Entry = Bullet, Class = 6, SubClass = 3, Name = "Light Shot", DisplayId = 5997, InventoryType = 24, Stackable = 200 },
            new ItemTemplate { Entry = 38, Class = 4, Name = "Recruit's Shirt", DisplayId = 1, InventoryType = 4 },
        ]);
        content.Templates.StartingItems.Add(new StartingItem(1, 1, Arrow, 100));
        WorldTestHost host;
        using (content.Use())
        {
            host = WorldTestHost.Start();
        }

        await using (host)
        {
            byte[] key = await host.AddAccountAsync(Account);
            await using (WorldTestClient client = await host.ConnectAsync())
            {
                await client.AuthenticateAsync(Account, key);
                await client.CreateCharacterAsync(Name);
                await client.LoginAsync(1);
                Assert.Equal(Arrow, await host.PlayerStateAsync(Name, p => p.Inventory.AmmoId));
                Assert.Equal(Arrow, await host.PlayerStateAsync(Name, p => p.GetUInt32(UpdateFields.PlayerAmmoId)));

                await host.PlayerStateAsync(Name, p => p.Inventory.AddItem(Bullet, 20, out _));
                byte[] setBullet = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(setBullet, Bullet);
                await client.SendAsync(WorldOpcode.CmsgSetAmmo, setBullet);
                await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(Name)!.Inventory.AmmoId == Bullet, "ammo switched");

                // An item the player does not carry: ITEM_NOT_FOUND, selection unchanged.
                byte[] notCarried = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(notCarried, 123456);
                await client.SendAsync(WorldOpcode.CmsgSetAmmo, notCarried);
                byte[] failure = await client.ReadUntilAsync(WorldOpcode.SmsgInventoryChangeFailure);
                Assert.Equal((byte)InventoryResult.ItemNotFound, failure[0]);
                Assert.Equal(Bullet, await host.PlayerStateAsync(Name, p => p.Inventory.AmmoId));
            }

            await WorldTestHost.WaitForAsync(() => host.World.OnlinePlayerCount == 0, "player saved and removed");
            await WorldTestHost.WaitForAsync(() => host.SaveQueue.Pending == 0, "the save");
            Assert.Equal(Bullet, await content.Items.GetAmmoAsync(1));

            await using (WorldTestClient client = await host.ConnectAsync())
            {
                await client.AuthenticateAsync(Account, key);
                await client.LoginAsync(1);
                Assert.Equal(Bullet, await host.PlayerStateAsync(Name, p => p.Inventory.AmmoId));

                await client.SendAsync(WorldOpcode.CmsgSetAmmo, new byte[4]); // 0 removes the ammo
                await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(Name)!.Inventory.AmmoId == 0, "ammo removed");
            }
        }
    }
}
