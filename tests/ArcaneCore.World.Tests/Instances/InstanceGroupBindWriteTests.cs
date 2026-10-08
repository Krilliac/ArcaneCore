using ArcaneCore.Game.Instances;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.World.Instances;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Instances;

/// <summary>
/// The world daemon's instance write queue stores the group binds the instance manager reports (review finding 89; vmangos
/// <c>group_instance</c> INSERT / DELETE in Group::BindToInstance and Group::UnbindInstance), in order with the other instance writes.
/// </summary>
public sealed class InstanceGroupBindWriteTests
{
    [Fact]
    public async Task GroupBindsAndUnbinds_ReachTheStore_InOrder()
    {
        var store = new InMemoryInstanceStore();
        using ServiceProvider services = new ServiceCollection().AddSingleton<IInstanceStore>(store).BuildServiceProvider();
        var queue = new InstanceWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        queue.Start();
        IInstancePersistence persistence = queue;

        persistence.GroupBound(7, 150, permanent: true);
        persistence.PlayerBound(8, 150, permanent: true);
        persistence.GroupUnbound(7, 150);
        await queue.FlushAsync();
        await queue.StopAsync();

        Assert.Equal(["group bind 7 150 True", "bind 8 150 True", "group unbind 7 150"], store.Writes);
    }
}
