using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class ManagedPlayerbotSetAsideRestartTests
{
    [Fact]
    public async Task ThrowingSetAsideStore_DoesNotPreventBotShutdownOrLaterCleanup()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var setAsides = new MemorySetAsideStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners,
            setAsideStore: setAsides);
        var feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        Guid first = Assert.IsType<Guid>((await feature.CreateAsync("Asideone", 1, 1)).BotId);
        Guid second = Assert.IsType<Guid>((await feature.CreateAsync("Asidetwo", 1, 1)).BotId);
        Assert.True((await feature.StartAsync(first.ToString())).Success);
        Assert.True((await feature.StartAsync(second.ToString())).Success);
        await host.World.InvokeAsync(() =>
        {
            feature.FindBrain(first)!.Suspensions.SuspendQuest(1361, host.World.NowMs);
            for (ulong bot = 1; bot <= 3; bot++)
                feature.SharedSetAsides.Report(PlayerbotGoalKind.Quest, 0, 6605, bot, host.World.NowMs);
            return true;
        });
        setAsides.ThrowOnReplace = true;

        await feature.ShutdownBeforeWorldStopAsync();

        Assert.Equal(ManagedPlayerbotState.Stopped, (await owners.FindAsync(first))!.State);
        Assert.Equal(ManagedPlayerbotState.Stopped, (await owners.FindAsync(second))!.State);
        Assert.Null(feature.Chat);
    }

    [Fact]
    public async Task Restart_RestoresBotAndSharedQuestSetAsides()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var setAsides = new MemorySetAsideStore();
        Guid id;
        await using (WorldTestHost first = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners,
            setAsideStore: setAsides))
        {
            var feature = first.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
            await feature.StartupAsync(default);
            id = Assert.IsType<Guid>((await feature.CreateAsync("Setaside", 1, 1)).BotId);
            Assert.True((await feature.StartAsync(id.ToString())).Success);
            await first.World.InvokeAsync(() =>
            {
                feature.FindBrain(id)!.Suspensions.SuspendQuest(1361, first.World.NowMs);
                for (ulong bot = 1; bot <= 3; bot++)
                    feature.SharedSetAsides.Report(PlayerbotGoalKind.Quest, 0, 6605, bot, first.World.NowMs);
                return true;
            });
            await feature.ShutdownBeforeWorldStopAsync();
        }

        Assert.Contains(await setAsides.LoadAllAsync(), row => row.BotId == id && row.TargetId == 1361);
        Assert.Contains(await setAsides.LoadAllAsync(), row => row.BotId == Guid.Empty && row.TargetId == 6605);
        await using WorldTestHost second = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners,
            setAsideStore: setAsides);
        var restarted = second.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await restarted.StartupAsync(default);
        Assert.True((await restarted.StartAsync(id.ToString())).Success);
        await second.World.InvokeAsync(() =>
        {
            Assert.True(restarted.FindBrain(id)!.Suspensions.IsQuestSuspended(1361, second.World.NowMs));
            Assert.True(restarted.FindBrain(id)!.Suspensions.IsQuestSuspended(6605, second.World.NowMs));
            return true;
        });
        await restarted.ShutdownBeforeWorldStopAsync();
    }

    [Fact]
    public async Task Restart_ContinuesEntryEscalation_AndMigratesLegacyUnreachableRow()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var setAsides = new MemorySetAsideStore();
        Guid id;
        await using (WorldTestHost first = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners,
            setAsideStore: setAsides))
        {
            var feature = first.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
            await feature.StartupAsync(default);
            id = Assert.IsType<Guid>((await feature.CreateAsync("Escalate", 1, 1)).BotId);
            Assert.True((await feature.StartAsync(id.ToString())).Success);
            await first.World.InvokeAsync(() =>
            {
                PlayerbotSuspensions suspensions = feature.FindBrain(id)!.Suspensions;
                suspensions.SuspendEntryEscalating(4321, first.World.NowMs); // strike 2: 20 minutes
                return true;
            });
            await feature.ShutdownBeforeWorldStopAsync();
        }

        PlayerbotSetAside entry = Assert.Single(await setAsides.LoadAllAsync(), row => row.TargetId == 4321);
        Assert.Equal(2, entry.RepeatCount);
        // A row an older build wrote: the unreachable quest that never expires.
        await setAsides.ReplaceAsync(id, [entry,
            new PlayerbotSetAside(id, entry.CharacterId, PlayerbotSetAsideKind.UnreachableQuest, 1361, long.MaxValue)]);

        await using WorldTestHost second = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners,
            setAsideStore: setAsides);
        var restarted = second.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await restarted.StartupAsync(default);
        Assert.True((await restarted.StartAsync(id.ToString())).Success);
        await second.World.InvokeAsync(() =>
        {
            PlayerbotSuspensions suspensions = restarted.FindBrain(id)!.Suspensions;
            uint now = second.World.NowMs;
            Assert.True(suspensions.IsEntrySuspended(4321, now + PlayerbotSuspensions.SuspendMs + 1_000)); // the escalated 20 minutes survived
            Assert.Equal(2, suspensions.EntryStrikeCount(4321));
            Assert.True(suspensions.IsQuestSuspended(1361, now));
            return true;
        });
        await restarted.ShutdownBeforeWorldStopAsync();
        PlayerbotSetAside unreachable = Assert.Single(await setAsides.LoadAllAsync(), row => row.Kind == PlayerbotSetAsideKind.UnreachableQuest);
        Assert.InRange(unreachable.ExpiresUnixMs, 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 301_000); // finite: a minute and a stagger of four
        Assert.Equal(2, unreachable.RepeatCount);
    }

    [Fact]
    public async Task Restart_KeepsTheStrikesOfAnUnreachableQuestWhoseSetAsideEnded()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        var setAsides = new MemorySetAsideStore();
        Guid id;
        await using (WorldTestHost first = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners,
            setAsideStore: setAsides))
        {
            var feature = first.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
            await feature.StartupAsync(default);
            id = Assert.IsType<Guid>((await feature.CreateAsync("Strikes", 1, 1)).BotId);
            Assert.True((await feature.StartAsync(id.ToString())).Success);
            await first.World.InvokeAsync(() =>
            {
                PlayerbotSuspensions suspensions = feature.FindBrain(id)!.Suspensions;
                long now = PlayerbotWorldClock.UptimeMs(first.World);
                var key = new DeferKey(DeferKind.UnreachableQuest, 1361);
                suspensions.Ledger.Defer(key, now, untilMs: now); // two diagnoses, each over at once
                suspensions.Ledger.Defer(key, now, untilMs: now);
                Assert.False(suspensions.IsQuestSuspended(1361, first.World.NowMs));
                return true;
            });
            await feature.ShutdownBeforeWorldStopAsync();
        }

        PlayerbotSetAside saved = Assert.Single(await setAsides.LoadAllAsync(), row => row.Kind == PlayerbotSetAsideKind.UnreachableQuest);
        Assert.Equal(2, saved.RepeatCount);

        await using WorldTestHost second = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners,
            setAsideStore: setAsides);
        var restarted = second.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await restarted.StartupAsync(default);
        Assert.True((await restarted.StartAsync(id.ToString())).Success);
        await second.World.InvokeAsync(() =>
        {
            PlayerbotSuspensions suspensions = restarted.FindBrain(id)!.Suspensions;
            uint now = second.World.NowMs;
            Assert.Equal(2, suspensions.Ledger.Strikes(new DeferKey(DeferKind.UnreachableQuest, 1361)));
            Assert.False(suspensions.IsQuestSuspended(1361, now));
            suspensions.MarkUnreachableQuest(1361, now); // the third diagnosis: an hour, not a fresh minute
            Assert.True(suspensions.IsQuestSuspended(1361, now + 3_599_000));
            return true;
        });
        await restarted.ShutdownBeforeWorldStopAsync();
    }

    private sealed class MemorySetAsideStore : IManagedPlayerbotSetAsideStore
    {
        private readonly Dictionary<Guid, PlayerbotSetAside[]> _rows = [];
        public bool ThrowOnReplace { get; set; }

        public Task<IReadOnlyList<PlayerbotSetAside>> LoadAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PlayerbotSetAside>>([.. _rows.Values.SelectMany(rows => rows)]);

        public Task ReplaceAsync(Guid botId, IReadOnlyList<PlayerbotSetAside> rows, CancellationToken cancellationToken = default)
        {
            if (ThrowOnReplace) throw new InvalidOperationException("set-aside-storage-refused");
            _rows[botId] = [.. rows];
            return Task.CompletedTask;
        }
    }
}
