using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.MockClient.Tests.EconomyMailSendParityTests;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// vmangos removes a successful Feign Death in all seven auction handlers, after the auction house lookup: hello (AuctionHouseHandler.cpp:51-52),
/// sell (:299-300), place bid (:443-444), remove item (:576-577), list bidder items (:718-719), list owner items (:741-742) and list items
/// (:777-778). Each packet is sent to the real handler of a synthetic server while the player holds a real Feign Death aura.
/// </summary>
public sealed class EconomyAuctionFeignDeathTests
{
    private const uint FeignSpell = 9909101;

    public static TheoryData<WorldOpcode> Operations => new()
    {
        WorldOpcode.MsgAuctionHello,
        WorldOpcode.CmsgAuctionSellItem,
        WorldOpcode.CmsgAuctionPlaceBid,
        WorldOpcode.CmsgAuctionRemoveItem,
        WorldOpcode.CmsgAuctionListBidderItems,
        WorldOpcode.CmsgAuctionListOwnerItems,
        WorldOpcode.CmsgAuctionListItems,
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task An_auction_house_request_ends_a_successful_feign_death(WorldOpcode opcode)
    {
        await using Rig rig = await Rig.StartAsync();
        Assert.True(await FeignAsync(rig));

        await rig.Sender.SendAsync(opcode, Payload(opcode), rig.Token);

        Assert.True(await WaitUntilAsync(rig, () => !IsFeigning(rig)), $"{opcode} left the player feigning death");
    }

    [Fact]
    public async Task A_player_that_is_not_near_an_auctioneer_keeps_feigning()
    {
        await using Rig rig = await Rig.StartAsync();
        Assert.True(await FeignAsync(rig));
        var hello = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(hello, SyntheticArcaneServer.NpcGuid + 1); // not an auctioneer: the lookup fails first

        await rig.Sender.SendAsync(WorldOpcode.MsgAuctionHello, hello, rig.Token);
        await rig.Sender.SendAsync(WorldOpcode.CmsgAuctionListOwnerItems, new byte[12], rig.Token); // guid 0: also no house

        Assert.False(await WaitUntilAsync(rig, () => !IsFeigning(rig), TimeSpan.FromMilliseconds(500)));
    }

    private static byte[] Payload(WorldOpcode opcode)
    {
        var data = new byte[opcode == WorldOpcode.CmsgAuctionListItems ? 32 : 28];
        BinaryPrimitives.WriteUInt64LittleEndian(data, SyntheticArcaneServer.NpcGuid);
        switch (opcode)
        {
            case WorldOpcode.MsgAuctionHello:
                return data[..8];
            case WorldOpcode.CmsgAuctionSellItem:
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 100); // an empty item guid still passes the feign break, which runs before the item lookup
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 120);
                return data;
            case WorldOpcode.CmsgAuctionPlaceBid:
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 999_999); // an auction that does not exist
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 1);
                return data[..16];
            case WorldOpcode.CmsgAuctionRemoveItem:
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 999_999);
                return data[..12];
            case WorldOpcode.CmsgAuctionListBidderItems:
            case WorldOpcode.CmsgAuctionListOwnerItems:
                return data[..12];
            default:
                foreach (int offset in new[] { 15, 19, 23, 27 })
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), uint.MaxValue);
                }

                return data;
        }
    }

    private static Player PlayerOf(Rig rig) => rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;

    private static bool IsFeigning(Rig rig)
        => rig.Server.Services.GetRequiredService<SpellFeature>().System.IsFeigningDeath(PlayerOf(rig));

    private static Task<bool> FeignAsync(Rig rig) => rig.Server.World.InvokeAsync(() =>
    {
        SpellSystem spells = rig.Server.Services.GetRequiredService<SpellFeature>().System;
        spells.Store = new SpellStore([.. spells.Store.All, new SpellInfo
        {
            Id = FeignSpell, Name = "Synthetic Feign Death", RangeIndex = SpellConstants.RangeIndexSelfOnly,
            Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.FeignDeath, BaseDice = 1, DieSides = 1,
                TargetA = SpellImplicitTarget.UnitCaster }],
        }], [], []);
        Player player = PlayerOf(rig);
        return spells.AddAura(player, FeignSpell, permanent: true) && spells.IsFeigningDeath(player);
    }).WaitAsync(rig.Token);

    private static async Task<bool> WaitUntilAsync(Rig rig, Func<bool> condition, TimeSpan? budget = null)
    {
        DateTime end = DateTime.UtcNow + (budget ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < end)
        {
            if (await rig.Server.World.InvokeAsync(condition).WaitAsync(rig.Token))
            {
                return true;
            }

            await Task.Delay(25, rig.Token);
        }

        return false;
    }
}
