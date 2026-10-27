using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Economy;
using ArcaneCore.Game.Economy;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.World.Economy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.MockClient.Tests.EconomyMailSendParityTests;
using System.Reflection;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.MockClient.Tests;

public sealed class EconomyMailRefreshRaceTests
{
    [Fact]
    public async Task NewerMailRefreshWinsOverOlderLoginRead_AndDeleteStillRefusesCod()
    {
        var control = new DelayedReadControl();
        await using Rig rig = await Rig.StartAsync(loginReceiver: true,
            services => services.AddScoped<IEconomyStore>(provider => new DelayedReadStore(
                new EfEconomyStore(provider.GetRequiredService<CharacterDbContext>()), control)));
        rig.Server.Services.GetRequiredService<EconomyFeature>().Options.AllowDeleteWithAttachments = true;

        await control.FirstReadCaptured.Task.WaitAsync(rig.Token);
        try
        {
            await SeedAsync(rig);
            await rig.Receiver!.SendAsync(WorldOpcode.CmsgGetMailList, ScenarioWire.Guid(Mailbox.Value), rig.Token);
            Assert.Equal(2, (await rig.Receiver.ReadUntilAsync(WorldOpcode.SmsgMailListResult, rig.Token))[0]);

            control.ReleaseFirstRead.TrySetResult(true);
            await rig.Server.Services.GetRequiredService<EconomyFeature>().DrainAsync().WaitAsync(rig.Token);
            uint[] cached = await rig.Server.World.InvokeAsync(() => rig.Server.Services.GetRequiredService<EconomyFeature>()
                .CachedMail(checked((int)rig.ReceiverGuid))!.Select(m => m.Mail.Id).Order().ToArray()).WaitAsync(rig.Token);
            Assert.Equal(new uint[] { 6002, 6003 }, cached);

            foreach ((uint id, MailResult expected) in new[] { (6002u, MailResult.Ok), (6003u, MailResult.InternalError) })
            {
                await rig.Receiver.SendAsync(WorldOpcode.CmsgMailDelete, ScenarioWire.GuidQuest(Mailbox.Value, id), rig.Token);
                var reader = new PacketReader(await rig.Receiver.ReadUntilAsync(WorldOpcode.SmsgSendMailResult, rig.Token));
                Assert.Equal(id, reader.ReadUInt32());
                Assert.Equal((uint)MailAction.Deleted, reader.ReadUInt32());
                Assert.Equal(expected, (MailResult)reader.ReadUInt32());
            }

            await rig.Server.Services.GetRequiredService<EconomyFeature>().DrainAsync().WaitAsync(rig.Token);
            await using AsyncServiceScope verify = rig.Server.Services.CreateAsyncScope();
            CharacterDbContext db = verify.ServiceProvider.GetRequiredService<CharacterDbContext>();
            EfEconomyStore store = new(db);
            Assert.DoesNotContain(await store.GetMailsAsync(checked((int)rig.ReceiverGuid), rig.Token), m => m.Id == 6002);
            Assert.Contains(await store.GetMailsAsync(checked((int)rig.ReceiverGuid), rig.Token), m => m.Id == 6003 && m.Cod == 5);
            IReadOnlyDictionary<uint, ItemInstanceData> escrow = await store.GetEscrowItemsAsync([7003, 7004], rig.Token);
            Assert.DoesNotContain(7003u, escrow.Keys);
            Assert.Contains(7004u, escrow.Keys);
        }
        finally
        {
            control.ReleaseFirstRead.TrySetResult(true);
        }
    }

    [Fact]
    public async Task AFloodOfMailListRequests_SharesOneMailboxRead_AndStillAnswers()
    {
        var gate = new ListReadGate();
        await using Rig rig = await Rig.StartAsync(loginReceiver: true,
            services => services.AddScoped<IEconomyStore>(provider => new GatedReadStore(
                new EfEconomyStore(provider.GetRequiredService<CharacterDbContext>()), gate)));
        EconomyFeature economy = rig.Server.Services.GetRequiredService<EconomyFeature>();
        await economy.DrainAsync().WaitAsync(rig.Token);

        int baseline = Volatile.Read(ref gate.Calls);
        gate.Armed = true;
        try
        {
            const int Requests = 20;
            for (int i = 0; i < Requests; i++)
            {
                await rig.Receiver!.SendAsync(WorldOpcode.CmsgGetMailList, ScenarioWire.Guid(Mailbox.Value), rig.Token);
            }

            // Packets are handled in order, so the answer to this one means every list request above has been handled.
            await rig.Receiver!.SendAsync(WorldOpcode.MsgQueryNextMailTime, [], rig.Token);
            await rig.Receiver.ReadUntilAsync(WorldOpcode.MsgQueryNextMailTime, rig.Token);
        }
        finally
        {
            gate.Release.TrySetResult(true);
        }

        await economy.DrainAsync().WaitAsync(rig.Token);
        await rig.Receiver.ReadUntilAsync(WorldOpcode.SmsgMailListResult, rig.Token);
        Assert.Equal(1, Volatile.Read(ref gate.Calls) - baseline);
    }

    [Fact]
    public async Task LaterNonListReadBumpingReadGeneration_DoesNotDropOriginalListReply()
    {
        var gate = new ListReadGate();
        await using Rig rig = await Rig.StartAsync(loginReceiver: true,
            services => services.AddScoped<IEconomyStore>(provider => new GatedReadStore(
                new EfEconomyStore(provider.GetRequiredService<CharacterDbContext>()), gate)));
        EconomyFeature economy = rig.Server.Services.GetRequiredService<EconomyFeature>();
        await economy.DrainAsync().WaitAsync(rig.Token);

        await SeedAsync(rig);

        gate.Armed = true;
        try
        {
            // 1. Client requests mail list: starts a background DB read with sendList = true (generation 1).
            await rig.Receiver!.SendAsync(WorldOpcode.CmsgGetMailList, ScenarioWire.Guid(Mailbox.Value), rig.Token);

            // Wait until the list read has started and entered the store.
            await gate.FirstReadEntered.Task.WaitAsync(rig.Token);

            // 2. A later non-list read arrives (login / delete cleanup path) and bumps ReadGeneration to 2.
            await rig.Server.World.InvokeAsync(() =>
            {
                Player receiver = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!;
                typeof(EconomyFeature).GetMethod("LoadMailbox", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(economy, [receiver, false]); return true;
            }).WaitAsync(rig.Token);
        }
        finally
        {
            gate.Release.TrySetResult(true);
        }

        await economy.DrainAsync().WaitAsync(rig.Token);

        // Even though generation was bumped by the non-list read, the original requester still gets their mail list.
        byte[] result = await rig.Receiver!.ReadUntilAsync(WorldOpcode.SmsgMailListResult, rig.Token);
        Assert.Equal(2, result[0]);
    }

    [Fact]
    public async Task FailedNonListReadSupersedingTheListRead_StillAnswersTheWaitingListRequest()
    {
        var gate = new ListReadGate();
        await using Rig rig = await Rig.StartAsync(loginReceiver: true,
            services => services.AddScoped<IEconomyStore>(provider => new GatedReadStore(
                new EfEconomyStore(provider.GetRequiredService<CharacterDbContext>()), gate)));
        EconomyFeature economy = rig.Server.Services.GetRequiredService<EconomyFeature>();
        await economy.DrainAsync().WaitAsync(rig.Token);

        await SeedAsync(rig);

        // The list read is the next store call, the superseding non-list read the one after it (it throws), and the list is read again after that.
        gate.ThrowOnCall = Volatile.Read(ref gate.Calls) + 2;
        gate.Armed = true;
        try
        {
            await rig.Receiver!.SendAsync(WorldOpcode.CmsgGetMailList, ScenarioWire.Guid(Mailbox.Value), rig.Token);
            await gate.FirstReadEntered.Task.WaitAsync(rig.Token);

            await rig.Server.World.InvokeAsync(() =>
            {
                Player receiver = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.ReceiverGuid))!;
                typeof(EconomyFeature).GetMethod("LoadMailbox", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(economy, [receiver, false]); return true;
            }).WaitAsync(rig.Token);
        }
        finally
        {
            gate.Release.TrySetResult(true);
        }

        await economy.DrainAsync().WaitAsync(rig.Token);

        byte[] result = await rig.Receiver!.ReadUntilAsync(WorldOpcode.SmsgMailListResult, rig.Token);
        Assert.Equal(2, result[0]);
        Assert.Equal(gate.ThrowOnCall + 1, Volatile.Read(ref gate.Calls));
    }

    private static async Task SeedAsync(Rig rig)
    {
        await using (AsyncServiceScope itemScope = rig.Server.Services.CreateAsyncScope())
        {
            CharacterDbContext items = itemScope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            foreach (uint guid in new[] { 7003u, 7004u })
            {
                var row = new ItemInstanceRow { Guid = guid };
                row.CopyFrom(0, new ItemInstanceData { Guid = guid, Entry = 117, Count = 1, Charges = new int[5], Enchantments = new uint[21] });
                items.Add(row);
            }

            await items.SaveChangesAsync(rig.Token);
        }

        long now = rig.Clock.GetUtcNow().ToUnixTimeSeconds();
        var letters = new[]
        {
            new MailRecord { Id = 6002, MessageType = MailMessageType.Normal, SenderId = checked((uint)rig.SenderGuid), ReceiverId = checked((int)rig.ReceiverGuid), Subject = "attachment", ItemGuid = 7003, ItemEntry = 117, Checked = MailCheckMask.Copied, DeliverTime = now - 1, ExpireTime = now + 86400 },
            new MailRecord { Id = 6003, MessageType = MailMessageType.Normal, SenderId = checked((uint)rig.SenderGuid), ReceiverId = checked((int)rig.ReceiverGuid), Subject = "cod", ItemGuid = 7004, ItemEntry = 117, Cod = 5, Checked = MailCheckMask.Copied, DeliverTime = now - 1, ExpireTime = now + 86400 },
        };
        await using AsyncServiceScope mailScope = rig.Server.Services.CreateAsyncScope();
        CharacterDbContext db = mailScope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        Assert.Equal(EconomyCommitResult.Committed, await new EfEconomyStore(db).CommitAsync(
            new EconomyCommitRequest(Guid.NewGuid(), [], [.. letters.Select(l => (EconomyChange)new InsertMail(l, null))]), rig.Token));
    }

    private sealed class DelayedReadControl
    {
        internal TaskCompletionSource<bool> FirstReadCaptured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> ReleaseFirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int FirstReceiverId = -1;
    }

    private sealed class ListReadGate
    {
        internal volatile bool Armed;
        internal int Calls;

        /// <summary>The 1-based store call that throws instead of reading (0: none).</summary>
        internal int ThrowOnCall;
        internal TaskCompletionSource<bool> FirstReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class GatedReadStore(IEconomyStore inner, ListReadGate gate) : IEconomyStore
    {
        public async Task<IReadOnlyList<MailRecord>> GetMailsAsync(int id, CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref gate.Calls);
            if (call == gate.ThrowOnCall)
            {
                throw new InvalidOperationException("injected mail read failure");
            }

            IReadOnlyList<MailRecord> result = await inner.GetMailsAsync(id, cancellationToken);
            if (gate.Armed)
            {
                gate.FirstReadEntered.TrySetResult(true);
                await gate.Release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }

        public Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest r, CancellationToken c = default) => inner.CommitAsync(r, c);
        public Task<bool> IsCommittedAsync(Guid id, CancellationToken c = default) => inner.IsCommittedAsync(id, c);
        public Task<IReadOnlyList<MailRecord>> GetExpiredMailsAsync(long n, int m, CancellationToken c = default) => inner.GetExpiredMailsAsync(n, m, c);
        public Task<IReadOnlyList<MailRecord>> GetMailsInvolvingAsync(int id, CancellationToken c = default) => inner.GetMailsInvolvingAsync(id, c);
        public Task<string?> GetItemTextAsync(uint id, CancellationToken c = default) => inner.GetItemTextAsync(id, c);
        public Task<IReadOnlyList<AuctionRecord>> GetAuctionsAsync(CancellationToken c = default) => inner.GetAuctionsAsync(c);
        public Task<IReadOnlyDictionary<uint, ItemInstanceData>> GetEscrowItemsAsync(IReadOnlyCollection<uint> ids, CancellationToken c = default) => inner.GetEscrowItemsAsync(ids, c);
        public Task<AuctionSnapshot> GetAuctionSnapshotAsync(AuctionSnapshotFilter f, CancellationToken c = default) => inner.GetAuctionSnapshotAsync(f, c);
        public Task<EconomyIdSeed> GetIdSeedAsync(CancellationToken c = default) => inner.GetIdSeedAsync(c);
    }

    private sealed class DelayedReadStore(IEconomyStore inner, DelayedReadControl control) : IEconomyStore
    {
        public async Task<IReadOnlyList<MailRecord>> GetMailsAsync(int id, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<MailRecord> result = await inner.GetMailsAsync(id, cancellationToken);
            if (Interlocked.CompareExchange(ref control.FirstReceiverId, id, -1) == -1)
            {
                control.FirstReadCaptured.TrySetResult(true);
                await control.ReleaseFirstRead.Task.WaitAsync(cancellationToken);
            }

            return result;
        }

        public Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest r, CancellationToken c = default) => inner.CommitAsync(r, c);
        public Task<bool> IsCommittedAsync(Guid id, CancellationToken c = default) => inner.IsCommittedAsync(id, c);
        public Task<IReadOnlyList<MailRecord>> GetExpiredMailsAsync(long n, int m, CancellationToken c = default) => inner.GetExpiredMailsAsync(n, m, c);
        public Task<IReadOnlyList<MailRecord>> GetMailsInvolvingAsync(int id, CancellationToken c = default) => inner.GetMailsInvolvingAsync(id, c);
        public Task<string?> GetItemTextAsync(uint id, CancellationToken c = default) => inner.GetItemTextAsync(id, c);
        public Task<IReadOnlyList<AuctionRecord>> GetAuctionsAsync(CancellationToken c = default) => inner.GetAuctionsAsync(c);
        public Task<IReadOnlyDictionary<uint, ItemInstanceData>> GetEscrowItemsAsync(IReadOnlyCollection<uint> ids, CancellationToken c = default) => inner.GetEscrowItemsAsync(ids, c);
        public Task<AuctionSnapshot> GetAuctionSnapshotAsync(AuctionSnapshotFilter f, CancellationToken c = default) => inner.GetAuctionSnapshotAsync(f, c);
        public Task<EconomyIdSeed> GetIdSeedAsync(CancellationToken c = default) => inner.GetIdSeedAsync(c);
    }
}
