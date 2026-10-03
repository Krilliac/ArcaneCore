using System.Buffers.Binary;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Economy;
using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>Expiry edge of the auction contract: an expired auction is the sweep's, not the seller's, to settle.</summary>
public sealed class EconomyAuctionExpiryEdgeTests
{
    private const string Account = "AUCTIONEXPIRY";
    private const string Password = "PASSWORD";

    [Fact]
    public async Task Expired_auction_cannot_be_cancelled_before_the_sweep()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = deadline.Token;
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(services =>
        {
            services.AddSingleton<IAuctioneerAccess>(new OwnedAuctioneer());
            services.AddSingleton<TimeProvider>(clock);
        }, token);
        await server.AddAccountAsync(Account, Password, token);
        await using WorldClient client = await AuthenticateAsync(server, token);
        var connection = new ScenarioConnection(client);
        await connection.CreateCharacterAsync("Expiryseller", token);
        ulong seller = Assert.Single(await connection.EnumerateAsync(token)).Guid;
        await connection.LoginAsync(seller, token);
        ulong item = await server.World.InvokeAsync(() =>
        {
            Player player = server.World.FindOnlinePlayer(new ObjectGuid(seller))!;
            player.Money = 100;
            Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(SyntheticArcaneServer.FixedRewardItem, 1, out Item? added));
            server.World.SavePlayer(player);
            return added!.Guid.Value;
        }).WaitAsync(token);
        await server.Services.GetRequiredService<CharacterSaveQueue>().FlushCharacterAsync(checked((int)seller), token);

        var sell = new byte[28];
        BinaryPrimitives.WriteUInt64LittleEndian(sell, SyntheticArcaneServer.NpcGuid);
        BinaryPrimitives.WriteUInt64LittleEndian(sell.AsSpan(8), item);
        BinaryPrimitives.WriteUInt32LittleEndian(sell.AsSpan(16), 10);
        BinaryPrimitives.WriteUInt32LittleEndian(sell.AsSpan(24), 120);
        await connection.SendAsync(WorldOpcode.CmsgAuctionSellItem, sell, token);
        byte[] started = await connection.ReadUntilAsync(WorldOpcode.SmsgAuctionCommandResult, token);
        uint auction = BinaryPrimitives.ReadUInt32LittleEndian(started);
        Assert.Equal((uint)AuctionError.Ok, BinaryPrimitives.ReadUInt32LittleEndian(started.AsSpan(8)));

        clock.Advance(TimeSpan.FromMinutes(121));
        var remove = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(remove, SyntheticArcaneServer.NpcGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(remove.AsSpan(8), auction);
        await connection.SendAsync(WorldOpcode.CmsgAuctionRemoveItem, remove, token);
        byte[] removed = await connection.ReadUntilAsync(WorldOpcode.SmsgAuctionCommandResult, token);
        Assert.Equal(auction, BinaryPrimitives.ReadUInt32LittleEndian(removed));
        Assert.Equal((uint)AuctionError.Database, BinaryPrimitives.ReadUInt32LittleEndian(removed.AsSpan(8)));
        await AssertAuctionRowAsync(server, auction, present: true, token);

        EconomyFeature economy = server.Services.GetRequiredService<EconomyFeature>();
        await server.World.InvokeAsync(() =>
        {
            economy.RunExpirySweep();
            return true;
        }).WaitAsync(token);
        await economy.DrainAsync().WaitAsync(token);
        await AssertAuctionRowAsync(server, auction, present: false, token);
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        MailRow letter = await db.Set<MailRow>().AsNoTracking().SingleAsync(m => m.ReceiverId == checked((int)seller), token);
        Assert.Equal(MailRules.AuctionSubject(SyntheticArcaneServer.FixedRewardItem, AuctionMailAction.Expired), letter.Subject);
    }

    private static async Task AssertAuctionRowAsync(SyntheticArcaneServer server, uint auction, bool present, CancellationToken token)
    {
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        Assert.Equal(present, await db.Set<AuctionRow>().AsNoTracking().AnyAsync(a => a.Id == auction, token));
    }

    private static async Task<WorldClient> AuthenticateAsync(SyntheticArcaneServer server, CancellationToken token)
    {
        LogonResult logon = await LogonClient.AuthenticateAsync(server.RealmEndpoint, Account, Password, token);
        WorldClient client = await WorldClient.ConnectAsync(Assert.Single(logon.Realms).GetLoopbackEndpoint(), token);
        Assert.Equal((byte)0x0C, await client.AuthenticateAsync(Account, logon.SessionKey, token));
        return client;
    }

    private sealed class OwnedAuctioneer : IAuctioneerAccess
    {
        public AuctionHouseEntry? FindHouse(Player player, ObjectGuid auctioneer)
            => player.IsInWorld && auctioneer.Value == SyntheticArcaneServer.NpcGuid ? new(2, 0, 5) : null;
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
