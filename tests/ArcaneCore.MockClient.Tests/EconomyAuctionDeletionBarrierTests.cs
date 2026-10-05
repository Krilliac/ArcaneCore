using System.Buffers.Binary;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Economy;
using ArcaneCore.Data.Stores;
using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

public sealed class EconomyAuctionDeletionBarrierTests
{
    private const string Account = "AUCTIONDELETE";
    private const string Password = "PASSWORD";

    [Fact]
    public async Task SuccessfulAuctionCommit_DisconnectThenDeleteWaitsForPublicationBeforeCacheCleanup()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = deadline.Token;
        var control = new Control();
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(services =>
        {
            services.AddSingleton<IAuctioneerAccess>(new OwnedAuctioneer());
            services.AddSingleton<ICharacterDeleteHook>(new ObservedDeleteHook(control));
            services.AddScoped<IEconomyStore>(provider => new HeldStore(
                new EfEconomyStore(provider.GetRequiredService<CharacterDbContext>()), control));
            services.AddScoped<ICharacterStore>(provider => new ObservedCharacters(
                new EfCharacterStore(provider.GetRequiredService<CharacterDbContext>()), control));
        }, token);
        await server.AddAccountAsync(Account, Password, token);
        await using WorldClient original = await AuthenticateAsync(server, token);
        var connection = new ScenarioConnection(original);
        await connection.CreateCharacterAsync("Auctionwatch", token);
        await connection.CreateCharacterAsync("Auctionsell", token);
        IReadOnlyList<MockCharacter> characters = await connection.EnumerateAsync(token);
        ulong seller = characters.Single(c => c.Name == "Auctionsell").Guid;
        ulong watcher = characters.Single(c => c.Name == "Auctionwatch").Guid;
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
        Task<byte[]>? deletion = null;
        try
        {
            var sell = new byte[28];
            BinaryPrimitives.WriteUInt64LittleEndian(sell, SyntheticArcaneServer.NpcGuid);
            BinaryPrimitives.WriteUInt64LittleEndian(sell.AsSpan(8), item);
            BinaryPrimitives.WriteUInt32LittleEndian(sell.AsSpan(16), 10);
            BinaryPrimitives.WriteUInt32LittleEndian(sell.AsSpan(24), 120);
            await connection.SendAsync(WorldOpcode.CmsgAuctionSellItem, sell, token);
            uint auction = await control.Committed.Task.WaitAsync(token);
            EconomyFeature economy = server.Services.GetRequiredService<EconomyFeature>();
            Assert.Equal(1, economy.Settlements.PendingCount);
            await original.DisposeAsync();
            while (await server.World.InvokeAsync(() => server.World.OnlinePlayerCount).WaitAsync(token) != 0)
            {
                await Task.Delay(10, token);
            }

            await using WorldClient nextClient = await AuthenticateAsync(server, token);
            var next = new ScenarioConnection(nextClient);
            Volatile.Write(ref control.ObserveDeletion, 1);
            await next.SendAsync(WorldOpcode.CmsgCharDelete, ScenarioWire.Guid(seller), token);
            deletion = next.ReadUntilAsync(WorldOpcode.SmsgCharDelete, token);
            await control.OwnershipRead.Task.WaitAsync(token);
            await Task.Delay(150, token);
            Assert.False(control.DeleteHookEntered.Task.IsCompleted);
            Assert.False(deletion.IsCompleted);
            await using (AsyncServiceScope scope = server.Services.CreateAsyncScope())
            {
                Assert.NotNull(await new EfCharacterStore(scope.ServiceProvider.GetRequiredService<CharacterDbContext>())
                    .GetByIdAsync(checked((int)seller), token));
            }

            control.Release.TrySetResult(true);
            Assert.Equal(new byte[] { 0x39 }, await deletion.WaitAsync(token));
            Assert.True(control.DeleteHookEntered.Task.IsCompleted);
            await economy.WaitForSettlementAsync(checked((int)seller), token);
            Assert.Empty(await server.World.InvokeAsync(() => economy.Auctions.ToArray()).WaitAsync(token));
            await using (AsyncServiceScope scope = server.Services.CreateAsyncScope())
            {
                CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
                Assert.Null(await new EfCharacterStore(db).GetByIdAsync(checked((int)seller), token));
                var store = new EfEconomyStore(db);
                Assert.DoesNotContain(await store.GetAuctionsAsync(token), row => row.Id == auction);
                Assert.Empty(await store.GetEscrowItemsAsync([new ObjectGuid(item).Low], token));
            }

            Assert.Single(await next.EnumerateAsync(token));
            await next.LoginAsync(watcher, token);
            var list = new byte[32];
            BinaryPrimitives.WriteUInt64LittleEndian(list, SyntheticArcaneServer.NpcGuid);
            foreach (int offset in new[] { 15, 19, 23, 27 })
            {
                BinaryPrimitives.WriteUInt32LittleEndian(list.AsSpan(offset), uint.MaxValue);
            }
            await next.SendAsync(WorldOpcode.CmsgAuctionListItems, list, token);
            byte[] auctions = await next.ReadUntilAsync(WorldOpcode.SmsgAuctionListResult, token);
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(auctions));
        }
        finally
        {
            control.Release.TrySetResult(true);
            if (deletion is not null)
            {
                try { await deletion.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException) { }
            }
        }
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

    private sealed class Control
    {
        internal int ObserveDeletion;
        internal TaskCompletionSource<uint> Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> OwnershipRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> DeleteHookEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ObservedDeleteHook(Control control) : ICharacterDeleteHook
    {
        public Task OnCharacterDeletingAsync(ArcaneCore.World.Net.WorldSession session, CharacterRecord character)
        {
            control.DeleteHookEntered.TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    private sealed class HeldStore(IEconomyStore inner, Control control) : IEconomyStore
    {
        public async Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest request, CancellationToken cancellationToken = default)
        {
            EconomyCommitResult result = await inner.CommitAsync(request, cancellationToken);
            if (result == EconomyCommitResult.Committed && request.Changes.OfType<InsertAuction>().SingleOrDefault() is { } insert)
            {
                control.Committed.TrySetResult(insert.Auction.Id);
                await control.Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
        public Task<bool> IsCommittedAsync(Guid id, CancellationToken ct = default) => inner.IsCommittedAsync(id, ct);
        public Task<IReadOnlyList<MailRecord>> GetMailsAsync(int id, CancellationToken ct = default) => inner.GetMailsAsync(id, ct);
        public Task<IReadOnlyList<MailRecord>> GetExpiredMailsAsync(long now, int max, CancellationToken ct = default) => inner.GetExpiredMailsAsync(now, max, ct);
        public Task<IReadOnlyList<MailRecord>> GetMailsInvolvingAsync(int id, CancellationToken ct = default) => inner.GetMailsInvolvingAsync(id, ct);
        public Task<string?> GetItemTextAsync(uint id, CancellationToken ct = default) => inner.GetItemTextAsync(id, ct);
        public Task<IReadOnlyList<AuctionRecord>> GetAuctionsAsync(CancellationToken ct = default) => inner.GetAuctionsAsync(ct);
        public Task<IReadOnlyDictionary<uint, ItemInstanceData>> GetEscrowItemsAsync(IReadOnlyCollection<uint> ids, CancellationToken ct = default) => inner.GetEscrowItemsAsync(ids, ct);
        public Task<AuctionSnapshot> GetAuctionSnapshotAsync(AuctionSnapshotFilter filter, CancellationToken ct = default) => inner.GetAuctionSnapshotAsync(filter, ct);
        public Task<EconomyIdSeed> GetIdSeedAsync(CancellationToken ct = default) => inner.GetIdSeedAsync(ct);
    }

    private sealed class ObservedCharacters(ICharacterStore inner, Control control) : ICharacterStore
    {
        public async Task<CharacterRecord?> GetByIdAsync(int id, CancellationToken ct = default)
        {
            CharacterRecord? character = await inner.GetByIdAsync(id, ct);
            if (Volatile.Read(ref control.ObserveDeletion) != 0) control.OwnershipRead.TrySetResult(true);
            return character;
        }
        public Task<IReadOnlyList<CharacterRecord>> GetByAccountAsync(int id, CancellationToken ct = default) => inner.GetByAccountAsync(id, ct);
        public Task<bool> IsNameTakenAsync(string name, CancellationToken ct = default) => inner.IsNameTakenAsync(name, ct);
        public Task<int> CountByAccountAsync(int id, CancellationToken ct = default) => inner.CountByAccountAsync(id, ct);
        public Task<CharacterRecord> CreateAsync(CharacterRecord row, CancellationToken ct = default) => inner.CreateAsync(row, ct);
        public Task<bool> DeleteAsync(int id, int accountId, CancellationToken ct = default) => inner.DeleteAsync(id, accountId, ct);
        public Task SaveStateAsync(CharacterState state, CancellationToken ct = default) => inner.SaveStateAsync(state, ct);
        public Task<IReadOnlyList<ActionButton>> GetActionButtonsAsync(int id, CancellationToken ct = default) => inner.GetActionButtonsAsync(id, ct);
        public Task<IReadOnlyList<CharacterIdentity>> GetAllIdentitiesAsync(CancellationToken ct = default) => inner.GetAllIdentitiesAsync(ct);
        public Task<IReadOnlyList<int>> FindAccountIdsByNamePrefixAsync(string prefix, int limit, CancellationToken ct = default) => inner.FindAccountIdsByNamePrefixAsync(prefix, limit, ct);
    }
}
