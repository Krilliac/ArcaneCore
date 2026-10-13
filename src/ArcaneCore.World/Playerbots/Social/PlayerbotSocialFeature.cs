using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Data.Content.Names;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Chat;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Names;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>One running, non-scripted managed bot as the social features see it for one pass (world thread).</summary>
internal sealed record PlayerbotSocialBot(Guid Id, WorldSession Session, Player Player, PlayerbotPartyAI Party, bool PartyDriven, bool GroupDriven);

/// <summary>
/// Every interval the social feature uses, in milliseconds of world time. The defaults are the documented ones (docs/areas/playbots-social.md);
/// tests shorten them. Never configuration-bound.
/// </summary>
internal sealed class PlayerbotSocialTimings
{
    /// <summary>World time between two passes.</summary>
    public int PassIntervalMs { get; set; } = 1000;

    /// <summary>How often the registry of bots is read again from the store.</summary>
    public long RegistryRefreshMs { get; set; } = 60_000;

    /// <summary>The soonest the registry is read again because a running bot was missing from it.</summary>
    public long RegistryMissGapMs { get; set; } = 1000;

    /// <summary>The least time between two founding attempts.</summary>
    public long FoundingSpacingMs { get; set; } = 300_000;

    /// <summary>The least time between two invitations of one guild.</summary>
    public long RecruitSpacingMs { get; set; } = 5000;

    /// <summary>How long a recruiting invitation is expected to be answered.</summary>
    public long PendingInviteTimeoutMs { get; set; } = 60_000;

    /// <summary>How long a charter may take before its founding is given up.</summary>
    public long CharterStallMs { get; set; } = 600_000;

    /// <summary>The least time between two offers of a charter to one signer.</summary>
    public long OfferSpacingMs { get; set; } = 5000;

    /// <summary>The least time between two promotions in one guild.</summary>
    public long PromotionSpacingMs { get; set; } = 60_000;

    /// <summary>The least time between two guild lines of one bot.</summary>
    public long SpeakerGapMs { get; set; } = 120_000;

    /// <summary>The least time between two battleground-win remarks in one guild.</summary>
    public long BgWinSpacingMs { get; set; } = 600_000;

    /// <summary>Whether small talk is rolled each pass.</summary>
    public bool IdleChatter { get; set; } = true;

    /// <summary>The least time between two greetings from one bot.</summary>
    public long EmoteBotGapMs { get; set; } = 5 * 60_000;

    /// <summary>The least time between two greetings to one player (from any bot).</summary>
    public long EmotePlayerGapMs { get; set; } = 10 * 60_000;

    /// <summary>The least time between two greetings from one bot to one player.</summary>
    public long EmotePairGapMs { get; set; } = 30 * 60_000;

    /// <summary>The most greetings in a minute, from all bots.</summary>
    public int EmotesPerMinute { get; set; } = 12;
}

/// <summary>Everything one social pass works with (world thread). Bots are gathered on first use, so an event handler's pass costs nothing extra.</summary>
internal sealed class PlayerbotSocialPass(
    PlayerbotOptions bot, SocialContext social, WorldRuntime world, IServiceProvider services, PlayerbotChat? chat, IReadOnlySet<uint> registered,
    bool registryLoaded, PlayerbotSocialTimings timings, Random random, long nowMs, long elapsedMs, Func<List<PlayerbotSocialBot>> gather)
{
    private List<PlayerbotSocialBot>? _bots;
    private Dictionary<uint, PlayerbotSocialBot>? _byCharacter;
    private HashSet<Guid>? _ids;

    internal PlayerbotOptions Bot => bot;

    internal PlayerbotSocialOptions Options => bot.Social;

    internal SocialContext Social => social;

    internal WorldRuntime World => world;

    internal PlayerbotChat? Chat => chat;

    internal IReadOnlySet<uint> Registered => registered;

    /// <summary>Whether the registry of bots has been read at least once (until then no guild is founded).</summary>
    internal bool RegistryLoaded => registryLoaded;

    internal PlayerbotSocialTimings Timings => timings;

    internal Random Random => random;

    /// <summary>World time, in milliseconds.</summary>
    internal long NowMs => nowMs;

    /// <summary>World time since the previous pass.</summary>
    internal long ElapsedMs => elapsedMs;

    internal IReadOnlyList<PlayerbotSocialBot> Bots => _bots ??= gather();

    /// <summary>The running bots by character id.</summary>
    internal IReadOnlyDictionary<uint, PlayerbotSocialBot> ByCharacter => _byCharacter ??= Bots.ToDictionary(b => b.Player.Guid.Low);

    internal IReadOnlySet<Guid> BotIds => _ids ??= [.. Bots.Select(b => b.Id)];

    internal QuestNpcServices? Quests => services.GetService<QuestNpcFeature>()?.Services;

    internal ChatWordFilter? WordFilter => services.GetService<ChatWordFilterFeature>()?.Filter;

    /// <summary>The screening of a guild name from the live services (a filter the server has not installed adds no check).</summary>
    internal PlayerbotGuildNameRules NameRules()
    {
        GuildManager guilds = social.Guilds;
        PetitionManager petitions = social.Petitions;
        return new PlayerbotGuildNameRules(guilds.Options, petitions.Blacklist, guilds.Antispam,
            name => guilds.GetByName(name) is not null,
            name => petitions.All.Any(petition => string.Equals(petition.Name, name, StringComparison.OrdinalIgnoreCase)),
            services.GetService<NameCatalogFeature>()?.Catalog, WordFilter, chat?.Safety.Filter() ?? PlayerbotChatSafetyFilter.Default);
    }
}

/// <summary>
/// Bot guilds and social life (<c>World:Playerbots:Social</c>, docs/areas/playbots-social.md). A world feature: once per
/// <see cref="PlayerbotSocialTimings.PassIntervalMs"/> of world time, while <c>World:Playerbots:Enabled</c> is on, it looks over the running,
/// non-scripted bots (a scripted bot's controller owns its packet queue; the feature never touches it) and
/// <list type="number">
/// <item>reads, from each bot's capture queue, only the guild invitations, charter signature lists and battleground wins
/// (<see cref="PlayerbotSocialPackets.IntakeOpcodes"/>);</item>
/// <item>answers pending guild invitations, always (<see cref="PlayerbotGuildMembership.AnswerInvites"/>);</item>
/// <item>founds guilds, recruits bots into them and ranks their members (<see cref="PlayerbotGuildFounding"/>, <see cref="PlayerbotGuildMembership"/>);</item>
/// <item>hands guild remarks to the bot chat service (<see cref="PlayerbotGuildChat"/>) and, when asked, waves at players (<see cref="PlayerbotSocialEmotes"/>).</item>
/// </list>
/// Level-ups, boss kills and new members reach it through events (progression, each map's combat, the guild manager); they only queue
/// topics. The bots known to the registry are read from the managed bot store off the world thread (every minute, and at once when a running
/// bot is missing) and published with a volatile write; until the first read no guild is founded. No schema is involved: guilds, ranks and
/// charters persist through the social system, and the state kept here is in memory or read back from it.
/// </summary>
public sealed class PlayerbotSocialFeature(IServiceProvider services, ILogger<PlayerbotSocialFeature> logger) : IWorldFeature
{
    private readonly PlayerbotOptions _options = services.GetService<IOptions<PlayerbotOptions>>()?.Value ?? new();
    private readonly PlayerbotGuildMembership _membership = new(logger);
    private readonly PlayerbotGuildFounding _founding = new(logger);
    private readonly PlayerbotGuildChat _chat = new(logger);
    private readonly PlayerbotSocialEmotes _emotes = new();
    private readonly HashSet<Map> _maps = [];
    private WorldRuntime? _world;
    private long _clockMs;
    private long _sinceLastPassMs;
    private bool _subscribed;
    private GuildManager? _subscribedGuilds;
    private HashSet<uint>? _registry;
    private long _registryAtMs = long.MinValue / 2;
    private int _refreshing;
    private int _faults;

    /// <summary>The intervals in use (tests shorten them).</summary>
    internal PlayerbotSocialTimings Timings { get; } = new();

    /// <summary>The generator of every random choice (tests seed it).</summary>
    internal Random Random { get; set; } = new();

    /// <summary>Passes run so far (tests wait on it).</summary>
    internal int Passes { get; private set; }

    internal PlayerbotGuildMembership Membership => _membership;

    internal PlayerbotGuildFounding Founding => _founding;

    internal PlayerbotGuildChat GuildChat => _chat;

    internal PlayerbotSocialEmotes Emotes => _emotes;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        world.Updated += Update;
        world.MapCreated += OnMapCreated;
        world.MapUnloading += OnMapUnloading;
        foreach (Map map in world.Maps) OnMapCreated(map);
    }

    public Task StopAsync()
    {
        if (_world is { } world)
        {
            world.Updated -= Update;
            world.MapCreated -= OnMapCreated;
            world.MapUnloading -= OnMapUnloading;
        }

        foreach (Map map in _maps) map.Combat.UnitKilled -= OnUnitKilled;
        _maps.Clear();
        if (_subscribedGuilds is { } guilds) guilds.MemberJoined -= OnMemberJoined;
        if (_subscribed && services.GetService<ProgressionFeature>() is { } progression) progression.Progression.LevelChanged -= OnLevelChanged;
        _subscribedGuilds = null;
        _subscribed = false;
        return Task.CompletedTask;
    }

    // --- the pass ----------------------------------------------------------------------------------------------------

    private void Update(uint elapsedMs)
    {
        _clockMs += elapsedMs;
        if (!_options.Enabled) return;
        _sinceLastPassMs += elapsedMs;
        if (_sinceLastPassMs < Timings.PassIntervalMs) return;
        long elapsed = _sinceLastPassMs;
        _sinceLastPassMs = 0;
        try
        {
            RunPass(elapsed);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Social life is never worth the world thread: log (the first few times in full) and carry on at the next pass.
            if (_faults++ < 5) logger.LogWarning(error, "Playerbot social pass failed ({Type}: {Message})", error.GetType().Name, error.Message);
        }
    }

    private SocialContext? TryContext()
    {
        try
        {
            return services.GetService<SocialFeature>()?.Context;
        }
        catch (InvalidOperationException)
        {
            return null; // not attached
        }
    }

    private PlayerbotSocialPass? NewPass(long elapsed, bool gatherBots)
    {
        if (_world is not { } world || TryContext() is not { } social) return null;
        ManagedPlayerbotFeature? bots = services.GetService<ManagedPlayerbotFeature>();
        HashSet<uint>? registry = Volatile.Read(ref _registry);
        return new PlayerbotSocialPass(_options, social, world, services, bots?.Chat, registry ?? [], registry is not null, Timings, Random, _clockMs,
            elapsed, () => gatherBots && bots is not null ? Gather(bots) : []);
    }

    private void RunPass(long elapsed)
    {
        if (services.GetService<ManagedPlayerbotFeature>() is null || NewPass(elapsed, gatherBots: true) is not { } pass) return;
        EnsureSubscribed(pass.Social);
        MaintainRegistry(pass);
        Intake(pass);
        _membership.AnswerInvites(pass);
        _founding.Run(pass, _membership);
        _founding.SetMotds(pass);
        _membership.Recruit(pass, _founding);
        _membership.Promote(pass);
        _chat.Speak(pass);
        _emotes.Run(pass);
        _membership.Prune(pass);
        Passes++;
    }

    private List<PlayerbotSocialBot> Gather(ManagedPlayerbotFeature bots)
    {
        PlayerbotGroupCoordinator? groups = services.GetService<PlayerbotGroupCoordinator>();
        var list = new List<PlayerbotSocialBot>();
        foreach (PlayerbotStatus status in bots.Snapshot())
        {
            if (status.State != ManagedPlayerbotState.Running || bots.IsScripted(status.BotId)) continue;
            if (bots.FindSession(status.BotId) is not { State: SessionState.InWorld, Player: { IsInWorld: true, Map: not null } player } session) continue;
            if (bots.FindPartyAI(status.BotId) is not { } party) continue;
            list.Add(new PlayerbotSocialBot(status.BotId, session, player, party, bots.IsPartyDriven(status.BotId), groups?.Drives(status.BotId) == true));
        }

        return list;
    }

    /// <summary>The server packets this feature reads from each bot, and nothing else (the party AI and the brain drain their own).</summary>
    private void Intake(PlayerbotSocialPass pass)
    {
        foreach (PlayerbotSocialBot bot in pass.Bots)
        {
            foreach (ManagedSessionPacket packet in bot.Session.DrainManagedPackets(PlayerbotSocialPackets.IntakeOpcodes))
            {
                switch (packet.Opcode)
                {
                    case WorldOpcode.SmsgGuildInvite:
                        if (PlayerbotSocialPackets.TryReadGuildInvite(packet.Payload, out string inviter, out string guild))
                            _membership.NoteInvite(bot.Id, inviter, guild);
                        break;
                    case WorldOpcode.SmsgPetitionShowSignatures:
                        if (PlayerbotSocialPackets.TryReadShowSignatures(packet.Payload, out ObjectGuid item, out ObjectGuid owner, out uint petitionId))
                            _founding.OnShowSignatures(pass, _membership, bot, item, owner, petitionId);
                        break;
                    case WorldOpcode.SmsgBattlefieldWin:
                        if (pass.Options.GuildChat.Enabled) _chat.OnBattlegroundWon(pass, bot, bot.Player.Map?.Template?.Name ?? "the battleground");
                        break;
                }
            }
        }
    }

    // --- the registry of bots ----------------------------------------------------------------------------------------

    private void MaintainRegistry(PlayerbotSocialPass pass)
    {
        HashSet<uint>? registry = Volatile.Read(ref _registry);
        bool due = registry is null || pass.NowMs - _registryAtMs >= Timings.RegistryRefreshMs;
        if (!due && pass.NowMs - _registryAtMs >= Timings.RegistryMissGapMs)
            foreach (PlayerbotSocialBot bot in pass.Bots)
                if (!registry!.Contains(bot.Player.Guid.Low)) { due = true; break; }
        if (!due || Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        _registryAtMs = pass.NowMs;
        _ = Task.Run(RefreshRegistryAsync);
    }

    private async Task RefreshRegistryAsync()
    {
        try
        {
            IServiceScopeFactory? scopes = services.GetService<IServiceScopeFactory>();
            if (scopes is null) return;
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<IManagedPlayerbotStore>() is not { } store) return;
            IReadOnlyList<ManagedPlayerbot> bots = await store.LoadAllAsync().ConfigureAwait(false);
            Volatile.Write(ref _registry, [.. bots.Select(bot => (uint)bot.CharacterId)]);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            logger.LogFault("playerbot-social", error, "Playerbot social: the bot registry could not be read ({Type}); the last one stays", error.GetType().Name);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    // --- events ------------------------------------------------------------------------------------------------------

    private void EnsureSubscribed(SocialContext social)
    {
        if (_subscribed) return;
        _subscribed = true;
        _subscribedGuilds = social.Guilds;
        social.Guilds.MemberJoined += OnMemberJoined;
        if (services.GetService<ProgressionFeature>() is { } progression) progression.Progression.LevelChanged += OnLevelChanged;
    }

    private bool ChatTopicsWanted => _options.Enabled && _options.Social.GuildChat.Enabled && _options.Social.GuildChat.MessagesPerHour > 0;

    private void OnMemberJoined(Guild guild, uint characterId, int petitionId)
    {
        if (ChatTopicsWanted && NewPass(0, gatherBots: false) is { } pass) _chat.OnMemberJoined(pass, guild, characterId, petitionId);
    }

    private void OnLevelChanged(Player player)
    {
        if (ChatTopicsWanted && NewPass(0, gatherBots: false) is { } pass) _chat.OnLevelChanged(pass, player);
    }

    private void OnMapCreated(Map map)
    {
        if (_maps.Add(map)) map.Combat.UnitKilled += OnUnitKilled;
    }

    private void OnMapUnloading(Map map)
    {
        if (_maps.Remove(map)) map.Combat.UnitKilled -= OnUnitKilled;
    }

    private void OnUnitKilled(Game.Entities.Unit? killer, Game.Entities.Unit victim)
    {
        if (!ChatTopicsWanted || victim.Map is not { } map || NewPass(0, gatherBots: false) is not { } pass) return;
        _chat.OnUnitKilled(pass, map, killer, victim);
    }
}
