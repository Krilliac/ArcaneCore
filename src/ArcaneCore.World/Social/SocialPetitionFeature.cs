using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Social;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Social;

/// <summary>
/// Guild charters in the world daemon: loads the stored petitions once the guilds are installed
/// (a petition's owner must not be guilded), routes the petitioner gossip option to the petition
/// list, watches each player's inventory for a destroyed charter and heals an owner whose charter is
/// gone at login, and cleans up after a deleted character. The rules live in
/// <see cref="PetitionManager"/>; writes ride the one ordered <see cref="SocialWriteQueue"/>, which
/// <see cref="SocialFeature"/> drains at shutdown.
/// <para>
/// The name sorts after <see cref="SocialFeature"/> on purpose: features attach in full-name order
/// and this one waits for the guild preload that <see cref="SocialFeature.Attach"/> starts
/// (docs/integration/seams.md).
/// </para>
/// </summary>
public sealed class SocialPetitionFeature(IServiceProvider services, IServiceScopeFactory scopes, ILoggerFactory loggers)
    : IWorldFeature, ICharacterDeleteHook, IAsyncDisposable
{
    /// <summary>Upper bound for one world-thread round trip during a character deletion.</summary>
    public static readonly TimeSpan WorldCallTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger = loggers.CreateLogger<SocialPetitionFeature>();
    private readonly CancellationTokenSource _stop = new();
    private WorldRuntime? _world;
    private QuestNpcServices? _wired;
    private Task _loading = Task.CompletedTask;
    private volatile bool _stopping;

    /// <summary>Completes when the stored petitions are installed (or loading was abandoned).</summary>
    public Task PetitionsLoaded => _loading;

    private SocialFeature Social => services.GetRequiredService<SocialFeature>();

    private PetitionManager Petitions => Social.Context.Petitions;

    public void Attach(WorldRuntime world)
    {
        _world = world;
        world.PlayerLoggedIn += OnLoggedIn;
        world.PlayerLoggingOut += OnLoggingOut;
        _loading = Task.Run(LoadAsync);
    }

    public async Task StopAsync()
    {
        _stopping = true;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_world is { } world)
        {
            world.PlayerLoggedIn -= OnLoggedIn;
            world.PlayerLoggingOut -= OnLoggingOut;
        }

        try
        {
            await _loading.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    /// <summary>
    /// The Petitioner gossip option closed the menu; the petition list opens (vmangos Player::OnGossipSelect,
    /// Player.cpp:12264-12267 → SendPetitionShowList). The TabardDesigner option closed it too and the tabard
    /// designer opens (Player.cpp:12268-12271 → SendTabardVendorActivate).
    /// </summary>
    public void OnForeignOptionSelected(Player player, NpcInfo npc, GossipOption option)
    {
        switch (option)
        {
            case GossipOption.Petitioner:
                Petitions.ShowList(player, npc.Guid);
                break;
            case GossipOption.TabardDesigner:
                Social.Context.Guilds.ActivateTabardVendor(player, npc.Guid);
                break;
        }
    }

    // --- load ----------------------------------------------------------------------------------------

    private async Task LoadAsync()
    {
        try
        {
            SocialFeature social = Social;
            await social.GuildsLoaded.WaitAsync(_stop.Token).ConfigureAwait(false);
            IReadOnlyList<PetitionData>? petitions = await ReadPetitionsAsync().ConfigureAwait(false);
            if (petitions is null || _stopping)
            {
                return;
            }

            await _world!.InvokeAsync(() =>
            {
                if (_stopping)
                {
                    return false;
                }

                if (!social.Context.Guilds.IsLoaded)
                {
                    // Without the guilds a petition owner cannot be validated; petitions stay disabled.
                    _logger.LogError("guilds are unavailable; petitions stay disabled until restart");
                    return false;
                }

                EnsureWired();
                social.Context.Petitions.Load(petitions);
                foreach (Player player in _world.OnlinePlayers.ToArray())
                {
                    social.Context.Petitions.OnPlayerLoggedIn(player);
                }

                _logger.LogInformation("Loaded {Count} guild petitions", petitions.Count);
                return true;
            }).WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping)
        {
            // stopping: the world may already be gone
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "loading petitions failed; petitions are unavailable until restart");
        }
    }

    private async Task<IReadOnlyList<PetitionData>?> ReadPetitionsAsync()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                return scope.ServiceProvider.GetService<IPetitionStore>() is { } store
                    ? await store.GetPetitionsAsync(_stop.Token).ConfigureAwait(false)
                    : [];
            }
            catch (OperationCanceledException) when (_stopping)
            {
                return null;
            }
            catch (Exception ex) when (attempt < 3)
            {
                _logger.LogWarning(ex, "loading petitions failed (attempt {Attempt}); retrying", attempt);
                await Task.Delay(500 * attempt, _stop.Token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Connect the NPC services (money and interaction checks, the gossip event). Done on the world thread
    /// and repeated at every login, so a replaced <see cref="QuestNpcServices"/> instance is picked up.
    /// </summary>
    private void EnsureWired()
    {
        QuestNpcServices? npc = services.GetService<QuestNpcFeature>()?.Services;
        if (npc is null || ReferenceEquals(npc, _wired))
        {
            return;
        }

        if (_wired is not null)
        {
            _wired.ForeignOptionSelected -= OnForeignOptionSelected;
        }

        _wired = npc;
        npc.ForeignOptionSelected += OnForeignOptionSelected;
        Petitions.Npc = npc;
        Social.Context.Guilds.Npc = npc;
    }

    // --- player lifecycle ----------------------------------------------------------------------------

    private void OnLoggedIn(Player player)
    {
        if (_stopping)
        {
            return;
        }

        EnsureWired();
        Petitions.OnPlayerLoggedIn(player);
    }

    private void OnLoggingOut(Player player) => Petitions.OnPlayerLoggingOut(player);

    // --- character deletion --------------------------------------------------------------------------

    /// <summary>
    /// Every queued petition write is attempted before the rows go, so a snapshot queued earlier cannot
    /// resurrect the deleted character's petition after the cleanup transaction (docs/integration/character-delete.md).
    /// </summary>
    public async Task OnCharacterDeletingAsync(WorldSession session, CharacterRecord character)
        => await Social.Context.Persistence.FlushAsync().WaitAsync(CharacterDeletion.DrainTimeout).ConfigureAwait(false);

    /// <summary>
    /// The rows are gone: the character's petition and signatures leave memory. The conditional purge of any
    /// late write is queued by <see cref="SocialWriteQueue.PurgeCharacter"/>, which the social delete hook
    /// calls, so this hook queues nothing and awaits that purge.
    /// </summary>
    public async Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
    {
        if (_world is not { } world)
        {
            return;
        }

        uint id = (uint)character.Id;
        await world.InvokeAsync(() =>
        {
            Petitions.OnCharacterDeleted(id);
            return true;
        }).WaitAsync(WorldCallTimeout).ConfigureAwait(false);
    }
}
