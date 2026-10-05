using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Social;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ArcaneCore.World.Spells;

namespace ArcaneCore.World.Social;

/// <summary>
/// The social systems in the world daemon: friend/ignore lists, groups, guilds and chat
/// channels (state in <see cref="SocialContext"/>), their login/logout hooks, group/guild/
/// channel chat and the background work (guild preload, ordered writes, out-of-range party
/// stats). Discovered through <see cref="IWorldFeature"/> (docs/integration/seams.md).
/// </summary>
public sealed class SocialFeature(
    CharacterDirectory directory,
    IServiceScopeFactory scopes,
    ILoggerFactory loggers,
    IConfiguration? configuration = null,
    IOptions<SocialOptions>? socialOptions = null,
    IServiceProvider? services = null)
    : IWorldFeature, IChatMessageHandler, IAsyncDisposable
{
    /// <summary>How often grouped players' changed stats go to out-of-range members (vmangos sends them from the player update).</summary>
    public const int StatsIntervalMs = 1000;
    private const int MaxDeferredCommands = 128;

    private readonly ILogger _logger = loggers.CreateLogger<SocialFeature>();
    private readonly HashSet<ObjectGuid> _ready = [];
    private readonly Dictionary<Player, List<Action<SocialContext>>> _pendingCommands = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Player> _rejectedCommands = new(ReferenceEqualityComparer.Instance);
    private SocialWriteQueue? _writes;
    private Timer? _statsTimer;
    private Task _guildsLoaded = Task.CompletedTask;
    private WorldRuntime? _world;
    private SocialContext? _context;
    private readonly Lock _lifecycleLock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<Task> _loginReads = [];
    private volatile bool _stopping;
    private Task? _stopped;

    /// <summary>The social state (world thread only).</summary>
    public SocialContext Context => _context ?? throw new InvalidOperationException("social feature not attached");

    /// <summary>Realm rules for cross-faction interaction (vmangos AllowTwoSide.*); off by default.</summary>
    public SocialOptions Options { get; } = socialOptions?.Value ?? new SocialOptions();

    /// <summary>Bounds of the write queue (configuration section <c>World:Social:WriteQueue</c>).</summary>
    public SocialWriteQueueOptions WriteQueueOptions { get; } = new();

    /// <summary>Guild, charter and petition rules (World:Guild, bound at startup; restart-only).</summary>
    public GuildOptions GuildOptions { get; } = new();

    /// <summary>Completes when the stored guilds are installed (world thread).</summary>
    public Task GuildsLoaded => _guildsLoaded;

    /// <summary>Writes queued or in progress.</summary>
    public int PendingWrites => _writes?.Pending ?? 0;

    /// <summary>
    /// World thread: defer parsed social commands until this exact player's stored list is
    /// installed. Commands from a departed session cannot mutate a replacement with its GUID.
    /// </summary>
    public void ExecuteWhenReady(Player player, Action<SocialContext> action)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(action);
        if (_world is not { } world || !world.IsWorldThread)
        {
            throw new InvalidOperationException("social commands require the attached world thread");
        }

        if (_stopping || _rejectedCommands.Contains(player) || !ReferenceEquals(world.FindOnlinePlayer(player.Guid), player))
        {
            return;
        }

        if (_ready.Contains(player.Guid))
        {
            action(Context);
            return;
        }

        if (!_pendingCommands.TryGetValue(player, out List<Action<SocialContext>>? pending))
        {
            _pendingCommands[player] = pending = [];
        }

        if (pending.Count >= MaxDeferredCommands)
        {
            _pendingCommands.Remove(player);
            _rejectedCommands.Add(player);
            _logger.LogWarning("deferred social command limit exceeded for {Player}; disconnecting", player.Name);
            player.Session.Kick();
            return;
        }

        pending.Add(action);
    }

    public void Attach(WorldRuntime world)
    {
        _world = world;
        configuration?.GetSection(SocialOptions.SectionName).Bind(Options);
        configuration?.GetSection(GuildOptions.SectionName).Bind(GuildOptions);
        configuration?.GetSection(SocialOptions.SectionName + ":WriteQueue").Bind(WriteQueueOptions);
        _writes = new SocialWriteQueue(scopes, loggers.CreateLogger<SocialWriteQueue>(), WriteQueueOptions);
        _context = new SocialContext(world, new CharacterLookup(directory), _writes, Options);
        if (services?.GetService<SpellFeature>() is { } spellFeature)
            spellFeature.System.VisibleAuraSlotChanged += OnVisibleAuraSlotChanged;
        _context.Guilds.Options = GuildOptions;
        _writes.Start();
        world.PlayerLoggedIn += OnLoggedIn;
        world.PlayerLoggingOut += OnLoggingOut;
        _guildsLoaded = Task.Run(LoadGuildsAsync);
        _statsTimer = new Timer(_ => world.Post(() => Context.Groups.UpdateOutOfRangeStats()), null, StatsIntervalMs, StatsIntervalMs);
    }

    /// <summary>
    /// Guild, officer, party, raid and channel chat (vmangos HandleMessagechatOpcode). Group
    /// chat is Universal on two-side group realms; guild and officer chat are always Universal
    /// (addon messages keep the addon language).
    /// </summary>
    public bool TryHandle(WorldSession session, Player player, ClientChatMessage message)
    {
        switch (message.Type)
        {
            case ChatType.Party or ChatType.Raid or ChatType.RaidLeader or ChatType.RaidWarning:
            {
                Language language = message.Language != Language.Addon && Options.AllowTwoSideGroup ? Language.Universal : message.Language;
                byte[] packet = ChatPackets.BuildMessage(message.Type, language, player.Guid, message.Text, player.ChatTag);
                Context.Groups.BroadcastChat(player, message.Type, packet);
                return true;
            }

            case ChatType.Guild or ChatType.Officer:
            {
                Language language = message.Language == Language.Addon ? Language.Addon : Language.Universal;
                byte[] packet = ChatPackets.BuildMessage(message.Type, language, player.Guid, message.Text, player.ChatTag);
                Context.Guilds.BroadcastChat(player, message.Type == ChatType.Officer, packet);
                return true;
            }

            case ChatType.Channel:
                Context.Channels.Say(player, message.Target, message.Text, message.Language);
                return true;

            default:
                return false;
        }
    }

    /// <summary>Stop background reads and timers, then drain writes before the provider is disposed.</summary>
    public Task StopAsync()
    {
        lock (_lifecycleLock)
        {
            _stopping = true;
            return _stopped ??= StopCoreAsync();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task StopCoreAsync()
    {
        // Do not run cancellation callbacks while holding the lifecycle lock.
        await Task.Yield();
        _stop.Cancel();
        if (_world is not null)
        {
            _world.PlayerLoggedIn -= OnLoggedIn;
            _world.PlayerLoggingOut -= OnLoggingOut;
        }
        if (services?.GetService<SpellFeature>() is { } spellFeature)
            spellFeature.System.VisibleAuraSlotChanged -= OnVisibleAuraSlotChanged;

        if (_statsTimer is not null)
        {
            await _statsTimer.DisposeAsync().ConfigureAwait(false);
        }

        Task[] reads;
        lock (_lifecycleLock)
        {
            reads = [.. _loginReads];
        }
        await Task.WhenAll(reads.Append(_guildsLoaded)).ConfigureAwait(false);

        try
        {
            if (_writes is not null)
            {
                await _writes.StopAsync().ConfigureAwait(false); // throws, naming what is not durable, if storage is still failing
            }
        }
        finally
        {
            _stop.Dispose();
        }
    }

    /// <summary>
    /// After the login sequence: load the friend/ignore list off the world thread, then (world
    /// thread) install it and run the vmangos HandlePlayerLogin order — social lists, guild
    /// MOTD and SIGNED_ON, group online update, friends told the player is online.
    /// </summary>
    private void OnLoggedIn(Player player)
    {
        lock (_lifecycleLock)
        {
            if (_stopping)
            {
                return;
            }
            Task read = Task.Run(() => LoadSocialAsync(player));
            _loginReads.Add(read);
            _ = read.ContinueWith(completed =>
            {
                lock (_lifecycleLock)
                {
                    _loginReads.Remove(completed);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void OnVisibleAuraSlotChanged(Unit target, byte slot)
    {
        if (target.GetCharmerOrOwnerPlayer() is { } owner && ReferenceEquals(owner.GetPet(), target))
            Context.Groups.MarkPetAuraChanged(owner, slot);
    }

    private async Task LoadSocialAsync(Player player)
    {
        WorldRuntime world = _world!;
        int characterId = (int)player.Guid.Low;
        IReadOnlyList<SocialEntry> entries = [];
        try
        {
            await _guildsLoaded.WaitAsync(_stop.Token).ConfigureAwait(false);
            _stop.Token.ThrowIfCancellationRequested();
            if (_writes is { } writes)
            {
                // Earlier writes first; a change of this character that still cannot be stored is
                // applied over the stored rows below, so storage never rolls the live list back.
                try
                {
                    await writes.FlushCharacterAsync(characterId).WaitAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogWarning(ex, "social writes of character {Id} are retained; its list is loaded with them applied", characterId);
                }
            }

            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<ISocialStore>() is { } store)
            {
                entries = await store.GetSocialAsync(characterId, _stop.Token).ConfigureAwait(false);
            }

            if (_writes is not null)
            {
                entries = _writes.WithRetained(characterId, entries);
            }
        }
        catch (OperationCanceledException) when (_stopping)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "loading the social list of character {Id} failed; starting empty", characterId);
        }

        if (!_stopping)
        {
            world.Post(() => CompleteLogin(player, entries));
        }
    }

    private void CompleteLogin(Player player, IReadOnlyList<SocialEntry> entries)
    {
        WorldRuntime world = _world!;
        if (_stopping || !ReferenceEquals(world.FindOnlinePlayer(player.Guid), player))
        {
            return; // logged out meanwhile
        }

        SocialContext context = Context;
        context.Friends.Load(player, entries);

        // LoginSequence already sent empty lists (the seam); only non-empty ones are resent.
        PlayerSocial social = context.Friends.Get(player);
        if (social.Count(SocialFlags.Friend) > 0)
        {
            context.Friends.SendFriendList(player);
        }

        if (social.Count(SocialFlags.Ignored) > 0)
        {
            context.Friends.SendIgnoreList(player);
        }

        context.Guilds.OnLoggedIn(player);
        context.Groups.OnLoggedIn(player);
        context.Friends.BroadcastPresence(player, online: true);
        _ready.Add(player.Guid);
        if (_pendingCommands.Remove(player, out List<Action<SocialContext>>? pending))
        {
            foreach (Action<SocialContext> action in pending)
            {
                if (_stopping || !ReferenceEquals(world.FindOnlinePlayer(player.Guid), player))
                {
                    break;
                }

                try
                {
                    action(context);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "deferred social command failed for {Player}", player.Name);
                }
            }
        }
    }

    /// <summary>
    /// Before the player leaves the map (vmangos WorldSession::LogoutPlayer order): guild
    /// SIGNED_OFF, channels left silently, pending invites dropped and the group told the
    /// member is offline, friends told the player is offline.
    /// </summary>
    private void OnLoggingOut(Player player)
    {
        _pendingCommands.Remove(player);
        _rejectedCommands.Remove(player);
        SocialContext context = Context;

        // Keep the character directory's level and zone current: an offline guild member's roster
        // line and a later Guild::AddMember read them (vmangos PlayerCacheData, Guild.cpp:230-256).
        if (directory.Find((int)player.Guid.Low) is { } identity)
        {
            directory.Add(identity with { Level = player.Level, ZoneId = player.ZoneId });
        }

        bool ready = _ready.Remove(player.Guid);
        if (ready)
        {
            context.Guilds.OnLoggingOut(player);
        }

        context.Channels.LeaveAll(player);
        context.Groups.OnLoggingOut(player);

        // Friends can already see this player online while its own social rows are loading.
        // Presence uses the observers' lists, so their offline update must not wait for ours.
        context.Friends.BroadcastPresence(player, online: false);

        context.Friends.Unload(player);

        int characterId = (int)player.Guid.Low;
        if (_writes is { } writes && writes.HasRetainedFailure(characterId))
        {
            writes.RequestRetry(characterId); // an early retry; the login barrier and shutdown still retry
        }
    }

    private async Task LoadGuildsAsync()
    {
        WorldRuntime world = _world!;
        IReadOnlyList<GuildData> guilds = [];
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                if (scope.ServiceProvider.GetService<ISocialStore>() is { } store)
                {
                    guilds = await store.GetGuildsAsync(_stop.Token).ConfigureAwait(false);
                }

                break;
            }
            catch (OperationCanceledException) when (_stopping)
            {
                return;
            }
            catch (Exception ex) when (attempt < 3)
            {
                _logger.LogWarning(ex, "loading guilds failed (attempt {Attempt}); retrying", attempt);
                try
                {
                    await Task.Delay(500 * attempt, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stopping)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                // Guild creation stays disabled (GuildManager.IsLoaded) so stored ids are never reused.
                _logger.LogError(ex, "loading guilds failed; guilds are unavailable until restart");
                return;
            }
        }

        try
        {
            await world.InvokeAsync(() =>
            {
                if (!_stopping)
                {
                    Context.Guilds.Load(guilds);
                }
                return true;
            }).WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping)
        {
            // The world may already be stopped; no pending world invocation holds up disposal.
        }
    }
}
