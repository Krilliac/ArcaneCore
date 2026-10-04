using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Spells.SpellTestServices;

namespace ArcaneCore.World.Tests.Ranged;

/// <summary>
/// Quivers in the world daemon (ranged lane S06): the feature follows a player's bag slots from login on and re-applies a worn quiver at
/// login exactly once (vmangos Player::ApplyEquipSpell for the bag slots, Player.cpp:6828-6833). Nothing is persisted for the aura:
/// it is recomputed from the equipment, so the relog has to find it again. Item and spell content are in-memory doubles.
/// </summary>
public sealed class QuiverLoginTests
{
    private const string Account = "QUIVER1";
    private const string Name = "Fletcher";
    private const uint Bow = 94601;
    private const uint Quiver = 94602;

    private static ItemTestContent Content()
    {
        var content = new ItemTestContent();
        content.Templates.Templates.AddRange(
        [
            new ItemTemplate { Entry = Bow, Class = 2, SubClass = 2, Name = "Test Bow", DisplayId = 300, InventoryType = 15, Delay = 2500, AmmoType = 2 },
            new ItemTemplate
            {
                Entry = Quiver, Class = 11, SubClass = 2, Name = "Test Quiver", DisplayId = 21328, InventoryType = 18, ContainerSlots = 8, BagFamily = 1,
                Spells = [new ItemSpell(QuiverHasteSpell, 1, 0, 0, -1, 0, -1)],
            },
        ]);
        return content;
    }

    private static (int Holders, float Pct) Read(Player player, WorldTestHost host)
    {
        SpellSystem spells = ((ArcaneCore.World.Net.WorldSession)player.Session).Services.GetRequiredService<SpellFeature>().System;
        int holders = spells.GetAuras(player).Count(h => h.Spell.Id == QuiverHasteSpell && !h.IsRemoved);
        return (holders, player.Combat.GetAttackSpeedPct(WeaponAttackType.RangedAttack));
    }

    [Fact]
    public async Task WearingAQuiver_SpeedsUpTheBow_AndTheRelogFindsItAgainExactlyOnce()
    {
        ItemTestContent content = Content();
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

                await host.OnWorldAsync(() =>
                {
                    Player player = host.World.FindOnlinePlayer(Name)!;
                    foreach (uint entry in new[] { Bow, Quiver })
                    {
                        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(entry, 1, out Item? item));
                        player.Inventory.AutoEquipItem(item!.BagSlot, item.Slot);
                    }

                    player.SetUInt32(UpdateFields.UnitFieldRangedattacktime, 2500);
                });
                // The bow went on first, then the quiver: the aura applies to the bow's 2500 ms.
                (int holders, float pct) = await host.PlayerStateAsync(Name, p => Read(p, host));
                Assert.Equal(1, holders);
                Assert.Equal(100f / 110f, pct, 0.0001f);
            }

            await WorldTestHost.WaitForAsync(() => host.World.OnlinePlayerCount == 0, "player saved and removed");
            await WorldTestHost.WaitForAsync(() => host.SaveQueue.Pending == 0, "the save");

            await using (WorldTestClient client = await host.ConnectAsync())
            {
                await client.AuthenticateAsync(Account, key);
                await client.LoginAsync(1);

                (int holders, float pct) = await host.PlayerStateAsync(Name, p => Read(p, host));
                Assert.Equal(1, holders);
                Assert.Equal(100f / 110f, pct, 0.0001f);
            }
        }
    }
}
