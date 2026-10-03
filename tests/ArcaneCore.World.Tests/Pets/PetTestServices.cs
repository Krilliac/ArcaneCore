using ArcaneCore.Kernel.WorldData.Pets;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.Pets;

/// <summary>An in-memory pet table store for <see cref="WorldTestHost"/> (discovered): one invented creature with level 1 stats and one create spell.</summary>
internal sealed class PetTestServices : IWorldTestServices
{
    public const uint Entry = 910416;
    public const uint Health = 77;
    public const uint Spell = 910417;

    public void Register(IServiceCollection services)
        => services.AddSingleton<IPetDataStore>(new InMemoryPetDataStore(new PetContent(
            [new PetLevelStats(Entry, 1, Health, 10, 5, 1, 2, 3, 4, 5, 6, 7)],
            [new PetCreateSpells(Entry, [Spell])])));

    private sealed class InMemoryPetDataStore(PetContent content) : IPetDataStore
    {
        public Task<PetContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }
}
