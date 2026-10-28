using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// Issue #1618: <c>logger.LogFault</c> already records into the fault ledger, so the registry-read catch of the social feature must not
/// also call <c>FaultLedger.Shared.Record</c> under the same site. A failing registry read must count once under <c>playerbot.social</c>.
/// </summary>
public sealed class PlayerbotSocialFaultCountTests
{
    [Fact]
    public async Task AFailingRegistryRead_CountsTheFaultOnce()
    {
        var probe = new FaultCountProbe("playerbot.social");
        var store = new FailableLoadStore(new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore());
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true, services: s => s.AddSingleton<IManagedPlayerbotStore>(store));
        await world.AddBotAsync("Faultsoca");
        PlayerbotSocialFeature feature = SocialTestKit.Feature(world.Host);

        // The registry is due again at once and its read fails; one hand-driven pass on the world thread starts the read, the quiesce waits for it.
        store.Failure = probe.Fault();
        await world.Host.OnWorldAsync(() =>
        {
            feature.Timings.RegistryRefreshMs = 0;
            feature.Timings.PassIntervalMs = 1000;
            try { feature.Update(1000); }
            finally { feature.Timings.PassIntervalMs = int.MaxValue; }
        });
        await feature.QuiesceAsync(default);

        probe.AssertCountedOnce("the bot registry read catch");
    }

    /// <summary>Delegates to an in-memory store; <see cref="LoadAllAsync"/> throws <see cref="Failure"/> while it is set.</summary>
    private sealed class FailableLoadStore(IManagedPlayerbotStore inner) : IManagedPlayerbotStore
    {
        private Exception? _failure;

        public Exception? Failure
        {
            get => Volatile.Read(ref _failure);
            set => Volatile.Write(ref _failure, value);
        }

        public Task<IReadOnlyList<ManagedPlayerbot>> LoadAllAsync(CancellationToken cancellationToken = default)
            => Failure is { } failure ? throw failure : inner.LoadAllAsync(cancellationToken);

        public Task<ManagedPlayerbot?> FindAsync(Guid botId, CancellationToken cancellationToken = default) => inner.FindAsync(botId, cancellationToken);

        public Task CreateAsync(ManagedPlayerbot bot, CancellationToken cancellationToken = default) => inner.CreateAsync(bot, cancellationToken);

        public Task<bool> UpdateAsync(ManagedPlayerbot bot, long expectedRevision, CancellationToken cancellationToken = default)
            => inner.UpdateAsync(bot, expectedRevision, cancellationToken);

        public Task<IReadOnlyList<bool>> UpdateManyAsync(IReadOnlyList<ManagedPlayerbotUpdate> updates, CancellationToken cancellationToken = default)
            => inner.UpdateManyAsync(updates, cancellationToken);

        public Task<bool> DeleteAsync(Guid botId, int accountId, int characterId, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(botId, accountId, characterId, cancellationToken);
    }
}
