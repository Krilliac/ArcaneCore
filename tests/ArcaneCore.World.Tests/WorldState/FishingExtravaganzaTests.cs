using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>Riggle Bassbait's tournament state (vmangos npcs_special.cpp:1250-1355).</summary>
public sealed class FishingExtravaganzaTests
{
    private static FishingExtravaganzaFeature Create(Func<bool> active)
    {
        ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        return new FishingExtravaganzaFeature(provider.GetRequiredService<IServiceScopeFactory>(),
            new GameEventFeature(provider, NullLogger<GameEventFeature>.Instance)) { TournamentActive = active };
    }

    [Fact]
    public void Tournament_yells_begin_once_then_over_once_after_it_ends()
    {
        bool active = false;
        FishingExtravaganzaFeature feature = Create(() => active);

        Assert.Equal((false, 0), feature.Step());
        active = true;
        Assert.Equal((true, FishingExtravaganzaFeature.YellBegin), feature.Step());
        Assert.Equal((true, 0), feature.Step());
        active = false;
        Assert.Equal((false, FishingExtravaganzaFeature.YellOver), feature.Step());
        Assert.Equal((false, 0), feature.Step());
    }

    [Fact]
    public void A_winner_stops_the_quest_and_the_over_yell_still_waits_for_the_pools()
    {
        bool active = true;
        FishingExtravaganzaFeature feature = Create(() => active);
        feature.Step();
        feature.HasWinner = true;

        Assert.Equal((false, 0), feature.Step());
        active = false;
        Assert.Equal((false, FishingExtravaganzaFeature.YellOver), feature.Step());
    }

    [Fact]
    public void A_win_older_than_a_day_resets_the_contest()
    {
        long now = 1_000_000;
        FishingExtravaganzaFeature feature = Create(() => true);
        feature.NowUnix = () => now;
        feature.HasWinner = true;
        feature.AnnounceBegin = false;
        feature.PreviousWinTime = now - 3600;

        feature.ResetIfStale();
        Assert.True(feature.HasWinner);

        feature.PreviousWinTime = now - 2 * 86400;
        feature.ResetIfStale();
        Assert.False(feature.HasWinner);
        Assert.True(feature.AnnounceBegin);
        Assert.False(feature.AnnounceOver);
    }

    private sealed class MemoryStore : IFishingExtravaganzaStore
    {
        public FishingExtravaganzaState? Saved;
        public Task<FishingExtravaganzaState?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Saved);
        public Task SaveAsync(FishingExtravaganzaState state, CancellationToken cancellationToken = default)
        {
            Saved = state;
            return Task.CompletedTask;
        }
    }

    private static FishingExtravaganzaFeature Create(MemoryStore store, Func<bool> active)
    {
        ServiceProvider provider = new ServiceCollection().AddSingleton<IFishingExtravaganzaStore>(store).BuildServiceProvider();
        return new FishingExtravaganzaFeature(provider.GetRequiredService<IServiceScopeFactory>(),
            new GameEventFeature(provider, NullLogger<GameEventFeature>.Instance)) { TournamentActive = active };
    }

    [Fact]
    public void A_restart_keeps_the_announced_start_and_the_winner()
    {
        var store = new MemoryStore();
        FishingExtravaganzaFeature before = Create(store, () => true);
        before.Load();
        Assert.Equal((true, FishingExtravaganzaFeature.YellBegin), before.Step());
        before.HasWinner = true;
        before.PreviousWinTime = before.NowUnix();
        before.ResetIfStale(); // not stale: nothing changes
        Assert.Equal(new FishingExtravaganzaState(false, true, false, 0), store.Saved);

        // the winner is saved by the quest reward; simulate that save
        store.Saved = before.State;
        FishingExtravaganzaFeature after = Create(store, () => true);
        after.Load();
        after.ResetIfStale();
        Assert.Equal((false, 0), after.Step()); // no repeated start yell, no quest offered again
        Assert.True(after.HasWinner);
    }
}
