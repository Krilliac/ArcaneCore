using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Economy;
using ArcaneCore.Game.Maps;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Tests.Npc;
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
    public async Task Known_commit_releases_and_conflict_resyncs_then_releases()
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
        var conflict = NewCompletion<(EconomyOutcome Outcome, bool Reserved)>();
        await fixture.OnWorld(() =>
        {
            Assert.False(fixture.Feature.IsAuctionQuarantined(1));
            // Observe the reservation in the completion callback (world thread): the recovery read starts at once and its
            // world-thread completion can release the ID before a separate OnWorld probe from the test thread gets to run.
            fixture.Feature.RunAuctionOperation([], [new UpdateAuction(fixture.Primary, committed)], 1,
                outcome => conflict.TrySetResult((outcome, fixture.Feature.IsAuctionQuarantined(1))));
            return true;
        });
        (EconomyOutcome conflictOutcome, bool reserved) = await conflict.Task.WaitAsync(Fixture.Budget);
        Assert.Equal(EconomyOutcome.Before, conflictOutcome);
        // A refused same-ID update means the cache no longer matches the row: the ID stays
        // reserved until a fresh authoritative read repairs it, then it is released.
        Assert.True(reserved, "a conflicting update must reserve the auction until it is resynced");
        Assert.True(await fixture.TryWaitUntilAsync(() => !fixture.Feature.IsAuctionQuarantined(1)),
            "the reserved auction was never resynced and released");
        var next = NewCompletion<EconomyOutcome>();
        await fixture.OnWorld(() =>
        {
            Assert.Equal(committed, fixture.Feature.Auctions.Single(v => v.Auction.Id == 1).Auction);
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
        // The failEscrow case is a readable row with a mismatching escrow: it backs off on the feature clock.
        ManualQuestClock? clock = failEscrow ? new ManualQuestClock() : null;
        await using var fixture = await Fixture.CreateAsync(existing, failEscrow, clock: clock);
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
        clock?.Advance(TimeSpan.FromSeconds(31));
        // Exercise the dedicated bounded retry timer with optional expiry sweeping disabled.
        await fixture.WaitUntilAsync(() =>
        {
            // RecoveryFailed signals before the partial snapshot finishes and its world-thread
            // callback publishes NotBeforeMs. That callback can follow the one-shot advance,
            // so continue advancing the fake clock while the ordinary retry timer runs.
            // The real five-second budget and all recovery/reservation assertions stay intact.
            clock?.Advance(TimeSpan.FromSeconds(1));
            return !fixture.Feature.IsAuctionQuarantined(1);
        });

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

    [Fact]
    public async Task Conflict_after_external_same_id_update_resyncs_only_that_auction()
    {
        await using var fixture = await Fixture.CreateAsync(true, false, loseAcknowledgement: false);
        AuctionRecord external = fixture.Primary with { BidderId = 55, Bid = 70 };
        await using (CharacterDbContext db = fixture.NewContext())
        {
            Assert.Equal(EconomyCommitResult.Committed, await new EfEconomyStore(db).CommitAsync(
                new EconomyCommitRequest(Guid.NewGuid(), [], [new UpdateAuction(fixture.Primary, external)])));
        }

        object unrelatedView = await fixture.OnWorld(() => fixture.Feature.Auctions.Single(v => v.Auction.Id == 2));
        var outcome = NewCompletion<EconomyOutcome>();
        await fixture.OnWorld(() =>
        {
            fixture.Feature.RunAuctionOperation([], [new UpdateAuction(fixture.Primary, fixture.Primary with { BidderId = 56, Bid = 90 })], 1,
                result => outcome.TrySetResult(result));
            return true;
        });
        Assert.Equal(EconomyOutcome.Before, await outcome.Task.WaitAsync(Fixture.Budget));
        Assert.True(await fixture.TryWaitUntilAsync(() => !fixture.Feature.IsAuctionQuarantined(1)
                && fixture.Feature.Auctions.Single(v => v.Auction.Id == 1).Auction == external),
            "the cache for the externally changed auction was never resynced");
        await fixture.OnWorld(() =>
        {
            Assert.Same(unrelatedView, fixture.Feature.Auctions.Single(v => v.Auction.Id == 2));
            return true;
        });
    }

    [Fact]
    public async Task Conflicting_expiry_backs_off_and_does_not_starve_later_expired_auction()
    {
        const int Poisoned = 16;
        int goodSeller = 0;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using var fixture = await Fixture.CreateAsync(true, false, loseAcknowledgement: false, seed: async db =>
        {
            goodSeller = (await new EfCharacterStore(db).CreateAsync(new CharacterRecord
            { AccountId = 1, Name = "Goodseller", Race = 1, Class = 1, Level = 10 })).Id;
            for (int i = 0; i < Poisoned + 1; i++)
            {
                bool good = i == Poisoned;
                // Poisoned sellers have no characters row: the expiry letter is refused every time.
                var auction = new AuctionRecord
                {
                    Id = (uint)(100 + i), HouseId = 1, ItemGuid = (uint)(1000 + i), ItemEntry = 4100, ItemCount = 1,
                    SellerId = good ? goodSeller : 5000 + i, StartBid = 50, Buyout = 0,
                    ExpireTime = now - 1000 + i, Deposit = 1,
                };
                var item = new ItemInstanceRow { Guid = auction.ItemGuid };
                item.CopyFrom(0, new ItemInstanceData { Guid = auction.ItemGuid, Entry = auction.ItemEntry, Count = 1 });
                db.Add(item);
                var row = new AuctionRow();
                row.CopyFrom(auction);
                db.Add(row);
            }

            await db.SaveChangesAsync();
        });

        uint goodId = 100 + Poisoned;
        bool settled = false;
        for (int sweep = 0; sweep < 6 && !settled; sweep++)
        {
            await fixture.OnWorld(() =>
            {
                fixture.Feature.RunExpirySweep();
                return true;
            });
            await fixture.Feature.DrainAsync().WaitAsync(Fixture.Budget);
            await using CharacterDbContext probe = fixture.NewContext();
            settled = !await probe.Set<AuctionRow>().AnyAsync(a => a.Id == goodId);
        }

        Assert.True(settled, "the later expired auction was starved by earlier auctions that always conflict");
        await using CharacterDbContext db = fixture.NewContext();
        Assert.Equal(1, await db.Set<MailRow>().CountAsync(m => m.ReceiverId == goodSeller));
        Assert.True(await fixture.TryWaitUntilAsync(() => fixture.Feature.Auctions.All(v => v.Auction.Id != goodId)));
        Assert.Equal(Poisoned, await db.Set<AuctionRow>().CountAsync(a => a.Id >= 100 && a.Id < goodId));
    }

    [Fact]
    public async Task Startup_reserves_auction_without_matching_escrow_and_lists_it_after_repair()
    {
        var clock = new ManualQuestClock();
        await using var fixture = await Fixture.CreateAsync(true, false, loseAcknowledgement: false, clock: clock, omitPrimaryEscrow: true);
        await fixture.OnWorld(() =>
        {
            Assert.True(fixture.Feature.IsAuctionQuarantined(1), "an auction without its escrow item must be reserved, not silently dropped");
            Assert.DoesNotContain(fixture.Feature.Auctions, v => v.Auction.Id == 1);
            Assert.Contains(fixture.Feature.Auctions, v => v.Auction.Id == 2);
            return true;
        });
        await using (CharacterDbContext db = fixture.NewContext())
        {
            var item = new ItemInstanceRow { Guid = fixture.Primary.ItemGuid };
            item.CopyFrom(0, new ItemInstanceData { Guid = fixture.Primary.ItemGuid, Entry = fixture.Primary.ItemEntry, Count = fixture.Primary.ItemCount });
            db.Add(item);
            await db.SaveChangesAsync();
        }

        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.True(await fixture.TryWaitUntilAsync(() => !fixture.Feature.IsAuctionQuarantined(1)
                && fixture.Feature.Auctions.Any(v => v.Auction.Id == 1)),
            "the repaired auction was never listed");
    }

    [Fact]
    public async Task Escrow_mismatch_backs_off_on_the_feature_clock()
    {
        var clock = new ManualQuestClock();
        // Exactly one failed read: the recovery timer retries a failed read on wall-clock ticks and, after three failures, backs off on the
        // feature clock, which this test freezes. Without the hold, a slow escrow removal below let a fourth failure park the retry forever.
        await using var fixture = await Fixture.CreateAsync(true, false, clock: clock, holdRetryAfterFirstFailure: true);
        AuctionRecord committed = fixture.Primary with { BidderId = 99, Bid = 80 };
        var unknown = NewCompletion<EconomyOutcome>();
        await fixture.OnWorld(() =>
        {
            fixture.Feature.RunAuctionOperation([], [new UpdateAuction(fixture.Primary, committed)], 1, result => unknown.TrySetResult(result));
            return true;
        });
        Assert.Equal(EconomyOutcome.Unknown, await unknown.Task.WaitAsync(Fixture.Budget));
        await fixture.Control.RecoveryFailed.Task.WaitAsync(Fixture.Budget);
        await using (CharacterDbContext db = fixture.NewContext())
        {
            // CommitAsync refuses to orphan an auction reference, so remove the escrow row directly.
            db.Remove(await db.Set<ItemInstanceRow>().SingleAsync(i => i.Guid == committed.ItemGuid));
            await db.SaveChangesAsync();
        }

        fixture.Control.RecoveryAvailable = true;
        fixture.Control.RetryRelease.TrySetResult();
        Assert.True(await fixture.TryWaitUntilAsync(() => fixture.Control.AvailableReads >= 1, pollOnWorld: false));
        // The clock is frozen: a mismatch must not be re-read on every one-second timer tick.
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        Assert.Equal(1, fixture.Control.AvailableReads);
        Assert.True(await fixture.OnWorld(() => fixture.Feature.IsAuctionQuarantined(1)));

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(await fixture.TryWaitUntilAsync(() => fixture.Control.AvailableReads >= 2, pollOnWorld: false));
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Equal(2, fixture.Control.AvailableReads);
    }

    [Fact]
    public async Task Auction_id_collision_with_an_external_insert_refuses_that_operation_and_reseeds_the_allocator()
    {
        await using var fixture = await Fixture.CreateAsync(true, false, loseAcknowledgement: false);
        // Another writer takes IDs 3..6 (they are not in the cache, which only the startup load fills).
        await using (CharacterDbContext db = fixture.NewContext())
        {
            for (uint id = 3; id <= 6; id++)
            {
                uint guid = 300 + id;
                var item = new ItemInstanceRow { Guid = guid };
                item.CopyFrom(0, new ItemInstanceData { Guid = guid, Entry = 4002, Count = 1 });
                db.Add(item);
                var row = new AuctionRow();
                row.CopyFrom(fixture.Primary with { Id = id, ItemGuid = guid, ItemEntry = 4002, ItemCount = 1, SellerId = 30 });
                db.Add(row);
            }

            await db.SaveChangesAsync();
        }

        uint allocated = await fixture.OnWorld(() => fixture.Feature.NextAuctionId());
        Assert.Equal(3u, allocated);
        var outcome = NewCompletion<EconomyOutcome>();
        AuctionRecord mine = fixture.Primary with { Id = allocated, ItemGuid = 301, ItemEntry = 4003, ItemCount = 1 };
        await fixture.OnWorld(() =>
        {
            fixture.Feature.RunAuctionOperation([], [new InsertAuction(mine)], allocated, result => outcome.TrySetResult(result));
            return true;
        });
        Assert.Equal(EconomyOutcome.Before, await outcome.Task.WaitAsync(Fixture.Budget));
        await fixture.Feature.DrainAsync().WaitAsync(Fixture.Budget);
        // Peek without allocating: polling NextAuctionId() would itself walk the counter past the external IDs.
        Assert.True(await fixture.TryWaitUntilAsync(() => fixture.Feature.LastAllocatedIds().Auction >= 6),
            "the allocator was not raised past the externally inserted IDs");
        Assert.Equal(7u, await fixture.OnWorld(() => fixture.Feature.NextAuctionId()));
        // Only the colliding operation was refused: its ID is released, not quarantined.
        Assert.False(await fixture.OnWorld(() => fixture.Feature.IsAuctionQuarantined(allocated)));
    }

    [Fact]
    public async Task Mail_id_collision_with_an_external_insert_refuses_that_operation_and_reseeds_mail_and_text_allocators()
    {
        await using var fixture = await Fixture.CreateAsync(true, false, loseAcknowledgement: false);
        // Another writer takes mail IDs 1..5 and item-text IDs 1..7.
        await using (CharacterDbContext db = fixture.NewContext())
        {
            for (uint id = 1; id <= 5; id++)
            {
                var row = new MailRow();
                row.CopyFrom(new MailRecord { Id = id, ReceiverId = 10, Subject = "external", ItemTextId = id == 5 ? 7u : 0u });
                db.Add(row);
            }

            for (uint id = 1; id <= 7; id++)
            {
                db.Add(new ItemTextRow { Id = id, Text = "external" });
            }

            await db.SaveChangesAsync();
        }

        Assert.Equal((0u, 2u, 0u), await fixture.OnWorld(() => fixture.Feature.LastAllocatedIds()));
        var outcome = NewCompletion<EconomyOutcome>();
        var mine = new MailRecord { Id = 1, ReceiverId = 10, Subject = "mine", ItemTextId = 1 };
        await fixture.OnWorld(() =>
        {
            fixture.Feature.RunAuctionOperation([], [new InsertMail(mine, "body")], 0, result => outcome.TrySetResult(result));
            return true;
        });
        Assert.Equal(EconomyOutcome.Before, await outcome.Task.WaitAsync(Fixture.Budget));
        await fixture.Feature.DrainAsync().WaitAsync(Fixture.Budget);
        Assert.True(await fixture.TryWaitUntilAsync(() => fixture.Feature.LastAllocatedIds() is { Mail: >= 5, Text: >= 7 }),
            "the mail and item-text allocators were not raised past the externally inserted IDs");
    }

    [Fact]
    public async Task Deletion_resync_reserves_a_kept_auction_whose_escrow_does_not_match_instead_of_dropping_it()
    {
        var clock = new ManualQuestClock();
        await using var fixture = await Fixture.CreateAsync(true, false, loseAcknowledgement: false, clock: clock);
        await fixture.OnWorld(() =>
        {
            // Deletion kept auction 1 (the seller row survived the cleanup) but its snapshot has no matching escrow item.
            fixture.Feature.ResyncDeletedCharacter(fixture.Primary.SellerId, [fixture.Primary], new Dictionary<uint, ItemInstanceData>(), []);
            return true;
        });
        await fixture.OnWorld(() =>
        {
            Assert.True(fixture.Feature.IsAuctionQuarantined(1), "the kept auction must stay reserved while its escrow is unmatched");
            Assert.DoesNotContain(fixture.Feature.Auctions, v => v.Auction.Id == 1);
            Assert.Contains(fixture.Feature.Auctions, v => v.Auction.Id == 2);
            return true;
        });

        // The real escrow row exists, so the first retry after the mismatch backoff repairs the cache.
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(await fixture.TryWaitUntilAsync(() => !fixture.Feature.IsAuctionQuarantined(1)
                && fixture.Feature.Auctions.Any(v => v.Auction.Id == 1)),
            "the reserved auction was never repaired from the authoritative rows");
    }

    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Fixture : IAsyncDisposable
    {
        // Every wait on this budget returns the moment its condition holds, so a generous ceiling costs nothing when healthy;
        // 5 s was too tight for a loaded machine (recovery progresses on the world's one-second timer and the sqlite writer).
        public static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
        private readonly string _path;
        private readonly DbContextOptions<CharacterDbContext> _options;
        private readonly ServiceProvider _services;
        private readonly CharacterSaveQueue _saves;

        private Fixture(string path, DbContextOptions<CharacterDbContext> options, ServiceProvider services,
            Control control, AuctionRecord primary, AuctionRecord other, TimeProvider? clock)
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
            Feature = new EconomyFeature(services, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<EconomyFeature>.Instance, clock);
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

        /// <summary>Poll until the predicate holds (on the world thread unless told otherwise) or the budget ends.</summary>
        public async Task<bool> TryWaitUntilAsync(Func<bool> predicate, TimeSpan? budget = null, bool pollOnWorld = true)
        {
            using var timeout = new CancellationTokenSource(budget ?? Budget);
            try
            {
                while (!(pollOnWorld ? await OnWorld(predicate).WaitAsync(timeout.Token) : predicate()))
                {
                    await Task.Delay(10, timeout.Token);
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public async Task WaitUntilAsync(Func<bool> predicate)
        {
            using var timeout = new CancellationTokenSource(Budget);
            while (!await OnWorld(predicate).WaitAsync(timeout.Token))
            {
                await Task.Delay(10, timeout.Token);
            }
        }

        public static async Task<Fixture> CreateAsync(bool existing, bool failEscrow, bool loseAcknowledgement = true,
            TimeProvider? clock = null, bool omitPrimaryEscrow = false, Func<CharacterDbContext, Task>? seed = null,
            bool holdRetryAfterFirstFailure = false)
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
                    if (!(row.Id == 1 && omitPrimaryEscrow))
                    {
                        var item = new ItemInstanceRow { Guid = row.ItemGuid };
                        item.CopyFrom(0, new ItemInstanceData { Guid = row.ItemGuid, Entry = row.ItemEntry, Count = row.ItemCount });
                        db.Add(item);
                    }

                    if (row.Id == 2 || existing)
                    {
                        var auction = new AuctionRow();
                        auction.CopyFrom(row);
                        db.Add(auction);
                    }
                }
                await db.SaveChangesAsync();
                if (seed is not null)
                {
                    await seed(db);
                }
            }

            var control = new Control
            {
                FailEscrow = failEscrow, LoseAcknowledgement = loseAcknowledgement, HoldRetryAfterFirstFailure = holdRetryAfterFirstFailure,
            };
            var services = new ServiceCollection();
            services.AddScoped(_ => new CharacterDbContext(options));
            services.AddScoped<IEconomyStore>(provider => new ControlledStore(
                new EfEconomyStore(provider.GetRequiredService<CharacterDbContext>()), control));
            services.AddSingleton(provider => new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CharacterSaveQueue>.Instance));
            return new Fixture(path, options, services.BuildServiceProvider(), control, primary, other, clock);
        }

        public async ValueTask DisposeAsync()
        {
            Control.OtherRelease.TrySetResult();
            Control.SnapshotRelease.TrySetResult();
            Control.RetryRelease.TrySetResult();
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

        /// <summary>
        /// Fail only the first recovery read while storage is unavailable; the retry after it waits for <see cref="RetryRelease"/> (the test
        /// sets <see cref="RecoveryAvailable"/> first) instead of failing again on every wall-clock timer tick.
        /// </summary>
        public bool HoldRetryAfterFirstFailure { get; init; }

        public int UnavailableReads;
        public volatile bool RecoveryAvailable;
        public volatile bool LostAcknowledgement;
        public volatile bool HoldRecoverySnapshot;
        public int SnapshotClaimed;
        public Guid UnknownOperation;
        public int PrimaryCommitAttempts;
        private int _availableReads;

        /// <summary>Authoritative auction reads served after the lost acknowledgement and once reads are available.</summary>
        public int AvailableReads => Volatile.Read(ref _availableReads);

        public void CountAvailableRead()
        {
            if (LostAcknowledgement && RecoveryAvailable)
            {
                Interlocked.Increment(ref _availableReads);
            }
        }
        public TaskCompletionSource<EconomyCommitResult> PrimaryCommitted { get; } = NewCompletion<EconomyCommitResult>();
        public TaskCompletionSource RecoveryFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OtherEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OtherRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SnapshotCaptured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SnapshotRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RetryRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ControlledStore(IEconomyStore inner, Control control) : IEconomyStore
    {
        public async Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest request, CancellationToken cancellationToken = default)
        {
            bool primary = request.Changes.Any(c => c is InsertAuction { Auction.Id: 1 } or UpdateAuction { Expected.Id: 1 });
            if (request.Changes.Any(c => c is UpdateAuction { Expected.Id: 2 }))
            {
                // The held commit ignores the settlement budget (5 s of wall time): the test holds it across the whole
                // recovery of auction 1, which under load took longer, so the budget cancelled the hold and the
                // operation reconciled to Before. The hold stands for "still in flight", not for a slow store.
                control.OtherEntered.TrySetResult();
                await control.OtherRelease.Task.ConfigureAwait(false);
                cancellationToken = CancellationToken.None;
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
            => inner.GetAuctionsAsync(cancellationToken);

        public Task<IReadOnlyDictionary<uint, ItemInstanceData>> GetEscrowItemsAsync(IReadOnlyCollection<uint> itemGuids,
            CancellationToken cancellationToken = default) => inner.GetEscrowItemsAsync(itemGuids, cancellationToken);

        /// <summary>Every recovery, startup and deletion read goes through here; the hooks live on this one method.</summary>
        public async Task<AuctionSnapshot> GetAuctionSnapshotAsync(AuctionSnapshotFilter filter, CancellationToken cancellationToken = default)
        {
            if (control.LostAcknowledgement && !control.RecoveryAvailable && control.HoldRetryAfterFirstFailure
                && Interlocked.Increment(ref control.UnavailableReads) > 1)
            {
                // The held retry ignores the read budget: it must neither fail nor be retried while the test prepares storage.
                await control.RetryRelease.Task.ConfigureAwait(false);
                cancellationToken = CancellationToken.None;
            }

            if (control.LostAcknowledgement && !control.RecoveryAvailable)
            {
                control.RecoveryFailed.TrySetResult();
                if (!control.FailEscrow)
                {
                    throw new IOException("controlled recovery snapshot read failure");
                }

                // The row is readable but its escrow does not match yet (repaired once RecoveryAvailable).
                AuctionSnapshot partial = await inner.GetAuctionSnapshotAsync(filter, cancellationToken);
                return partial with { Escrow = new Dictionary<uint, ItemInstanceData>() };
            }

            control.CountAvailableRead();
            AuctionSnapshot snapshot = await inner.GetAuctionSnapshotAsync(filter, cancellationToken);
            if (control.HoldRecoverySnapshot && Interlocked.CompareExchange(ref control.SnapshotClaimed, 1, 0) == 0)
            {
                // Like the held commit, the held read ignores the 5 s wall-time read budget; the snapshot is already taken.
                control.SnapshotCaptured.TrySetResult();
                await control.SnapshotRelease.Task.ConfigureAwait(false);
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
