using ArcaneCore.Game.Maps;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World;

/// <summary>
/// Owns the world simulation's lifecycle. Registered before the listener, so it starts first
/// and stops last: on shutdown every online character is saved, then the save queue drains.
/// </summary>
public sealed class WorldHost(WorldRuntime world, CharacterSaveQueue saveQueue, ILogger<WorldHost> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        saveQueue.Start();
        world.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        world.Stop();
        await saveQueue.StopAsync().ConfigureAwait(false);
        logger.LogInformation("World saved and stopped");
    }
}
