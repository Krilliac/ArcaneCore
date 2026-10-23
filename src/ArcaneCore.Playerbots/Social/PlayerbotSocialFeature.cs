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
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>One running, non-scripted managed bot as the social features see it for one pass (world thread).</summary>
internal sealed record PlayerbotSocialBot(Guid Id, WorldSession Session, Player Player, PlayerbotPartyAI Party, bool PartyDriven, bool GroupDriven, PlayerbotBrain? Brain = null);

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
    bool registryLoaded, PlayerbotSocialTimings timings, Random random, long nowMs, long elapsedMs, Func<List<PlayerbotSocialBot>> gather,
    Dictionary<uint, PlayerbotSocialBot>? byCharacterScratch = null, HashSet<Guid>? botIdsScratch = null)
{
    private List<PlayerbotSocialBot>? _bots;
    private Dictionary<uint, PlayerbotSocialBot>? _byCharacter;
    private HashSet<Guid>? _ids;
    private QuestNpcServices? _quests;
    private bool _questsResolved;
    private ChatWordFilter? _wordFilter;
    private bool _wordFilterResolved;

    internal PlayerbotOptions Bot => bot;

    internal PlayerbotSocialOptions Options => bot.Social;

    internal SocialContext Social => social;

    internal WorldRuntime World => world;

    internal PlayerbotChat? Chat => chat;

    /// <summary>The group coordinator, when the host runs one (guild calls for a forming group, <see cref="PlayerbotGuildChat"/>).</summary>
    internal PlayerbotGroupCoordinator? Coordinator { get; init; }

    /// <summary>The bots' social memory seam (friend and ignore lists, guild invitations by affinity); null without one.</summary>
    internal PlayerbotSocialRecorder? Recorder { get; init; }

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
    internal IReadOnlyDictionary<uint, PlayerbotSocialBot> ByCharacter => _byCharacter ??= BuildByCharacter();

    internal IReadOnlySet<Guid> BotIds => _ids ??= BuildBotIds();

    private Dictionary<uint, PlayerbotSocialBot> BuildByCharacter()
    {
        IReadOnlyList<PlayerbotSocialBot> bots = Bots;
        Dictionary<uint, PlayerbotSocialBot> map = byCharacterScratch ?? new Dictionary<uint, PlayerbotSocialBot>(bots.Count);
        map.Clear();
        map.EnsureCapacity(bots.Count);
        for (int i = 0; i < bots.Count; i++)
        {
            PlayerbotSocialBot entry = bots[i];
            map[entry.Player.Guid.Low] = entry;
        }
        return map;
    }

    private HashSet<Guid> BuildBotIds()
    {
        IReadOnlyList<PlayerbotSocialBot> bots = Bots;
        HashSet<Guid> set = botIdsScratch ?? new HashSet<Guid>(bots.Count);
        set.Clear();
        set.EnsureCapacity(bots.Count);
        for (int i = 0; i < bots.Count; i++)
        {
            set.Add(bots[i].Id);
        }
        return set;
    }

    internal QuestNpcServices? Quests
    {
        get
        {
            if (!_questsResolved)
            {
                _quests = services.GetService<QuestNpcFeature>()?.Services;
                _questsResolved = true;
            }
            return _quests;
        }
    }

    internal ChatWordFilter? WordFilter
    {
        get
        {
            if (!_wordFilterResolved)
            {
                _wordFilter = services.GetService<ChatWordFilterFeature>()?.Filter;
                _wordFilterResolved = true;
            }
            return _wordFilter;
        }
    }

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
public sealed class PlayerbotSocialFeature(IServiceProvider services, ILogger<PlayerbotSocialFeature> logger) : IDetachableWorldFeature
{
    private readonly PlayerbotOptions _options = services.GetService<IOptions<PlayerbotOptions>>()?.Value ?? new();
    private readonly PlayerbotGuildMembership _membership = new(logger);
    private readonly PlayerbotSocialLists _lists = new();
    private readonly PlayerbotGuildFounding _founding = new(logger);
    private readonly PlayerbotGuildChat _chat = new(logger);
    private readonly PlayerbotSocialEmotes _emotes = new();
    private readonly PlayerbotHelpLane _help = new();
    private readonly PlayerbotChannelMembership _channels = new();
    private readonly PlayerbotConversations _conversations = new();
    private readonly PlayerbotWorldChatter _worldChatter = new();
    private readonly ArcaneCore.World.Playerbots.Chat.PlayerbotNeedsVoice _voice = new(services, logger);
    private static readonly HashSet<uint> EmptyRegistry = [];
    private static readonly List<PlayerbotSocialBot> EmptyBots = [];
    private static readonly Func<List<PlayerbotSocialBot>> EmptyGather = static () => EmptyBots;
    private readonly HashSet<Map> _maps = [];
    private WorldRuntime? _world;
    private SocialFeature? _socialFeature;
    private ManagedPlayerbotFeature? _managedBots;
    private PlayerbotGroupCoordinator? _groupCoordinator;
    private Func<List<PlayerbotSocialBot>>? _gatherBots;
    private long _clockMs;
    private long _sinceLastPassMs;
    private bool _subscribed;
    private GuildManager? _subscribedGuilds;
    private HashSet<uint>? _registry;
    private long _registryAtMs = long.MinValue / 2;
    private long _registryCheckedAtMs = long.MinValue / 2;
    private HashSet<uint>? _verifiedRegistry;
    private int _verifiedBotsCount = -1;
    private uint _verifiedBotsChecksum;
    private int _refreshing;
    private int _faults;

    /// <summary>The intervals in use (tests shorten them).</summary>
    internal PlayerbotSocialTimings Timings { get; } = new();

    /// <summary>The generator of every random choice (tests seed it).</summary>
    internal Random Random { get; set; } = new();

    /// <summary>Passes run so far (tests wait on it).</summary>
    internal int Passes { get; private set; }

    internal PlayerbotGuildMembership Membership => _membership;

    /// <summary>The friend and ignore list lane (tests, status).</summary>
    internal PlayerbotSocialLists Lists => _lists;

    internal PlayerbotGuildFounding Founding => _founding;

    internal PlayerbotGuildChat GuildChat => _chat;

    internal PlayerbotSocialEmotes Emotes => _emotes;

    /// <summary>Answering calls for help and the trouble scan (<see cref="PlayerbotHelpLane"/>).</summary>
    internal PlayerbotHelpLane Help => _help;

    /// <summary>The bots' world channel membership (General, Trade, LocalDefense, LookingForGroup).</summary>
    internal PlayerbotChannelMembership Channels => _channels;

    /// <summary>The bot-to-bot conversation director.</summary>
    internal PlayerbotConversations Conversations => _conversations;

    /// <summary>The bots' answers to real players' LFM/WTB lines in world channels.</summary>
    internal PlayerbotWorldChatter WorldChatter => _worldChatter;

    /// <summary>The bot voice: wants and needs said in world, party and battleground chat (<see cref="ArcaneCore.World.Playerbots.Chat.PlayerbotNeedsVoice"/>).</summary>
    internal ArcaneCore.World.Playerbots.Chat.PlayerbotNeedsVoice Voice => _voice;

    // --- lifecycle (docs/design/hot-reload-tiers.md, section 4.2) -------------------------------------------------------

    /// <summary>
    /// Stateless: a detach ends the conversations and channel chatter in flight and lets the voice go; guild membership, founding and
    /// chat start over from the world as it stands at the next attach (the guilds themselves are core state).
    /// </summary>
    public DetachKind DetachKind => DetachKind.Stateless;

    private FeatureSubscriptions? _subscriptions;
    private Task _refresh = Task.CompletedTask;

    // Set by a quiesce, cleared by the next attach: no registry read starts in between (one started after the quiesce waited would
    // run on after the detach).
    private volatile bool _quiesced;

    public void Attach(WorldRuntime world) => AttachFrom(world, null, new FeatureSubscriptions(nameof(PlayerbotSocialFeature)));

    public void AttachFrom(WorldRuntime world, FeatureHandover? handover, FeatureSubscriptions subscriptions)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(subscriptions);
        _world = world;
        _subscriptions = subscriptions;
        _quiesced = false;
        subscriptions.OnUpdated(world, Update);
        subscriptions.OnMapCreated(world, OnMapCreated);
        subscriptions.OnMapUnloading(world, OnMapUnloading);
        // The per-map kill handlers come and go with the maps; the bag undoes whichever are still on at detach.
        subscriptions.Add(new Undo(UnsubscribeMaps));
        foreach (Map map in world.Maps) OnMapCreated(map);
    }

    /// <summary>Start no further bot registry read, then wait for one still running (the only background work).</summary>
    public async Task QuiesceAsync(CancellationToken cancellationToken)
    {
        _quiesced = true;
        if (_world is { IsStopped: false, IsWorldThread: false } world)
        {
            // One hop through the world thread: an update that read the flag before it was set has finished, and its read is in _refresh.
            await world.InvokeAsync(() => true, cancellationToken).ConfigureAwait(false);
        }

        await Volatile.Read(ref _refresh).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Detach(FeatureHandover handover) => Release();

    public Task StopAsync()
    {
        try
        {
            _subscriptions?.Dispose(); // the feature is done: its handlers come off the world (the host never reuses the bag)
        }
        finally
        {
            Release();
        }

        return Task.CompletedTask;
    }

    /// <summary>End what is in flight and forget the world (the bag removes the subscriptions).</summary>
    private void Release()
    {
        UnsubscribeMaps();
        _conversations.Stop();
        _worldChatter.Stop();
        _voice.Unhook();
        _subscribedGuilds = null;
        _subscribed = false;
        _subscriptions = null;
        _socialFeature = null;
        _managedBots = null;
        _groupCoordinator = null;
        _verifiedRegistry = null;
        _verifiedBotsCount = -1;
        _verifiedBotsChecksum = 0;
        _world = null;
    }

    private void UnsubscribeMaps()
    {
        foreach (Map map in _maps) map.Combat.UnitKilled -= OnUnitKilled;
        _maps.Clear();
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        public void Dispose() => undo();
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
            FaultLedger.Shared.Record("playerbot.social", error);
            AnomalyLedger.Shared.Report("bot-social", "feature-fault", "social", $"{error.GetType().Name}: {error.Message}", origin: _world?.Identity);
            if (_faults++ < 5) logger.LogWarning(error, "Playerbot social pass failed ({Type}: {Message})", error.GetType().Name, error.Message);
        }
    }

    private SocialContext? TryContext()
    {
        try
        {
            return (_socialFeature ??= services.GetService<SocialFeature>())?.Context;
        }
        catch (InvalidOperationException error)
        {
            FaultLedger.Shared.Record("playerbot.social", error);
            AnomalyLedger.Shared.Report("bot-social", "feature-fault", "social", error.Message, origin: _world?.Identity);
            return null; // not attached
        }
    }

    private ManagedPlayerbotFeature? ResolveManagedBots() => _managedBots ??= services.GetService<ManagedPlayerbotFeature>();

    private List<PlayerbotSocialBot> GatherCurrentBots() => ResolveManagedBots() is { } bots ? Gather(bots) : EmptyBots;

    private PlayerbotSocialPass? NewPass(long elapsed, bool gatherBots)
    {
        if (_world is not { } world || TryContext() is not { } social) return null;
        ManagedPlayerbotFeature? bots = ResolveManagedBots();
        HashSet<uint>? registry = Volatile.Read(ref _registry);
        Func<List<PlayerbotSocialBot>> gather = gatherBots && bots is not null ? (_gatherBots ??= GatherCurrentBots) : EmptyGather;
        return new PlayerbotSocialPass(_options, social, world, services, bots?.Chat, registry ?? EmptyRegistry, registry is not null, Timings, Random, _clockMs,
            elapsed, gather,
            gatherBots ? _byCharacterScratch : null, gatherBots ? _botIdsScratch : null)
        {
            Coordinator = _groupCoordinator ??= services.GetService<PlayerbotGroupCoordinator>(),
            Recorder = bots?.Social,
        };
    }

    private void RunPass(long elapsed)
    {
        if (ResolveManagedBots() is null || NewPass(elapsed, gatherBots: true) is not { } pass) return;
        EnsureSubscribed(pass.Social);
        MaintainRegistry(pass);
        Intake(pass);
        _membership.AnswerInvites(pass);
        _founding.Run(pass, _membership);
        _founding.SetMotds(pass);
        _membership.Recruit(pass, _founding);
        _membership.InviteByAffinity(pass);
        _membership.Promote(pass);
        _lists.Run(pass);
        _help.Run(pass);
        _chat.Speak(pass);
        _emotes.Run(pass);
        RunChannels(pass);
        _conversations.Run(pass);
        _worldChatter.Run(pass);
        _voice.Run(pass);
        _membership.Prune(pass);
        Passes++;
    }

    private readonly List<PlayerbotChannelBot> _channelBotsScratch = [];
    private readonly List<PlayerbotSocialBot> _socialBotsScratch = [];
    private readonly Dictionary<uint, PlayerbotSocialBot> _byCharacterScratch = [];
    private readonly HashSet<Guid> _botIdsScratch = [];

    /// <summary>
    /// Keep each bot in its zone's world channels (<see cref="PlayerbotChannelMembership"/>) while bot speech is on; with it off the bots
    /// leave them (the wanted set becomes none) and are then left alone.
    /// </summary>
    private void RunChannels(PlayerbotSocialPass pass)
    {
        if (pass.Chat is not { } chat || pass.Bots.Count == 0) return;
        _channelBotsScratch.Clear();
        _channelBotsScratch.EnsureCapacity(pass.Bots.Count);
        foreach (PlayerbotSocialBot bot in pass.Bots) _channelBotsScratch.Add(new PlayerbotChannelBot(bot.Id, bot.Session, bot.Player));
        _channels.Run(_channelBotsScratch, pass.Social.Channels, ArcaneCore.Game.Maps.Templates.WorldMaps.Of(pass.World).Areas, chat.SpeechOptions, pass.NowMs);
    }

    private List<PlayerbotSocialBot> Gather(ManagedPlayerbotFeature bots)
    {
        PlayerbotGroupCoordinator? groups = _groupCoordinator ??= services.GetService<PlayerbotGroupCoordinator>();
        bots.GatherSocialBots(groups, _socialBotsScratch);
        return _socialBotsScratch;
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
        if (!due && pass.NowMs - _registryAtMs >= Timings.RegistryMissGapMs && pass.NowMs - _registryCheckedAtMs >= Timings.RegistryMissGapMs)
        {
            _registryCheckedAtMs = pass.NowMs;
            IReadOnlyList<PlayerbotSocialBot> bots = pass.Bots;
            uint checksum = 0;
            for (int i = 0; i < bots.Count; i++)
                checksum = (checksum * 16777619u) ^ bots[i].Player.Guid.Low;
            if (!ReferenceEquals(registry, _verifiedRegistry) || bots.Count != _verifiedBotsCount || checksum != _verifiedBotsChecksum)
            {
                for (int i = 0; i < bots.Count; i++)
                {
                    if (!registry!.Contains(bots[i].Player.Guid.Low))
                    {
                        due = true;
                        break;
                    }
                }
                if (!due)
                {
                    _verifiedRegistry = registry;
                    _verifiedBotsCount = bots.Count;
                    _verifiedBotsChecksum = checksum;
                }
            }
        }
        if (!due || _quiesced || Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        _registryAtMs = pass.NowMs;
        _registryCheckedAtMs = pass.NowMs;
        Volatile.Write(ref _refresh, Task.Run(RefreshRegistryAsync));
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
            FaultLedger.Shared.Record("playerbot.social", error);
            AnomalyLedger.Shared.Report("bot-social", "feature-fault", "social", $"{error.GetType().Name}: {error.Message}", origin: _world?.Identity);
            logger.LogFault("playerbot.social", error, "Playerbot social: the bot registry could not be read ({Type}); the last one stays", error.GetType().Name);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    // --- events ------------------------------------------------------------------------------------------------------

    private void EnsureSubscribed(SocialContext social)
    {
        if (_subscribed || _subscriptions is not { IsDisposed: false } bag) return;
        _subscribed = true;
        _subscribedGuilds = social.Guilds;
        GuildManager guilds = social.Guilds;
        bag.Subscribe<Action<Guild, uint, int>>(OnMemberJoined, h => guilds.MemberJoined += h, h => guilds.MemberJoined -= h);
        if (services.GetService<ProgressionFeature>() is { } progression)
            bag.Subscribe<Action<Player>>(OnLevelChanged, h => progression.Progression.LevelChanged += h, h => progression.Progression.LevelChanged -= h);
    }

    private bool ChatTopicsWanted => _options.Enabled && _options.Social.GuildChat.Enabled && _options.Social.GuildChat.MessagesPerHour > 0;

    private void OnMemberJoined(Guild guild, uint characterId, int petitionId)
    {
        if (ChatTopicsWanted && NewPass(0, gatherBots: false) is { } pass) _chat.OnMemberJoined(pass, guild, characterId, petitionId);
    }

    private void OnLevelChanged(Player player)
    {
        if (_options.Enabled) _voice.OnLevelChanged(player);
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
        if (!_options.Enabled || victim.Map is not { } map) return;
        _conversations.OnUnitKilled(map, killer, victim);
        if (!ChatTopicsWanted) return;
        if (victim is not Game.Creatures.Creature { Template.Rank: >= 1 }) return;
        if (NewPass(0, gatherBots: false) is { } pass) _chat.OnUnitKilled(pass, map, killer, victim);
    }
}
