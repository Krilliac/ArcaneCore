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
