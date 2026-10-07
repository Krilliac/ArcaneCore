using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World;

/// <summary>
/// Owns the world simulation's lifecycle. Registered before the listener, so it starts first
/// and stops last: on start the character name cache is filled and the world features are
/// attached; on shutdown every online character is saved, then the save queue drains.
/// </summary>
public sealed class WorldHost(
    WorldRuntime world,
    CharacterSaveQueue saveQueue,
    CharacterDirectory directory,
    IServiceScopeFactory scopes,
    ILogger<WorldHost> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
        {
            IReadOnlyList<CharacterIdentity> identities = await scope.ServiceProvider
                .GetRequiredService<ICharacterStore>().GetAllIdentitiesAsync(cancellationToken).ConfigureAwait(false);
            directory.Load(identities);

            // Features are singletons, so the scope hands out the same instances.
            scope.ServiceProvider.AttachWorldFeatures(world);
        }

        logger.LogInformation("Loaded {Count} character names", directory.Count);
        saveQueue.Start();
        world.Start();
        await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
        {
            if (scope.ServiceProvider.GetService<Playerbots.ManagedPlayerbotFeature>() is { } playerbots)
                await playerbots.StartupAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        List<Exception> failures = [];
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<Playerbots.ManagedPlayerbotFeature>() is { } playerbots)
                await playerbots.ShutdownBeforeWorldStopAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { failures.Add(ex); }
        finally { world.Stop(); }
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.StopWorldFeaturesAsync().ConfigureAwait(false);
        }
        catch (Exception ex) { failures.Add(ex); }
        finally
        {
            await saveQueue.StopAsync().ConfigureAwait(false);
        }
        logger.LogInformation("World saved and stopped");
        if (failures.Count > 0) throw new AggregateException("World feature shutdown failed", failures);
    }
}
