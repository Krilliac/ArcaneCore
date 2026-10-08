using System.Collections.Concurrent;
using ArcaneCore.Kernel.Instances;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.Instances;

/// <summary>An in-memory <see cref="IInstanceStore"/> recording every write in order.</summary>
internal sealed class InMemoryInstanceStore : IInstanceStore
{
    /// <summary>What the next host's store holds at startup (a world restarted over stored instances); set by the test that starts the host.</summary>
    public static readonly AsyncLocal<InstanceStoreSnapshot?> Seed = new();

    private readonly InstanceStoreSnapshot _seed = Seed.Value ?? InstanceStoreSnapshot.Empty;

    public InMemoryInstanceStore()
    {
        foreach (InstanceRecord instance in _seed.Instances)
        {
            Live[instance.Id] = 0;
        }
    }

    public ConcurrentQueue<string> Writes { get; } = new();

    /// <summary>The instances whose row exists (saved and not deleted), for the loot state double's scope check.</summary>
    public ConcurrentDictionary<uint, byte> Live { get; } = new();

    /// <summary>Raised after an instance row was deleted (the real store deletes the instance's chest loot in the same transaction).</summary>
    public event Action<uint>? Deleted;

    /// <summary>Delay before an instance row is saved (a slow queued write).</summary>
    public TimeSpan SaveDelay { get; set; }

    public Task<InstanceStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_seed);

    public async Task SaveInstanceAsync(InstanceRecord instance, CancellationToken cancellationToken = default)
    {
        if (SaveDelay > TimeSpan.Zero)
        {
            await Task.Delay(SaveDelay, cancellationToken);
        }

        Live[instance.Id] = 0;
        await Record($"instance {instance.Id} map {instance.MapId}");
    }

    public async Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default)
    {
        Live.TryRemove(instanceId, out _);
        Deleted?.Invoke(instanceId);
        await Record($"delete {instanceId}");
    }

    public Task SaveBindAsync(CharacterInstanceBindRecord bind, CancellationToken cancellationToken = default) => Record($"bind {bind.CharacterId} {bind.InstanceId} {bind.Permanent}");

    public Task DeleteBindAsync(int characterId, uint instanceId, CancellationToken cancellationToken = default) => Record($"unbind {characterId} {instanceId}");

    public Task SaveResetTimeAsync(InstanceResetRecord reset, CancellationToken cancellationToken = default) => Record($"reset {reset.MapId}");

    public Task SaveLastInstanceAsync(CharacterLastInstanceRecord last, CancellationToken cancellationToken = default) => Record($"last {last.CharacterId} {last.MapId} {last.InstanceId}");

    public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Record($"delete character {characterId}");

    public Task SaveGroupBindAsync(GroupInstanceBindRecord bind, CancellationToken cancellationToken = default)
        => Record($"group bind {bind.LeaderCharacterId} {bind.InstanceId} {bind.Permanent}");

    public Task DeleteGroupBindAsync(int leaderCharacterId, uint instanceId, CancellationToken cancellationToken = default)
        => Record($"group unbind {leaderCharacterId} {instanceId}");

    private Task Record(string write)
    {
        Writes.Enqueue(write);
        return Task.CompletedTask;
    }
}

/// <summary>Registers <see cref="InMemoryInstanceStore"/> (one per host) in every <see cref="WorldTestHost"/>.</summary>
internal sealed class InstanceTestServices : IWorldTestServices
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton<InMemoryInstanceStore>();
        services.AddSingleton<IInstanceStore>(sp => sp.GetRequiredService<InMemoryInstanceStore>());
    }
}
