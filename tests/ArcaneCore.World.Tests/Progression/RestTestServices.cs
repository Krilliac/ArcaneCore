using System.Collections.Concurrent;
using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Data.Characters.Rename;
using ArcaneCore.Data.World.Rest;
using ArcaneCore.Kernel.Characters;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.Progression;

/// <summary>An in-memory rested-state store whose writes can be made to fail.</summary>
internal sealed class InMemoryRestStore : ICharacterRestStore
{
    private readonly ConcurrentDictionary<int, CharacterRestState> _rows = new();
    private int _saves;

    /// <summary>While set, every save throws (a database outage).</summary>
    public volatile bool FailSaves;

    public int Saves => Volatile.Read(ref _saves);

    public CharacterRestState? Get(int characterId) => _rows.TryGetValue(characterId, out CharacterRestState state) ? state : null;

    public void Set(int characterId, CharacterRestState state) => _rows[characterId] = state;

    public Task<CharacterRestState?> LoadAsync(int characterId, CancellationToken cancellationToken = default)
        => Task.FromResult(Get(characterId));

    public Task SaveAsync(int characterId, CharacterRestState state, CancellationToken cancellationToken = default)
    {
        if (FailSaves)
        {
            throw new InvalidOperationException("the characters database is unavailable");
        }

        Interlocked.Increment(ref _saves);
        _rows[characterId] = state;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default)
    {
        _rows.TryRemove(characterId, out _);
        return Task.CompletedTask;
    }
}

/// <summary>The <c>areatrigger_tavern</c> rows of a test host.</summary>
internal sealed class InMemoryTavernStore : IAreaTriggerTavernStore
{
    public List<uint> Ids { get; } = [];

    public volatile Exception? Failure;

    public Task<IReadOnlyList<uint>> LoadAsync(CancellationToken cancellationToken = default)
        => Failure is { } failure ? Task.FromException<IReadOnlyList<uint>>(failure) : Task.FromResult<IReadOnlyList<uint>>([.. Ids.Order()]);
}

/// <summary>
/// The at-login flags and the rename over the host's in-memory characters: the same rules as <see cref="EfCharacterRenameStore"/>
/// (account ownership, flag, case-insensitive name check; the Data tests run the real store on every engine).
/// </summary>
internal sealed class InMemoryRenameStore(InMemoryCharacterStore characters) : ICharacterRenameStore
{
    private readonly ConcurrentDictionary<int, uint> _flags = new();

    public bool FailRenames;

    public uint FlagsOf(int characterId) => _flags.GetValueOrDefault(characterId);

    public async Task<bool> SetFlagAsync(int characterId, uint flag, CancellationToken cancellationToken = default)
    {
        if (await characters.GetByIdAsync(characterId, cancellationToken) is null)
        {
            return false;
        }

        _flags.AddOrUpdate(characterId, flag, (_, old) => old | flag);
        return true;
    }

    public async Task<IReadOnlyDictionary<int, uint>> GetFlagsAsync(int accountId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CharacterRecord> own = await characters.GetByAccountAsync(accountId, cancellationToken);
        return own.Where(c => _flags.GetValueOrDefault(c.Id) != 0).ToDictionary(c => c.Id, c => _flags[c.Id]);
    }

    public async Task<CharacterRenameResult> RenameAsync(int characterId, int accountId, string newName, CancellationToken cancellationToken = default)
    {
        if (FailRenames)
        {
            throw new InvalidOperationException("the characters database is unavailable");
        }

        CharacterRecord? character = await characters.GetByIdAsync(characterId, cancellationToken);
        if (character is null || character.AccountId != accountId || (_flags.GetValueOrDefault(characterId) & CharacterAtLoginFlags.Rename) == 0)
        {
            return new CharacterRenameResult(CharacterRenameOutcome.NotAllowed);
        }

        if (await characters.IsNameTakenAsync(newName, cancellationToken))
        {
            return new CharacterRenameResult(CharacterRenameOutcome.NameTaken, character.Name);
        }

        string old = character.Name;
        character.Name = newName;
        _flags.AddOrUpdate(characterId, 0u, (_, flags) => flags & ~CharacterAtLoginFlags.Rename);
        return new CharacterRenameResult(CharacterRenameOutcome.Renamed, old);
    }
}

/// <summary>Registers the rest, tavern and rename stores in every <see cref="WorldTestHost"/> (empty unless a test fills them).</summary>
internal sealed class RestTestServices : IWorldTestServices
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<InMemoryRestStore>();
        services.AddSingleton<ICharacterRestStore>(sp => sp.GetRequiredService<InMemoryRestStore>());
        services.AddSingleton<InMemoryTavernStore>();
        services.AddSingleton<IAreaTriggerTavernStore>(sp => sp.GetRequiredService<InMemoryTavernStore>());
        services.AddSingleton(sp => new InMemoryRenameStore((InMemoryCharacterStore)sp.GetRequiredService<ICharacterStore>()));
        services.AddSingleton<ICharacterRenameStore>(sp => sp.GetRequiredService<InMemoryRenameStore>());
    }
}
