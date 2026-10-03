using System.Collections.Concurrent;
using ArcaneCore.Kernel.Instances;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.Instances;

/// <summary>An in-memory <see cref="IInstanceStore"/> recording every write in order.</summary>
internal sealed class InMemoryInstanceStore : IInstanceStore
{
    public ConcurrentQueue<string> Writes { get; } = new();

    public Task<InstanceStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(InstanceStoreSnapshot.Empty);

    public Task SaveInstanceAsync(InstanceRecord instance, CancellationToken cancellationToken = default) => Record($"instance {instance.Id} map {instance.MapId}");

    public Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default) => Record($"delete {instanceId}");

    public Task SaveBindAsync(CharacterInstanceBindRecord bind, CancellationToken cancellationToken = default) => Record($"bind {bind.CharacterId} {bind.InstanceId} {bind.Permanent}");

    public Task DeleteBindAsync(int characterId, uint instanceId, CancellationToken cancellationToken = default) => Record($"unbind {characterId} {instanceId}");

    public Task SaveResetTimeAsync(InstanceResetRecord reset, CancellationToken cancellationToken = default) => Record($"reset {reset.MapId}");

    public Task SaveLastInstanceAsync(CharacterLastInstanceRecord last, CancellationToken cancellationToken = default) => Record($"last {last.CharacterId} {last.MapId} {last.InstanceId}");

    public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Record($"delete character {characterId}");

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
