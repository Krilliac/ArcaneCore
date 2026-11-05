using ArcaneCore.World.Characters;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Persistence;

/// <summary>A handoff names the characters a settlement barrier or a persistence participant holds open (<see cref="CharacterPersistenceBarrier.OpenCharacterIdsAsync"/>).</summary>
public sealed class CharacterPersistenceBarrierOpenIdsTests
{
    private sealed class Participant(params int[] open) : ICharacterPersistenceParticipant
    {
        public string Name => "OpenIdsParticipant";

        public Task FlushCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IReadOnlyCollection<int> OpenCharacterIds => open;
    }

    private sealed class SilentParticipant : ICharacterPersistenceParticipant
    {
        public string Name => "SilentParticipant";

        public Task FlushCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Settlements(params int[] settling) : ICharacterSettlementBarrier
    {
        public Task WaitForSettlementAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IReadOnlyCollection<int> SettlingCharacters => settling;
    }

    private sealed class SilentSettlements : ICharacterSettlementBarrier
    {
        public Task WaitForSettlementAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task AParticipantsAndASettlementBarriersIds_AreNamedAscending_AndMergedWithoutDuplicates()
    {
        await using ServiceProvider provider = new ServiceCollection()
            .AddSingleton<ICharacterPersistenceParticipant>(new Participant(9, 4))
            .AddSingleton<ICharacterSettlementBarrier>(new Settlements(4, 2))
            .BuildServiceProvider();

        IReadOnlyList<int> open = await new CharacterPersistenceBarrier(provider).OpenCharacterIdsAsync();

        Assert.Equal([2, 4, 9], open);
    }

    [Fact]
    public async Task AParticipantAndASettlementBarrierThatCannotEnumerate_NameNoOne()
    {
        await using ServiceProvider provider = new ServiceCollection()
            .AddSingleton<ICharacterPersistenceParticipant>(new SilentParticipant())
            .AddSingleton<ICharacterSettlementBarrier>(new SilentSettlements())
            .BuildServiceProvider();

        Assert.Empty(await new CharacterPersistenceBarrier(provider).OpenCharacterIdsAsync());
    }
}
