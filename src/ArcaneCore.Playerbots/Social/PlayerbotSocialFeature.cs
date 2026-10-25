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

    internal IServiceProvider Services => services;

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

    /// <summary>
    /// The pass over the whole population, set on a slice of an incremental pass (<see cref="Bots"/> is then only the slice a per-bot lane
    /// works on now; the lookups by character and id still cover every bot of the pass). Null on a pass that is not a slice.
    /// </summary>
    internal PlayerbotSocialPass? Whole { get; init; }

    internal IReadOnlyList<PlayerbotSocialBot> Bots => _bots ??= gather();

    /// <summary>The running bots by character id.</summary>
    internal IReadOnlyDictionary<uint, PlayerbotSocialBot> ByCharacter => Whole?.ByCharacter ?? (_byCharacter ??= BuildByCharacter());

    internal IReadOnlySet<Guid> BotIds => Whole?.BotIds ?? (_ids ??= BuildBotIds());

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
    private readonly PlayerbotGratitudeLane _gratitude = new();
    private readonly PlayerbotMoodLane _mood = new();
    private readonly PlayerbotInitiativeLane _initiative = new();
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

    /// <summary>The gratitude lane (tests and status).</summary>
    internal PlayerbotGratitudeLane Gratitude => _gratitude;

    /// <summary>The mood lane (tests).</summary>
    internal PlayerbotMoodLane MoodLane => _mood;

    /// <summary>The initiative lane (tests and status).</summary>
    internal PlayerbotInitiativeLane Initiative => _initiative;

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
        // The kill talk asks the group coordinator's frozen world-boss snapshot (any thread) and names the zone a catalogued boss fell in.
        _chat.IsWorldBossEntry = _conversations.IsWorldBossEntry = IsWorldBossEntry;
        _chat.ZoneOf = _conversations.ZoneOf = ZoneNameOf;
    }

    /// <summary>Whether <paramref name="entry"/> is in the group coordinator's world-boss catalogue (read from the map threads: a volatile snapshot).</summary>
    private bool IsWorldBossEntry(uint entry) => (_groupCoordinator ??= services.GetService<PlayerbotGroupCoordinator>())?.IsWorldBossEntry(entry) == true;

    /// <summary>The zone name of the place a creature stands in, from the area table; null where the terrain or the table does not know it.</summary>
    /// <summary>The zone-and-area lookup of a place (tests replace it to make the lookup fail; null: the map's terrain).</summary>
    internal Func<Map, ArcaneCore.Game.Creatures.Creature, (uint ZoneId, uint AreaId)>? ZoneLookup { get; set; }

    internal string? ZoneNameOf(Map map, ArcaneCore.Game.Creatures.Creature creature)
    {
        if (_world is not { } world) return null;
        try
        {
            (uint zoneId, _) = ZoneLookup?.Invoke(map, creature) ?? map.GetZoneAndAreaId(creature.X, creature.Y, creature.Z);
            return zoneId == 0 ? null : ArcaneCore.Game.Maps.Templates.WorldMaps.Of(world).Areas.GetById(zoneId)?.Name;
        }
        catch (Exception error) when (error is InvalidOperationException or NullReferenceException or KeyNotFoundException)
        {
            FaultLedger.Shared.Record("playerbot.social.bosskill", error);
            return null; // a minimal host without terrain or area data: the map's name is named instead
        }
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

    // A tip in flight leaves its trade window open for the helper: close it, on the world thread (the cancel is a session action).
    private void CancelTips()
    {
        if (_world is not { IsStopped: false } world) return;
        try
        {
            if (world.IsWorldThread) _gratitude.CancelAllTips();
            else _ = world.InvokeAsync(() =>
            {
                _gratitude.CancelAllTips();
                return true;
            });
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.social", error);
        }
    }

    /// <summary>End what is in flight and forget the world (the bag removes the subscriptions).</summary>
    private void Release()
    {
        EndPass();
        CancelTips();
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

    internal void Update(uint elapsedMs)
    {
        _clockMs += elapsedMs;
        if (!_options.Enabled)
        {
            EndPass();
            return;
        }

        _sinceLastPassMs += elapsedMs;
        // A pass in flight is finished before the next one starts (and the interval then counts from the start of the finished one, so a
        // pass that outlasts its interval is followed by the next at once, never by one pass per missed interval).
        if (!_running && _sinceLastPassMs < Timings.PassIntervalMs) return;
        try
        {
            if (!_running)
            {
                long elapsed = _sinceLastPassMs;
                _sinceLastPassMs = 0;
                if (!BeginPass(elapsed)) return;
            }

            Advance(_options.Social.PassBudgetMs);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            EndPass();
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

    /// <summary>One whole pass now, with no time limit (tests; the world thread's own passes go through <see cref="Update"/> under the budget).</summary>
    internal void RunPass(long elapsed)
    {
        if (!_running && !BeginPass(elapsed)) return;
        Advance(0);
    }

    // --- the incremental pass ----------------------------------------------------------------------------------------
    //
    // A pass is a list of stages run in order: the packet intake, the injected lanes, then the built-in lanes (LaneNames). Per-bot stages
    // (the intake, the injected lanes and the lanes IsSlicedLane names) walk the pass's snapshot of bots in slices and stop between slices
    // once the tick's budget (World:Playerbots:Social:PassBudgetMs) is spent; the next tick carries on at the next bot. Every other lane
    // is population-wide (it keeps a cursor of its own, a per-pass quota or a set of "who is running", or it works out the whole
    // population's guilds and charters) and runs whole, as one stage, over the snapshot. A stage never starts before the one before it has
    // finished, so every bot still meets the lanes in their usual order, and a new pass starts only when the last one is over.

    /// <summary>Most bots one slice may take (the time a slice takes is only known after it has run).</summary>
    private const int MaxSliceBots = 128;

    /// <summary>A pass in flight for longer than this many intervals is reported once as starved.</summary>
    private const long StarvedPassIntervals = 20;

    /// <summary>The lanes that act on one bot at a time, independently of the rest, and so may run on a slice of the population.</summary>
    private static bool IsSlicedLane(int lane) => lane is 0 or 12; // answer-invites, emotes

    private sealed class PassRun
    {
        public PassRun()
        {
            WholeGather = () => Bots;
            ChunkGather = () => Chunk;
        }

        /// <summary>The bots of the pass, taken when it started and thinned of those who have left the world since.</summary>
        public readonly List<PlayerbotSocialBot> Bots = [];

        /// <summary>The slice a per-bot stage is running on.</summary>
        public readonly List<PlayerbotSocialBot> Chunk = [];

        public readonly Func<List<PlayerbotSocialBot>> WholeGather;
        public readonly Func<List<PlayerbotSocialBot>> ChunkGather;
        public long ElapsedMs;
        public long StartedMs;
        public int InjectedCount;

        /// <summary>0: the intake; then the injected lanes; then the built-in lanes.</summary>
        public int Stage;

        /// <summary>The next bot of <see cref="Bots"/> a per-bot stage takes.</summary>
        public int Cursor;

        public bool StarvedReported;

        public int StageCount => 1 + InjectedCount + LaneNames.Length;

        public void Reset()
        {
            Bots.Clear();
            Chunk.Clear();
            ElapsedMs = 0;
            StartedMs = 0;
            InjectedCount = 0;
            Stage = 0;
            Cursor = 0;
            StarvedReported = false;
        }
    }

    private readonly PassRun _state = new();
    private bool _running;

    /// <summary>The clock, in milliseconds, that the pass's time budget is measured with (tests substitute a fake one).</summary>
    internal Func<double> PassClockMs { get; set; } = static () => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Whether a pass has started and not yet finished (its remaining stages run in the next ticks).</summary>
    internal bool PassInProgress => _running;

    /// <summary>Take the snapshot of bots and do the once-per-pass work that must precede every lane. False: nothing to run now.</summary>
    private bool BeginPass(long elapsed)
    {
        if (ResolveManagedBots() is null || NewPass(elapsed, gatherBots: true) is not { } pass) return false;
        EnsureSubscribed(pass.Social);
        MaintainRegistry(pass);
        PassRun run = _state;
        run.Reset();
        run.Bots.AddRange(pass.Bots);
        run.ElapsedMs = elapsed;
        run.StartedMs = _clockMs;
        run.InjectedCount = InjectedLanes.Count;
        _running = true;
        return true;
    }

    private void EndPass()
    {
        _running = false;
        _state.Reset();
    }

    /// <summary>A pass over <paramref name="gather"/>'s bots at the current world time; <paramref name="whole"/> is set for a slice of a pass.</summary>
    private PlayerbotSocialPass? NewPassOver(long elapsed, Func<List<PlayerbotSocialBot>> gather, PlayerbotSocialPass? whole)
    {
        if (_world is not { } world || TryContext() is not { } social) return null;
        ManagedPlayerbotFeature? bots = ResolveManagedBots();
        HashSet<uint>? registry = Volatile.Read(ref _registry);
        return new PlayerbotSocialPass(_options, social, world, services, bots?.Chat, registry ?? EmptyRegistry, registry is not null, Timings, Random, _clockMs,
            elapsed, gather,
            whole is null ? _byCharacterScratch : null, whole is null ? _botIdsScratch : null)
        {
            Coordinator = _groupCoordinator ??= services.GetService<PlayerbotGroupCoordinator>(),
            Recorder = bots?.Social,
            Whole = whole,
        };
    }

    private static bool IsLive(PlayerbotSocialBot bot)
        => bot.Session.State == SessionState.InWorld && bot.Player is { IsInWorld: true, Map: not null };

    /// <summary>Drop the bots that left the world since the snapshot was taken, keeping the cursor on the bot it pointed at.</summary>
    private static void DropGone(PassRun run)
    {
        List<PlayerbotSocialBot> bots = run.Bots;
        int kept = 0;
        int cursor = run.Cursor;
        for (int i = 0; i < bots.Count; i++)
        {
            if (IsLive(bots[i])) bots[kept++] = bots[i];
            else if (i < run.Cursor) cursor--;
        }

        if (kept < bots.Count) bots.RemoveRange(kept, bots.Count - kept);
        run.Cursor = cursor;
    }

    /// <summary>
    /// Run the pass's stages until it is over or <paramref name="budgetMs"/> (0: no limit) is spent in this call. At least one stage or
    /// slice of one bot runs per call, so a pass always moves on. World thread.
    /// </summary>
    private void Advance(double budgetMs)
    {
        PassRun run = _state;
        DropGone(run);
        if (NewPassOver(run.ElapsedMs, run.WholeGather, null) is not { } whole)
        {
            EndPass();
            return;
        }

        Func<double> clock = PassClockMs;
        double start = clock();
        bool limited = budgetMs > 0;
        int stages = run.StageCount;
        while (run.Stage < stages)
        {
            if (!RunStage(run, whole, clock, start, budgetMs))
            {
                ReportStarved(run);
                return;
            }

            run.Stage++;
            run.Cursor = 0;
            // The pass yields, between stages and between slices alike, only once the budget is exceeded: a tick spends the budget plus at most one slice.
            if (limited && run.Stage < stages && clock() - start > budgetMs)
            {
                ReportStarved(run);
                return;
            }
        }

        Passes++;
        EndPass();
    }

    /// <summary>Run the current stage; false when it is unfinished because the budget is spent (the next call resumes it).</summary>
    private bool RunStage(PassRun run, PlayerbotSocialPass whole, Func<double> clock, double start, double budgetMs)
    {
        int lane = run.Stage - 1 - run.InjectedCount;
        if (lane >= 0 && !IsSlicedLane(lane))
        {
            RunLane(lane, whole);
            return true;
        }

        List<PlayerbotSocialBot> bots = run.Bots;
        if (bots.Count == 0 && run.Stage > 0) RunSlice(run, whole, 0, 0); // an injected lane still runs, once, over no bots
        bool limited = budgetMs > 0;
        double perBotMs = -1; // unknown until the first slice has run: that one takes a single bot
        while (run.Cursor < bots.Count)
        {
            int count = bots.Count - run.Cursor;
            if (limited)
            {
                if (perBotMs < 0) count = 1;
                else if (perBotMs > 1e-6) count = (int)Math.Clamp((budgetMs - (clock() - start)) / perBotMs, 1, Math.Min(count, MaxSliceBots));
                else count = Math.Min(count, MaxSliceBots);
            }

            double before = clock();
            RunSlice(run, whole, run.Cursor, count);
            run.Cursor += count;
            double now = clock();
            perBotMs = (now - before) / count;
            if (limited && run.Cursor < bots.Count && now - start > budgetMs) return false;
        }

        return true;
    }

    private void RunSlice(PassRun run, PlayerbotSocialPass whole, int from, int count)
    {
        if (run.Stage == 0)
        {
            Intake(whole, run.Bots, from, from + count);
            return;
        }

        List<PlayerbotSocialBot> chunk = run.Chunk;
        chunk.Clear();
        for (int i = from; i < from + count; i++) chunk.Add(run.Bots[i]);
        if (NewPassOver(run.ElapsedMs, run.ChunkGather, whole) is not { } slice) return;
        if (run.Stage <= run.InjectedCount)
        {
            int index = run.Stage - 1;
            if (index < InjectedLanes.Count) RunInjected(InjectedLanes[index], slice);
        }
        else
        {
            RunLane(run.Stage - 1 - run.InjectedCount, slice);
        }
    }

    /// <summary>Report, once per pass, a pass that has been in flight for far longer than its interval (the think rotation's starvation report, for the social pass).</summary>
    private void ReportStarved(PassRun run)
    {
        long ageMs = _clockMs - run.StartedMs;
        if (run.StarvedReported || ageMs <= Math.Max(1, Timings.PassIntervalMs) * StarvedPassIntervals) return;
        run.StarvedReported = true;
        AnomalyLedger.Shared.Report("bot-social", "pass-starved", "social",
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"social pass unfinished after {ageMs} ms (interval {Timings.PassIntervalMs} ms, budget {_options.Social.PassBudgetMs:F1} ms per tick; stage {run.Stage + 1} of {run.StageCount}, {run.Bots.Count} bots)"),
            origin: _world?.Identity);
    }

    private static readonly string[] LaneNames =
    [
        "answer-invites", "founding", "motds", "recruit", "invite-by-affinity", "promote", "lists", "help", "mood", "gratitude", "initiative",
        "chat", "emotes", "channels", "conversations", "world-chatter", "voice", "prune",
    ];

    /// <summary>Extra lanes run before the built-in ones, each isolated like them (a test seam: a lane that throws must not stop the others).</summary>
    internal List<(string Name, Action<PlayerbotSocialPass> Run)> InjectedLanes { get; } = [];

    /// <summary>
    /// One lane of the pass, isolated: a lane that throws is recorded under <c>playerbot.social</c> with its name and the lanes after it still run.
    /// </summary>
    private void RunLane(int lane, PlayerbotSocialPass pass)
    {
        try
        {
            switch (lane)
            {
                case 0: _membership.AnswerInvites(pass); break;
                case 1: _founding.Run(pass, _membership); break;
                case 2: _founding.SetMotds(pass); break;
                case 3: _membership.Recruit(pass, _founding); break;
                case 4: _membership.InviteByAffinity(pass); break;
                case 5: _membership.Promote(pass); break;
                case 6: _lists.Run(pass); break;
                case 7: _help.Run(pass); break;
                case 8: _mood.Run(pass); break;
                case 9: _gratitude.Run(pass, _help); break;
                case 10: _initiative.Run(pass); break;
                case 11: _chat.Speak(pass); break;
                case 12: _emotes.Run(pass); break;
                case 13: RunChannels(pass); break;
                case 14: _conversations.Run(pass); break;
                case 15: _worldChatter.Run(pass); break;
                case 16: _voice.Run(pass); break;
                default: _membership.Prune(pass); break;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            LaneFaulted(LaneNames[lane], error);
        }
    }

    private void RunInjected((string Name, Action<PlayerbotSocialPass> Run) lane, PlayerbotSocialPass pass)
    {
        try
        {
            lane.Run(pass);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            LaneFaulted(lane.Name, error);
        }
    }

    /// <summary>Every lane fault so far, reported or not (a lane that fails every pass is reported once a minute, see <see cref="LaneFaulted"/>).</summary>
    internal long LaneFaultCount { get; private set; }

    private readonly Dictionary<string, long> _laneReportedAt = [];

    private const long LaneReportGapMs = 60_000;

    private void LaneFaulted(string lane, Exception error)
    {
        LaneFaultCount++;
        // A lane that throws on every pass would flood the ledgers and the log: one report per lane per minute; the count above keeps the rest.
        long now = Environment.TickCount64;
        if (_laneReportedAt.TryGetValue(lane, out long last) && now - last < LaneReportGapMs) return;
        _laneReportedAt[lane] = now;
        error.Data["playerbot.social.lane"] = lane;
        FaultLedger.Shared.Record("playerbot.social", error);
        AnomalyLedger.Shared.Report("bot-social", "feature-fault", "social", $"lane {lane}: {error.GetType().Name}: {error.Message}", origin: _world?.Identity);
        if (_faults++ < 5) logger.LogWarning(error, "Playerbot social lane {Lane} failed ({Type}: {Message})", lane, error.GetType().Name, error.Message);
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
    private void Intake(PlayerbotSocialPass pass, List<PlayerbotSocialBot> bots, int from, int to)
    {
        for (int index = from; index < to; index++)
        {
            PlayerbotSocialBot bot = bots[index];
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
