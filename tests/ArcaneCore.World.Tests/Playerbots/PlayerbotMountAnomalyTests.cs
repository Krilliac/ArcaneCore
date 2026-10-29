using System.Numerics;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// H1 hardening: <c>bot-mount/summon-failed</c> (a mount-up that did not mount), <c>bot-mount/riding-refused</c> (a mount vendor given
/// up on, a riding trip that found no way) and <c>bot-progression/train-failed</c> (the riding trainer trip). Each has a test where it
/// fires and one where the healthy or legitimately deferred path stays quiet. Fixtures are those of <see cref="PlayerbotMountTests"/>.
/// </summary>
public sealed class PlayerbotMountAnomalyTests
{
    private static readonly string SummonFailed = PlayerbotAnomalies.Key("mount", "summon-failed");
    private static readonly string RidingRefused = PlayerbotAnomalies.Key("mount", "riding-refused");
    private static readonly string TrainFailed = PlayerbotAnomalies.Key("progression", "train-failed");

    private static ItemTestContent Items()
    {
        var items = new ItemTestContent();
        ItemSpell mount = new(MountFixture.MountSpell, 0, -1, 0, 0, 0, 0);
        items.Templates.Templates.AddRange(
        [
            new ItemTemplate { Entry = 98001, Name = "Skill mount", Class = 15, Stackable = 1, BuyPrice = 300, RequiredSkill = 762, RequiredSkillRank = 75, Spells = [mount] },
            new ItemTemplate { Entry = 98002, Name = "Orc mount", Class = 15, Stackable = 1, BuyPrice = 100, RequiredSkill = 762, RequiredSkillRank = 75, AllowableRace = 1u << 1, Spells = [mount] },
            new ItemTemplate { Entry = 98003, Name = "Spell mount", Class = 15, Stackable = 1, BuyPrice = 250, Spells = [mount] },
            new ItemTemplate { Entry = 98004, Name = "Epic mount", Class = 15, Stackable = 1, BuyPrice = 120, RequiredSkill = 762, RequiredSkillRank = 150, Spells = [mount] },
            new ItemTemplate { Entry = 98005, Name = "Late mount", Class = 15, Stackable = 1, BuyPrice = 90, RequiredSkill = 762, RequiredSkillRank = 75, Spells = [mount] },
        ]);
        return items;
    }

    private static void InstallMountSpell(WorldTestHost host, SpellAttributes attributes = 0)
    {
        SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
        spells.Store = new SpellStore([.. spells.Store.All.Where(s => s.Id != MountFixture.MountSpell), new SpellInfo
        {
            Id = MountFixture.MountSpell, Attributes = attributes,
            Duration = new SpellDuration(-1, 0, -1), // permanent like a real mount (see PlayerbotMountTests.InstallMountSpell): no 300 ms aura floor
            Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Mounted, MiscValue = (int)MountFixture.MountCreature }, new(), new()],
        }], [], []);
    }

    private static async Task RunAsync(MountFixture fixture, Func<WorldTestHost, WorldSession, Task> body)
    {
        using IDisposable scope = Items().Use();
        MountServices.Current.Value = fixture;
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton<ISpellContentStore>(fixture));
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            try { await body(host, session); }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { MountServices.Current.Value = null; }
    }

    private static PlayerbotRoute LongRoute(Player player, float yards = 200)
        => new([new Vector3(player.X, player.Y, player.Z), new Vector3(player.X + yards, player.Y, player.Z)], yards);

    private static PlayerbotRidingTrips FarTrips(WorldTestHost host, WorldSession session, PlayerbotOptions options)
    {
        WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
        InstallMountSpell(host);
        return new PlayerbotRidingTrips(session, options) { Budget = new PlayerbotBudget(session, options) };
    }

    // --- bot-mount/summon-failed ---------------------------------------------------------------------------------------

    [Fact]
    public async Task SummonFailed_Fires_OncePerRefusedAttempt_AndNotDuringTheBackoff()
    {
        await RunAsync(new MountFixture(), (host, session) => host.OnWorldAsync(() =>
        {
            // A mount spell that may be cast only indoors: the server's own check refuses it outdoors (the existing refusal test's setup).
            InstallMountSpell(host, (SpellAttributes)0x4000);
            Player player = session.Player!;
            player.Inventory.AddItem(98003, 1, out _);
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            var options = new PlayerbotOptions { Enabled = true };
            session.ManagedBudget = new ManagedActionBudget(50);

            PlayerbotMount.BeforeStep(session, player, LongRoute(player), options);
            Assert.False(MountService.IsMounted(player));
            Assert.Equal(1, PlayerbotMount.For(session).Failures);
            Assert.Equal(1, capture.Count(SummonFailed));
            AnomalyNotice notice = capture.Notices.Single(n => n.Key == SummonFailed);
            Assert.Equal(AnomalySeverity.Warning, notice.Severity);
            Assert.Contains("(failures=1)", notice.Detail, StringComparison.Ordinal);
            Assert.Contains("use was refused", notice.Detail, StringComparison.Ordinal);

            // The backoff (30 s) holds: further thinks neither retry nor report.
            for (int i = 0; i < 5; i++) PlayerbotMount.BeforeStep(session, player, LongRoute(player), options);
            Assert.Equal(1, capture.Count(SummonFailed));
            return true;
        }));
    }

    [Fact]
    public async Task SummonFailed_Silent_WhenTheBotMounts_AndWhenItIsIndoorsAndDefers()
    {
        await RunAsync(new MountFixture(), async (host, session) =>
        {
            Player player = session.Player!;
            using AnomalyCapture capture = await host.OnWorldAsync(() => PlayerbotAnomalies.CaptureFor(player));
            await host.OnWorldAsync(() =>
            {
                InstallMountSpell(host);
                player.Inventory.AddItem(98003, 1, out _);
                var options = new PlayerbotOptions { Enabled = true };
                session.ManagedBudget = new ManagedActionBudget(5);

                // Healthy: the item use mounts the bot (at once, or at its go when spell batching holds it).
                PlayerbotMount.BeforeStep(session, player, LongRoute(player), options);
                return true;
            });
            await host.WaitForWorldAsync(() => !host.WorldServices.GetRequiredService<SpellFeature>().System.HasPendingGo(player), "the batched mount-up went off");
            await host.OnWorldAsync(() =>
            {
                Assert.True(MountService.IsMounted(player));
                Assert.Equal(0, PlayerbotMount.For(session).Failures);
                Assert.False(capture.Any(SummonFailed), capture.Describe());
                return true;
            });
        });

        await RunAsync(new MountFixture(), (host, session) => host.OnWorldAsync(() =>
        {
            InstallMountSpell(host);
            Player player = session.Player!;
            WorldCollision.Of(host.World).Install(lineOfSight: new IndoorFloor(), pathfinder: new OpenPathfinder());
            player.Inventory.AddItem(98003, 1, out _);
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            session.ManagedBudget = new ManagedActionBudget(5);

            // Legitimate deferral: indoors the bot sends nothing and backs off. That is not a failed summon.
            PlayerbotMount.BeforeStep(session, player, LongRoute(player), new PlayerbotOptions { Enabled = true });
            Assert.False(MountService.IsMounted(player));
            Assert.NotEqual(0u, PlayerbotMount.For(session).BackoffUntil);
            Assert.False(capture.Any(SummonFailed), capture.Describe());
            return true;
        }));
    }

    [Fact]
    public async Task SummonFailed_Silent_WhenCastIsInterruptedByMovementOrCombat()
    {
        await RunAsync(new MountFixture(), (host, session) => host.OnWorldAsync(() =>
        {
            SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
            spells.Store = new SpellStore([.. spells.Store.All.Where(s => s.Id != MountFixture.MountSpell), new SpellInfo
            {
                Id = MountFixture.MountSpell,
                CastTime = new SpellCastTime(3000, 0, 3000),
                Duration = new SpellDuration(-1, 0, -1), // permanent like a real mount (see PlayerbotMountTests.InstallMountSpell)
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Mounted, MiscValue = (int)MountFixture.MountCreature }, new(), new()],
            }], [], []);

            Player player = session.Player!;
            player.Inventory.AddItem(98003, 1, out _);
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            var options = new PlayerbotOptions { Enabled = true };
            session.ManagedBudget = new ManagedActionBudget(5);

            // Start the 3s mount cast.
            Assert.True(PlayerbotMount.BeforeStep(session, player, LongRoute(player), options));
            Assert.True(PlayerbotMount.IsBusy(session));

            // External movement cancels the in-flight cast before the next step: must not report summon-failed.
            spells.GetState(player.Guid)!.CurrentCast = null;
            player.SetPosition(player.X + 2f, player.Y, player.Z, player.Orientation);
            PlayerbotMount.BeforeStep(session, player, LongRoute(player), options);

            Assert.Equal(0, PlayerbotMount.For(session).Failures);
            Assert.False(capture.Any(SummonFailed), capture.Describe());
            return true;
        }));
    }

    // --- bot-mount/riding-refused: the vendor exclusion -----------------------------------------------------------------

    [Fact]
    public async Task RidingRefused_Fires_WhenAMountVendorReachesTheFailureLimit_Once()
    {
        await RunAsync(new MountFixture(), (host, session) => host.OnWorldAsync(() =>
        {
            InstallMountSpell(host);
            Player player = session.Player!;
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            var budget = new PlayerbotBudget(session, new PlayerbotOptions { Enabled = true });

            for (int i = 1; i < PlayerbotBudget.VendorFailureLimit; i++) budget.NoteVendorFailure(MountFixture.Vendor);
            Assert.False(capture.Any(RidingRefused), "one failure short of the limit: " + capture.Describe());

            budget.NoteVendorFailure(MountFixture.Vendor);
            Assert.Equal(1, capture.Count(RidingRefused));
            AnomalyNotice notice = capture.Notices.Single(n => n.Key == RidingRefused);
            Assert.Equal(AnomalySeverity.Warning, notice.Severity);
            Assert.Contains($"mount vendor {MountFixture.Vendor} excluded after {PlayerbotBudget.VendorFailureLimit} failed visits", notice.Detail, StringComparison.Ordinal);
            return true;
        }));
    }

    [Fact]
    public async Task RidingRefused_Silent_WhileAMountVendorHasFewerFailuresThanTheLimit()
    {
        await RunAsync(new MountFixture(), (host, session) => host.OnWorldAsync(() =>
        {
            InstallMountSpell(host);
            Player player = session.Player!;
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            var budget = new PlayerbotBudget(session, new PlayerbotOptions { Enabled = true });

            // A different vendor each time: no vendor reaches the limit, so no vendor is given up on.
            for (int i = 0; i < PlayerbotBudget.VendorFailureLimit - 1; i++) budget.NoteVendorFailure(MountFixture.Vendor);
            budget.NoteVendorFailure(MountFixture.SecondVendor);
            Assert.False(capture.Any(RidingRefused), capture.Describe());
            return true;
        }));
    }

    // --- bot-mount/riding-refused and bot-progression/train-failed: the riding trip -------------------------------------

    [Fact]
    public async Task RidingTrip_ThatFindsNoWay_ReportsRidingRefusedAndTrainFailed_ForTheTrainerTrip()
    {
        await RunAsync(new MountFixture { SpawnOffset = 130 }, (host, session) => host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            PlayerbotRidingTrips trips = FarTrips(host, session, new PlayerbotOptions { Enabled = true });
            player.Money = 10_000;
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);

            session.ManagedBudget = new ManagedActionBudget(1);
            trips.Update(player, 500);
            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(trips.Update(player, 500));
            Assert.Equal(PlayerbotRidingTripKind.Trainer, trips.Kind);
            Assert.False(capture.Any(RidingRefused), "walking toward the trainer is healthy: " + capture.Describe());

            // No step can be taken: after the stuck time the only site is set aside and the trip gives up.
            session.ManagedBudget = new ManagedActionBudget(0);
            trips.Update(player, 500);
            Assert.False(trips.Update(player, PlayerbotRidingTrips.StuckMs));

            Assert.True(capture.Count(RidingRefused) == 1, capture.Describe());
            Assert.True(capture.Count(TrainFailed) == 1, capture.Describe());
            Assert.Contains("riding trainer trip", capture.Notices.Single(n => n.Key == TrainFailed).Detail, StringComparison.Ordinal);

            // The trip now rests: asking again within the rest reports nothing more.
            session.ManagedBudget = new ManagedActionBudget(5);
            Assert.False(trips.Update(player, 500));
            Assert.True(capture.Count(RidingRefused) == 1, capture.Describe());
            Assert.True(capture.Count(TrainFailed) == 1, capture.Describe());
            return true;
        }));
    }

    [Fact]
    public async Task RidingTrip_Silent_WhenItWalksTowardTheTrainer_AndWhenNoSiteStandsOnTheMap()
    {
        await RunAsync(new MountFixture { SpawnOffset = 130 }, (host, session) => host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            PlayerbotRidingTrips trips = FarTrips(host, session, new PlayerbotOptions { Enabled = true });
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);

            player.Money = 700; // the training but not the mount: saving, no trip yet
            Assert.False(trips.Update(player, 500));
            player.Money = 10_000;
            session.ManagedBudget = new ManagedActionBudget(1);
            trips.Update(player, 500);
            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(trips.Update(player, 500));
            Assert.Equal(MountFixture.TrainerEntry, trips.TargetEntry);

            Assert.False(capture.Any(RidingRefused), capture.Describe());
            Assert.False(capture.Any(TrainFailed), capture.Describe());
            return true;
        }));

        // No trainer or vendor on the bot's map: there is no trip to give up on, so nothing is reported.
        await RunAsync(new MountFixture { SpawnOffset = 130, SpawnMap = 1 }, (host, session) => host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            PlayerbotRidingTrips trips = FarTrips(host, session, new PlayerbotOptions { Enabled = true });
            player.Money = 10_000;
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);
            session.ManagedBudget = new ManagedActionBudget(5);
            Assert.False(trips.Update(player, 500));
            Assert.False(capture.Any(RidingRefused), capture.Describe());
            Assert.False(capture.Any(TrainFailed), capture.Describe());
            return true;
        }));
    }
}
