using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Economy;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Economy;

public sealed class AuctionRecoveryTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Deletion_resync_rejects_older_recovery_snapshot_for_removed_or_retained_auction(bool existing, bool removed)
    {
        await using var fixture = await Fixture.CreateAsync(existing, false);
        AuctionRecord committed = existing ? fixture.Primary with { BidderId = 99, Bid = 80 } : fixture.Primary;
        EconomyChange change = existing ? new UpdateAuction(fixture.Primary, committed) : new InsertAuction(committed);
        var unknown = NewCompletion<EconomyOutcome>();
        await fixture.OnWorld(() =>
        {
            fixture.Feature.RunAuctionOperation([], [change], 1, result => unknown.TrySetResult(result));
            return true;
        });
        Assert.Equal(EconomyOutcome.Unknown, await unknown.Task.WaitAsync(Fixture.Budget));
        await fixture.Control.RecoveryFailed.Task.WaitAsync(Fixture.Budget);
        fixture.Control.HoldRecoverySnapshot = true;
        fixture.Control.RecoveryAvailable = true;
        await fixture.Control.SnapshotCaptured.Task.WaitAsync(Fixture.Budget);
        AuctionRecord neutralized = committed with { BidderId = 0, Bid = 0 };
        await using (CharacterDbContext db = fixture.NewContext())
        {
            IReadOnlyList<EconomyChange> deletion = removed
                ? [new DeleteAuction(committed), new DeleteEscrowItem(committed.ItemGuid)]
                : [new UpdateAuction(committed, neutralized)];
            Assert.Equal(EconomyCommitResult.Committed, await new EfEconomyStore(db).CommitAsync(
                new EconomyCommitRequest(Guid.NewGuid(), [], deletion)));
        }
        if (!removed)
        {
            fixture.Control.RecoveryAvailable = false;
        }
        await fixture.OnWorld(() =>
        {
            fixture.Feature.ResyncDeletedCharacter(removed ? committed.SellerId : 99, [],
                new Dictionary<uint, ItemInstanceData>(), []);
            return true;
        });
        fixture.Control.SnapshotRelease.TrySetResult();
        await fixture.Feature.DrainAsync().WaitAsync(Fixture.Budget);
        await fixture.OnWorld(() =>
        {
            if (removed)
            {
                Assert.False(fixture.Feature.IsAuctionQuarantined(1));
                Assert.DoesNotContain(fixture.Feature.Auctions, view => view.Auction.Id == 1);
            }
            else
            {
                Assert.True(fixture.Feature.IsAuctionQuarantined(1));
                Assert.NotEqual(committed, fixture.Feature.Auctions.Single(view => view.Auction.Id == 1).Auction);
            }
            Assert.Equal(fixture.Other, fixture.Feature.Auctions.Single(view => view.Auction.Id == 2).Auction);
            return true;
        });
        if (!removed)
        {
            fixture.Control.RecoveryAvailable = true;
            await fixture.WaitUntilAsync(() => !fixture.Feature.IsAuctionQuarantined(1));
            Assert.Equal(neutralized, await fixture.OnWorld(() => fixture.Feature.Auctions.Single(view => view.Auction.Id == 1).Auction));
        }
        Assert.Equal(1, fixture.Control.PrimaryCommitAttempts);
    }

    [Fact]
    public async Task Known_committed_and_conflict_outcomes_release_the_reservation_without_quarantine()
    {
        await using var fixture = await Fixture.CreateAsync(true, false, loseAcknowledgement: false);
        AuctionRecord committed = fixture.Primary with { BidderId = 99, Bid = 80 };
        var after = NewCompletion<EconomyOutcome>();
        await fixture.OnWorld(() =>
        {
            fixture.Feature.RunAuctionOperation([], [new UpdateAuction(fixture.Primary, committed)], 1,
                outcome => after.TrySetResult(outcome));
            return true;
        });
        Assert.Equal(EconomyOutcome.After, await after.Task.WaitAsync(Fixture.Budget));
        var conflict = NewCompletion<EconomyOutcome>();
        await fixture.OnWorld(() =>
        {
            Assert.False(fixture.Feature.IsAuctionQuarantined(1));
            fixture.Feature.RunAuctionOperation([], [new UpdateAuction(fixture.Primary, committed)], 1,
                outcome => conflict.TrySetResult(outcome));
            return true;
        });
        Assert.Equal(EconomyOutcome.Before, await conflict.Task.WaitAsync(Fixture.Budget));
        var next = NewCompletion<EconomyOutcome>();
        await fixture.OnWorld(() =>
        {
            Assert.False(fixture.Feature.IsAuctionQuarantined(1));
            fixture.Feature.RunAuctionOperation([], [new UpdateAuction(committed, committed with { Bid = 100 })], 1,
                outcome => next.TrySetResult(outcome));
            return true;
        });
        Assert.Equal(EconomyOutcome.After, await next.Task.WaitAsync(Fixture.Budget));
        await using CharacterDbContext db = fixture.NewContext();
        Assert.Equal(2, await db.Set<EconomyOperationRow>().CountAsync());
    }

    [Fact]
    public async Task Recovery_removes_the_cached_auction_when_authoritative_row_no_longer_exists()
    {
        await using var fixture = await Fixture.CreateAsync(true, false);
        AuctionRecord committed = fixture.Primary with { BidderId = 99, Bid = 80 };
        var unknown = NewCompletion<EconomyOutcome>();
        await fixture.OnWorld(() =>
        {
            fixture.Feature.RunAuctionOperation([], [new UpdateAuction(fixture.Primary, committed)], 1,
                outcome => unknown.TrySetResult(outcome));
            return true;
        });
        Assert.Equal(EconomyOutcome.Unknown, await unknown.Task.WaitAsync(Fixture.Budget));
        Assert.True(await fixture.OnWorld(() => fixture.Feature.IsAuctionQuarantined(1)));
        await fixture.Control.RecoveryFailed.Task.WaitAsync(Fixture.Budget);
        await using (CharacterDbContext db = fixture.NewContext())
        {
            Assert.Equal(EconomyCommitResult.Committed, await new EfEconomyStore(db).CommitAsync(
                new EconomyCommitRequest(Guid.NewGuid(), [], [new DeleteAuction(committed), new DeleteEscrowItem(committed.ItemGuid)])));
        }
        fixture.Control.RecoveryAvailable = true;
        await fixture.WaitUntilAsync(() => !fixture.Feature.IsAuctionQuarantined(1));
        Assert.True(await fixture.OnWorld(() => fixture.Feature.Auctions.All(v => v.Auction.Id != 1)));
        await using CharacterDbContext final = fixture.NewContext();
        Assert.False(await final.Set<AuctionRow>().AnyAsync(a => a.Id == 1));
        Assert.False(await final.Set<ItemInstanceRow>().AnyAsync(i => i.Guid == committed.ItemGuid));
        Assert.Equal(1, fixture.Control.PrimaryCommitAttempts);
    }

    [Theory]
    [InlineData(false, false)] // New listing, authoritative auction read initially unavailable.
    [InlineData(true, false)] // Existing bid, authoritative auction read initially unavailable.
    [InlineData(true, true)] // Auction row readable but escrow read initially unavailable.
    public async Task Lost_ack_and_failed_reconciliation_reserve_then_recover_only_affected_auction(bool existing, bool failEscrow)
    {
        await using var fixture = await Fixture.CreateAsync(existing, failEscrow);
        AuctionRecord expected = fixture.Primary;
        AuctionRecord committed = existing ? expected with { BidderId = 99, Bid = 80 } : expected;
        EconomyChange change = existing ? new UpdateAuction(expected, committed) : new InsertAuction(committed);
        var outcome = NewCompletion<EconomyOutcome>();
        object unrelatedView = await fixture.OnWorld(() => fixture.Feature.Auctions.Single(v => v.Auction.Id == 2));

        await fixture.OnWorld(() =>
        {
            fixture.Feature.RunAuctionOperation([], [change], 1, result => outcome.TrySetResult(result));
            return true;
        });
        Assert.Equal(EconomyCommitResult.Committed, await fixture.Control.PrimaryCommitted.Task.WaitAsync(Fixture.Budget));
        Assert.Equal(EconomyOutcome.Unknown, await outcome.Task.WaitAsync(Fixture.Budget));

        await fixture.OnWorld(() =>
        {
            Assert.True(fixture.Feature.IsAuctionQuarantined(1));
            if (existing)
            {
                Assert.Equal(expected, fixture.Feature.Auctions.Single(v => v.Auction.Id == 1).Auction);
            }
            else
            {
                Assert.DoesNotContain(fixture.Feature.Auctions, v => v.Auction.Id == 1);
            }

            EconomyOutcome? refused = null;
            fixture.Feature.RunAuctionOperation([], [change], 1, result => refused = result);
            Assert.Equal(EconomyOutcome.NotStarted, refused);
            return true;
        });
        Assert.Equal(1, fixture.Control.PrimaryCommitAttempts);
        await fixture.Control.RecoveryFailed.Task.WaitAsync(Fixture.Budget);

        // Reserve another auction while recovery reads a full store snapshot. A targeted
        // repair must preserve that cached object and its in-flight reservation.
        AuctionRecord otherAfter = fixture.Other with { BidderId = 77, Bid = 60 };
        var otherOutcome = NewCompletion<EconomyOutcome>();
        await fixture.OnWorld(() =>
        {
            fixture.Feature.RunAuctionOperation([], [new UpdateAuction(fixture.Other, otherAfter)], 2,
                result => otherOutcome.TrySetResult(result));
            return true;
        });
        await fixture.Control.OtherEntered.Task.WaitAsync(Fixture.Budget);
        fixture.Control.RecoveryAvailable = true;
        // Exercise the dedicated bounded retry timer with optional expiry sweeping disabled.
        await fixture.WaitUntilAsync(() => !fixture.Feature.IsAuctionQuarantined(1));

        await fixture.OnWorld(() =>
        {
            var recovered = fixture.Feature.Auctions.Single(v => v.Auction.Id == 1);
            Assert.Equal(committed, recovered.Auction);
            Assert.Equal(committed.ItemGuid, recovered.Item.Guid);
            Assert.Equal(committed.ItemEntry, recovered.Item.Entry);
            Assert.Equal(committed.ItemCount, recovered.Item.Count);
            Assert.Same(unrelatedView, fixture.Feature.Auctions.Single(v => v.Auction.Id == 2));
            EconomyOutcome? refused = null;
            fixture.Feature.RunAuctionOperation([], [new UpdateAuction(fixture.Other, otherAfter)], 2,
                result => refused = result);
            Assert.Equal(EconomyOutcome.NotStarted, refused);
            return true;
        });

        // The repaired expected record permits the next real EF transaction; the lost
        // operation was never replayed and its item still has exactly one escrow owner.
        AuctionRecord next = committed with { BidderId = 100, Bid = 100 };
        var nextOutcome = NewCompletion<EconomyOutcome>();
        await fixture.OnWorld(() =>
        {
            fixture.Feature.RunAuctionOperation([], [new UpdateAuction(committed, next)], 1,
                result => nextOutcome.TrySetResult(result));
            return true;
        });
        Assert.Equal(EconomyOutcome.After, await nextOutcome.Task.WaitAsync(Fixture.Budget));
        fixture.Control.OtherRelease.TrySetResult();
        Assert.Equal(EconomyOutcome.After, await otherOutcome.Task.WaitAsync(Fixture.Budget));
        await using CharacterDbContext db = fixture.NewContext();
        Assert.Equal(next, (await db.Set<AuctionRow>().AsNoTracking().SingleAsync(a => a.Id == 1)).ToRecord());
        Assert.Equal(otherAfter, (await db.Set<AuctionRow>().AsNoTracking().SingleAsync(a => a.Id == 2)).ToRecord());
        Assert.Equal(3, await db.Set<EconomyOperationRow>().CountAsync());
        Assert.Equal(1, await db.Set<ItemInstanceRow>().CountAsync(i => i.Guid == expected.ItemGuid && i.OwnerGuid == 0));
        Assert.Equal(1, await db.Set<AuctionRow>().CountAsync(a => a.ItemGuid == expected.ItemGuid));
        Assert.Equal(2, fixture.Control.PrimaryCommitAttempts);
    }

    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Fixture : IAsyncDisposable
    {
        public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
        private readonly string _path;
        private readonly DbContextOptions<CharacterDbContext> _options;
        private readonly ServiceProvider _services;
        private readonly CharacterSaveQueue _saves;

        private Fixture(string path, DbContextOptions<CharacterDbContext> options, ServiceProvider services,
            Control control, AuctionRecord primary, AuctionRecord other)
        {
            _path = path;
            _options = options;
            _services = services;
            Control = control;
            Primary = primary;
            Other = other;
            _saves = services.GetRequiredService<CharacterSaveQueue>();
            World = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
                _saves, NullLogger<WorldRuntime>.Instance);
            Feature = new EconomyFeature(services, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<EconomyFeature>.Instance);
            Feature.Options.ExpirySweepSeconds = 0;
            Feature.Attach(World);
            World.Start();
        }

        public Control Control { get; }
        public AuctionRecord Primary { get; }
        public AuctionRecord Other { get; }
        public WorldRuntime World { get; }
        public EconomyFeature Feature { get; }
        public CharacterDbContext NewContext() => new(_options);
        public Task<T> OnWorld<T>(Func<T> action) => World.InvokeAsync(action).WaitAsync(Budget);

        public async Task WaitUntilAsync(Func<bool> predicate)
        {
            using var timeout = new CancellationTokenSource(Budget);
            while (!await OnWorld(predicate).WaitAsync(timeout.Token))
            {
                await Task.Delay(10, timeout.Token);
            }
        }

        public static async Task<Fixture> CreateAsync(bool existing, bool failEscrow, bool loseAcknowledgement = true)
        {
            string path = Path.Combine(Path.GetTempPath(), $"arcanecore-auction-recovery-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            long expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 7200;
            var primary = new AuctionRecord
            {
                Id = 1, HouseId = 1, ItemGuid = 100, ItemEntry = 4000, ItemCount = 2,
                SellerId = 10, StartBid = 50, Buyout = 500, ExpireTime = expires, Deposit = 1,
            };
            AuctionRecord other = primary with { Id = 2, ItemGuid = 200, ItemEntry = 4001, SellerId = 20 };
            await using (var db = new CharacterDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                foreach (AuctionRecord row in new[] { primary, other })
                {
                    var item = new ItemInstanceRow { Guid = row.ItemGuid };
                    item.CopyFrom(0, new ItemInstanceData { Guid = row.ItemGuid, Entry = row.ItemEntry, Count = row.ItemCount });
                    db.Add(item);
                    if (row.Id == 2 || existing)
                    {
                        var auction = new AuctionRow();
                        auction.CopyFrom(row);
                        db.Add(auction);
                    }
                }
                await db.SaveChangesAsync();
            }

            var control = new Control { FailEscrow = failEscrow, LoseAcknowledgement = loseAcknowledgement };
            var services = new ServiceCollection();
            services.AddScoped(_ => new CharacterDbContext(options));
            services.AddScoped<IEconomyStore>(provider => new ControlledStore(
                new EfEconomyStore(provider.GetRequiredService<CharacterDbContext>()), control));
            services.AddSingleton(provider => new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CharacterSaveQueue>.Instance));
            return new Fixture(path, options, services.BuildServiceProvider(), control, primary, other);
        }

        public async ValueTask DisposeAsync()
        {
            Control.OtherRelease.TrySetResult();
            Control.SnapshotRelease.TrySetResult();
            await Feature.DisposeAsync();
            World.Dispose();
            await _saves.StopAsync();
            await _services.DisposeAsync();
            File.Delete(_path);
        }
    }

    private sealed class Control
    {
        public bool FailEscrow { get; init; }
        public bool LoseAcknowledgement { get; init; }
        public volatile bool RecoveryAvailable;
        public volatile bool LostAcknowledgement;
        public volatile bool HoldRecoverySnapshot;
        public int SnapshotClaimed;
        public Guid UnknownOperation;
        public int PrimaryCommitAttempts;
        public TaskCompletionSource<EconomyCommitResult> PrimaryCommitted { get; } = NewCompletion<EconomyCommitResult>();
        public TaskCompletionSource RecoveryFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OtherEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OtherRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SnapshotCaptured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SnapshotRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ControlledStore(IEconomyStore inner, Control control) : IEconomyStore
    {
        public async Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest request, CancellationToken cancellationToken = default)
        {
            bool primary = request.Changes.Any(c => c is InsertAuction { Auction.Id: 1 } or UpdateAuction { Expected.Id: 1 });
            if (!primary)
            {
                control.OtherEntered.TrySetResult();
                await control.OtherRelease.Task.WaitAsync(cancellationToken);
            }
            int attempt = primary ? Interlocked.Increment(ref control.PrimaryCommitAttempts) : 0;
            EconomyCommitResult result = await inner.CommitAsync(request, cancellationToken);
            if (primary && attempt == 1 && control.LoseAcknowledgement)
            {
                control.UnknownOperation = request.OperationId;
                control.LostAcknowledgement = true;
                control.PrimaryCommitted.TrySetResult(result);
                throw new IOException("controlled acknowledgement loss after actual EF commit");
            }
            return result;
        }

        public Task<bool> IsCommittedAsync(Guid operationId, CancellationToken cancellationToken = default)
            => operationId == control.UnknownOperation
                ? Task.FromException<bool>(new IOException("controlled initial ledger read failure"))
                : inner.IsCommittedAsync(operationId, cancellationToken);

        public Task<IReadOnlyList<AuctionRecord>> GetAuctionsAsync(CancellationToken cancellationToken = default)
        {
            if (control.LostAcknowledgement && !control.RecoveryAvailable && !control.FailEscrow)
            {
                control.RecoveryFailed.TrySetResult();
                return Task.FromException<IReadOnlyList<AuctionRecord>>(new IOException("controlled recovery auction read failure"));
            }
            return inner.GetAuctionsAsync(cancellationToken);
        }

        public async Task<IReadOnlyDictionary<uint, ItemInstanceData>> GetEscrowItemsAsync(IReadOnlyCollection<uint> itemGuids,
            CancellationToken cancellationToken = default)
        {
            if (control.LostAcknowledgement && !control.RecoveryAvailable && control.FailEscrow)
            {
                control.RecoveryFailed.TrySetResult();
                throw new IOException("controlled recovery escrow read failure");
            }
            IReadOnlyDictionary<uint, ItemInstanceData> snapshot = await inner.GetEscrowItemsAsync(itemGuids, cancellationToken);
            if (control.HoldRecoverySnapshot && Interlocked.CompareExchange(ref control.SnapshotClaimed, 1, 0) == 0)
            {
                control.SnapshotCaptured.TrySetResult();
                await control.SnapshotRelease.Task.WaitAsync(cancellationToken);
            }
            return snapshot;
        }

        public Task<IReadOnlyList<MailRecord>> GetMailsAsync(int id, CancellationToken ct = default) => inner.GetMailsAsync(id, ct);
        public Task<IReadOnlyList<MailRecord>> GetExpiredMailsAsync(long now, int max, CancellationToken ct = default) => inner.GetExpiredMailsAsync(now, max, ct);
        public Task<IReadOnlyList<MailRecord>> GetMailsInvolvingAsync(int id, CancellationToken ct = default) => inner.GetMailsInvolvingAsync(id, ct);
        public Task<string?> GetItemTextAsync(uint id, CancellationToken ct = default) => inner.GetItemTextAsync(id, ct);
        public Task<EconomyIdSeed> GetIdSeedAsync(CancellationToken ct = default) => inner.GetIdSeedAsync(ct);
    }
}
