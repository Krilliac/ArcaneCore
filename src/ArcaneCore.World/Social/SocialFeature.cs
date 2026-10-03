using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Social;

/// <summary>
/// The social systems in the world daemon: friend/ignore lists, groups, guilds and chat
/// channels (state in <see cref="SocialContext"/>), their login/logout hooks, group/guild/
/// channel chat and the background work (guild preload, ordered writes, out-of-range party
/// stats). Discovered through <see cref="IWorldFeature"/> (docs/integration/seams.md).
/// </summary>
public sealed class SocialFeature(CharacterDirectory directory, IServiceScopeFactory scopes, ILoggerFactory loggers)
    : IWorldFeature, IChatMessageHandler, IAsyncDisposable
{
    /// <summary>How often grouped players' changed stats go to out-of-range members (vmangos sends them from the player update).</summary>
    public const int StatsIntervalMs = 1000;

    private readonly ILogger _logger = loggers.CreateLogger<SocialFeature>();
    private readonly HashSet<ObjectGuid> _ready = [];
    private SocialWriteQueue? _writes;
    private Timer? _statsTimer;
    private Task _guildsLoaded = Task.CompletedTask;
    private WorldRuntime? _world;
    private SocialContext? _context;

    /// <summary>The social state (world thread only).</summary>
    public SocialContext Context => _context ?? throw new InvalidOperationException("social feature not attached");

    /// <summary>Realm rules for cross-faction interaction (vmangos AllowTwoSide.*); off by default.</summary>
    public SocialOptions Options { get; } = new();

    /// <summary>Completes when the stored guilds are installed (world thread).</summary>
    public Task GuildsLoaded => _guildsLoaded;

    /// <summary>Writes queued or in progress.</summary>
    public int PendingWrites => _writes?.Pending ?? 0;

    public void Attach(WorldRuntime world)
    {
        _world = world;
        _writes = new SocialWriteQueue(scopes, loggers.CreateLogger<SocialWriteQueue>());
        _context = new SocialContext(world, new CharacterLookup(directory), _writes, Options);
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

    /// <summary>Stop the stats timer and write out everything queued (the container disposes singletons at shutdown).</summary>
    public async ValueTask DisposeAsync()
    {
        if (_statsTimer is not null)
        {
            await _statsTimer.DisposeAsync().ConfigureAwait(false);
        }

        if (_writes is not null)
        {
            await _writes.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// After the login sequence: load the friend/ignore list off the world thread, then (world
    /// thread) install it and run the vmangos HandlePlayerLogin order — social lists, guild
    /// MOTD and SIGNED_ON, group online update, friends told the player is online.
    /// </summary>
    private void OnLoggedIn(Player player)
    {
        WorldRuntime world = _world!;
        int characterId = (int)player.Guid.Low;
        _ = Task.Run(async () =>
        {
            IReadOnlyList<SocialEntry> entries = [];
            try
            {
                await _guildsLoaded.ConfigureAwait(false);
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                if (scope.ServiceProvider.GetService<ISocialStore>() is { } store)
                {
                    entries = await store.GetSocialAsync(characterId).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "loading the social list of character {Id} failed; starting empty", characterId);
            }

            world.Post(() => CompleteLogin(player, entries));
        });
    }

    private void CompleteLogin(Player player, IReadOnlyList<SocialEntry> entries)
    {
        if (!ReferenceEquals(_world!.FindOnlinePlayer(player.Guid), player))
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
    }

    /// <summary>
    /// Before the player leaves the map (vmangos WorldSession::LogoutPlayer order): guild
    /// SIGNED_OFF, channels left silently, pending invites dropped and the group told the
    /// member is offline, friends told the player is offline.
    /// </summary>
    private void OnLoggingOut(Player player)
    {
        SocialContext context = Context;
        bool ready = _ready.Remove(player.Guid);
        if (ready)
        {
            context.Guilds.OnLoggingOut(player);
        }

        context.Channels.LeaveAll(player);
        context.Groups.OnLoggingOut(player);
        if (ready)
        {
            context.Friends.BroadcastPresence(player, online: false);
        }

        context.Friends.Unload(player);
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
                    guilds = await store.GetGuildsAsync().ConfigureAwait(false);
                }

                break;
            }
            catch (Exception ex) when (attempt < 3)
            {
                _logger.LogWarning(ex, "loading guilds failed (attempt {Attempt}); retrying", attempt);
                await Task.Delay(500 * attempt).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Guild creation stays disabled (GuildManager.IsLoaded) so stored ids are never reused.
                _logger.LogError(ex, "loading guilds failed; guilds are unavailable until restart");
                return;
            }
        }

        await world.InvokeAsync(() =>
        {
            Context.Guilds.Load(guilds);
            return true;
        }).ConfigureAwait(false);
    }
}
