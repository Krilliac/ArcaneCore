using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Characters.Transfers;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Clustering;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The playerbot start is wired to the real character claim: with nothing registered for <see cref="IPlayerbotLiveOwnerProbe"/>, the probe in force is the claim-backed one and a bot whose
/// character another live worker holds is refused without a row written. A mis-wire (no probe, a probe that never answers) starts the bot instead and fails here.
/// </summary>
public sealed class ManagedPlayerbotClaimWiringTests
{
    [Fact]
    public async Task TheProbeInForce_IsTheClaimBackedOne_AndARealClaimOfAnotherWorkerRefusesTheStart()
    {
        using var directory = new TempDirectory("arcanecore-botclaim-", sqlite: true);
        string file = Path.Combine(directory.Path, "characters.db");
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = $"Data Source={file}",
            [DataServiceCollectionExtensions.FencingEnabledKey] = "true",
        }).Build();
        var claimDb = new ServiceCollection();
        claimDb.AddLogging();
        claimDb.AddCharacterDatabase(configuration);
        await using ServiceProvider provider = claimDb.BuildServiceProvider();
        await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
        EfTransferDecisionStore store = provider.GetRequiredService<EfTransferDecisionStore>();
        ICharacterOwnershipStore ownership;
        CharacterOwnershipToken mine, theirs;
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            ownership = scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>();
            mine = (await ownership.TryAcquireAsync(CharacterOwnershipScopes.ForShard("a"), "node-a", Guid.NewGuid(), [0u]))!;
            theirs = (await ownership.TryAcquireAsync(CharacterOwnershipScopes.ForShard("b"), "node-b", Guid.NewGuid(), [1u]))!;
        }

        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners, extraServices: services =>
        {
            services.AddSingleton(store);
            services.AddSingleton(Options.Create(new CharacterFencingOptions { Scope = CharacterFencingScope.Worker, ReapDeadOwners = false }));
            services.TryAddSingleton<CharacterPersistenceBarrier>();
            services.TryAddSingleton<CharacterClaimService>();
        });
        host.World.CharacterOwnership = mine;
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await using (AsyncServiceScope scope = host.WorldServices.CreateAsyncScope())
            Assert.IsType<ClaimLiveOwnerProbe>(feature.LiveOwnerProbeFor(scope.ServiceProvider));

        Guid id = (await feature.CreateAsync("Claimwire", 1, 1)).BotId!.Value;
        ManagedPlayerbot before = owners.Peek(id)!;
        Assert.Equal(CharacterClaimOutcome.Claimed, (await store.TryClaimCharacterAsync((ulong)before.CharacterId, theirs)).Outcome);
        int rows = owners.RowsWritten;

        PlayerbotOperationResult result = await feature.StartAsync(id.ToString());

        Assert.Equal("live-on-other-worker", result.Code);
        Assert.Equal(before, owners.Peek(id));
        Assert.Equal(rows, owners.RowsWritten);
        Assert.Equal(theirs, await store.GetActiveOwnershipAsync((ulong)before.CharacterId)); // the holder keeps its claim
        Assert.False(feature.IsActive(id));
    }
}
