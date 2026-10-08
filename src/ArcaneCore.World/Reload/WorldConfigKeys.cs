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
        Fixed("Maps:DataDirectory", v => v.Runtime.Maps.DataDirectory),
        FixedListener("Port", v => v.Port),
        FixedListener("BindAddress", v => v.BindAddress),

        // Connection caps (security hardening lane): the listener's ConnectionLimiter is built at start.
        FixedListener("MaxConnections", v => v.MaxConnections),
        FixedListener("MaxConnectionsPerIp", v => v.MaxConnectionsPerIp),

        // Live: read from the shared options object at each use.
        LiveNonNegative("UpdateCompressionThreshold", o => o.UpdateCompressionThreshold, (o, v) => o.UpdateCompressionThreshold = v),
        LiveNonNegative("AutosaveIntervalMs", o => o.AutosaveIntervalMs, (o, v) => o.AutosaveIntervalMs = v),
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
        LiveNonNegative("Maps:GridCleanUpDelayMs", o => o.Maps.GridCleanUpDelayMs, (o, v) => o.Maps.GridCleanUpDelayMs = v),
        LiveNonNegative("Maps:GridActivationDistance", o => o.Maps.GridActivationDistance, (o, v) => o.Maps.GridActivationDistance = v),

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

        // Not a vmangos key: how bot movement reaches the world (client packets through the handlers, or applied by the server).
        // PlayerbotMotion reads the shared PlayerbotOptions at every movement packet. The playerbot options not listed here are read at start.
        LivePlayerbots("MovementPackets", o => o.MovementPackets, (o, v) => o.MovementPackets = v),

        // Not vmangos keys: risk against reward and retreat (docs/areas/playbots-risk.md). PlayerbotRisk, PlayerbotRiskModel and the
        // party AI read the shared PlayerbotOptions.Risk at every decision.
        LivePlayerbots("Risk:Enabled", o => o.Risk.Enabled, (o, v) => o.Risk.Enabled = v),
        LivePlayerbotsValue("Risk:Tolerance", o => o.Risk.Tolerance, (o, v) => o.Risk.Tolerance = v, v => PlayerbotRiskOptions.Check(v, 0.25f, 4f)),
        LivePlayerbotsValue("Risk:RetreatHealthPct", o => o.Risk.RetreatHealthPct, (o, v) => o.Risk.RetreatHealthPct = v, v => PlayerbotRiskOptions.Check(v, 5, 95)),
        LivePlayerbotsValue("Risk:NearlyWonHealthPct", o => o.Risk.NearlyWonHealthPct, (o, v) => o.Risk.NearlyWonHealthPct = v, v => PlayerbotRiskOptions.Check(v, 0, 60)),
        LivePlayerbotsValue("Risk:RecoverHealthPct", o => o.Risk.RecoverHealthPct, (o, v) => o.Risk.RecoverHealthPct = v, v => PlayerbotRiskOptions.Check(v, 10, 100)),
        LivePlayerbotsValue("Risk:DangerMemorySeconds", o => o.Risk.DangerMemorySeconds, (o, v) => o.Risk.DangerMemorySeconds = v,
            v => v is >= 0 and <= 86_400 ? null : "must be 0..86400"),
        LivePlayerbots("Risk:PartyRetreatOnWipe", o => o.Risk.PartyRetreatOnWipe, (o, v) => o.Risk.PartyRetreatOnWipe = v),

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
}
