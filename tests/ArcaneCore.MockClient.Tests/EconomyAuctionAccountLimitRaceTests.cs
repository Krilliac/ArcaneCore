using System.Buffers.Binary;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Economy;
using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// The optional per-account auction limit (vmangos AuctionHouseHandler.cpp:273-279, AuctionHouseObject::GetAccountAuctionCount)
/// counts a listing from the moment it is accepted. vmangos adds the auction to its in-memory house synchronously; here a
/// listing settles asynchronously, so a listing that is still settling, or whose outcome is unknown and is being recovered,
/// must hold its account's slot. Otherwise a second character of the same account, logged in while the first listing
/// settles, passes the check against the published auctions only.
/// </summary>
public sealed class EconomyAuctionAccountLimitRaceTests
{
    private const string Account = "AUCTIONQUOTA";
    private const string Password = "PASSWORD";
    private const string LimitMessage = "You have reached the limit of active auctions on your account.";

    [Fact]
    public async Task A_second_character_cannot_list_past_the_account_limit_while_the_first_listing_settles()
    {
        await using Harness harness = await Harness.StartAsync(Mode.HoldAfterCommit);
        await harness.LoginFirstAsync();
        ulong firstItem = await harness.GiveItemAsync(harness.First, SyntheticArcaneServer.FixedRewardItem);
        await harness.SellAsync(firstItem);
        uint firstAuction = await harness.Control.Committed.Task.WaitAsync(harness.Token);

        // The first listing is durable but not yet published to the cache.
        Assert.Equal(1, harness.Economy.Settlements.PendingCount);
        Assert.Empty(await harness.OnWorld(() => harness.Economy.Auctions.ToArray()));

        await harness.ReconnectAsSecondAsync();
        ulong secondItem = await harness.GiveItemAsync(harness.Second, SyntheticArcaneServer.UnchosenRewardItem);
        await harness.SellAsync(secondItem);
        var chat = new List<string>();
        Assert.Equal(AuctionError.Database, await harness.ReadSellResultAsync(chat));
        Assert.Contains(chat, text => text.Contains(LimitMessage));

        harness.Control.Release.TrySetResult(true);
        await harness.Economy.WaitForSettlementAsync(checked((int)harness.First), harness.Token);
        AuctionView published = Assert.Single(await harness.OnWorld(() => harness.Economy.Auctions.ToArray()));
        Assert.Equal(firstAuction, published.Auction.Id);
        Assert.Equal(checked((int)harness.First), published.Auction.SellerId);
        Assert.Single(await harness.DurableAuctionsAsync());
        Assert.True(await harness.HoldsItemAsync(harness.Second, secondItem), "the refused listing must leave the item in its owner's bags");

        // Once published it still counts.
        await harness.SellAsync(secondItem);
        Assert.Equal(AuctionError.Database, await harness.ReadSellResultAsync());
        Assert.Single(await harness.DurableAuctionsAsync());
    }

    [Fact]
    public async Task A_listing_whose_outcome_is_unknown_holds_its_slot_until_recovery_resolves_it()
    {
        await using Harness harness = await Harness.StartAsync(Mode.UnknownAfterCommit);
        await harness.LoginFirstAsync();
        ulong firstItem = await harness.GiveItemAsync(harness.First, SyntheticArcaneServer.FixedRewardItem);
        await harness.SellAsync(firstItem);
        uint firstAuction = await harness.Control.Committed.Task.WaitAsync(harness.Token);
        await harness.Economy.WaitForSettlementAsync(checked((int)harness.First), harness.Token);
        await harness.Control.RecoveryRefused.Task.WaitAsync(harness.Token);
        Assert.True(await harness.OnWorld(() => harness.Economy.IsAuctionQuarantined(firstAuction)));
        Assert.Empty(await harness.OnWorld(() => harness.Economy.Auctions.ToArray()));

        // The unknown outcome kicks the seller; the account comes back on its other character.
        await harness.ReconnectAsSecondAsync();
        ulong secondItem = await harness.GiveItemAsync(harness.Second, SyntheticArcaneServer.UnchosenRewardItem);
        await harness.SellAsync(secondItem);
        var chat = new List<string>();
        Assert.Equal(AuctionError.Database, await harness.ReadSellResultAsync(chat));
        Assert.Contains(chat, text => text.Contains(LimitMessage));
        Assert.True(await harness.OnWorld(() => harness.Economy.IsAuctionQuarantined(firstAuction)),
            "the refusal must have been decided while the first listing was still being recovered");

        harness.Control.Release.TrySetResult(true);
        await harness.WaitUntilAsync(() => !harness.Economy.IsAuctionQuarantined(firstAuction));
        AuctionView published = Assert.Single(await harness.OnWorld(() => harness.Economy.Auctions.ToArray()));
        Assert.Equal(firstAuction, published.Auction.Id);
        Assert.Single(await harness.DurableAuctionsAsync());
        Assert.True(await harness.HoldsItemAsync(harness.Second, secondItem));
    }

    [Fact]
    public async Task A_refused_listing_releases_its_slot()
    {
        await using Harness harness = await Harness.StartAsync(Mode.RefuseFirstCommit);
        await harness.LoginFirstAsync();
        ulong item = await harness.GiveItemAsync(harness.First, SyntheticArcaneServer.FixedRewardItem);
        await harness.SellAsync(item);
        Assert.Equal(AuctionError.Database, await harness.ReadSellResultAsync());
        await harness.Economy.WaitForSettlementAsync(checked((int)harness.First), harness.Token);
        Assert.Empty(await harness.DurableAuctionsAsync());

        await harness.SellAsync(item);
        var chat = new List<string>();
        Assert.Equal(AuctionError.Ok, await harness.ReadSellResultAsync(chat));
        Assert.DoesNotContain(chat, text => text.Contains(LimitMessage));
        Assert.Single(await harness.DurableAuctionsAsync());
    }

    private enum Mode
    {
        /// <summary>The first listing commits, then its acknowledgement waits for the release.</summary>
        HoldAfterCommit,

        /// <summary>The first listing commits, its acknowledgement is lost and recovery reads fail until the release.</summary>
        UnknownAfterCommit,

        /// <summary>The first listing is refused by storage without writing anything.</summary>
        RefuseFirstCommit,
    }

    private sealed class Control(Mode mode)
    {
        private int _inserts;
        internal Mode Mode { get; } = mode;
        internal TaskCompletionSource<uint> Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> RecoveryRefused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Held => Committed.Task.IsCompleted && !Release.Task.IsCompleted;
        internal bool IsFirstInsert() => Interlocked.Increment(ref _inserts) == 1;
    }

    private sealed class ControlledStore(IEconomyStore inner, Control control) : IEconomyStore
    {
        public async Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Changes.OfType<InsertAuction>().SingleOrDefault() is not { } insert || !control.IsFirstInsert())
            {
                return await inner.CommitAsync(request, cancellationToken);
            }

            if (control.Mode == Mode.RefuseFirstCommit)
            {
                return EconomyCommitResult.Conflict;
            }

            EconomyCommitResult result = await inner.CommitAsync(request, cancellationToken);
            Assert.Equal(EconomyCommitResult.Committed, result);
            control.Committed.TrySetResult(insert.Auction.Id);
            if (control.Mode == Mode.UnknownAfterCommit)
            {
                throw new IOException("acknowledgement lost (test)");
            }

            await control.Release.Task.WaitAsync(cancellationToken);
            return result;
        }

        public Task<bool> IsCommittedAsync(Guid id, CancellationToken ct = default)
            => control.Mode == Mode.UnknownAfterCommit && control.Held
                ? throw new IOException("ledger unavailable (test)")
                : inner.IsCommittedAsync(id, ct);

        public Task<AuctionSnapshot> GetAuctionSnapshotAsync(AuctionSnapshotFilter filter, CancellationToken ct = default)
        {
            if (control.Mode == Mode.UnknownAfterCommit && control.Held && filter.AuctionId is not null)
            {
                control.RecoveryRefused.TrySetResult(true);
                throw new IOException("auction read unavailable (test)");
            }

            return inner.GetAuctionSnapshotAsync(filter, ct);
        }

        public Task<IReadOnlyList<MailRecord>> GetMailsAsync(int id, CancellationToken ct = default) => inner.GetMailsAsync(id, ct);
        public Task<IReadOnlyList<MailRecord>> GetExpiredMailsAsync(long now, int max, CancellationToken ct = default) => inner.GetExpiredMailsAsync(now, max, ct);
        public Task<IReadOnlyList<MailRecord>> GetMailsInvolvingAsync(int id, CancellationToken ct = default) => inner.GetMailsInvolvingAsync(id, ct);
        public Task<string?> GetItemTextAsync(uint id, CancellationToken ct = default) => inner.GetItemTextAsync(id, ct);
        public Task<IReadOnlyList<AuctionRecord>> GetAuctionsAsync(CancellationToken ct = default) => inner.GetAuctionsAsync(ct);
        public Task<IReadOnlyDictionary<uint, ItemInstanceData>> GetEscrowItemsAsync(IReadOnlyCollection<uint> ids, CancellationToken ct = default) => inner.GetEscrowItemsAsync(ids, ct);
        public Task<EconomyIdSeed> GetIdSeedAsync(CancellationToken ct = default) => inner.GetIdSeedAsync(ct);
    }

    private sealed class OwnedAuctioneer : IAuctioneerAccess
    {
        public AuctionHouseEntry? FindHouse(Player player, ObjectGuid auctioneer)
            => player.IsInWorld && auctioneer.Value == SyntheticArcaneServer.NpcGuid ? new(2, 0, 5) : null;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(60));
        private WorldClient? _client;

        private Harness(SyntheticArcaneServer server, Control control)
        {
            Server = server;
            Control = control;
            Economy = server.Services.GetRequiredService<EconomyFeature>();
        }

        public SyntheticArcaneServer Server { get; }
        public Control Control { get; }
        public EconomyFeature Economy { get; }
        public ScenarioConnection Connection { get; private set; } = null!;
        public ulong First { get; private set; }
        public ulong Second { get; private set; }
        public CancellationToken Token => _deadline.Token;

        public static async Task<Harness> StartAsync(Mode mode)
        {
            var control = new Control(mode);
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(services =>
            {
                services.AddSingleton<IAuctioneerAccess>(new OwnedAuctioneer());
                services.AddScoped<IEconomyStore>(provider => new ControlledStore(
                    new EfEconomyStore(provider.GetRequiredService<CharacterDbContext>()), control));
            }, startup.Token);
            var harness = new Harness(server, control);
            harness.Economy.Options.AuctionAccountConcurrentLimit = 1;
            await server.AddAccountAsync(Account, Password, harness.Token);
            return harness;
        }

        public async Task LoginFirstAsync()
        {
            await ConnectAsync();
            await Connection.CreateCharacterAsync("Quotafirst", Token);
            await Connection.CreateCharacterAsync("Quotasecond", Token);
            IReadOnlyList<MockCharacter> characters = await Connection.EnumerateAsync(Token);
            First = characters.Single(c => c.Name == "Quotafirst").Guid;
            Second = characters.Single(c => c.Name == "Quotasecond").Guid;
            await Connection.LoginAsync(First, Token);
        }

        /// <summary>Drop the first session, wait until it has left the world, and log the second character in.</summary>
        public async Task ReconnectAsSecondAsync()
        {
            await _client!.DisposeAsync();
            while (await Server.World.InvokeAsync(() => Server.World.OnlinePlayerCount).WaitAsync(Token) != 0)
            {
                await Task.Delay(10, Token);
            }

            await ConnectAsync();
            await Connection.LoginAsync(Second, Token);
        }

        public async Task<ulong> GiveItemAsync(ulong character, uint entry)
        {
            ulong item = await Server.World.InvokeAsync(() =>
            {
                Player player = Server.World.FindOnlinePlayer(new ObjectGuid(character))!;
                player.Money = 10_000;
                Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(entry, 1, out Item? added));
                Server.World.SavePlayer(player);
                return added!.Guid.Value;
            }).WaitAsync(Token);
            await Server.Services.GetRequiredService<CharacterSaveQueue>().FlushCharacterAsync(checked((int)character), Token);
            return item;
        }

        public Task<bool> HoldsItemAsync(ulong character, ulong item)
            => Server.World.InvokeAsync(() => Server.World.FindOnlinePlayer(new ObjectGuid(character))!
                .Inventory.GetItemByGuid(new ObjectGuid(item)) is not null).WaitAsync(Token);

        public Task SellAsync(ulong item)
        {
            var sell = new byte[28];
            BinaryPrimitives.WriteUInt64LittleEndian(sell, SyntheticArcaneServer.NpcGuid);
            BinaryPrimitives.WriteUInt64LittleEndian(sell.AsSpan(8), item);
            BinaryPrimitives.WriteUInt32LittleEndian(sell.AsSpan(16), 10);
            BinaryPrimitives.WriteUInt32LittleEndian(sell.AsSpan(24), 120);
            return Connection.SendAsync(WorldOpcode.CmsgAuctionSellItem, sell, Token);
        }

        public async Task<AuctionError> ReadSellResultAsync(List<string>? chat = null)
        {
            while (true)
            {
                WorldFrame frame = await Connection.ReadAsync(Token);
                if (frame.Opcode == (ushort)WorldOpcode.SmsgMessagechat)
                {
                    chat?.Add(System.Text.Encoding.UTF8.GetString(frame.Payload));
                }
                else if (frame.Opcode == (ushort)WorldOpcode.SmsgAuctionCommandResult)
                {
                    var reader = new PacketReader(frame.Payload);
                    reader.ReadUInt32();
                    Assert.Equal((uint)AuctionAction.Started, reader.ReadUInt32());
                    return (AuctionError)reader.ReadUInt32();
                }
            }
        }

        public Task<T> OnWorld<T>(Func<T> action) => Server.World.InvokeAsync(action).WaitAsync(Token);

        public async Task WaitUntilAsync(Func<bool> predicate)
        {
            while (!await OnWorld(predicate))
            {
                await Task.Delay(10, Token);
            }
        }

        public async Task<IReadOnlyList<AuctionRecord>> DurableAuctionsAsync()
        {
            await using AsyncServiceScope scope = Server.Services.CreateAsyncScope();
            return await new EfEconomyStore(scope.ServiceProvider.GetRequiredService<CharacterDbContext>()).GetAuctionsAsync(Token);
        }

        private async Task ConnectAsync()
        {
            LogonResult logon = await LogonClient.AuthenticateAsync(Server.RealmEndpoint, Account, Password, Token);
            _client = await WorldClient.ConnectAsync(Assert.Single(logon.Realms).GetLoopbackEndpoint(), Token);
            Assert.Equal((byte)0x0C, await _client.AuthenticateAsync(Account, logon.SessionKey, Token));
            Connection = new ScenarioConnection(_client);
        }

        public async ValueTask DisposeAsync()
        {
            Control.Release.TrySetResult(true);
            if (_client is not null)
            {
                await _client.DisposeAsync();
            }

            await Server.DisposeAsync();
            _deadline.Dispose();
        }
    }
}
