using System.Globalization;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Playerbots;

namespace ArcaneCore.World.Reload;

/// <summary>
/// The option objects one configuration view carries: the runtime tuning (<see cref="WorldRuntimeOptions"/>,
/// shared by reference with every reader) and, when known, the listener options (<see cref="WorldOptions"/>)
/// the social rules (<see cref="SocialOptions"/>), the playerbot options (<see cref="PlayerbotOptions"/>) and the movement rules
/// (<see cref="LocomotionOptions"/>, whose player speed rates are live). A side that is not known reads as null.
/// </summary>
public readonly record struct WorldConfigView(WorldRuntimeOptions Runtime, WorldOptions? Listener, SocialOptions? Social = null, PlayerbotOptions? Playerbots = null, LocomotionOptions? Locomotion = null);

/// <summary>
/// One option under the <c>World</c> section and what <c>.reload config</c> does with it. A live
/// key is copied into the running options on the world thread (vmangos <c>setConfig…</c> in
/// <c>World::LoadConfigSettings(reload)</c>, World.cpp:445-1100); a restart-only key keeps its
/// value and logs "option can't be changed at reload" when the file now says something else
/// (vmangos <c>World::configNoReload</c>, World.cpp:3044-3055).
/// </summary>
public sealed class WorldConfigKey
{
    internal WorldConfigKey(
        string path,
        Func<WorldConfigView, object?> read,
        Action<WorldConfigView, object?>? apply,
        Func<object?, string?>? check,
        bool negativeUsesDefault = false)
    {
        NegativeUsesDefault = negativeUsesDefault;
        Path = path;
        Read = read;
        Apply = apply;
        Check = check;
    }

    /// <summary>The configuration path, e.g. <c>World:Maps:GridUnload</c>.</summary>
    public string Path { get; }

    /// <summary>True when a reload applies a changed value; false when the key needs a restart.</summary>
    public bool Live => Apply is not null;

    internal Func<WorldConfigView, object?> Read { get; }

    internal Action<WorldConfigView, object?>? Apply { get; }

    internal Func<object?, string?>? Check { get; }

    /// <summary>True when a negative value is replaced by the option default (retail) instead of rejecting the reload.</summary>
    internal bool NegativeUsesDefault { get; }

    /// <summary>The value as shown in messages.</summary>
    internal static string Show(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>
/// Every option of <see cref="WorldRuntimeOptions"/>, <see cref="Game.Maps.Grid.MapOptions"/>,
/// <see cref="WorldOptions"/> and <see cref="SocialOptions"/>, classified live or restart-only. A test fails when a new option is
/// added without being classified here, so no option is silently left out of the reload.
/// </summary>
public static class WorldConfigKeys
{
    private const string Root = WorldOptions.SectionName;

    /// <summary>All keys, in the order messages are produced.</summary>
    public static IReadOnlyList<WorldConfigKey> All { get; } =
    [
        // Restart-only. WorldRuntime.Run reads the tick once for the thread's sleep and SpellFeature.Attach for
        // its timer and MapUpdateIntervalMs; the listener and terrain data are bound or loaded at start.
        // vmangos: WorldServerPort World.cpp:598, DataDir World.cpp:932-935 (its MapUpdateInterval is live,
        // World.cpp:592-594, but ArcaneCore has no separate map-update interval to re-apply).
        Fixed("TickIntervalMs", v => v.Runtime.TickIntervalMs),
        // WorldRuntime.Run builds its waiter and scheduler once (docs/integration/perf-limits-20261008.md).
        Fixed("TickTimer", v => v.Runtime.TickTimer),
        Fixed("TickLateToleranceMs", v => v.Runtime.TickLateToleranceMs),
        // WorldRuntime builds the map threads and the stage gates once, on the first parallel tick (docs/research/multithreading-plan-20261017.md).
        Fixed("Parallel:MapUpdateThreads", v => v.Runtime.Parallel.MapUpdateThreads),
        Fixed("Parallel:GateAllStages", v => v.Runtime.Parallel.GateAllStages),
        Fixed("Parallel:Ungated", v => v.Runtime.Parallel.Ungated),
        Fixed("Parallel:Gated", v => v.Runtime.Parallel.Gated),
        Fixed("Parallel:OwnerGuard", v => v.Runtime.Parallel.OwnerGuard),
        Fixed("Parallel:CreatureSplinePass", v => v.Runtime.Parallel.CreatureSplinePass),
        Fixed("Maps:DataDirectory", v => v.Runtime.Maps.DataDirectory),
        FixedPlayerbots(nameof(PlayerbotOptions.AsyncNavigationEnabled), o => o.AsyncNavigationEnabled),
        FixedListener("Port", v => v.Port),
        FixedListener("BindAddress", v => v.BindAddress),

        // Connection caps (security hardening lane): the listener's ConnectionLimiter is built at start.
        FixedListener("MaxConnections", v => v.MaxConnections),
        FixedListener("MaxConnectionsPerIp", v => v.MaxConnectionsPerIp),

        // Live: read from the shared options object at each use.
        LiveNonNegative("UpdateCompressionThreshold", o => o.UpdateCompressionThreshold, (o, v) => o.UpdateCompressionThreshold = v),
        LiveNonNegative("AutosaveIntervalMs", o => o.AutosaveIntervalMs, (o, v) => o.AutosaveIntervalMs = v),
        // CharacterSaveQueue.MaybeReport reads both thresholds from the shared options at each backlog check (once a second; 0 disables that check).
        LiveNonNegative("SaveQueueWarnDepth", o => o.SaveQueueWarnDepth, (o, v) => o.SaveQueueWarnDepth = v),
        LiveNonNegative("SaveQueueWarnAgeMs", o => o.SaveQueueWarnAgeMs, (o, v) => o.SaveQueueWarnAgeMs = v),
        Live("MaxCommandsPerTick", o => o.MaxCommandsPerTick, (o, v) => o.MaxCommandsPerTick = v,
            value => value > 0 ? null : "must be positive"),
        LiveNonNegative("CommandTimeBudgetMs", o => o.CommandTimeBudgetMs, (o, v) => o.CommandTimeBudgetMs = v),
        LiveNonNegative("CharactersPerRealm", o => o.CharactersPerRealm, (o, v) => o.CharactersPerRealm = v),
        Live("Motd", o => o.Motd, (o, v) => o.Motd = v),
        LiveNonNegative("ListenRangeSay", o => o.ListenRangeSay, (o, v) => o.ListenRangeSay = v),
        LiveNonNegative("ListenRangeYell", o => o.ListenRangeYell, (o, v) => o.ListenRangeYell = v),
        LiveNonNegative("ListenRangeTextEmote", o => o.ListenRangeTextEmote, (o, v) => o.ListenRangeTextEmote = v),
        Live("AllowTwoSideChat", o => o.AllowTwoSideChat, (o, v) => o.AllowTwoSideChat = v),
        Live("AllowTwoSideWhoList", o => o.AllowTwoSideWhoList, (o, v) => o.AllowTwoSideWhoList = v),
        Live("LogoutDelayMs", o => o.LogoutDelayMs, (o, v) => o.LogoutDelayMs = v),
        Live("InstantLogoutSecurity", o => o.InstantLogoutSecurity, (o, v) => o.InstantLogoutSecurity = v, DefinedSecurity),
        Live("GmLevelInWhoList", o => o.GmLevelInWhoList, (o, v) => o.GmLevelInWhoList = v, DefinedSecurity),
        Live("PlayerCommands", o => o.PlayerCommands, (o, v) => o.PlayerCommands = v),

        // Map.CountUpdaterFault reads the shared options on every updater failure (code hot reload lane; 0 never skips).
        LiveNonNegative("MaxConsecutiveUpdaterFaults", o => o.MaxConsecutiveUpdaterFaults, (o, v) => o.MaxConsecutiveUpdaterFaults = v),

        // GridContainer reads the shared MapOptions at each use; running grids keep the timer they have
        // until it is reset, as vmangos MapManager::SetGridCleanUpDelay does (World.cpp:588-590).
        Live("Maps:GridUnload", o => o.Maps.GridUnload, (o, v) => o.Maps.GridUnload = v),
        Live("Maps:UpdateActiveCellsOnly", o => o.Maps.UpdateActiveCellsOnly, (o, v) => o.Maps.UpdateActiveCellsOnly = v),
        LiveNonNegative("Maps:GridCleanUpDelayMs", o => o.Maps.GridCleanUpDelayMs, (o, v) => o.Maps.GridCleanUpDelayMs = v),
        LiveNonNegative("Maps:GridActivationDistance", o => o.Maps.GridActivationDistance, (o, v) => o.Maps.GridActivationDistance = v),
        LiveNonNegative("Maps:VisibilityRelocationLowerLimit", o => o.Maps.VisibilityRelocationLowerLimit, (o, v) => o.Maps.VisibilityRelocationLowerLimit = v),
        LiveNonNegative("Maps:GridPrefetchDistance", o => o.Maps.GridPrefetchDistance, (o, v) => o.Maps.GridPrefetchDistance = v),

        // Population-scaled respawn: CreatureMapSystem.OnCreatureDied and GameObjectMapSystem.Despawn read the shared options at each death
        // and despawn (docs/areas/creature-movement-spawns.md, "Population-scaled respawn"); a respawn already scheduled keeps its time.
        Live("Respawn:Dynamic:Enabled", o => o.Respawn.Dynamic.Enabled, (o, v) => o.Respawn.Dynamic.Enabled = v),
        Live("Respawn:Dynamic:PlayerThreshold", o => o.Respawn.Dynamic.PlayerThreshold, (o, v) => o.Respawn.Dynamic.PlayerThreshold = v, DynamicRespawnOptions.CheckPlayerThreshold),
        Live("Respawn:Dynamic:ScalePerPlayer", o => o.Respawn.Dynamic.ScalePerPlayer, (o, v) => o.Respawn.Dynamic.ScalePerPlayer = v, DynamicRespawnOptions.CheckScalePerPlayer),
        Live("Respawn:Dynamic:MinimumSeconds", o => o.Respawn.Dynamic.MinimumSeconds, (o, v) => o.Respawn.Dynamic.MinimumSeconds = v, DynamicRespawnOptions.CheckMinimumSeconds),
        Live("Respawn:Dynamic:MaxReduction", o => o.Respawn.Dynamic.MaxReduction, (o, v) => o.Respawn.Dynamic.MaxReduction = v, DynamicRespawnOptions.CheckMaxReduction),
        Live("Respawn:Dynamic:CountBots", o => o.Respawn.Dynamic.CountBots, (o, v) => o.Respawn.Dynamic.CountBots = v),
        Live("Respawn:Dynamic:Radius", o => o.Respawn.Dynamic.Radius, (o, v) => o.Respawn.Dynamic.Radius = v, DynamicRespawnOptions.CheckRadius),
        Live("Respawn:Dynamic:IncludeElitesAndRares", o => o.Respawn.Dynamic.IncludeElitesAndRares, (o, v) => o.Respawn.Dynamic.IncludeElitesAndRares = v),
        Live("Respawn:Dynamic:IncludeGameObjects", o => o.Respawn.Dynamic.IncludeGameObjects, (o, v) => o.Respawn.Dynamic.IncludeGameObjects = v),

        // The cross-faction rules, read from the shared SocialOptions at each use. Keys map to vmangos
        // AllowTwoSide.Interaction.Group / .Guild / .Channel (World.cpp:610-613) and AllowTwoSide.AddFriend (:618).
        LiveSocial("AllowTwoSideAddFriend", o => o.AllowTwoSideAddFriend, (o, v) => o.AllowTwoSideAddFriend = v),
        LiveSocial("AllowTwoSideGroup", o => o.AllowTwoSideGroup, (o, v) => o.AllowTwoSideGroup = v),
        LiveSocial("AllowTwoSideGuild", o => o.AllowTwoSideGuild, (o, v) => o.AllowTwoSideGuild = v),
        LiveSocial("AllowTwoSideChannel", o => o.AllowTwoSideChannel, (o, v) => o.AllowTwoSideChannel = v),

        // Opt-in channel-join cap (security hardening lane; 0 = unlimited, the retail behaviour): ChannelManager reads it at each join.
        LiveSocialCount("MaxJoinedChannels", o => o.MaxJoinedChannels, (o, v) => o.MaxJoinedChannels = v),
        // vmangos Group.OfflineLeaderDelay (World.cpp:811): seconds before an offline group leader yields; 0 disables. GroupManager reads it at each tick.
        LiveSocialCount("OfflineLeaderDelaySeconds", o => o.OfflineLeaderDelaySeconds, (o, v) => o.OfflineLeaderDelaySeconds = v),
        // Not a vmangos key: the switch for its custom "World"/"China" channel names, read when a channel is created.
        LiveSocial("VmangosChannelExtensions", o => o.VmangosChannelExtensions, (o, v) => o.VmangosChannelExtensions = v),

        // Not vmangos keys: the running and registered bot caps. ManagedPlayerbotFeature reads them at each start and create, so a
        // raise applies at once and a lowering refuses new starts without stopping running bots (docs/areas/playbots.md).
        LivePlayerbotsValue("MaxBots", o => o.MaxBots, (o, v) => o.MaxBots = v,
            v => v is >= 0 and <= PlayerbotOptions.MaxBotsCeiling ? null : $"must be 0..{PlayerbotOptions.MaxBotsCeiling}"),
        LivePlayerbotsValue("MaxRegisteredBots", o => o.MaxRegisteredBots, (o, v) => o.MaxRegisteredBots = v,
            v => v is >= 0 and <= PlayerbotOptions.MaxRegisteredBotsCeiling ? null : $"must be 0..{PlayerbotOptions.MaxRegisteredBotsCeiling}"),
        // The bulk-command cap of .playerbot summon|send|start|stop (read when a command runs).
        LivePlayerbotsValue("MaxBulkCommand", o => o.MaxBulkCommand, (o, v) => o.MaxBulkCommand = v,
            v => v is >= 1 and <= PlayerbotOptions.MaxBotsCeiling ? null : $"must be 1..{PlayerbotOptions.MaxBotsCeiling}"),

        // Not a vmangos key: how bot movement reaches the world (client packets through the handlers, or applied by the server).
        // PlayerbotMotion reads the shared PlayerbotOptions at every movement packet. The playerbot options not listed here are read at start.
        LivePlayerbots("MovementPackets", o => o.MovementPackets, (o, v) => o.MovementPackets = v),
        // Not vmangos keys: the slow-think log threshold and the fallback search budget of one bot think (ManagedPlayerbotFeature reads
        // both from the shared PlayerbotOptions at every think).
        LivePlayerbotsValue("SlowThinkLogMs", o => o.SlowThinkLogMs, (o, v) => o.SlowThinkLogMs = v,
            v => double.IsFinite(v) && v is >= 0 and <= 60_000 ? null : "must be 0..60000"),
        LivePlayerbotsValue("MaxThinkSearchNodes", o => o.MaxThinkSearchNodes, (o, v) => o.MaxThinkSearchNodes = v,
            v => v == 0 || v is >= Game.Maps.Collision.PathOptions.DefaultMaxSearchNodes and <= 10_000_000 ? null : $"must be 0 or {Game.Maps.Collision.PathOptions.DefaultMaxSearchNodes}..10000000"),
        // Not vmangos keys: bot path jobs (PlayerbotPathJobs) and the world-thread search deadline; read at each query and think.
        LivePlayerbots("PathJobs", o => o.PathJobs, (o, v) => o.PathJobs = v),
        LivePlayerbotsValue("PathJobsMaxPerBot", o => o.PathJobsMaxPerBot, (o, v) => o.PathJobsMaxPerBot = v,
            v => v is >= 1 and <= 8 ? null : "must be 1..8"),
        LivePlayerbotsValue("PathJobsMaxInFlight", o => o.PathJobsMaxInFlight, (o, v) => o.PathJobsMaxInFlight = v,
            v => v is >= 1 and <= 1024 ? null : "must be 1..1024"),
        LivePlayerbotsValue("PathJobsMaxInFlightNodes", o => o.PathJobsMaxInFlightNodes, (o, v) => o.PathJobsMaxInFlightNodes = v,
            v => v is >= 4096 and <= 100_000_000 ? null : "must be 4096..100000000"),
        LivePlayerbotsValue("PathJobSyncNodeThreshold", o => o.PathJobSyncNodeThreshold, (o, v) => o.PathJobSyncNodeThreshold = v,
            v => v is >= 0 and <= 32_768 ? null : "must be 0..32768"),
        LivePlayerbotsValue("PathJobWorkerMaxMs", o => o.PathJobWorkerMaxMs, (o, v) => o.PathJobWorkerMaxMs = v,
            v => v is >= 10 and <= 60_000 ? null : "must be 10..60000"),
        LivePlayerbotsValue("PathJobWaitMaxMs", o => o.PathJobWaitMaxMs, (o, v) => o.PathJobWaitMaxMs = v,
            v => v is >= 1000 and <= 600_000 ? null : "must be 1000..600000"),
        LivePlayerbotsValue("SyncPathQueryMaxMs", o => o.SyncPathQueryMaxMs, (o, v) => o.SyncPathQueryMaxMs = v,
            v => v == 0 || v is >= 10 and <= 60_000 ? null : "must be 0 or 10..60000"),

        // Not vmangos keys: risk against reward and retreat (docs/areas/playbots-risk.md). PlayerbotRisk, PlayerbotRiskModel and the
        // party AI read the shared PlayerbotOptions.Risk at every decision.
        LivePlayerbots("Risk:Enabled", o => o.Risk.Enabled, (o, v) => o.Risk.Enabled = v),
        LivePlayerbotsValue("Risk:Tolerance", o => o.Risk.Tolerance, (o, v) => o.Risk.Tolerance = v, v => PlayerbotRiskOptions.Check(v, 0.25f, 4f)),
        LivePlayerbotsValue("Risk:RetreatHealthPct", o => o.Risk.RetreatHealthPct, (o, v) => o.Risk.RetreatHealthPct = v, v => PlayerbotRiskOptions.Check(v, 5, 95)),
        LivePlayerbotsValue("Risk:NearlyWonHealthPct", o => o.Risk.NearlyWonHealthPct, (o, v) => o.Risk.NearlyWonHealthPct = v, v => PlayerbotRiskOptions.Check(v, 0, 60)),
        LivePlayerbotsValue("Risk:RecoverHealthPct", o => o.Risk.RecoverHealthPct, (o, v) => o.Risk.RecoverHealthPct = v, v => PlayerbotRiskOptions.Check(v, 10, 100)),
        LivePlayerbotsValue("Risk:RestManaBelowPct", o => o.Risk.RestManaBelowPct, (o, v) => o.Risk.RestManaBelowPct = v, v => PlayerbotRiskOptions.Check(v, 0, 100)),
        LivePlayerbotsValue("Risk:DangerMemorySeconds", o => o.Risk.DangerMemorySeconds, (o, v) => o.Risk.DangerMemorySeconds = v,
            v => v is >= 0 and <= 86_400 ? null : "must be 0..86400"),
        LivePlayerbotsValue("Risk:DeathLoopMemorySeconds", o => o.Risk.DeathLoopMemorySeconds, (o, v) => o.Risk.DeathLoopMemorySeconds = v,
            v => v is >= 0 and <= 86_400 ? null : "must be 0..86400"),
        LivePlayerbots("Risk:PartyRetreatOnWipe", o => o.Risk.PartyRetreatOnWipe, (o, v) => o.Risk.PartyRetreatOnWipe = v),

        // Not vmangos keys: kiting (docs/areas/playbots-combat.md). PlayerbotKite reads the shared PlayerbotOptions.Combat at every
        // decision; the rotations never read it, they see only RotationState.KiteHolding.
        LivePlayerbots("Combat:Kite:Enabled", o => o.Combat.Kite.Enabled, (o, v) => o.Combat.Kite.Enabled = v),
        LivePlayerbotsValue("Combat:Kite:HealthPct", o => o.Combat.Kite.HealthPct, (o, v) => o.Combat.Kite.HealthPct = v, v => PlayerbotRiskOptions.Check(v, 10, 95)),
        LivePlayerbotsValue("Combat:Kite:StepYards", o => o.Combat.Kite.StepYards, (o, v) => o.Combat.Kite.StepYards = v, v => PlayerbotRiskOptions.Check(v, 4, 20)),
        LivePlayerbotsValue("Combat:Kite:MaxStepsPerFight", o => o.Combat.Kite.MaxStepsPerFight, (o, v) => o.Combat.Kite.MaxStepsPerFight = v,
            v => v is >= 1 and <= 32 ? null : "must be 1..32"),

        // Not vmangos keys: chests, nodes and rares weighed against their guards (docs/areas/playbots-opportunities.md).
        // PlayerbotOpportunities reads the shared PlayerbotOptions.Opportunities at every scan and decision.
        LivePlayerbots("Opportunities:Enabled", o => o.Opportunities.Enabled, (o, v) => o.Opportunities.Enabled = v),
        LivePlayerbotsValue("Opportunities:ScanRadiusYards", o => o.Opportunities.ScanRadiusYards, (o, v) => o.Opportunities.ScanRadiusYards = v,
            PlayerbotOpportunityOptions.CheckRadius),
        LivePlayerbotsValue("Opportunities:RiskAppetite", o => o.Opportunities.RiskAppetite, (o, v) => o.Opportunities.RiskAppetite = v,
            PlayerbotOpportunityOptions.CheckAppetite),
        LivePlayerbotsValue("Opportunities:MinValue", o => o.Opportunities.MinValue, (o, v) => o.Opportunities.MinValue = v,
            PlayerbotOpportunityOptions.CheckMinValue),
        LivePlayerbotsValue("Opportunities:MaxGuards", o => o.Opportunities.MaxGuards, (o, v) => o.Opportunities.MaxGuards = v,
            PlayerbotOpportunityOptions.CheckGuards),
        LivePlayerbotsValue("Opportunities:MaxDeathProbability", o => o.Opportunities.MaxDeathProbability, (o, v) => o.Opportunities.MaxDeathProbability = v,
            PlayerbotOpportunityOptions.CheckDeath),
        LivePlayerbotsValue("Opportunities:PersonalityVariance", o => o.Opportunities.PersonalityVariance, (o, v) => o.Opportunities.PersonalityVariance = v,
            PlayerbotOpportunityOptions.CheckVariance),
        LivePlayerbots("Opportunities:Chests", o => o.Opportunities.Chests, (o, v) => o.Opportunities.Chests = v),
        LivePlayerbots("Opportunities:LockedChests", o => o.Opportunities.LockedChests, (o, v) => o.Opportunities.LockedChests = v),
        LivePlayerbots("Opportunities:Nodes", o => o.Opportunities.Nodes, (o, v) => o.Opportunities.Nodes = v),
        LivePlayerbots("Opportunities:Rares", o => o.Opportunities.Rares, (o, v) => o.Opportunities.Rares = v),

        // Not vmangos keys: bot chat (docs/areas/playbots.md, Bot chat). PlayerbotChat reads the shared PlayerbotOptions.Chat at every
        // line and every provider attempt; the provider list is compared and replaced as one value.
        LivePlayerbots("Chat:Enabled", o => o.Chat.Enabled, (o, v) => o.Chat.Enabled = v),
        LivePlayerbotsValue("Chat:Providers", o => new Playerbots.Chat.PlayerbotChatProviderList([.. o.Chat.Providers.Select(p => p.Clone())]),
            (o, v) => o.Chat.Providers = [.. v.Items.Select(p => p.Clone())], v => Playerbots.Chat.PlayerbotChatOptions.CheckProviders(v.Items)),
        LivePlayerbotsValue("Chat:Channels", o => o.Chat.Channels, (o, v) => o.Chat.Channels = v, Playerbots.Chat.PlayerbotChatOptions.CheckChannels),
        LivePlayerbotsValue("Chat:PerPlayerCooldownSeconds", o => o.Chat.PerPlayerCooldownSeconds, (o, v) => o.Chat.PerPlayerCooldownSeconds = v,
            v => v is >= 0 and <= 3600 ? null : "must be 0..3600"),
        LivePlayerbotsValue("Chat:MaxDailySpendUsd", o => o.Chat.MaxDailySpendUsd, (o, v) => o.Chat.MaxDailySpendUsd = v, Playerbots.Chat.PlayerbotChatOptions.CheckSpend),
        LivePlayerbotsValue("Chat:MemoryExchanges", o => o.Chat.MemoryExchanges, (o, v) => o.Chat.MemoryExchanges = v, v => v is >= 0 and <= 16 ? null : "must be 0..16"),
        LivePlayerbotsValue("Chat:TimeoutSeconds", o => o.Chat.TimeoutSeconds, (o, v) => o.Chat.TimeoutSeconds = v, v => v is >= 1 and <= 60 ? null : "must be 1..60"),
        LivePlayerbotsValue("Chat:MaxQueuedRequests", o => o.Chat.MaxQueuedRequests, (o, v) => o.Chat.MaxQueuedRequests = v, v => v is >= 1 and <= 256 ? null : "must be 1..256"),
        LivePlayerbots("Chat:NaturalLanguageCommands", o => o.Chat.NaturalLanguageCommands, (o, v) => o.Chat.NaturalLanguageCommands = v),
        // Not vmangos keys: bot speech (docs/areas/playbots.md, Bot speech). PlayerbotChat and the speech limiter read the shared
        // PlayerbotOptions.Chat.Speech at every line; the world channels at every social pass.
        LivePlayerbots("Chat:Speech:Enabled", o => o.Chat.Speech.Enabled, (o, v) => o.Chat.Speech.Enabled = v),
        LivePlayerbots("Chat:Speech:RequireRealListener", o => o.Chat.Speech.RequireRealListener, (o, v) => o.Chat.Speech.RequireRealListener = v),
        LivePlayerbotsValue("Chat:Speech:BotGapSeconds", o => o.Chat.Speech.BotGapSeconds, (o, v) => o.Chat.Speech.BotGapSeconds = v, v => v is >= 0 and <= 3600 ? null : "must be 0..3600"),
        LivePlayerbotsValue("Chat:Speech:BotLinesPerMinute", o => o.Chat.Speech.BotLinesPerMinute, (o, v) => o.Chat.Speech.BotLinesPerMinute = v, v => v is >= 1 and <= 60 ? null : "must be 1..60"),
        LivePlayerbotsValue("Chat:Speech:TargetGapSeconds", o => o.Chat.Speech.TargetGapSeconds, (o, v) => o.Chat.Speech.TargetGapSeconds = v, v => v is >= 0 and <= 3600 ? null : "must be 0..3600"),
        LivePlayerbotsValue("Chat:Speech:ChannelLinesPerMinute", o => o.Chat.Speech.ChannelLinesPerMinute, (o, v) => o.Chat.Speech.ChannelLinesPerMinute = v, v => v is >= 0 and <= 60 ? null : "must be 0..60"),
        LivePlayerbotsValue("Chat:Speech:ChannelCrowdBots", o => o.Chat.Speech.ChannelCrowdBots, (o, v) => o.Chat.Speech.ChannelCrowdBots = v, v => v is >= 1 and <= 10000 ? null : "must be 1..10000"),
        LivePlayerbotsValue("Chat:Speech:LocalLinesPerMinute", o => o.Chat.Speech.LocalLinesPerMinute, (o, v) => o.Chat.Speech.LocalLinesPerMinute = v, v => v is >= 0 and <= 120 ? null : "must be 0..120"),
        LivePlayerbotsValue("Chat:Speech:GroupLinesPerMinute", o => o.Chat.Speech.GroupLinesPerMinute, (o, v) => o.Chat.Speech.GroupLinesPerMinute = v, v => v is >= 0 and <= 120 ? null : "must be 0..120"),
        LivePlayerbotsValue("Chat:Speech:GuildLinesPerMinute", o => o.Chat.Speech.GuildLinesPerMinute, (o, v) => o.Chat.Speech.GuildLinesPerMinute = v, v => v is >= 0 and <= 120 ? null : "must be 0..120"),
        LivePlayerbotsValue("Chat:Speech:DedupeSeconds", o => o.Chat.Speech.DedupeSeconds, (o, v) => o.Chat.Speech.DedupeSeconds = v, v => v is >= 0 and <= 3600 ? null : "must be 0..3600"),
        LivePlayerbotsValue("Chat:Speech:WorldChannels", o => o.Chat.Speech.WorldChannels, (o, v) => o.Chat.Speech.WorldChannels = v, Playerbots.Chat.PlayerbotSpeechOptions.CheckWorldChannels),
        LivePlayerbotsValue("Chat:Speech:ChannelChangesPerPass", o => o.Chat.Speech.ChannelChangesPerPass, (o, v) => o.Chat.Speech.ChannelChangesPerPass = v, v => v is >= 1 and <= 1000 ? null : "must be 1..1000"),
        LivePlayerbots("Chat:Speech:BotToBot", o => o.Chat.Speech.BotToBot, (o, v) => o.Chat.Speech.BotToBot = v),
        LivePlayerbotsValue("Chat:Speech:BotToBotMaxTurns", o => o.Chat.Speech.BotToBotMaxTurns, (o, v) => o.Chat.Speech.BotToBotMaxTurns = v, v => v is >= 0 and <= 10 ? null : "must be 0..10"),
        LivePlayerbotsValue("Chat:Speech:BotToBotQuietSeconds", o => o.Chat.Speech.BotToBotQuietSeconds, (o, v) => o.Chat.Speech.BotToBotQuietSeconds = v, v => v is >= 10 and <= 3600 ? null : "must be 10..3600"),
        LivePlayerbotsValue("Chat:Speech:BotToBotPairLinesPerHour", o => o.Chat.Speech.BotToBotPairLinesPerHour, (o, v) => o.Chat.Speech.BotToBotPairLinesPerHour = v, v => v is >= 0 and <= 600 ? null : "must be 0..600"),
        LivePlayerbotsValue("Chat:Speech:BotToBotLinesPerMinute", o => o.Chat.Speech.BotToBotLinesPerMinute, (o, v) => o.Chat.Speech.BotToBotLinesPerMinute = v, v => v is >= 0 and <= 600 ? null : "must be 0..600"),
        LivePlayerbots("Chat:Speech:EmoteReplies", o => o.Chat.Speech.EmoteReplies, (o, v) => o.Chat.Speech.EmoteReplies = v),
        LivePlayerbotsValue("Chat:Speech:EmoteReplyGapSeconds", o => o.Chat.Speech.EmoteReplyGapSeconds, (o, v) => o.Chat.Speech.EmoteReplyGapSeconds = v, v => v is >= 0 and <= 3600 ? null : "must be 0..3600"),
        LivePlayerbotsValue("Chat:Speech:MaxQueuedLines", o => o.Chat.Speech.MaxQueuedLines, (o, v) => o.Chat.Speech.MaxQueuedLines = v, v => v is >= 16 and <= 4096 ? null : "must be 16..4096"),
        // Not vmangos keys: bot-to-bot conversations and world channel reactions (docs/areas/playbots.md, Conversations). The social
        // pass reads the shared PlayerbotOptions.Chat.Conversation at every pass.
        LivePlayerbots("Chat:Conversation:Enabled", o => o.Chat.Conversation.Enabled, (o, v) => o.Chat.Conversation.Enabled = v),
        LivePlayerbotsValue("Chat:Conversation:ConversationsPerHour", o => o.Chat.Conversation.ConversationsPerHour, (o, v) => o.Chat.Conversation.ConversationsPerHour = v, v => v is >= 0 and <= 600 ? null : "must be 0..600"),
        LivePlayerbotsValue("Chat:Conversation:MaxActive", o => o.Chat.Conversation.MaxActive, (o, v) => o.Chat.Conversation.MaxActive = v, v => v is >= 1 and <= 64 ? null : "must be 1..64"),
        LivePlayerbotsValue("Chat:Conversation:MaxLines", o => o.Chat.Conversation.MaxLines, (o, v) => o.Chat.Conversation.MaxLines = v, v => v is >= 2 and <= 6 ? null : "must be 2..6"),
        LivePlayerbotsValue("Chat:Conversation:PairCooldownMinutes", o => o.Chat.Conversation.PairCooldownMinutes, (o, v) => o.Chat.Conversation.PairCooldownMinutes = v, v => v is >= 1 and <= 1440 ? null : "must be 1..1440"),
        LivePlayerbotsValue("Chat:Conversation:BotCooldownMinutes", o => o.Chat.Conversation.BotCooldownMinutes, (o, v) => o.Chat.Conversation.BotCooldownMinutes = v, v => v is >= 1 and <= 1440 ? null : "must be 1..1440"),
        LivePlayerbotsValue("Chat:Conversation:LineGapSeconds", o => o.Chat.Conversation.LineGapSeconds, (o, v) => o.Chat.Conversation.LineGapSeconds = v, v => v is >= 2 and <= 120 ? null : "must be 2..120"),
        LivePlayerbotsValue("Chat:Conversation:LineJitterSeconds", o => o.Chat.Conversation.LineJitterSeconds, (o, v) => o.Chat.Conversation.LineJitterSeconds = v, v => v is >= 0 and <= 120 ? null : "must be 0..120"),
        LivePlayerbotsValue("Chat:Conversation:LineDeadlineSeconds", o => o.Chat.Conversation.LineDeadlineSeconds, (o, v) => o.Chat.Conversation.LineDeadlineSeconds = v, v => v is >= 10 and <= 600 ? null : "must be 10..600"),
        LivePlayerbotsValue("Chat:Conversation:CandidatesPerPass", o => o.Chat.Conversation.CandidatesPerPass, (o, v) => o.Chat.Conversation.CandidatesPerPass = v, v => v is >= 1 and <= 256 ? null : "must be 1..256"),
        LivePlayerbotsValue("Chat:Conversation:NearYards", o => o.Chat.Conversation.NearYards, (o, v) => o.Chat.Conversation.NearYards = v, v => v is >= 5 and <= 100 ? null : "must be 5..100"),
        LivePlayerbots("Chat:Conversation:WorldReactions", o => o.Chat.Conversation.WorldReactions, (o, v) => o.Chat.Conversation.WorldReactions = v),
        LivePlayerbotsValue("Chat:Conversation:WorldReactionsPerMinute", o => o.Chat.Conversation.WorldReactionsPerMinute, (o, v) => o.Chat.Conversation.WorldReactionsPerMinute = v, v => v is >= 0 and <= 60 ? null : "must be 0..60"),
        LivePlayerbotsValue("Chat:Conversation:ReactionsPerLine", o => o.Chat.Conversation.ReactionsPerLine, (o, v) => o.Chat.Conversation.ReactionsPerLine = v, v => v is >= 1 and <= 5 ? null : "must be 1..5"),
        LivePlayerbotsValue("Chat:Conversation:ReactionPlayerCooldownMinutes", o => o.Chat.Conversation.ReactionPlayerCooldownMinutes, (o, v) => o.Chat.Conversation.ReactionPlayerCooldownMinutes = v, v => v is >= 1 and <= 1440 ? null : "must be 1..1440"),
        LivePlayerbotsValue("Chat:Conversation:MaxHeardQueue", o => o.Chat.Conversation.MaxHeardQueue, (o, v) => o.Chat.Conversation.MaxHeardQueue = v, v => v is >= 8 and <= 1024 ? null : "must be 8..1024"),
        // Not vmangos keys: the bot voice (docs/areas/playbots.md, Bot voice). PlayerbotNeedsVoice reads the shared
        // PlayerbotOptions.Chat.Voice at every social pass and every heard whisper.
        LivePlayerbots("Chat:Voice:Enabled", o => o.Chat.Voice.Enabled, (o, v) => o.Chat.Voice.Enabled = v),
        LivePlayerbotsValue("Chat:Voice:BotsPerPass", o => o.Chat.Voice.BotsPerPass, (o, v) => o.Chat.Voice.BotsPerPass = v, v => v is >= 1 and <= 1000 ? null : "must be 1..1000"),
        LivePlayerbotsValue("Chat:Voice:LinesPerPass", o => o.Chat.Voice.LinesPerPass, (o, v) => o.Chat.Voice.LinesPerPass = v, v => v is >= 1 and <= 100 ? null : "must be 1..100"),
        LivePlayerbots("Chat:Voice:LookingForGroup", o => o.Chat.Voice.LookingForGroup, (o, v) => o.Chat.Voice.LookingForGroup = v),
        LivePlayerbotsValue("Chat:Voice:LookingForGroupRepeatSeconds", o => o.Chat.Voice.LookingForGroupRepeatSeconds, (o, v) => o.Chat.Voice.LookingForGroupRepeatSeconds = v, v => v is >= 30 and <= 3600 ? null : "must be 30..3600"),
        LivePlayerbots("Chat:Voice:Trade", o => o.Chat.Voice.Trade, (o, v) => o.Chat.Voice.Trade = v),
        LivePlayerbotsValue("Chat:Voice:TradeRepeatSeconds", o => o.Chat.Voice.TradeRepeatSeconds, (o, v) => o.Chat.Voice.TradeRepeatSeconds = v, v => v is >= 60 and <= 86400 ? null : "must be 60..86400"),
        LivePlayerbotsValue("Chat:Voice:PriceMultiplier", o => o.Chat.Voice.PriceMultiplier, (o, v) => o.Chat.Voice.PriceMultiplier = v, v => v is >= 1 and <= 50 ? null : "must be 1..50"),
        LivePlayerbotsValue("Chat:Voice:MinSellCopper", o => o.Chat.Voice.MinSellCopper, (o, v) => o.Chat.Voice.MinSellCopper = v, v => v is >= 0 and <= 1000000 ? null : "must be 0..1000000"),
        LivePlayerbotsValue("Chat:Voice:GeneralSharePercent", o => o.Chat.Voice.GeneralSharePercent, (o, v) => o.Chat.Voice.GeneralSharePercent = v, v => v is >= 0 and <= 100 ? null : "must be 0..100"),
        LivePlayerbots("Chat:Voice:GroupCalls", o => o.Chat.Voice.GroupCalls, (o, v) => o.Chat.Voice.GroupCalls = v),
        LivePlayerbots("Chat:Voice:RunCalls", o => o.Chat.Voice.RunCalls, (o, v) => o.Chat.Voice.RunCalls = v),
        LivePlayerbots("Chat:Voice:BattlegroundCalls", o => o.Chat.Voice.BattlegroundCalls, (o, v) => o.Chat.Voice.BattlegroundCalls = v),
        LivePlayerbotsValue("Chat:Voice:BattlegroundCallSeconds", o => o.Chat.Voice.BattlegroundCallSeconds, (o, v) => o.Chat.Voice.BattlegroundCallSeconds = v, v => v is >= 5 and <= 600 ? null : "must be 5..600"),
        LivePlayerbots("Chat:Voice:QuestHelp", o => o.Chat.Voice.QuestHelp, (o, v) => o.Chat.Voice.QuestHelp = v),
        LivePlayerbotsValue("Chat:Voice:QuestHelpRepeatSeconds", o => o.Chat.Voice.QuestHelpRepeatSeconds, (o, v) => o.Chat.Voice.QuestHelpRepeatSeconds = v, v => v is >= 60 and <= 86400 ? null : "must be 60..86400"),
        LivePlayerbots("Chat:Voice:AnswerPosts", o => o.Chat.Voice.AnswerPosts, (o, v) => o.Chat.Voice.AnswerPosts = v),
        LivePlayerbotsValue("Chat:Voice:PostLifeSeconds", o => o.Chat.Voice.PostLifeSeconds, (o, v) => o.Chat.Voice.PostLifeSeconds = v, v => v is >= 60 and <= 3600 ? null : "must be 60..3600"),
        // Not vmangos keys: bots grouping up for group content (docs/areas/playbots-groups.md). PlayerbotGroupCoordinator reads the
        // shared PlayerbotOptions.Groups at every decision.
        LivePlayerbots("Groups:Enabled", o => o.Groups.Enabled, (o, v) => o.Groups.Enabled = v),
        LivePlayerbotsValue("Groups:MaxGroups", o => o.Groups.MaxGroups, (o, v) => o.Groups.MaxGroups = v, v => v is >= 0 and <= 64 ? null : "must be 0..64"),
        LivePlayerbotsValue("Groups:LevelRange", o => o.Groups.LevelRange, (o, v) => o.Groups.LevelRange = v, v => v is >= 0 and <= 60 ? null : "must be 0..60"),
        LivePlayerbotsValue("Groups:MinTank", o => o.Groups.MinTank, (o, v) => o.Groups.MinTank = v, v => v is >= 0 and <= 5 ? null : "must be 0..5"),
        LivePlayerbotsValue("Groups:MinHealer", o => o.Groups.MinHealer, (o, v) => o.Groups.MinHealer = v, v => v is >= 0 and <= 5 ? null : "must be 0..5"),
        LivePlayerbotsValue("Groups:FormationTimeoutSeconds", o => o.Groups.FormationTimeoutSeconds, (o, v) => o.Groups.FormationTimeoutSeconds = v,
            v => v is >= 30 and <= 86_400 ? null : "must be 30..86400"),
        LivePlayerbots("Groups:RaidsEnabled", o => o.Groups.RaidsEnabled, (o, v) => o.Groups.RaidsEnabled = v),
        LivePlayerbots("Groups:InvitePlayers", o => o.Groups.InvitePlayers, (o, v) => o.Groups.InvitePlayers = v),
        LivePlayerbotsValue("Groups:InvitePlayerAffinity", o => o.Groups.InvitePlayerAffinity, (o, v) => o.Groups.InvitePlayerAffinity = v, v => v is >= -100 and <= 100 ? null : "must be -100..100"),
        LivePlayerbotsValue("Groups:InvitePlayerCooldownMinutes", o => o.Groups.InvitePlayerCooldownMinutes, (o, v) => o.Groups.InvitePlayerCooldownMinutes = v, v => v is >= 0 and <= 1440 ? null : "must be 0..1440"),

        // Cooperation (docs/areas/playbots-groups.md): recruiting beyond the goal key, answering LFG calls, guild calls, mentoring, raid escalation.
        LivePlayerbots("Groups:Cooperation:Recruit", o => o.Groups.Cooperation.Recruit, (o, v) => o.Groups.Cooperation.Recruit = v),
        LivePlayerbotsValue("Groups:Cooperation:RecruitWaitSeconds", o => o.Groups.Cooperation.RecruitWaitSeconds, (o, v) => o.Groups.Cooperation.RecruitWaitSeconds = v, v => v is >= 1 and <= 3600 ? null : "must be 1..3600"),
        LivePlayerbotsValue("Groups:Cooperation:RecruitRadiusYards", o => o.Groups.Cooperation.RecruitRadiusYards, (o, v) => o.Groups.Cooperation.RecruitRadiusYards = v, v => v is >= 10 and <= 1500 ? null : "must be 10..1500"),
        LivePlayerbotsValue("Groups:Cooperation:RecruitBaseChance", o => o.Groups.Cooperation.RecruitBaseChance, (o, v) => o.Groups.Cooperation.RecruitBaseChance = v, v => float.IsFinite(v) && v is >= 0f and <= 1f ? null : "must be 0..1"),
        LivePlayerbotsValue("Groups:Cooperation:GuildBonus", o => o.Groups.Cooperation.GuildBonus, (o, v) => o.Groups.Cooperation.GuildBonus = v, v => float.IsFinite(v) && v is >= 0f and <= 1f ? null : "must be 0..1"),
        LivePlayerbotsValue("Groups:Cooperation:LfgAnswerBonus", o => o.Groups.Cooperation.LfgAnswerBonus, (o, v) => o.Groups.Cooperation.LfgAnswerBonus = v, v => float.IsFinite(v) && v is >= 0f and <= 1f ? null : "must be 0..1"),
        LivePlayerbotsValue("Groups:Cooperation:RefuseAffinity", o => o.Groups.Cooperation.RefuseAffinity, (o, v) => o.Groups.Cooperation.RefuseAffinity = v, v => v is >= -100 and <= 0 ? null : "must be -100..0"),
        LivePlayerbots("Groups:Cooperation:AnswerBotCalls", o => o.Groups.Cooperation.AnswerBotCalls, (o, v) => o.Groups.Cooperation.AnswerBotCalls = v),
        LivePlayerbotsValue("Groups:Cooperation:RefusalMemoryMinutes", o => o.Groups.Cooperation.RefusalMemoryMinutes, (o, v) => o.Groups.Cooperation.RefusalMemoryMinutes = v, v => v is >= 1 and <= 1440 ? null : "must be 1..1440"),
        LivePlayerbotsValue("Groups:Cooperation:MaxLfgAnswersPerLine", o => o.Groups.Cooperation.MaxLfgAnswersPerLine, (o, v) => o.Groups.Cooperation.MaxLfgAnswersPerLine = v, v => v is >= 1 and <= 64 ? null : "must be 1..64"),
        LivePlayerbots("Groups:Cooperation:GuildCalls", o => o.Groups.Cooperation.GuildCalls, (o, v) => o.Groups.Cooperation.GuildCalls = v),
        LivePlayerbots("Groups:Cooperation:Mentoring", o => o.Groups.Cooperation.Mentoring, (o, v) => o.Groups.Cooperation.Mentoring = v),
        LivePlayerbotsValue("Groups:Cooperation:MentorChance", o => o.Groups.Cooperation.MentorChance, (o, v) => o.Groups.Cooperation.MentorChance = v, v => float.IsFinite(v) && v is >= 0f and <= 1f ? null : "must be 0..1"),
        LivePlayerbotsValue("Groups:Cooperation:MentorMaxMinutes", o => o.Groups.Cooperation.MentorMaxMinutes, (o, v) => o.Groups.Cooperation.MentorMaxMinutes = v, v => v is >= 1 and <= 240 ? null : "must be 1..240"),
        LivePlayerbotsValue("Groups:Cooperation:MentorLevelGap", o => o.Groups.Cooperation.MentorLevelGap, (o, v) => o.Groups.Cooperation.MentorLevelGap = v, v => v is >= 1 and <= 60 ? null : "must be 1..60"),
        LivePlayerbotsValue("Groups:Cooperation:MentorAffinity", o => o.Groups.Cooperation.MentorAffinity, (o, v) => o.Groups.Cooperation.MentorAffinity = v, v => v is >= -100 and <= 100 ? null : "must be -100..100"),
        LivePlayerbots("Groups:Cooperation:RaidEscalation", o => o.Groups.Cooperation.RaidEscalation, (o, v) => o.Groups.Cooperation.RaidEscalation = v),
        LivePlayerbotsValue("Groups:Cooperation:MaxRaidSize", o => o.Groups.Cooperation.MaxRaidSize, (o, v) => o.Groups.Cooperation.MaxRaidSize = v, v => v is >= 6 and <= 40 ? null : "must be 6..40"),
        LivePlayerbotsValue("Groups:Cooperation:SameGoalMeetingRadiusYards", o => o.Groups.Cooperation.SameGoalMeetingRadiusYards, (o, v) => o.Groups.Cooperation.SameGoalMeetingRadiusYards = v, v => v is >= 10 and <= 1500 ? null : "must be 10..1500"),

        // Dungeon and raid runs (docs/areas/playbots-groups.md): every key live, the coordinator reads them at each decision.
        LivePlayerbots("Groups:DungeonRuns:Enabled", o => o.Groups.DungeonRuns.Enabled, (o, v) => o.Groups.DungeonRuns.Enabled = v),
        LivePlayerbotsValue("Groups:DungeonRuns:StartChancePerMinute", o => o.Groups.DungeonRuns.StartChancePerMinute,
            (o, v) => o.Groups.DungeonRuns.StartChancePerMinute = v, PlayerbotRunOptions.CheckChance),
        LivePlayerbotsValue("Groups:DungeonRuns:MaxConcurrentRuns", o => o.Groups.DungeonRuns.MaxConcurrentRuns,
            (o, v) => o.Groups.DungeonRuns.MaxConcurrentRuns = v, v => PlayerbotRunOptions.CheckRange(v, 0, 16)),
        LivePlayerbotsValue("Groups:DungeonRuns:MaxWipes", o => o.Groups.DungeonRuns.MaxWipes,
            (o, v) => o.Groups.DungeonRuns.MaxWipes = v, v => PlayerbotRunOptions.CheckRange(v, 1, 20)),
        LivePlayerbotsValue("Groups:DungeonRuns:MaxRunMinutes", o => o.Groups.DungeonRuns.MaxRunMinutes,
            (o, v) => o.Groups.DungeonRuns.MaxRunMinutes = v, v => PlayerbotRunOptions.CheckRange(v, 10, 720)),
        LivePlayerbotsValue("Groups:DungeonRuns:TravelTimeoutMinutes", o => o.Groups.DungeonRuns.TravelTimeoutMinutes,
            (o, v) => o.Groups.DungeonRuns.TravelTimeoutMinutes = v, v => PlayerbotRunOptions.CheckRange(v, 5, 240)),
        LivePlayerbotsValue("Groups:DungeonRuns:BandBelowBoss", o => o.Groups.DungeonRuns.BandBelowBoss,
            (o, v) => o.Groups.DungeonRuns.BandBelowBoss = v, v => PlayerbotRunOptions.CheckRange(v, 0, 20)),
        LivePlayerbotsValue("Groups:DungeonRuns:BandAboveBoss", o => o.Groups.DungeonRuns.BandAboveBoss,
            (o, v) => o.Groups.DungeonRuns.BandAboveBoss = v, v => PlayerbotRunOptions.CheckRange(v, 0, 20)),
        LivePlayerbotsValue("Groups:DungeonRuns:LevelBandOverrides",
            o => new PlayerbotLevelBandOverrides(new Dictionary<string, string>(o.Groups.DungeonRuns.LevelBandOverrides)),
            (o, v) => o.Groups.DungeonRuns.LevelBandOverrides = new Dictionary<string, string>(v.Items),
            v => PlayerbotRunOptions.CheckOverrides(v.Items)),
        LivePlayerbots("Groups:DungeonRuns:TeleportToEntrance", o => o.Groups.DungeonRuns.TeleportToEntrance,
            (o, v) => o.Groups.DungeonRuns.TeleportToEntrance = v),
        LivePlayerbotsValue("Groups:DungeonRuns:MaxTravelYards", o => o.Groups.DungeonRuns.MaxTravelYards,
            (o, v) => o.Groups.DungeonRuns.MaxTravelYards = v, PlayerbotRunOptions.CheckYards),
        LivePlayerbots("Groups:DungeonRuns:Raids:Enabled", o => o.Groups.DungeonRuns.Raids.Enabled, (o, v) => o.Groups.DungeonRuns.Raids.Enabled = v),
        LivePlayerbots("Groups:DungeonRuns:Raids:SkipPrerequisites", o => o.Groups.DungeonRuns.Raids.SkipPrerequisites,
            (o, v) => o.Groups.DungeonRuns.Raids.SkipPrerequisites = v),
        LivePlayerbotsValue("Groups:DungeonRuns:Raids:MaxConcurrentRaids", o => o.Groups.DungeonRuns.Raids.MaxConcurrentRaids,
            (o, v) => o.Groups.DungeonRuns.Raids.MaxConcurrentRaids = v, v => PlayerbotRunOptions.CheckRange(v, 0, 4)),
        LivePlayerbotsValue("Groups:DungeonRuns:Raids:MaxWipes", o => o.Groups.DungeonRuns.Raids.MaxWipes,
            (o, v) => o.Groups.DungeonRuns.Raids.MaxWipes = v, v => PlayerbotRunOptions.CheckRange(v, 1, 20)),
        LivePlayerbotsValue("Groups:DungeonRuns:Raids:MaxRunMinutes", o => o.Groups.DungeonRuns.Raids.MaxRunMinutes,
            (o, v) => o.Groups.DungeonRuns.Raids.MaxRunMinutes = v, v => PlayerbotRunOptions.CheckRange(v, 10, 1440)),
        LivePlayerbotsValue("Groups:DungeonRuns:Raids:TanksPerTen", o => o.Groups.DungeonRuns.Raids.TanksPerTen,
            (o, v) => o.Groups.DungeonRuns.Raids.TanksPerTen = v, v => PlayerbotRunOptions.CheckRange(v, 1, 5)),
        LivePlayerbotsValue("Groups:DungeonRuns:Raids:HealerPercent", o => o.Groups.DungeonRuns.Raids.HealerPercent,
            (o, v) => o.Groups.DungeonRuns.Raids.HealerPercent = v, v => PlayerbotRunOptions.CheckRange(v, 10, 50)),

        // World-boss raids (docs/areas/playbots-groups.md, World-boss raids): every key live, the coordinator reads them at each decision.
        LivePlayerbots("Groups:WorldBosses:Enabled", o => o.Groups.WorldBosses.Enabled, (o, v) => o.Groups.WorldBosses.Enabled = v),
        LivePlayerbotsValue("Groups:WorldBosses:WatchSeconds", o => o.Groups.WorldBosses.WatchSeconds,
            (o, v) => o.Groups.WorldBosses.WatchSeconds = v, v => PlayerbotRunOptions.CheckRange(v, 5, 600)),
        LivePlayerbotsValue("Groups:WorldBosses:PlayerGraceMinutes", o => o.Groups.WorldBosses.PlayerGraceMinutes,
            (o, v) => o.Groups.WorldBosses.PlayerGraceMinutes = v, v => PlayerbotRunOptions.CheckRange(v, 0, 240)),
        LivePlayerbotsValue("Groups:WorldBosses:PlayersNearYards", o => o.Groups.WorldBosses.PlayersNearYards,
            (o, v) => o.Groups.WorldBosses.PlayersNearYards = v, v => PlayerbotWorldBossOptions.CheckRange(v, 0f, 400f)),
        LivePlayerbotsValue("Groups:WorldBosses:PlayersNearCount", o => o.Groups.WorldBosses.PlayersNearCount,
            (o, v) => o.Groups.WorldBosses.PlayersNearCount = v, v => PlayerbotRunOptions.CheckRange(v, 1, 40)),
        LivePlayerbotsValue("Groups:WorldBosses:MusterRadiusYards", o => o.Groups.WorldBosses.MusterRadiusYards,
            (o, v) => o.Groups.WorldBosses.MusterRadiusYards = v, v => PlayerbotWorldBossOptions.CheckRange(v, 300f, 20_000f)),
        LivePlayerbotsValue("Groups:WorldBosses:LevelGap", o => o.Groups.WorldBosses.LevelGap,
            (o, v) => o.Groups.WorldBosses.LevelGap = v, v => PlayerbotRunOptions.CheckRange(v, 0, 20)),
        LivePlayerbotsValue("Groups:WorldBosses:MinRaidSize", o => o.Groups.WorldBosses.MinRaidSize,
            (o, v) => o.Groups.WorldBosses.MinRaidSize = v, v => PlayerbotRunOptions.CheckRange(v, 5, 40)),
        LivePlayerbotsValue("Groups:WorldBosses:DefaultRaidSize", o => o.Groups.WorldBosses.DefaultRaidSize,
            (o, v) => o.Groups.WorldBosses.DefaultRaidSize = v, v => PlayerbotRunOptions.CheckRange(v, 5, 40)),
        LivePlayerbotsValue("Groups:WorldBosses:SizeOverrides", o => o.Groups.WorldBosses.SizeOverrides,
            (o, v) => o.Groups.WorldBosses.SizeOverrides = v, PlayerbotWorldBossOptions.CheckSizeOverrides),
        LivePlayerbotsValue("Groups:WorldBosses:MusterWindowSeconds", o => o.Groups.WorldBosses.MusterWindowSeconds,
            (o, v) => o.Groups.WorldBosses.MusterWindowSeconds = v, v => PlayerbotRunOptions.CheckRange(v, 30, 1800)),
        LivePlayerbotsValue("Groups:WorldBosses:MaxConcurrentRaids", o => o.Groups.WorldBosses.MaxConcurrentRaids,
            (o, v) => o.Groups.WorldBosses.MaxConcurrentRaids = v, v => PlayerbotRunOptions.CheckRange(v, 0, 8)),
        LivePlayerbotsValue("Groups:WorldBosses:MaxAttemptsPerDay", o => o.Groups.WorldBosses.MaxAttemptsPerDay,
            (o, v) => o.Groups.WorldBosses.MaxAttemptsPerDay = v, v => PlayerbotRunOptions.CheckRange(v, 0, 24)),
        LivePlayerbotsValue("Groups:WorldBosses:RetrySeconds", o => o.Groups.WorldBosses.RetrySeconds,
            (o, v) => o.Groups.WorldBosses.RetrySeconds = v, v => PlayerbotRunOptions.CheckRange(v, 60, 86_400)),
        LivePlayerbotsValue("Groups:WorldBosses:RallyYards", o => o.Groups.WorldBosses.RallyYards,
            (o, v) => o.Groups.WorldBosses.RallyYards = v, v => PlayerbotWorldBossOptions.CheckRange(v, 20f, 200f)),
        LivePlayerbotsValue("Groups:WorldBosses:PresentFraction", o => o.Groups.WorldBosses.PresentFraction,
            (o, v) => o.Groups.WorldBosses.PresentFraction = v, v => PlayerbotWorldBossOptions.CheckRange(v, 0.5f, 1f)),
        LivePlayerbotsValue("Groups:WorldBosses:PullWaitSeconds", o => o.Groups.WorldBosses.PullWaitSeconds,
            (o, v) => o.Groups.WorldBosses.PullWaitSeconds = v, v => PlayerbotRunOptions.CheckRange(v, 10, 900)),
        LivePlayerbotsValue("Groups:WorldBosses:HoldSeconds", o => o.Groups.WorldBosses.HoldSeconds,
            (o, v) => o.Groups.WorldBosses.HoldSeconds = v, v => PlayerbotRunOptions.CheckRange(v, 10, 1800)),
        LivePlayerbotsValue("Groups:WorldBosses:MaxWipes", o => o.Groups.WorldBosses.MaxWipes,
            (o, v) => o.Groups.WorldBosses.MaxWipes = v, v => PlayerbotRunOptions.CheckRange(v, 1, 10)),
        LivePlayerbotsValue("Groups:WorldBosses:MaxRaidMinutes", o => o.Groups.WorldBosses.MaxRaidMinutes,
            (o, v) => o.Groups.WorldBosses.MaxRaidMinutes = v, v => PlayerbotRunOptions.CheckRange(v, 10, 480)),
        LivePlayerbots("Groups:WorldBosses:CatchUpTeleport", o => o.Groups.WorldBosses.CatchUpTeleport, (o, v) => o.Groups.WorldBosses.CatchUpTeleport = v),
        LivePlayerbots("Groups:WorldBosses:DeriveFromContent", o => o.Groups.WorldBosses.DeriveFromContent, (o, v) => o.Groups.WorldBosses.DeriveFromContent = v),
        LivePlayerbotsValue("Groups:WorldBosses:IncludedEntries", o => o.Groups.WorldBosses.IncludedEntries,
            (o, v) => o.Groups.WorldBosses.IncludedEntries = v, PlayerbotWorldBossOptions.CheckIncluded),
        LivePlayerbotsValue("Groups:WorldBosses:ExcludedEntries", o => o.Groups.WorldBosses.ExcludedEntries,
            (o, v) => o.Groups.WorldBosses.ExcludedEntries = v, PlayerbotWorldBossOptions.CheckExcluded),

        // Following and catch-up (docs/areas/playbots.md): a follower reads the shared object at each decision.
        LivePlayerbotsValue("Follow:CatchUpMode", o => o.Follow.CatchUpMode, (o, v) => o.Follow.CatchUpMode = v, PlayerbotFollowOptions.CheckMode),
        LivePlayerbotsValue("Follow:TeleportDistanceYards", o => o.Follow.TeleportDistanceYards, (o, v) => o.Follow.TeleportDistanceYards = v,
            PlayerbotFollowOptions.CheckTeleportDistance),
        LivePlayerbotsValue("Follow:MaxPathRatio", o => o.Follow.MaxPathRatio, (o, v) => o.Follow.MaxPathRatio = v, PlayerbotFollowOptions.CheckPathRatio),
        LivePlayerbotsValue("Follow:MinDetourYards", o => o.Follow.MinDetourYards, (o, v) => o.Follow.MinDetourYards = v, PlayerbotFollowOptions.CheckDetour),
        LivePlayerbotsValue("Follow:DangerLevelMargin", o => o.Follow.DangerLevelMargin, (o, v) => o.Follow.DangerLevelMargin = v,
            PlayerbotFollowOptions.CheckLevelMargin),
        LivePlayerbotsValue("Follow:LeaderCombatRangeYards", o => o.Follow.LeaderCombatRangeYards, (o, v) => o.Follow.LeaderCombatRangeYards = v,
            PlayerbotFollowOptions.CheckCombatRange),
        LivePlayerbotsValue("Follow:NoProgressSeconds", o => o.Follow.NoProgressSeconds, (o, v) => o.Follow.NoProgressSeconds = v,
            PlayerbotFollowOptions.CheckNoProgress),
        LivePlayerbotsValue("Follow:MaxFallHealthPercent", o => o.Follow.MaxFallHealthPercent, (o, v) => o.Follow.MaxFallHealthPercent = v,
            PlayerbotFollowOptions.CheckFallPercent),

        // The way home of a homed bot (docs/areas/playbots.md, Home area): a returning bot reads the shared object at each decision.
        LivePlayerbotsValue("Home:ReturnMode", o => o.Home.ReturnMode, (o, v) => o.Home.ReturnMode = v, PlayerbotHomeOptions.CheckMode),
        LivePlayerbotsValue("Home:TeleportDistanceYards", o => o.Home.TeleportDistanceYards, (o, v) => o.Home.TeleportDistanceYards = v,
            PlayerbotHomeOptions.CheckTeleportDistance),
        LivePlayerbotsValue("Home:DangerLevelMargin", o => o.Home.DangerLevelMargin, (o, v) => o.Home.DangerLevelMargin = v,
            PlayerbotHomeOptions.CheckLevelMargin),

        // How bots share the mobs of one place (docs/areas/playbots.md, Contention): each bot reads the shared object at its next think.
        LivePlayerbotsValue("Contention:HelpChance", o => o.Contention.HelpChance, (o, v) => o.Contention.HelpChance = v,
            PlayerbotContentionOptions.CheckChance),
        LivePlayerbotsValue("Contention:ClaimPenaltyYards", o => o.Contention.ClaimPenaltyYards, (o, v) => o.Contention.ClaimPenaltyYards = v,
            PlayerbotContentionOptions.CheckPenalty),
        LivePlayerbotsValue("Contention:SomeoneElsesMemorySeconds", o => o.Contention.SomeoneElsesMemorySeconds,
            (o, v) => o.Contention.SomeoneElsesMemorySeconds = v, PlayerbotContentionOptions.CheckMemory),
        LivePlayerbotsValue("Contention:ContentionSuspendMinutes", o => o.Contention.ContentionSuspendMinutes,
            (o, v) => o.Contention.ContentionSuspendMinutes = v, PlayerbotContentionOptions.CheckSuspend),
        LivePlayerbotsValue("Contention:InvincibleTargetAbandonSeconds", o => o.Contention.InvincibleTargetAbandonSeconds,
            (o, v) => o.Contention.InvincibleTargetAbandonSeconds = v, PlayerbotContentionOptions.CheckMemory),

        // Not vmangos keys: autonomous flight-path use by bots (docs/areas/playbots.md, Flight paths). PlayerbotTaxi reads the shared
        // PlayerbotOptions.Taxi at each decision.
        LivePlayerbots("Taxi:Enabled", o => o.Taxi.Enabled, (o, v) => o.Taxi.Enabled = v),
        LivePlayerbotsValue("Taxi:MinRouteYards", o => o.Taxi.MinRouteYards, (o, v) => o.Taxi.MinRouteYards = v, PlayerbotTaxiOptions.CheckMinRoute),
        LivePlayerbotsValue("Taxi:MaxWalkToMasterYards", o => o.Taxi.MaxWalkToMasterYards, (o, v) => o.Taxi.MaxWalkToMasterYards = v,
            PlayerbotTaxiOptions.CheckMaxWalk),
        LivePlayerbotsValue("Taxi:BackoffMinutes", o => o.Taxi.BackoffMinutes, (o, v) => o.Taxi.BackoffMinutes = v, PlayerbotTaxiOptions.CheckBackoff),

        // Not vmangos keys: multi-leg travel by boat, zeppelin, hearthstone and mage portal (docs/areas/playbots.md, Travel). The travel
        // planner and its legs read the shared PlayerbotOptions.Travel at each decision.
        LivePlayerbots("Travel:Enabled", o => o.Travel.Enabled, (o, v) => o.Travel.Enabled = v),
        LivePlayerbots("Travel:AreaTriggers", o => o.Travel.AreaTriggers, (o, v) => o.Travel.AreaTriggers = v),
        LivePlayerbots("Travel:Boats", o => o.Travel.Boats, (o, v) => o.Travel.Boats = v),
        LivePlayerbots("Travel:Zeppelins", o => o.Travel.Zeppelins, (o, v) => o.Travel.Zeppelins = v),
        LivePlayerbots("Travel:Hearthstone", o => o.Travel.Hearthstone, (o, v) => o.Travel.Hearthstone = v),
        LivePlayerbots("Travel:MageService", o => o.Travel.MageService, (o, v) => o.Travel.MageService = v),
        LivePlayerbotsValue("Travel:MageServiceRadiusYards", o => o.Travel.MageServiceRadiusYards, (o, v) => o.Travel.MageServiceRadiusYards = v,
            PlayerbotTravelOptions.CheckRadius),
        LivePlayerbotsValue("Travel:MageServiceFeeCopper", o => o.Travel.MageServiceFeeCopper, (o, v) => o.Travel.MageServiceFeeCopper = v,
            PlayerbotTravelOptions.CheckFee),
        LivePlayerbotsValue("Travel:MaxTravelSpendCopper", o => o.Travel.MaxTravelSpendCopper, (o, v) => o.Travel.MaxTravelSpendCopper = v,
            PlayerbotTravelOptions.CheckSpend),
        LivePlayerbotsValue("Travel:TransitBackoffSeconds", o => o.Travel.TransitBackoffSeconds, (o, v) => o.Travel.TransitBackoffSeconds = v,
            PlayerbotTravelOptions.CheckBackoff),
        LivePlayerbotsValue("Travel:TransitBackoffMaxSeconds", o => o.Travel.TransitBackoffMaxSeconds, (o, v) => o.Travel.TransitBackoffMaxSeconds = v,
            PlayerbotTravelOptions.CheckBackoffMax),

        // Not vmangos keys: the population manager (docs/areas/playbots-population.md). PlayerbotPopulationFeature reads the shared
        // PlayerbotOptions.Population at every pass; the lists are compared and replaced as one value.
        LivePlayerbots("Population:Enabled", o => o.Population.Enabled, (o, v) => o.Population.Enabled = v),
        LivePlayerbotsValue("Population:TargetOnline", o => o.Population.TargetOnline, (o, v) => o.Population.TargetOnline = v, Playerbots.Population.PlayerbotPopulationOptions.CheckTarget),
        LivePlayerbotsValue("Population:IntervalSeconds", o => o.Population.IntervalSeconds, (o, v) => o.Population.IntervalSeconds = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 2, 600)),
        LivePlayerbotsValue("Population:TickP95BudgetMs", o => o.Population.TickP95BudgetMs, (o, v) => o.Population.TickP95BudgetMs = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 5, 1000)),
        LivePlayerbotsValue("Population:ResumeBelowPct", o => o.Population.ResumeBelowPct, (o, v) => o.Population.ResumeBelowPct = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 10, 99)),
        LivePlayerbotsValue("Population:WindowTicks", o => o.Population.WindowTicks, (o, v) => o.Population.WindowTicks = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 20, 4096)),
        LivePlayerbots("Population:ParkWhenOverBudget", o => o.Population.ParkWhenOverBudget, (o, v) => o.Population.ParkWhenOverBudget = v),
        LivePlayerbotsValue("Population:ConfirmWindows", o => o.Population.ConfirmWindows, (o, v) => o.Population.ConfirmWindows = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 1, 20)),
        LivePlayerbotsValue("Population:MinRunSeconds", o => o.Population.MinRunSeconds, (o, v) => o.Population.MinRunSeconds = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 3600)),
        LivePlayerbotsValue("Population:ActionLogSeconds", o => o.Population.ActionLogSeconds, (o, v) => o.Population.ActionLogSeconds = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 3600)),
        LivePlayerbotsValue("Population:MaxCreatePerMinute", o => o.Population.MaxCreatePerMinute, (o, v) => o.Population.MaxCreatePerMinute = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 600)),
        LivePlayerbotsValue("Population:MaxStartsPerInterval", o => o.Population.MaxStartsPerInterval, (o, v) => o.Population.MaxStartsPerInterval = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 1, 500)),
        LivePlayerbotsValue("Population:MaxParksPerInterval", o => o.Population.MaxParksPerInterval, (o, v) => o.Population.MaxParksPerInterval = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 1, 500)),
        LivePlayerbotsValue("Population:Factions", o => o.Population.Factions, (o, v) => o.Population.Factions = v, Playerbots.Population.PlayerbotPopulationOptions.CheckDefined),
        LivePlayerbotsValue("Population:AllianceSharePct", o => o.Population.AllianceSharePct, (o, v) => o.Population.AllianceSharePct = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 100)),
        LivePlayerbotsValue("Population:Classes", o => new Playerbots.Population.PopulationIdList([.. o.Population.Classes]),
            (o, v) => o.Population.Classes = [.. v.Items], v => Playerbots.Population.PlayerbotPopulationOptions.CheckClasses(v.Items)),
        LivePlayerbotsValue("Population:Bands", o => new Playerbots.Population.PopulationBandList([.. o.Population.Bands.Select(b => b.Clone())]),
            (o, v) => o.Population.Bands = [.. v.Items.Select(b => b.Clone())], v => Playerbots.Population.PlayerbotPopulationOptions.CheckBands(v.Items)),
        LivePlayerbotsValue("Population:ZoneLevelSlack", o => o.Population.ZoneLevelSlack, (o, v) => o.Population.ZoneLevelSlack = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 10)),
        LivePlayerbotsValue("Population:ZoneLevelSlackMaxFloor", o => o.Population.ZoneLevelSlackMaxFloor, (o, v) => o.Population.ZoneLevelSlackMaxFloor = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 1, Playerbots.Population.PlayerbotPopulationOptions.MaxLevel)),
        LivePlayerbotsValue("Population:CreatureLevelMargin", o => o.Population.CreatureLevelMargin, (o, v) => o.Population.CreatureLevelMargin = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 10)),
        LivePlayerbotsValue("Population:CreatureSurveyRadius", o => o.Population.CreatureSurveyRadius, (o, v) => o.Population.CreatureSurveyRadius = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 300)),
        LivePlayerbotsValue("Population:SpawnsPerBot", o => o.Population.SpawnsPerBot, (o, v) => o.Population.SpawnsPerBot = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 100)),
        LivePlayerbotsValue("Population:ValueQuestWeight", o => o.Population.ValueQuestWeight, (o, v) => o.Population.ValueQuestWeight = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 100)),
        LivePlayerbotsValue("Population:ValueReputationWeight", o => o.Population.ValueReputationWeight, (o, v) => o.Population.ValueReputationWeight = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 100)),
        LivePlayerbotsValue("Population:ValueGearWeight", o => o.Population.ValueGearWeight, (o, v) => o.Population.ValueGearWeight = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 100)),
        LivePlayerbotsValue("Population:ValueFarmWeight", o => o.Population.ValueFarmWeight, (o, v) => o.Population.ValueFarmWeight = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 100)),
        LivePlayerbotsValue("Population:ValueEliteWeight", o => o.Population.ValueEliteWeight, (o, v) => o.Population.ValueEliteWeight = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 100)),
        LivePlayerbotsValue("Population:ValueTroubleWeight", o => o.Population.ValueTroubleWeight, (o, v) => o.Population.ValueTroubleWeight = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 100)),
        LivePlayerbotsValue("Population:ValueDistanceWeight", o => o.Population.ValueDistanceWeight, (o, v) => o.Population.ValueDistanceWeight = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 100)),
        LivePlayerbotsValue("Population:ValueQuestGrayMargin", o => o.Population.ValueQuestGrayMargin, (o, v) => o.Population.ValueQuestGrayMargin = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 20)),
        LivePlayerbotsValue("Population:ValueEliteSoloPct", o => o.Population.ValueEliteSoloPct, (o, v) => o.Population.ValueEliteSoloPct = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 100)),
        LivePlayerbotsValue("Population:ValueTopZones", o => o.Population.ValueTopZones, (o, v) => o.Population.ValueTopZones = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 1, 20)),
        LivePlayerbotsValue("Population:ValueZoneSoftCap", o => o.Population.ValueZoneSoftCap, (o, v) => o.Population.ValueZoneSoftCap = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 1000)),
        LivePlayerbotsValue("Population:ValueCapReach", o => o.Population.ValueCapReach, (o, v) => o.Population.ValueCapReach = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 20)),
        LivePlayerbotsValue("Population:ValueRelocateMinStayMinutes", o => o.Population.ValueRelocateMinStayMinutes, (o, v) => o.Population.ValueRelocateMinStayMinutes = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 1440)),
        LivePlayerbotsValue("Population:ValueRelocateBelowPct", o => o.Population.ValueRelocateBelowPct, (o, v) => o.Population.ValueRelocateBelowPct = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 100)),
        LivePlayerbotsValue("Population:ValueChecksPerPass", o => o.Population.ValueChecksPerPass, (o, v) => o.Population.ValueChecksPerPass = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 200)),
        LivePlayerbots("Population:ValueRouting", o => o.Population.ValueRouting, (o, v) => o.Population.ValueRouting = v),
        LivePlayerbotsValue("Population:OutlevelMargin", o => o.Population.OutlevelMargin, (o, v) => o.Population.OutlevelMargin = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 10)),
        LivePlayerbotsValue("Population:Relocation", o => o.Population.Relocation, (o, v) => o.Population.Relocation = v, Playerbots.Population.PlayerbotPopulationOptions.CheckDefined),
        LivePlayerbotsValue("Population:MaxRelocationsPerInterval", o => o.Population.MaxRelocationsPerInterval, (o, v) => o.Population.MaxRelocationsPerInterval = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 50)),
        LivePlayerbots("Population:FreshLaunch", o => o.Population.FreshLaunch, (o, v) => o.Population.FreshLaunch = v),
        LivePlayerbotsValue("Population:RushBrakeP95Ms", o => o.Population.RushBrakeP95Ms, (o, v) => o.Population.RushBrakeP95Ms = v, Playerbots.Population.PlayerbotPopulationOptions.CheckRushCeiling),
        LivePlayerbotsValue("Population:RushBrakeWindows", o => o.Population.RushBrakeWindows, (o, v) => o.Population.RushBrakeWindows = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 1, 20)),
        LivePlayerbotsValue("Population:RushBrakeWindowSeconds", o => o.Population.RushBrakeWindowSeconds, (o, v) => o.Population.RushBrakeWindowSeconds = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 1, 60)),
        LivePlayerbots("Population:FreshStartHome", o => o.Population.FreshStartHome, (o, v) => o.Population.FreshStartHome = v),
        LivePlayerbotsValue("Population:FreshStartHomeRadius", o => o.Population.FreshStartHomeRadius, (o, v) => o.Population.FreshStartHomeRadius = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 50, 5000)),
        LivePlayerbotsValue("Population:FreshGraduateLevel", o => o.Population.FreshGraduateLevel, (o, v) => o.Population.FreshGraduateLevel = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 2, 60)),
        LivePlayerbotsValue("Population:FreshZoneHomeRadius", o => o.Population.FreshZoneHomeRadius, (o, v) => o.Population.FreshZoneHomeRadius = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 50, 5000)),
        LivePlayerbotsValue("Population:FreshReleaseLevel", o => o.Population.FreshReleaseLevel, (o, v) => o.Population.FreshReleaseLevel = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 2, 60)),
        LivePlayerbotsValue("Population:FreshStartZoneCap", o => o.Population.FreshStartZoneCap, (o, v) => o.Population.FreshStartZoneCap = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 1000)),
        LivePlayerbotsValue("Population:LeaveHomeLevelMargin", o => o.Population.LeaveHomeLevelMargin, (o, v) => o.Population.LeaveHomeLevelMargin = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 10)),
        LivePlayerbotsValue("Population:TramMinLevel", o => o.Population.TramMinLevel, (o, v) => o.Population.TramMinLevel = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 60)),
        LivePlayerbots("Population:TownProgression", o => o.Population.TownProgression, (o, v) => o.Population.TownProgression = v),
        LivePlayerbotsValue("Population:TownHomeRadius", o => o.Population.TownHomeRadius, (o, v) => o.Population.TownHomeRadius = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 50, 5000)),
        LivePlayerbotsValue("Population:CapitalHomeRadius", o => o.Population.CapitalHomeRadius, (o, v) => o.Population.CapitalHomeRadius = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 50, 5000)),
        LivePlayerbotsValue("Population:TownCap", o => o.Population.TownCap, (o, v) => o.Population.TownCap = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 1000)),
        LivePlayerbotsValue("Population:CapitalMajorSharePct", o => o.Population.CapitalMajorSharePct, (o, v) => o.Population.CapitalMajorSharePct = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 100)),
        LivePlayerbotsValue("Population:TownWalkYards", o => o.Population.TownWalkYards, (o, v) => o.Population.TownWalkYards = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 100, 20000)),
        LivePlayerbotsValue("Population:SecondaryStayMinMinutes", o => o.Population.SecondaryStayMinMinutes, (o, v) => o.Population.SecondaryStayMinMinutes = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 5, 1440)),
        LivePlayerbotsValue("Population:SecondaryStayMaxMinutes", o => o.Population.SecondaryStayMaxMinutes, (o, v) => o.Population.SecondaryStayMaxMinutes = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 5, 1440)),
        LivePlayerbotsValue("Population:CapitalStayMinMinutes", o => o.Population.CapitalStayMinMinutes, (o, v) => o.Population.CapitalStayMinMinutes = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 1, 240)),
        LivePlayerbotsValue("Population:CapitalStayMaxMinutes", o => o.Population.CapitalStayMaxMinutes, (o, v) => o.Population.CapitalStayMaxMinutes = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 1, 240)),
        LivePlayerbotsValue("Population:TownBandMargin", o => o.Population.TownBandMargin, (o, v) => o.Population.TownBandMargin = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckRange(v, 0, 10)),
        LivePlayerbots("Population:TownProgressionTeleport", o => o.Population.TownProgressionTeleport, (o, v) => o.Population.TownProgressionTeleport = v),
        LivePlayerbotsValue("Population:TownCaps", o => o.Population.TownCaps, (o, v) => o.Population.TownCaps = v, v => Playerbots.Population.PlayerbotPopulationOptions.CheckCaps(v)),
        LivePlayerbots("Population:AdoptOperatorHomes", o => o.Population.AdoptOperatorHomes, (o, v) => o.Population.AdoptOperatorHomes = v),
        LivePlayerbotsValue("Population:ExcludedZones", o => new Playerbots.Population.PopulationIdList([.. o.Population.ExcludedZones]),
            (o, v) => o.Population.ExcludedZones = [.. v.Items], v => Playerbots.Population.PlayerbotPopulationOptions.CheckZones(v.Items)),

        // Not vmangos keys: bot guilds, guild chat and greetings (docs/areas/playbots-social.md). PlayerbotSocialFeature reads the shared
        // PlayerbotOptions.Social at every pass.
        LivePlayerbots("Social:Guilds:Enabled", o => o.Social.Guilds.Enabled, (o, v) => o.Social.Guilds.Enabled = v),
        LivePlayerbotsValue("Social:Guilds:MaxBotGuilds", o => o.Social.Guilds.MaxBotGuilds, (o, v) => o.Social.Guilds.MaxBotGuilds = v,
            v => v is >= 0 and <= Playerbots.BotSocial.PlayerbotSocialGuildOptions.MaxBotGuildsCeiling ? null : "must be 0..64"),
        LivePlayerbotsValue("Social:Guilds:TargetSize", o => o.Social.Guilds.TargetSize, (o, v) => o.Social.Guilds.TargetSize = v,
            v => v is >= Playerbots.BotSocial.PlayerbotSocialGuildOptions.MinTargetSize and <= Playerbots.BotSocial.PlayerbotSocialGuildOptions.MaxTargetSize
                ? null : "must be 2..500"),
        LivePlayerbotsValue("Social:Guilds:InvitePolicy", o => o.Social.Guilds.InvitePolicy, (o, v) => o.Social.Guilds.InvitePolicy = v,
            v => Enum.IsDefined(v) ? null : "must be None, GuildOrFriends, Anyone or Affinity"),
        LivePlayerbotsValue("Social:Guilds:Creation", o => o.Social.Guilds.Creation, (o, v) => o.Social.Guilds.Creation = v,
            v => Enum.IsDefined(v) ? null : "must be Auto, Charter or ClientCreate"),
        LivePlayerbots("Social:Guilds:AffinityInvites", o => o.Social.Guilds.AffinityInvites, (o, v) => o.Social.Guilds.AffinityInvites = v),
        LivePlayerbotsValue("Social:Guilds:GuildInviteAffinity", o => o.Social.Guilds.GuildInviteAffinity, (o, v) => o.Social.Guilds.GuildInviteAffinity = v, v => v is >= -100 and <= 100 ? null : "must be -100..100"),
        LivePlayerbotsValue("Social:Guilds:GuildInviteMinFamiliarity", o => o.Social.Guilds.GuildInviteMinFamiliarity, (o, v) => o.Social.Guilds.GuildInviteMinFamiliarity = v, v => v is >= 0 and <= 1000 ? null : "must be 0..1000"),
        LivePlayerbotsValue("Social:Guilds:GuildInviteCooldownDays", o => o.Social.Guilds.GuildInviteCooldownDays, (o, v) => o.Social.Guilds.GuildInviteCooldownDays = v, v => v is >= 0 and <= 365 ? null : "must be 0..365"),
        LivePlayerbotsValue("Social:Guilds:GuildAcceptAffinity", o => o.Social.Guilds.GuildAcceptAffinity, (o, v) => o.Social.Guilds.GuildAcceptAffinity = v, v => v is >= -100 and <= 100 ? null : "must be -100..100"),
        LivePlayerbots("Social:Lists:Enabled", o => o.Social.Lists.Enabled, (o, v) => o.Social.Lists.Enabled = v),
        LivePlayerbotsValue("Social:Lists:FriendAffinity", o => o.Social.Lists.FriendAffinity, (o, v) => o.Social.Lists.FriendAffinity = v, v => v is >= -100 and <= 100 ? null : "must be -100..100"),
        LivePlayerbotsValue("Social:Lists:FriendMinFamiliarity", o => o.Social.Lists.FriendMinFamiliarity, (o, v) => o.Social.Lists.FriendMinFamiliarity = v, v => v is >= 0 and <= 1000 ? null : "must be 0..1000"),
        LivePlayerbotsValue("Social:Lists:UnfriendAffinity", o => o.Social.Lists.UnfriendAffinity, (o, v) => o.Social.Lists.UnfriendAffinity = v, v => v is >= -100 and <= 100 ? null : "must be -100..100"),
        LivePlayerbotsValue("Social:Lists:IgnoreAffinity", o => o.Social.Lists.IgnoreAffinity, (o, v) => o.Social.Lists.IgnoreAffinity = v, v => v is >= -100 and <= 0 ? null : "must be -100..0"),
        LivePlayerbotsValue("Social:Lists:UnignoreAffinity", o => o.Social.Lists.UnignoreAffinity, (o, v) => o.Social.Lists.UnignoreAffinity = v, v => v is >= -100 and <= 0 ? null : "must be -100..0"),
        LivePlayerbotsValue("Social:Lists:ChangeSpacingSeconds", o => o.Social.Lists.ChangeSpacingSeconds, (o, v) => o.Social.Lists.ChangeSpacingSeconds = v, v => v is >= 1 and <= 3600 ? null : "must be 1..3600"),
        LivePlayerbots("Social:Helping:Enabled", o => o.Social.Helping.Enabled, (o, v) => o.Social.Helping.Enabled = v),
        LivePlayerbotsValue("Social:Helping:TroubleRadiusYards", o => o.Social.Helping.TroubleRadiusYards, (o, v) => o.Social.Helping.TroubleRadiusYards = v, v => float.IsFinite(v) && v is >= 1f and <= 100f ? null : "must be 1..100"),
        LivePlayerbots("Social:Helping:ResurrectStrangers", o => o.Social.Helping.ResurrectStrangers, (o, v) => o.Social.Helping.ResurrectStrangers = v),
        LivePlayerbots("Social:Helping:BuffStrangers", o => o.Social.Helping.BuffStrangers, (o, v) => o.Social.Helping.BuffStrangers = v),
        LivePlayerbotsValue("Social:Helping:BuffCooldownMinutes", o => o.Social.Helping.BuffCooldownMinutes, (o, v) => o.Social.Helping.BuffCooldownMinutes = v, v => v is >= 0 and <= 1440 ? null : "must be 0..1440"),
        LivePlayerbots("Social:Helping:AnswerHelp", o => o.Social.Helping.AnswerHelp, (o, v) => o.Social.Helping.AnswerHelp = v),
        LivePlayerbotsValue("Social:Helping:MaxAnswersPerCall", o => o.Social.Helping.MaxAnswersPerCall, (o, v) => o.Social.Helping.MaxAnswersPerCall = v, v => v is >= 1 and <= 16 ? null : "must be 1..16"),
        LivePlayerbotsValue("Social:Helping:ResurrectMaxCorpseMinutes", o => o.Social.Helping.ResurrectMaxCorpseMinutes, (o, v) => o.Social.Helping.ResurrectMaxCorpseMinutes = v, v => v is >= 1 and <= 60 ? null : "must be 1..60"),
        LivePlayerbots("Social:Gratitude:Enabled", o => o.Social.Gratitude.Enabled, (o, v) => o.Social.Gratitude.Enabled = v),
        LivePlayerbotsValue("Social:Gratitude:TipChance", o => o.Social.Gratitude.TipChance, (o, v) => o.Social.Gratitude.TipChance = v, v => float.IsFinite(v) && v is >= 0f and <= 1f ? null : "must be 0..1"),
        LivePlayerbotsValue("Social:Gratitude:MaxTipCopper", o => o.Social.Gratitude.MaxTipCopper, (o, v) => o.Social.Gratitude.MaxTipCopper = v, v => v is >= 0 and <= 1000000 ? null : "must be 0..1000000"),
        LivePlayerbots("Social:Mood:Enabled", o => o.Social.Mood.Enabled, (o, v) => o.Social.Mood.Enabled = v),
        LivePlayerbotsValue("Social:Mood:HalfLifeMinutes", o => o.Social.Mood.HalfLifeMinutes, (o, v) => o.Social.Mood.HalfLifeMinutes = v, v => v is >= 1 and <= 1440 ? null : "must be 1..1440"),
        LivePlayerbotsValue("Social:PassBudgetMs", o => o.Social.PassBudgetMs, (o, v) => o.Social.PassBudgetMs = v,
            v => double.IsFinite(v) && v is >= 0 and <= 1000 ? null : "must be 0..1000"),
        LivePlayerbots("Social:Initiative:Enabled",o => o.Social.Initiative.Enabled, (o, v) => o.Social.Initiative.Enabled = v),
        LivePlayerbotsValue("Social:Initiative:MaxOpeningsPerPass", o => o.Social.Initiative.MaxOpeningsPerPass, (o, v) => o.Social.Initiative.MaxOpeningsPerPass = v, v => v is >= 1 and <= 64 ? null : "must be 1..64"),
        LivePlayerbotsValue("Social:Initiative:OpeningCooldownMinutes", o => o.Social.Initiative.OpeningCooldownMinutes, (o, v) => o.Social.Initiative.OpeningCooldownMinutes = v, v => v is >= 1 and <= 1440 ? null : "must be 1..1440"),
        LivePlayerbots("Social:Initiative:CommentsEnabled", o => o.Social.Initiative.CommentsEnabled, (o, v) => o.Social.Initiative.CommentsEnabled = v),
        LivePlayerbotsValue("Social:Initiative:CommentBotGapMinutes", o => o.Social.Initiative.CommentBotGapMinutes, (o, v) => o.Social.Initiative.CommentBotGapMinutes = v, v => v is >= 1 and <= 1440 ? null : "must be 1..1440"),
        LivePlayerbotsValue("Social:Initiative:CommentZoneGapSeconds", o => o.Social.Initiative.CommentZoneGapSeconds, (o, v) => o.Social.Initiative.CommentZoneGapSeconds = v, v => v is >= 1 and <= 86400 ? null : "must be 1..86400"),
        LivePlayerbots("Social:Trading:Enabled", o => o.Social.Trading.Enabled, (o, v) => o.Social.Trading.Enabled = v),
        LivePlayerbots("Social:Trading:AnswerTrades", o => o.Social.Trading.AnswerTrades, (o, v) => o.Social.Trading.AnswerTrades = v),
        LivePlayerbots("Social:Trading:AcceptGifts", o => o.Social.Trading.AcceptGifts, (o, v) => o.Social.Trading.AcceptGifts = v),
        LivePlayerbotsValue("Social:Trading:TradeTimeoutSeconds", o => o.Social.Trading.TradeTimeoutSeconds, (o, v) => o.Social.Trading.TradeTimeoutSeconds = v,
            v => v is >= 10 and <= 300 ? null : "must be 10..300"),
        LivePlayerbotsValue("Social:Trading:ScamMemoryDays", o => o.Social.Trading.ScamMemoryDays, (o, v) => o.Social.Trading.ScamMemoryDays = v,
            v => v is >= 0 and <= 365 ? null : "must be 0..365"),
        LivePlayerbotsValue("Social:Trading:NinjaMemoryDays", o => o.Social.Trading.NinjaMemoryDays, (o, v) => o.Social.Trading.NinjaMemoryDays = v,
            v => v is >= 0 and <= 365 ? null : "must be 0..365"),
        LivePlayerbotsValue("Social:Trading:LowTrustPermille", o => o.Social.Trading.LowTrustPermille, (o, v) => o.Social.Trading.LowTrustPermille = v,
            v => v is >= 0 and <= 1000 ? null : "must be 0..1000"),
        LivePlayerbotsValue("Social:Trading:GiftMinValueCopper", o => o.Social.Trading.GiftMinValueCopper, (o, v) => o.Social.Trading.GiftMinValueCopper = v,
            v => v is >= 0 and <= 1000000 ? null : "must be 0..1000000"),
        LivePlayerbotsValue("Social:Trading:MaxGiftsReceivedPerHour", o => o.Social.Trading.MaxGiftsReceivedPerHour, (o, v) => o.Social.Trading.MaxGiftsReceivedPerHour = v,
            v => v is >= 0 and <= 120 ? null : "must be 0..120"),
        LivePlayerbots("Social:Trading:Deals:Enabled", o => o.Social.Trading.Deals.Enabled, (o, v) => o.Social.Trading.Deals.Enabled = v),
        LivePlayerbotsValue("Social:Trading:Deals:DealMinutes", o => o.Social.Trading.Deals.DealMinutes, (o, v) => o.Social.Trading.Deals.DealMinutes = v,
            v => v is >= 1 and <= 60 ? null : "must be 1..60"),
        LivePlayerbotsValue("Social:Trading:Deals:PairCooldownMinutes", o => o.Social.Trading.Deals.PairCooldownMinutes, (o, v) => o.Social.Trading.Deals.PairCooldownMinutes = v,
            v => v is >= 0 and <= 1440 ? null : "must be 0..1440"),
        LivePlayerbotsValue("Social:Trading:Deals:MaxDealCopper", o => o.Social.Trading.Deals.MaxDealCopper, (o, v) => o.Social.Trading.Deals.MaxDealCopper = v,
            v => v is >= 0 and <= 100000000 ? null : "must be 0..100000000"),
        LivePlayerbotsValue("Social:Trading:Deals:FriendDiscountPercent", o => o.Social.Trading.Deals.FriendDiscountPercent, (o, v) => o.Social.Trading.Deals.FriendDiscountPercent = v,
            v => v is >= 0 and <= 100 ? null : "must be 0..100"),
        LivePlayerbotsValue("Social:Trading:Deals:DislikeMarkupPercent", o => o.Social.Trading.Deals.DislikeMarkupPercent, (o, v) => o.Social.Trading.Deals.DislikeMarkupPercent = v,
            v => v is >= 0 and <= 200 ? null : "must be 0..200"),
        LivePlayerbotsValue("Social:Trading:Deals:PriceBookHours", o => o.Social.Trading.Deals.PriceBookHours, (o, v) => o.Social.Trading.Deals.PriceBookHours = v,
            v => v is >= 1 and <= 720 ? null : "must be 1..720"),
        LivePlayerbotsValue("Social:Trading:Deals:PriceBookMinSellers", o => o.Social.Trading.Deals.PriceBookMinSellers, (o, v) => o.Social.Trading.Deals.PriceBookMinSellers = v,
            v => v is >= 1 and <= 20 ? null : "must be 1..20"),
        LivePlayerbots("Social:Trading:Gifts:Enabled", o => o.Social.Trading.Gifts.Enabled, (o, v) => o.Social.Trading.Gifts.Enabled = v),
        LivePlayerbotsValue("Social:Trading:Gifts:GiftClaimSeconds", o => o.Social.Trading.Gifts.GiftClaimSeconds, (o, v) => o.Social.Trading.Gifts.GiftClaimSeconds = v,
            v => v is >= 5 and <= 120 ? null : "must be 5..120"),
        LivePlayerbotsValue("Social:Trading:Gifts:MaxGiftValueCopper", o => o.Social.Trading.Gifts.MaxGiftValueCopper, (o, v) => o.Social.Trading.Gifts.MaxGiftValueCopper = v,
            v => v is >= 0 and <= 1000000 ? null : "must be 0..1000000"),
        LivePlayerbotsValue("Social:Trading:Limits:MaxTradesPerBotPerHour", o => o.Social.Trading.Limits.MaxTradesPerBotPerHour, (o, v) => o.Social.Trading.Limits.MaxTradesPerBotPerHour = v,
            v => v is >= 0 and <= 120 ? null : "must be 0..120"),
        LivePlayerbotsValue("Social:Trading:Limits:MaxTradesPerAccountPerHour", o => o.Social.Trading.Limits.MaxTradesPerAccountPerHour, (o, v) => o.Social.Trading.Limits.MaxTradesPerAccountPerHour = v,
            v => v is >= 0 and <= 120 ? null : "must be 0..120"),
        LivePlayerbotsValue("Social:Trading:Limits:MaxConcurrentTrades", o => o.Social.Trading.Limits.MaxConcurrentTrades, (o, v) => o.Social.Trading.Limits.MaxConcurrentTrades = v,
            v => v is >= 0 and <= 256 ? null : "must be 0..256"),
        LivePlayerbotsValue("Social:Trading:Limits:DailyOutflowCopperPerBot", o => o.Social.Trading.Limits.DailyOutflowCopperPerBot, (o, v) => o.Social.Trading.Limits.DailyOutflowCopperPerBot = v,
            v => v is >= 0 and <= 100000000 ? null : "must be 0..100000000"),
        LivePlayerbotsValue("Social:Trading:Limits:DailyOutflowCopperPerAccount", o => o.Social.Trading.Limits.DailyOutflowCopperPerAccount, (o, v) => o.Social.Trading.Limits.DailyOutflowCopperPerAccount = v,
            v => v is >= 0 and <= 100000000 ? null : "must be 0..100000000"),
        LivePlayerbotsValue("Social:Trading:Limits:DailyOutflowCopperServer", o => o.Social.Trading.Limits.DailyOutflowCopperServer, (o, v) => o.Social.Trading.Limits.DailyOutflowCopperServer = v,
            v => v is >= 0 and <= 1000000000 ? null : "must be 0..1000000000"),
        LivePlayerbots("Social:Events:Enabled", o => o.Social.Events.Enabled, (o, v) => o.Social.Events.Enabled = v),
        LivePlayerbots("Social:Events:Holidays:Enabled", o => o.Social.Events.Holidays.Enabled, (o, v) => o.Social.Events.Holidays.Enabled = v),
        LivePlayerbotsValue("Social:Events:Holidays:CelebrateChance", o => o.Social.Events.Holidays.CelebrateChance, (o, v) => o.Social.Events.Holidays.CelebrateChance = v, v => v is >= 0 and <= 100 ? null : "must be 0..100"),
        LivePlayerbotsValue("Social:Events:Holidays:SpotRadiusYards", o => o.Social.Events.Holidays.SpotRadiusYards, (o, v) => o.Social.Events.Holidays.SpotRadiusYards = v, v => v is >= 20 and <= 1000 ? null : "must be 20..1000"),
        LivePlayerbotsValue("Social:Events:Holidays:SpotBackoffMinutes", o => o.Social.Events.Holidays.SpotBackoffMinutes, (o, v) => o.Social.Events.Holidays.SpotBackoffMinutes = v, v => v is >= 1 and <= 1440 ? null : "must be 1..1440"),
        LivePlayerbotsValue("Social:Events:Holidays:MaxPerGossipAnchor", o => o.Social.Events.Holidays.MaxPerGossipAnchor, (o, v) => o.Social.Events.Holidays.MaxPerGossipAnchor = v, v => v is >= 1 and <= 20 ? null : "must be 1..20"),
        LivePlayerbotsValue("Social:Events:Holidays:MaxPerServiceAnchor", o => o.Social.Events.Holidays.MaxPerServiceAnchor, (o, v) => o.Social.Events.Holidays.MaxPerServiceAnchor = v, v => v is >= 0 and <= 10 ? null : "must be 0..10"),
        LivePlayerbotsValue("Social:Events:Holidays:MaxPerTown", o => o.Social.Events.Holidays.MaxPerTown, (o, v) => o.Social.Events.Holidays.MaxPerTown = v, v => v is >= 1 and <= 200 ? null : "must be 1..200"),
        LivePlayerbotsValue("Social:Events:Holidays:EmoteMinSeconds", o => o.Social.Events.Holidays.EmoteMinSeconds, (o, v) => o.Social.Events.Holidays.EmoteMinSeconds = v, v => v is >= 5 and <= 3600 ? null : "must be 5..3600"),
        LivePlayerbotsValue("Social:Events:Holidays:EmoteMaxSeconds", o => o.Social.Events.Holidays.EmoteMaxSeconds, (o, v) => o.Social.Events.Holidays.EmoteMaxSeconds = v, v => v is >= 5 and <= 3600 ? null : "must be 5..3600"),
        LivePlayerbotsValue("Social:Events:Holidays:EmotesPerListenerMinute", o => o.Social.Events.Holidays.EmotesPerListenerMinute, (o, v) => o.Social.Events.Holidays.EmotesPerListenerMinute = v, v => v is >= 0 and <= 10 ? null : "must be 0..10"),
        LivePlayerbotsValue("Social:Events:Holidays:EmotesPerAnchorMinute", o => o.Social.Events.Holidays.EmotesPerAnchorMinute, (o, v) => o.Social.Events.Holidays.EmotesPerAnchorMinute = v, v => v is >= 0 and <= 30 ? null : "must be 0..30"),
        LivePlayerbots("Social:Events:Holidays:TalkEnabled", o => o.Social.Events.Holidays.TalkEnabled, (o, v) => o.Social.Events.Holidays.TalkEnabled = v),
        LivePlayerbotsValue("Social:Events:Holidays:TalkBotGapMinutes", o => o.Social.Events.Holidays.TalkBotGapMinutes, (o, v) => o.Social.Events.Holidays.TalkBotGapMinutes = v, v => v is >= 1 and <= 1440 ? null : "must be 1..1440"),
        LivePlayerbotsValue("Social:Events:Holidays:TalkZoneGapSeconds", o => o.Social.Events.Holidays.TalkZoneGapSeconds, (o, v) => o.Social.Events.Holidays.TalkZoneGapSeconds = v, v => v is >= 1 and <= 86400 ? null : "must be 1..86400"),
        LivePlayerbotsValue("Social:Events:Holidays:ExcludedEvents", o => o.Social.Events.Holidays.ExcludedEvents, (o, v) => o.Social.Events.Holidays.ExcludedEvents = v, Playerbots.BotSocial.PlayerbotHolidayOptions.CheckExcludedEvents),
        LivePlayerbots("Social:GuildChat:Enabled", o => o.Social.GuildChat.Enabled, (o, v) => o.Social.GuildChat.Enabled = v),
        LivePlayerbotsValue("Social:GuildChat:MessagesPerHour", o => o.Social.GuildChat.MessagesPerHour, (o, v) => o.Social.GuildChat.MessagesPerHour = v,
            v => v is >= 0 and <= Playerbots.BotSocial.PlayerbotSocialGuildChatOptions.MaxMessagesPerHour ? null : "must be 0..60"),
        LivePlayerbots("Social:Emotes:Enabled", o => o.Social.Emotes.Enabled, (o, v) => o.Social.Emotes.Enabled = v),
        LivePlayerbots("Social:Rivalry:Enabled", o => o.Social.Rivalry.Enabled, (o, v) => o.Social.Rivalry.Enabled = v),
        LivePlayerbots("Social:Rivalry:AllowOptOut", o => o.Social.Rivalry.AllowOptOut, (o, v) => o.Social.Rivalry.AllowOptOut = v),
        LivePlayerbots("Social:Rivalry:Emotes:Enabled", o => o.Social.Rivalry.Emotes.Enabled, (o, v) => o.Social.Rivalry.Emotes.Enabled = v),
        LivePlayerbots("Social:Rivalry:Emotes:RivalEmotes", o => o.Social.Rivalry.Emotes.RivalEmotes, (o, v) => o.Social.Rivalry.Emotes.RivalEmotes = v),
        LivePlayerbots("Social:Rivalry:Emotes:RudeAtBots", o => o.Social.Rivalry.Emotes.RudeAtBots, (o, v) => o.Social.Rivalry.Emotes.RudeAtBots = v),
        LivePlayerbots("Social:Rivalry:Emotes:RudeRequiresOptIn", o => o.Social.Rivalry.Emotes.RudeRequiresOptIn, (o, v) => o.Social.Rivalry.Emotes.RudeRequiresOptIn = v),
        LivePlayerbots("Social:Rivalry:Emotes:BotToBot", o => o.Social.Rivalry.Emotes.BotToBot, (o, v) => o.Social.Rivalry.Emotes.BotToBot = v),
        LivePlayerbotsValue("Social:Rivalry:Emotes:FriendlyAffinity", o => o.Social.Rivalry.Emotes.FriendlyAffinity, (o, v) => o.Social.Rivalry.Emotes.FriendlyAffinity = v,
            v => v is >= 0 and <= 100 ? null : "must be 0..100"),
        LivePlayerbotsValue("Social:Rivalry:Emotes:RangeYards", o => o.Social.Rivalry.Emotes.RangeYards, (o, v) => o.Social.Rivalry.Emotes.RangeYards = v,
            v => v is >= 5 and <= 25 ? null : "must be 5..25"),
        LivePlayerbotsValue("Social:Rivalry:Emotes:GreetStrangerChance", o => o.Social.Rivalry.Emotes.GreetStrangerChance, (o, v) => o.Social.Rivalry.Emotes.GreetStrangerChance = v,
            v => double.IsFinite(v) && v is >= 0.0 and <= 1.0 ? null : "must be 0..1"),
        LivePlayerbotsValue("Social:Rivalry:Emotes:RudePairGapMinutes", o => o.Social.Rivalry.Emotes.RudePairGapMinutes, (o, v) => o.Social.Rivalry.Emotes.RudePairGapMinutes = v,
            v => v is >= 1 and <= 1440 ? null : "must be 1..1440"),
        LivePlayerbotsValue("Social:Rivalry:Emotes:RudePerTargetMinutes", o => o.Social.Rivalry.Emotes.RudePerTargetMinutes, (o, v) => o.Social.Rivalry.Emotes.RudePerTargetMinutes = v,
            v => v is >= 1 and <= 1440 ? null : "must be 1..1440"),
        LivePlayerbotsValue("Social:Rivalry:Emotes:EmotesPerMinute", o => o.Social.Rivalry.Emotes.EmotesPerMinute, (o, v) => o.Social.Rivalry.Emotes.EmotesPerMinute = v,
            v => v is >= 0 and <= 120 ? null : "must be 0..120"),
        LivePlayerbotsValue("Social:Rivalry:Safeguards:SafeAreas", o => o.Social.Rivalry.Safeguards.SafeAreas, (o, v) => o.Social.Rivalry.Safeguards.SafeAreas = v,
            v => Playerbots.BotSocial.PlayerbotSocialRivalrySafeguardOptions.Check(v)),
        LivePlayerbots("Social:Memory:Enabled", o => o.Social.Memory.Enabled, (o, v) => o.Social.Memory.Enabled = v),
        LivePlayerbots("Social:Memory:CallForHelpEnabled", o => o.Social.Memory.CallForHelpEnabled, (o, v) => o.Social.Memory.CallForHelpEnabled = v),
        LivePlayerbotsValue("Social:Memory:CallForHelpBaseChance", o => o.Social.Memory.CallForHelpBaseChance, (o, v) => o.Social.Memory.CallForHelpBaseChance = v,
            v => float.IsFinite(v) && v is >= 0f and <= 1f ? null : "must be 0..1"),
        LivePlayerbotsValue("Social:Memory:CallForHelpSociabilityWeight", o => o.Social.Memory.CallForHelpSociabilityWeight, (o, v) => o.Social.Memory.CallForHelpSociabilityWeight = v,
            v => float.IsFinite(v) && v is >= 0f and <= 1f ? null : "must be 0..1"),
        LivePlayerbots("Social:Memory:AnswerHelpEnabled", o => o.Social.Memory.AnswerHelpEnabled, (o, v) => o.Social.Memory.AnswerHelpEnabled = v),
        LivePlayerbotsValue("Social:Memory:MaxRelationsPerBot", o => o.Social.Memory.MaxRelationsPerBot, (o, v) => o.Social.Memory.MaxRelationsPerBot = v,
            v => v is >= 16 and <= 1024 ? null : "must be 16..1024"),
        LivePlayerbotsValue("Social:Memory:AffinityPerChat", o => o.Social.Memory.AffinityPerChat, (o, v) => o.Social.Memory.AffinityPerChat = v,
            v => v is >= 0 and <= 20 ? null : "must be 0..20"),
        LivePlayerbotsValue("Social:Memory:AffinityPerGroup", o => o.Social.Memory.AffinityPerGroup, (o, v) => o.Social.Memory.AffinityPerGroup = v,
            v => v is >= 0 and <= 30 ? null : "must be 0..30"),
        LivePlayerbotsValue("Social:Memory:AffinityPerGroupSuccess", o => o.Social.Memory.AffinityPerGroupSuccess, (o, v) => o.Social.Memory.AffinityPerGroupSuccess = v,
            v => v is >= 0 and <= 30 ? null : "must be 0..30"),
        LivePlayerbotsValue("Social:Memory:AffinityPerTrade", o => o.Social.Memory.AffinityPerTrade, (o, v) => o.Social.Memory.AffinityPerTrade = v,
            v => v is >= 0 and <= 30 ? null : "must be 0..30"),
        LivePlayerbotsValue("Social:Memory:AffinityPerHelp", o => o.Social.Memory.AffinityPerHelp, (o, v) => o.Social.Memory.AffinityPerHelp = v,
            v => v is >= 0 and <= 50 ? null : "must be 0..50"),
        LivePlayerbotsValue("Social:Memory:AffinityPerStrike", o => o.Social.Memory.AffinityPerStrike, (o, v) => o.Social.Memory.AffinityPerStrike = v,
            v => v is >= -100 and <= 0 ? null : "must be -100..0"),
        LivePlayerbotsValue("Social:Memory:AffinityPerKick", o => o.Social.Memory.AffinityPerKick, (o, v) => o.Social.Memory.AffinityPerKick = v,
            v => v is >= -100 and <= 0 ? null : "must be -100..0"),
        LivePlayerbotsValue("Social:Memory:FamiliarityHalfLifeDays", o => o.Social.Memory.FamiliarityHalfLifeDays, (o, v) => o.Social.Memory.FamiliarityHalfLifeDays = v,
            v => v is >= 1 and <= 365 ? null : "must be 1..365"),
        LivePlayerbotsValue("Social:Memory:AffinityPerKilledBy", o => o.Social.Memory.AffinityPerKilledBy, (o, v) => o.Social.Memory.AffinityPerKilledBy = v,
            v => v is >= -100 and <= 0 ? null : "must be -100..0"),
        LivePlayerbotsValue("Social:Memory:AffinityPerKilledThem", o => o.Social.Memory.AffinityPerKilledThem, (o, v) => o.Social.Memory.AffinityPerKilledThem = v,
            v => v is >= -100 and <= 0 ? null : "must be -100..0"),
        LivePlayerbotsValue("Social:Memory:AffinityPerFriendlyEmote", o => o.Social.Memory.AffinityPerFriendlyEmote, (o, v) => o.Social.Memory.AffinityPerFriendlyEmote = v,
            v => v is >= 0 and <= 20 ? null : "must be 0..20"),
        LivePlayerbotsValue("Social:Memory:AffinityPerRudeEmote", o => o.Social.Memory.AffinityPerRudeEmote, (o, v) => o.Social.Memory.AffinityPerRudeEmote = v,
            v => v is >= -100 and <= 0 ? null : "must be -100..0"),
        LivePlayerbotsValue("Social:Memory:RivalAffinity", o => o.Social.Memory.RivalAffinity, (o, v) => o.Social.Memory.RivalAffinity = v,
            v => v is >= -100 and <= 0 ? null : "must be -100..0"),
        LivePlayerbotsValue("Social:Memory:ChatFamiliarityCooldownMinutes", o => o.Social.Memory.ChatFamiliarityCooldownMinutes, (o, v) => o.Social.Memory.ChatFamiliarityCooldownMinutes = v,
            v => v is >= 0 and <= 1440 ? null : "must be 0..1440"),
        LivePlayerbotsValue("Social:Memory:AffinityPerWatchedDie", o => o.Social.Memory.AffinityPerWatchedDie, (o, v) => o.Social.Memory.AffinityPerWatchedDie = v,
            v => v is >= -100 and <= 0 ? null : "must be -100..0"),
        LivePlayerbotsValue("Social:Memory:BystanderRadiusYards", o => o.Social.Memory.BystanderRadiusYards, (o, v) => o.Social.Memory.BystanderRadiusYards = v,
            v => v is >= 5 and <= 60 ? null : "must be 5..60"),
        LivePlayerbotsValue("Social:Memory:BystanderMinHealthPercent", o => o.Social.Memory.BystanderMinHealthPercent, (o, v) => o.Social.Memory.BystanderMinHealthPercent = v,
            v => v is >= 1 and <= 100 ? null : "must be 1..100"),
        LivePlayerbotsValue("Social:Memory:BystanderMinSeconds", o => o.Social.Memory.BystanderMinSeconds, (o, v) => o.Social.Memory.BystanderMinSeconds = v,
            v => v is >= 1 and <= 60 ? null : "must be 1..60"),
        LivePlayerbotsValue("Social:Memory:AffinityHalfLifeDays", o => o.Social.Memory.AffinityHalfLifeDays, (o, v) => o.Social.Memory.AffinityHalfLifeDays = v,
            v => v is >= 1 and <= 365 ? null : "must be 1..365"),
        LivePlayerbotsValue("Social:Memory:RivalHalfLifeDays", o => o.Social.Memory.RivalHalfLifeDays, (o, v) => o.Social.Memory.RivalHalfLifeDays = v,
            v => v is >= 1 and <= 3650 ? null : "must be 1..3650"),
        LivePlayerbotsValue("Social:Memory:AffinityPerApology", o => o.Social.Memory.AffinityPerApology, (o, v) => o.Social.Memory.AffinityPerApology = v,
            v => v is >= 0 and <= 50 ? null : "must be 0..50"),
        LivePlayerbotsValue("Social:Memory:MaxDailyAffinityGainPerPair", o => o.Social.Memory.MaxDailyAffinityGainPerPair, (o, v) => o.Social.Memory.MaxDailyAffinityGainPerPair = v,
            v => v is >= 0 and <= 200 ? null : "must be 0..200"),
        LivePlayerbotsValue("Social:Memory:AffinityPerNinjaLoot", o => o.Social.Memory.AffinityPerNinjaLoot, (o, v) => o.Social.Memory.AffinityPerNinjaLoot = v,
            v => v is >= -100 and <= 0 ? null : "must be -100..0"),
        LivePlayerbotsValue("Social:Memory:AffinityPerKillSteal", o => o.Social.Memory.AffinityPerKillSteal, (o, v) => o.Social.Memory.AffinityPerKillSteal = v,
            v => v is >= -100 and <= 0 ? null : "must be -100..0"),
        LivePlayerbotsValue("Social:Memory:AffinityPerAbandonedRun", o => o.Social.Memory.AffinityPerAbandonedRun, (o, v) => o.Social.Memory.AffinityPerAbandonedRun = v,
            v => v is >= -100 and <= 0 ? null : "must be -100..0"),
        LivePlayerbotsValue("Social:Memory:TrustPerGroupSuccess", o => o.Social.Memory.TrustPerGroupSuccess, (o, v) => o.Social.Memory.TrustPerGroupSuccess = v,
            v => v is >= -500 and <= 500 ? null : "must be -500..500"),
        LivePlayerbotsValue("Social:Memory:TrustPerFirstDeathInWipe", o => o.Social.Memory.TrustPerFirstDeathInWipe, (o, v) => o.Social.Memory.TrustPerFirstDeathInWipe = v,
            v => v is >= -500 and <= 500 ? null : "must be -500..500"),
        LivePlayerbots("Social:Memory:OrganisedPvpKills", o => o.Social.Memory.OrganisedPvpKills, (o, v) => o.Social.Memory.OrganisedPvpKills = v),
        LivePlayerbotsValue("Social:Memory:AffinityPerFairDeal", o => o.Social.Memory.AffinityPerFairDeal, (o, v) => o.Social.Memory.AffinityPerFairDeal = v,
            v => v is >= 0 and <= 30 ? null : "must be 0..30"),
        LivePlayerbotsValue("Social:Memory:AffinityPerGiftReceived", o => o.Social.Memory.AffinityPerGiftReceived, (o, v) => o.Social.Memory.AffinityPerGiftReceived = v,
            v => v is >= 0 and <= 30 ? null : "must be 0..30"),
        LivePlayerbotsValue("Social:Memory:AffinityPerTradeScam", o => o.Social.Memory.AffinityPerTradeScam, (o, v) => o.Social.Memory.AffinityPerTradeScam = v,
            v => v is >= -100 and <= 0 ? null : "must be -100..0"),
        LivePlayerbotsValue("Social:Memory:AffinityPerDealFlaked", o => o.Social.Memory.AffinityPerDealFlaked, (o, v) => o.Social.Memory.AffinityPerDealFlaked = v,
            v => v is >= -100 and <= 0 ? null : "must be -100..0"),
        LivePlayerbotsValue("Social:Memory:TrustPerFairDeal", o => o.Social.Memory.TrustPerFairDeal, (o, v) => o.Social.Memory.TrustPerFairDeal = v,
            v => v is >= -500 and <= 500 ? null : "must be -500..500"),
        LivePlayerbotsValue("Social:Memory:TrustPerTradeScam", o => o.Social.Memory.TrustPerTradeScam, (o, v) => o.Social.Memory.TrustPerTradeScam = v,
            v => v is >= -500 and <= 500 ? null : "must be -500..500"),
        LivePlayerbots("Social:WarMode:Enabled", o => o.Social.WarMode.Enabled, (o, v) => o.Social.WarMode.Enabled = v),
        LivePlayerbotsValue("Social:WarMode:NegativeMultiplier", o => o.Social.WarMode.NegativeMultiplier, (o, v) => o.Social.WarMode.NegativeMultiplier = v,
            v => float.IsFinite(v) && v is >= 1f and <= 10f ? null : "must be 1..10"),
        LivePlayerbots("Social:WarMode:NeverForget", o => o.Social.WarMode.NeverForget, (o, v) => o.Social.WarMode.NeverForget = v),
        LivePlayerbots("Social:WarMode:RemoveCaps", o => o.Social.WarMode.RemoveCaps, (o, v) => o.Social.WarMode.RemoveCaps = v),
        LivePlayerbots("Social:WarMode:ClearOnDisable", o => o.Social.WarMode.ClearOnDisable, (o, v) => o.Social.WarMode.ClearOnDisable = v),

        // Not vmangos keys: bots using the faction auction house (docs/areas/playbots-economy.md). PlayerbotEconomy reads the shared
        // PlayerbotOptions.Economy at every decision.
        LivePlayerbots("Economy:Enabled", o => o.Economy.Enabled, (o, v) => o.Economy.Enabled = v),
        LivePlayerbotsValue("Economy:ListChancePerVisit", o => o.Economy.ListChancePerVisit, (o, v) => o.Economy.ListChancePerVisit = v,
            v => v is >= 0 and <= 100 ? null : "must be 0..100"),
        LivePlayerbotsValue("Economy:MaxListingsPerBot", o => o.Economy.MaxListingsPerBot, (o, v) => o.Economy.MaxListingsPerBot = v,
            v => v is >= 0 and <= PlayerbotEconomyOptions.MaxListingsCeiling ? null : $"must be 0..{PlayerbotEconomyOptions.MaxListingsCeiling}"),
        LivePlayerbotsValue("Economy:BuyUpgradeBudgetFraction", o => o.Economy.BuyUpgradeBudgetFraction, (o, v) => o.Economy.BuyUpgradeBudgetFraction = v,
            v => float.IsFinite(v) && v is >= 0f and <= 1f ? null : "must be 0..1"),
        LivePlayerbotsValue("Economy:VisitIntervalMinutes", o => o.Economy.VisitIntervalMinutes, (o, v) => o.Economy.VisitIntervalMinutes = v,
            v => v is >= 1 and <= PlayerbotEconomyOptions.MaxVisitIntervalMinutes ? null : $"must be 1..{PlayerbotEconomyOptions.MaxVisitIntervalMinutes}"),
        LivePlayerbots("Economy:BankDeposit", o => o.Economy.BankDeposit, (o, v) => o.Economy.BankDeposit = v),
        LivePlayerbots("Economy:BuyBankSlots", o => o.Economy.BuyBankSlots, (o, v) => o.Economy.BuyBankSlots = v),
        // Not vmangos keys: how bots plan their money and ride (docs/areas/playbots-economy.md). PlayerbotBudget and PlayerbotMount read the
        // shared PlayerbotOptions.Budget / Mounts at every decision.
        LivePlayerbots("Budget:Enabled", o => o.Budget.Enabled, (o, v) => o.Budget.Enabled = v),
        LivePlayerbotsValue("Budget:ClassLevelsAhead", o => o.Budget.ClassLevelsAhead, (o, v) => o.Budget.ClassLevelsAhead = v,
            v => v is >= 0 and <= PlayerbotBudgetOptions.MaxClassLevelsAhead ? null : $"must be 0..{PlayerbotBudgetOptions.MaxClassLevelsAhead}"),
        LivePlayerbotsValue("Budget:EssentialsAllowanceCopper", o => o.Budget.EssentialsAllowanceCopper, (o, v) => o.Budget.EssentialsAllowanceCopper = v,
            v => v <= 100_000_000u ? null : "must be 0..100000000"),
        LivePlayerbotsValue("Budget:RefreshSeconds", o => o.Budget.RefreshSeconds, (o, v) => o.Budget.RefreshSeconds = v,
            v => v is >= 1 and <= 3600 ? null : "must be 1..3600"),
        LivePlayerbotsValue("Budget:DiscretionaryFloorFraction", o => o.Budget.DiscretionaryFloorFraction, (o, v) => o.Budget.DiscretionaryFloorFraction = v,
            v => float.IsFinite(v) && v is >= 0f and <= 1f ? null : "must be 0..1"),
        LivePlayerbots("Mounts:Enabled", o => o.Mounts.Enabled, (o, v) => o.Mounts.Enabled = v),
        LivePlayerbotsValue("Mounts:SaveLevelsAhead", o => o.Mounts.SaveLevelsAhead, (o, v) => o.Mounts.SaveLevelsAhead = v,
            v => v is >= 0 and <= PlayerbotMountOptions.MaxSaveLevelsAhead ? null : $"must be 0..{PlayerbotMountOptions.MaxSaveLevelsAhead}"),
        LivePlayerbotsValue("Mounts:MountMinTravelYards", o => o.Mounts.MountMinTravelYards, (o, v) => o.Mounts.MountMinTravelYards = v,
            v => float.IsFinite(v) && v is >= 1f and <= 100000f ? null : "must be 1..100000"),
        // Not vmangos keys: autonomous bots in battlegrounds (docs/areas/playbots-battlegrounds.md). PlayerbotBattlegroundCoordinator
        // reads the shared PlayerbotOptions.Battlegrounds at every decision.
        LivePlayerbots("Battlegrounds:Enabled", o => o.Battlegrounds.Enabled, (o, v) => o.Battlegrounds.Enabled = v),
        LivePlayerbotsValue("Battlegrounds:QueueChancePerMinute", o => o.Battlegrounds.QueueChancePerMinute, (o, v) => o.Battlegrounds.QueueChancePerMinute = v,
            v => float.IsFinite(v) && v is >= 0f and <= 1f ? null : "must be 0..1"),
        LivePlayerbotsValue("Battlegrounds:MaxBotsPerBattleground", o => o.Battlegrounds.MaxBotsPerBattleground, (o, v) => o.Battlegrounds.MaxBotsPerBattleground = v,
            v => v is >= 0 and <= 80 ? null : "must be 0..80"),
        LivePlayerbots("Battlegrounds:PreferFillingRealPlayerQueues", o => o.Battlegrounds.PreferFillingRealPlayerQueues, (o, v) => o.Battlegrounds.PreferFillingRealPlayerQueues = v),
        LivePlayerbotsValue("Battlegrounds:MaxTravelYards", o => o.Battlegrounds.MaxTravelYards, (o, v) => o.Battlegrounds.MaxTravelYards = v,
            v => v is >= 100 and <= 20_000 ? null : "must be 100..20000"),
        LivePlayerbotsValue("Battlegrounds:MaxQueueWaitMinutes", o => o.Battlegrounds.MaxQueueWaitMinutes, (o, v) => o.Battlegrounds.MaxQueueWaitMinutes = v,
            v => v is >= 1 and <= 240 ? null : "must be 1..240"),

        // Not vmangos keys: adversarial chaos bots (docs/areas/playbots.md, Chaos bots). PlayerbotChaosEngine reads the shared
        // PlayerbotOptions.Chaos at every think.
        LivePlayerbots("Chaos:Enabled", o => o.Chaos.Enabled, (o, v) => o.Chaos.Enabled = v),
        LivePlayerbotsValue("Chaos:ActiveChaosBots", o => o.Chaos.ActiveChaosBots, (o, v) => o.Chaos.ActiveChaosBots = v,
            Playerbots.Chaos.PlayerbotChaosOptions.CheckActiveBots),
        LivePlayerbotsValue("Chaos:ProbesPerThink", o => o.Chaos.ProbesPerThink, (o, v) => o.Chaos.ProbesPerThink = v,
            Playerbots.Chaos.PlayerbotChaosOptions.CheckProbesPerThink),
        LivePlayerbotsValue("Chaos:Seed", o => o.Chaos.Seed, (o, v) => o.Chaos.Seed = v, _ => null),
        LivePlayerbots("Chaos:MalformedPayloads", o => o.Chaos.MalformedPayloads, (o, v) => o.Chaos.MalformedPayloads = v),
        LivePlayerbots("Chaos:StateRaces", o => o.Chaos.StateRaces, (o, v) => o.Chaos.StateRaces = v),
        LivePlayerbots("Chaos:BoundaryMovement", o => o.Chaos.BoundaryMovement, (o, v) => o.Chaos.BoundaryMovement = v),

        // Bot chat safety (docs/areas/playbots.md, Safety and provider policies): read from the same shared object at every line.
        LivePlayerbots("Chat:Safety:Enabled", o => o.Chat.Safety.Enabled, (o, v) => o.Chat.Safety.Enabled = v),
        LivePlayerbotsValue("Chat:Safety:Categories", o => o.Chat.Safety.Categories, (o, v) => o.Chat.Safety.Categories = v,
            Playerbots.Chat.PlayerbotChatSafetyOptions.CheckCategories),
        LivePlayerbotsValue("Chat:Safety:TermsFile", o => o.Chat.Safety.TermsFile, (o, v) => o.Chat.Safety.TermsFile = v,
            Playerbots.Chat.PlayerbotChatSafetyOptions.CheckTermsFile),
        LivePlayerbots("Chat:Safety:ReplaceDefaultTerms", o => o.Chat.Safety.ReplaceDefaultTerms, (o, v) => o.Chat.Safety.ReplaceDefaultTerms = v),
        LivePlayerbotsValue("Chat:Safety:OnFlagged", o => o.Chat.Safety.OnFlagged, (o, v) => o.Chat.Safety.OnFlagged = v,
            v => Enum.IsDefined(v) ? null : "must be BuiltinReply or Ignore"),
        LivePlayerbots("Chat:Safety:ScreenOutput", o => o.Chat.Safety.ScreenOutput, (o, v) => o.Chat.Safety.ScreenOutput = v),
        LivePlayerbotsValue("Chat:Safety:OnOutputFlagged", o => o.Chat.Safety.OnOutputFlagged, (o, v) => o.Chat.Safety.OnOutputFlagged = v,
            v => Enum.IsDefined(v) ? null : "must be BuiltinReply or Drop"),
        LivePlayerbotsValue("Chat:Safety:StrikesBeforeCutoff", o => o.Chat.Safety.StrikesBeforeCutoff, (o, v) => o.Chat.Safety.StrikesBeforeCutoff = v,
            v => v is >= 1 and <= 100 ? null : "must be 1..100"),
        LivePlayerbotsValue("Chat:Safety:StrikeWindowMinutes", o => o.Chat.Safety.StrikeWindowMinutes, (o, v) => o.Chat.Safety.StrikeWindowMinutes = v,
            Playerbots.Chat.PlayerbotChatSafetyOptions.CheckMinutes),
        LivePlayerbotsValue("Chat:Safety:CutoffMinutes", o => o.Chat.Safety.CutoffMinutes, (o, v) => o.Chat.Safety.CutoffMinutes = v,
            Playerbots.Chat.PlayerbotChatSafetyOptions.CheckMinutes),
        LivePlayerbots("Chat:Safety:AutoMute", o => o.Chat.Safety.AutoMute, (o, v) => o.Chat.Safety.AutoMute = v),
        LivePlayerbotsValue("Chat:Safety:AutoMuteMinutes", o => o.Chat.Safety.AutoMuteMinutes, (o, v) => o.Chat.Safety.AutoMuteMinutes = v,
            Playerbots.Chat.PlayerbotChatSafetyOptions.CheckMinutes),
        LivePlayerbotsValue("Chat:Safety:FlagLogSize", o => o.Chat.Safety.FlagLogSize, (o, v) => o.Chat.Safety.FlagLogSize = v,
            v => v is >= 0 and <= 10_000 ? null : "must be 0..10000"),
        LivePlayerbots("Chat:Safety:StoreExcerpt", o => o.Chat.Safety.StoreExcerpt, (o, v) => o.Chat.Safety.StoreExcerpt = v),
        LivePlayerbots("Chat:Safety:Disclosure", o => o.Chat.Safety.Disclosure, (o, v) => o.Chat.Safety.Disclosure = v),
        LivePlayerbotsValue("Chat:Safety:DisclosureText", o => o.Chat.Safety.DisclosureText, (o, v) => o.Chat.Safety.DisclosureText = v,
            Playerbots.Chat.PlayerbotChatSafetyOptions.CheckDisclosure),
        LivePlayerbots("Chat:Safety:AllowOptOut", o => o.Chat.Safety.AllowOptOut, (o, v) => o.Chat.Safety.AllowOptOut = v),
        LivePlayerbots("Chat:Safety:RequireOptIn", o => o.Chat.Safety.RequireOptIn, (o, v) => o.Chat.Safety.RequireOptIn = v),
        LivePlayerbots("Chat:Safety:StripPersonalData", o => o.Chat.Safety.StripPersonalData, (o, v) => o.Chat.Safety.StripPersonalData = v),

        // The player speed rates (non-retail when not 1; the MaNGOS Zero fork's Movement.*SpeedRate): UnitSpeed.SetRate reads them through the
        // player's copy, which the reload refreshes for every online player (ConfigContentReloadable re-sends the speeds after these keys).
        LiveSpeedRate("PlayerSpeedRate", o => o.PlayerSpeedRate, (o, v) => o.PlayerSpeedRate = v),
        LiveSpeedRate("PlayerRunSpeedRate", o => o.PlayerRunSpeedRate, (o, v) => o.PlayerRunSpeedRate = v),
        LiveSpeedRate("PlayerRunBackSpeedRate", o => o.PlayerRunBackSpeedRate, (o, v) => o.PlayerRunBackSpeedRate = v),
        LiveSpeedRate("PlayerSwimSpeedRate", o => o.PlayerSwimSpeedRate, (o, v) => o.PlayerSwimSpeedRate = v),
        LiveSpeedRate("PlayerSwimBackSpeedRate", o => o.PlayerSwimBackSpeedRate, (o, v) => o.PlayerSwimBackSpeedRate = v),
        LiveSpeedRate("PlayerWalkSpeedRate", o => o.PlayerWalkSpeedRate, (o, v) => o.PlayerWalkSpeedRate = v),
        LiveSpeedRate("PlayerTurnRate", o => o.PlayerTurnRate, (o, v) => o.PlayerTurnRate = v),
    ];

    /// <summary>True for the keys whose change must re-send the speeds of the online players (<see cref="LocomotionOptions"/> keys).</summary>
    public static bool IsSpeedRate(WorldConfigKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Path.StartsWith(LocomotionOptions.SectionName + ":", StringComparison.Ordinal);
    }

    private static WorldConfigKey LiveSpeedRate(string path, Func<LocomotionOptions, float> get, Action<LocomotionOptions, float> set)
        => new(
            $"{LocomotionOptions.SectionName}:{path}",
            v => v.Locomotion is { } locomotion ? get(locomotion) : null,
            (view, v) => set(view.Locomotion!, (float)v!),
            v => (float)v! is >= LocomotionOptions.MinSpeedRate and <= LocomotionOptions.MaxSpeedRate
                ? null
                : $"must be between {LocomotionOptions.MinSpeedRate.ToString(CultureInfo.InvariantCulture)} and {LocomotionOptions.MaxSpeedRate.ToString(CultureInfo.InvariantCulture)}");

    private static string? NonNegative<T>(T value) where T : struct, IComparable<T>
        => value.CompareTo(default) < 0 ? "must not be negative" : null;

    private static string? DefinedSecurity(AccountSecurity value)
        => Enum.IsDefined(value) ? null : "is not an account security level";

    /// <summary>A numeric key vmangos reads with setConfigPos/setConfigMin: a negative value falls back to the default (World.cpp:2949-2977).</summary>
    private static WorldConfigKey LiveNonNegative<T>(string path, Func<WorldRuntimeOptions, T> get, Action<WorldRuntimeOptions, T> set) where T : struct, IComparable<T>
        => new(
            $"{Root}:{path}",
            v => get(v.Runtime),
            (view, v) => set(view.Runtime, (T)v!),
            v => NonNegative((T)v!),
            negativeUsesDefault: true);

    private static WorldConfigKey Live<T>(string path, Func<WorldRuntimeOptions, T> get, Action<WorldRuntimeOptions, T> set, Func<T, string?>? check = null)
        => new(
            $"{Root}:{path}",
            v => get(v.Runtime),
            (view, v) => set(view.Runtime, (T)v!),
            check is null ? null : v => check((T)v!));

    private static WorldConfigKey LiveSocialCount(string path, Func<SocialOptions, int> get, Action<SocialOptions, int> set)
        => new(
            $"{SocialOptions.SectionName}:{path}",
            v => v.Social is { } social ? get(social) : null,
            (view, v) => set(view.Social!, (int)v!),
            v => NonNegative((int)v!));

    private static WorldConfigKey LiveSocial(string path, Func<SocialOptions, bool> get, Action<SocialOptions, bool> set)
        => new(
            $"{SocialOptions.SectionName}:{path}",
            v => v.Social is { } social ? get(social) : null,
            (view, v) => set(view.Social!, (bool)v!),
            null);

    private static WorldConfigKey LivePlayerbots(string path, Func<PlayerbotOptions, bool> get, Action<PlayerbotOptions, bool> set)
        => new(
            $"{PlayerbotOptions.SectionName}:{path}",
            v => v.Playerbots is { } playerbots ? get(playerbots) : null,
            (view, v) => set(view.Playerbots!, (bool)v!),
            null);

    /// <summary>A live playerbot key of another type, with its range check (a reload with a value outside it is refused).</summary>
    private static WorldConfigKey LivePlayerbotsValue<T>(string path, Func<PlayerbotOptions, T> get, Action<PlayerbotOptions, T> set,
        Func<T, string?> check)
        => new(
            $"{PlayerbotOptions.SectionName}:{path}",
            v => v.Playerbots is { } playerbots ? get(playerbots) : null,
            (view, v) => set(view.Playerbots!, (T)v!),
            v => v is T value ? check(value) : null);

    private static WorldConfigKey Fixed(string path, Func<WorldConfigView, object?> read)
        => new($"{Root}:{path}", read, null, null);

    private static WorldConfigKey FixedListener(string path, Func<WorldOptions, object?> read)
        => new($"{Root}:{path}", v => v.Listener is { } listener ? read(listener) : null, null, null);

    private static WorldConfigKey FixedPlayerbots(string path, Func<PlayerbotOptions, object?> read)
        => new($"{PlayerbotOptions.SectionName}:{path}", v => v.Playerbots is { } playerbots ? read(playerbots) : null, null, null);
}
