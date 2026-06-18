using System.Collections.Concurrent;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.World.Tests;

internal sealed class InMemoryCharacterStore : ICharacterStore
{
    private readonly ConcurrentDictionary<int, CharacterRecord> _characters = new();
    private int _nextId;

    public Task<IReadOnlyList<CharacterRecord>> GetByAccountAsync(int accountId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CharacterRecord>>(
            _characters.Values.Where(c => c.AccountId == accountId).OrderBy(c => c.Id).ToList());

    public Task<CharacterRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
        => Task.FromResult(_characters.GetValueOrDefault(id));

    public Task<bool> IsNameTakenAsync(string name, CancellationToken cancellationToken = default)
        => Task.FromResult(_characters.Values.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)));

    public Task<int> CountByAccountAsync(int accountId, CancellationToken cancellationToken = default)
        => Task.FromResult(_characters.Values.Count(c => c.AccountId == accountId));

    public Task<CharacterRecord> CreateAsync(CharacterRecord character, CancellationToken cancellationToken = default)
    {
        character.Id = Interlocked.Increment(ref _nextId);
        _characters[character.Id] = character;
        return Task.FromResult(character);
    }

    public Task<bool> DeleteAsync(int id, int accountId, CancellationToken cancellationToken = default)
    {
        if (_characters.TryGetValue(id, out CharacterRecord? c) && c.AccountId == accountId)
        {
            return Task.FromResult(_characters.TryRemove(id, out _));
        }

        return Task.FromResult(false);
    }
}

internal sealed class InMemoryWorldDataStore : IWorldDataStore
{
    public Task<StartPosition?> GetStartPositionAsync(byte race, byte cls, CancellationToken cancellationToken = default)
        => Task.FromResult<StartPosition?>(IsValid(race, cls) ? new StartPosition(0, 12, -8949.95f, -132.493f, 83.5312f, 0f) : null);

    public Task<RaceInfo?> GetRaceInfoAsync(byte race, byte gender, CancellationToken cancellationToken = default)
        => Task.FromResult<RaceInfo?>(new RaceInfo(gender == 0 ? 49u : 50u, 1));

    public Task<ClassInfo?> GetClassInfoAsync(byte cls, CancellationToken cancellationToken = default)
        => Task.FromResult<ClassInfo?>(new ClassInfo(60, 0, 1)); // warrior-ish defaults

    public Task<bool> IsValidRaceClassAsync(byte race, byte cls, CancellationToken cancellationToken = default)
        => Task.FromResult(IsValid(race, cls));

    private static bool IsValid(byte race, byte cls) => race == 1 && cls == 1; // human warrior for tests
}
