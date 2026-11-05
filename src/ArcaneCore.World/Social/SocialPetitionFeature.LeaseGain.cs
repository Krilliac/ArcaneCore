using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Social;
using ArcaneCore.World.Cluster.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Social;

// Guilds and petitions follow the Social lease: a worker that gains it at run time rereads both before any of its saves reach storage.
public sealed partial class SocialPetitionFeature
{
    /// <summary>How long one reread may take before it counts as failed and is tried again.</summary>
    private static readonly TimeSpan ReloadBudget = TimeSpan.FromSeconds(30);

    private const int ReloadRetryDelayMs = 1000;

    private PeerGuildHolderLink? _reloadLink;
    private Task? _reload;
    private bool _reloading; // world thread
    private long _reloadNotBeforeMs; // world thread: a failed read is tried again from then on

    /// <summary>
    /// World thread: whether the guilds and petitions here are current for this worker's holding of the Social lease. Always true on a worker that held the lease at attach (or has no peer link).
    /// On any other worker they are a replica: the previous holder may have stored changes that it never published, and a save writes a whole guild or petition. So until this is true the link says this worker is not
    /// the holder, even with the lease held (<see cref="PeerGuildHolderLink.ReloadPending"/>), and nothing is stored. The first call that finds the lease held, with the attach loads over, starts the read, off the world
    /// thread; the world tick makes that call. A failed read is tried again a second later and the saves stay held.
    /// </summary>
    internal bool EnsureGuildsCurrent()
    {
        if (_reloadLink is not { ReloadPending: true } link)
        {
            return true;
        }

        if (!_reloading && !_stopping && _loading.IsCompleted && link.ReloadDue && Environment.TickCount64 >= _reloadNotBeforeMs && _world is { } world)
        {
            _reloading = true;
            _reload = Task.Run(() => ReadAuthorityStateAsync(world, link));
        }

        return false;
    }

    private void ReloadOnLeaseGain(uint diffMs) => EnsureGuildsCurrent();

    private async Task ReadAuthorityStateAsync(WorldRuntime world, PeerGuildHolderLink link)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            budget.CancelAfter(ReloadBudget);
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            IReadOnlyList<GuildData>? guilds = null;
            IReadOnlyList<PetitionData>? petitions = null;
            if (scope.ServiceProvider.GetService<ISocialStore>() is { } store)
            {
                guilds = await store.GetGuildsAsync(budget.Token).ConfigureAwait(false);
            }

            if (scope.ServiceProvider.GetService<IPetitionStore>() is { } petitionStore)
            {
                petitions = await petitionStore.GetPetitionsAsync(budget.Token).ConfigureAwait(false);
            }

            world.Post(() => ApplyAuthorityState(link, guilds, petitions));
        }
        catch (OperationCanceledException) when (_stopping)
        {
            // Stopping: nothing was stored from the replica, and nothing is now.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault(LogLevel.Warning, "social.lease-gain-reload", ex, "Reading the guilds and petitions after gaining the social lease failed; their saves stay held and the read is tried again");
            world.Post(RetryReloadLater);
        }
    }

    private void RetryReloadLater()
    {
        _reloading = false;
        _reloadNotBeforeMs = Environment.TickCount64 + ReloadRetryDelayMs;
    }

    /// <summary>
    /// World thread: the link says holder, then storage replaces the replicas, guilds first (a petition's owner must not be guilded). In that order the repairs of the load are written and the result is published. With no
    /// store the replica is all there is, and it stays. If installing the rows fails this worker goes back to holding its saves and reads again, so a half-installed state is never stored.
    /// </summary>
    private void ApplyAuthorityState(PeerGuildHolderLink link, IReadOnlyList<GuildData>? guilds, IReadOnlyList<PetitionData>? petitions)
    {
        _reloading = false;
        if (_stopping)
        {
            return;
        }

        try
        {
            link.ReloadFinished();
            if (guilds is not null)
            {
                Social.Context.Guilds.ReloadFromStorage(guilds);
            }

            if (petitions is not null)
            {
                EnsureWired();
                Petitions.ReloadFromStorage(petitions);
                foreach (Player player in _world!.OnlinePlayers.ToArray())
                {
                    Petitions.OnPlayerLoggedIn(player);
                }
            }

            _logger.LogInformation("Social lease gained: {Guilds} guilds and {Petitions} petitions loaded from storage", Social.Context.Guilds.All.Count, Petitions.All.Count);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            link.MarkReloadPending();
            RetryReloadLater();
            _logger.LogFault("social.lease-gain-install", ex, "Installing the guilds and petitions read after gaining the social lease failed; their saves stay held and the read is tried again");
        }
    }
}
