using System.Collections.Concurrent;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using Xunit;

namespace ArcaneCore.World.Tests.Characters;

/// <summary>
/// The character directory is per process. <see cref="CharacterDirectory.RefreshAsync"/> brings it up to what storage holds, which a worker gaining the Social lease needs before its load repairs delete on an unknown character.
/// </summary>
public sealed class CharacterDirectoryRefreshTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static CharacterIdentity Identity(int id, string name = "") => new(id, 100 + id, name.Length == 0 ? "Char" + id : name, 1, 0, 1);

    [Fact]
    public async Task Refresh_learns_what_other_workers_created_renamed_and_deleted()
    {
        var directory = new CharacterDirectory();
        directory.Load([Identity(1), Identity(2), Identity(3)]);
        var store = new IdentityRows();
        store.Put(Identity(1));
        store.Put(Identity(2, "Renamed"));
        store.Put(Identity(4));

        await directory.RefreshAsync(store);

        Assert.NotNull(directory.Find(1));
        Assert.Equal("Renamed", directory.Find(2)!.Name);
        Assert.Null(directory.Find(3));
        Assert.NotNull(directory.Find(4));
    }

    [Fact]
    public async Task What_this_process_changed_during_the_read_stays()
    {
        var directory = new CharacterDirectory();
        directory.Load([Identity(1), Identity(2)]);
        var store = new IdentityRows();
        store.Put(Identity(1));
        store.Put(Identity(2));
        store.HoldReads();

        Task refresh = directory.RefreshAsync(store);
        await store.ReadHeld.WaitAsync(Budget);

        // The read has its rows already: it holds neither the new character nor the deletion, and has the old name.
        directory.Add(Identity(3));
        directory.Remove(2);
        directory.Add(Identity(1, "Renamed"));
        store.ReleaseReads();
        await refresh.WaitAsync(Budget);

        Assert.NotNull(directory.Find(3));
        Assert.Null(directory.Find(2));
        Assert.Equal("Renamed", directory.Find(1)!.Name);
    }

    [Fact]
    public async Task A_failed_read_changes_nothing()
    {
        var directory = new CharacterDirectory();
        directory.Load([Identity(1)]);
        var store = new IdentityRows();
        store.FailNextRead();

        await Assert.ThrowsAsync<IOException>(() => directory.RefreshAsync(store));

        Assert.NotNull(directory.Find(1));
        Assert.Equal(1, directory.Count);
    }
}

/// <summary>The identity read of a character table, shared by every worker: rows can change behind a directory's back, and a read can be held or failed. Nothing else of the store is used by these tests.</summary>
internal sealed class IdentityRows : ICharacterStore
{
    private readonly ConcurrentDictionary<int, CharacterIdentity> _rows = new();
    private readonly TaskCompletionSource _readFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _readHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _release;
    private int _failReads;

    /// <summary>A read failed (<see cref="FailNextRead"/>).</summary>
    public Task ReadFailed => _readFailed.Task;

    /// <summary>A read took its rows and waits (<see cref="HoldReads"/>).</summary>
    public Task ReadHeld => _readHeld.Task;

    public void Put(CharacterIdentity identity) => _rows[identity.Id] = identity;

    public void Delete(int id) => _rows.TryRemove(id, out _);

    public void HoldReads() => Volatile.Write(ref _release, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public void ReleaseReads() => Volatile.Read(ref _release)!.TrySetResult();

    public void FailNextRead() => Volatile.Write(ref _failReads, 1);

    public async Task<IReadOnlyList<CharacterIdentity>> GetAllIdentitiesAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _failReads, 0) == 1)
        {
            _readFailed.TrySetResult();
            throw new IOException("character read unavailable");
        }

        CharacterIdentity[] rows = [.. _rows.Values];
        if (Volatile.Read(ref _release) is { } release)
        {
            _readHeld.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }

        return rows;
    }

    public Task<IReadOnlyList<CharacterRecord>> GetByAccountAsync(int accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CharacterRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<bool> IsNameTakenAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<int> CountByAccountAsync(int accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CharacterRecord> CreateAsync(CharacterRecord character, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<bool> DeleteAsync(int id, int accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task SaveStateAsync(CharacterState state, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<ActionButton>> GetActionButtonsAsync(int characterId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
