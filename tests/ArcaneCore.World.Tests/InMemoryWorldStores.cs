using System.Collections.Concurrent;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.World.Tests;

internal sealed class InMemoryCharacterStore : ICharacterStore, ICharacterLifeStore
{
    private readonly ConcurrentDictionary<int, CharacterLife> _life = new();
    private readonly ConcurrentDictionary<int, CharacterRecord> _characters = new();
    private readonly ConcurrentDictionary<int, IReadOnlyList<ActionButton>> _buttons = new();
    private int _nextId;
    private int _saves;

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
            _buttons.TryRemove(id, out _);
            return Task.FromResult(_characters.TryRemove(id, out _));
        }

        return Task.FromResult(false);
    }

    public Task SaveStateAsync(CharacterState state, CancellationToken cancellationToken = default)
    {
        if (_characters.TryGetValue(state.Id, out CharacterRecord? c))
        {
            lock (c)
            {
                c.MapId = state.MapId;
                c.ZoneId = state.ZoneId;
                c.X = state.X;
                c.Y = state.Y;
                c.Z = state.Z;
                c.Orientation = state.Orientation;
                c.Level = state.Level;
                c.PlayedTime = state.PlayedTime;
                c.LevelPlayedTime = state.LevelPlayedTime;
                c.Money = state.Money;
                c.ActionBarToggles = state.ActionBarToggles;
                if (state.Home is { } home)
                {
                    (c.HomeMapId, c.HomeZoneId, c.HomeX, c.HomeY, c.HomeZ) = (home.MapId, home.ZoneId, home.X, home.Y, home.Z);
                }
            }

            if (state.ActionButtons is { } buttons)
            {
                _buttons[state.Id] = buttons.ToList();
            }

            if (state.Life is { } life)
            {
                _life[state.Id] = life;
            }
        }

        Interlocked.Increment(ref _saves);
        return Task.CompletedTask;
    }

    public Task<CharacterLife?> LoadAsync(int characterId, CancellationToken cancellationToken = default)
        => Task.FromResult(_life.GetValueOrDefault(characterId));

    /// <summary>The stored life of a character (what a previous save left), or null.</summary>
    public CharacterLife? Life(int characterId) => _life.GetValueOrDefault(characterId);

    /// <summary>Put a stored life in place, as a previous session's save would have.</summary>
    public void SetLife(int characterId, CharacterLife life) => _life[characterId] = life;

    public Task<IReadOnlyList<ActionButton>> GetActionButtonsAsync(int characterId, CancellationToken cancellationToken = default)
        => Task.FromResult(_buttons.GetValueOrDefault(characterId) ?? []);

    public Task<IReadOnlyList<CharacterIdentity>> GetAllIdentitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CharacterIdentity>>(
            _characters.Values.Select(c => new CharacterIdentity(c.Id, c.AccountId, c.Name, c.Race, c.Gender, c.Class, c.Level, c.ZoneId)).ToList());

    public Task<IReadOnlyList<int>> FindAccountIdsByNamePrefixAsync(string prefix, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        IEnumerable<int> matching = _characters.Values.Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.AccountId).Distinct().Order();
        return Task.FromResult<IReadOnlyList<int>>([.. limit == 0 ? matching : matching.Take(limit)]);
    }

    /// <summary>Number of state saves received.</summary>
    public int SaveCount => Volatile.Read(ref _saves);
}

internal sealed class InMemoryWorldDataStore : IWorldDataStore
{
    public Task<StartPosition?> GetStartPositionAsync(byte race, byte cls, CancellationToken cancellationToken = default)
        => Task.FromResult<StartPosition?>(IsValid(race, cls) ? new StartPosition(0, 12, -8949.95f, -132.493f, 83.5312f, 0f) : null);

    public Task<RaceInfo?> GetRaceInfoAsync(byte race, byte gender, CancellationToken cancellationToken = default)
        => Task.FromResult<RaceInfo?>(race == 2
            ? new RaceInfo(gender == 0 ? 51u : 52u, 2)   // orc: Orgrimmar faction template
            : new RaceInfo(gender == 0 ? 49u : 50u, 1)); // human: Stormwind

    public Task<ClassInfo?> GetClassInfoAsync(byte cls, CancellationToken cancellationToken = default)
        => Task.FromResult<ClassInfo?>(new ClassInfo(60, 0, 1)); // warrior-ish defaults

    public Task<bool> IsValidRaceClassAsync(byte race, byte cls, CancellationToken cancellationToken = default)
        => Task.FromResult(IsValid(race, cls));

    // Human and orc warriors (one per faction), both starting at the same spot so tests can
    // put the factions side by side.
    private static bool IsValid(byte race, byte cls) => race is 1 or 2 && cls == 1;
}

internal sealed class InMemoryAccountDataStore : IAccountDataStore
{
    private readonly ConcurrentDictionary<int, AccountSettings> _settings = new();

    public Task<AccountSettings> GetAsync(int accountId, CancellationToken cancellationToken = default)
    {
        // A copy, like a database read: sessions must not share one mutable instance.
        var copy = new AccountSettings();
        if (_settings.TryGetValue(accountId, out AccountSettings? stored))
        {
            lock (stored)
            {
                Array.Copy(stored.Data, copy.Data, AccountSettings.DataTypeCount);
                Array.Copy(stored.Tutorials, copy.Tutorials, AccountSettings.TutorialWordCount);
            }
        }

        return Task.FromResult(copy);
    }

    public Task SaveDataAsync(int accountId, int type, AccountDataEntry entry, CancellationToken cancellationToken = default)
    {
        AccountSettings stored = _settings.GetOrAdd(accountId, _ => new AccountSettings());
        lock (stored)
        {
            stored.Data[type] = entry.Data.Length == 0 ? null : entry;
        }

        return Task.CompletedTask;
    }

    public Task SaveTutorialsAsync(int accountId, IReadOnlyList<uint> tutorials, CancellationToken cancellationToken = default)
    {
        AccountSettings stored = _settings.GetOrAdd(accountId, _ => new AccountSettings());
        lock (stored)
        {
            for (int i = 0; i < AccountSettings.TutorialWordCount; i++)
            {
                stored.Tutorials[i] = tutorials[i];
            }
        }

        return Task.CompletedTask;
    }
}
