using System.Globalization;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;

namespace ArcaneCore.World.Reload;

/// <summary>
/// The option objects one configuration view carries: the runtime tuning (<see cref="WorldRuntimeOptions"/>,
/// shared by reference with every reader) and, when known, the listener options (<see cref="WorldOptions"/>)
/// and the social rules (<see cref="SocialOptions"/>). A side that is not known reads as null.
/// </summary>
public readonly record struct WorldConfigView(WorldRuntimeOptions Runtime, WorldOptions? Listener, SocialOptions? Social = null);

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
        Fixed("Maps:DataDirectory", v => v.Runtime.Maps.DataDirectory),
        FixedListener("Port", v => v.Port),
        FixedListener("BindAddress", v => v.BindAddress),

        // Connection caps (security hardening lane): the listener's ConnectionLimiter is built at start.
        FixedListener("MaxConnections", v => v.MaxConnections),
        FixedListener("MaxConnectionsPerIp", v => v.MaxConnectionsPerIp),

        // Live: read from the shared options object at each use.
        LiveNonNegative("UpdateCompressionThreshold", o => o.UpdateCompressionThreshold, (o, v) => o.UpdateCompressionThreshold = v),
        LiveNonNegative("AutosaveIntervalMs", o => o.AutosaveIntervalMs, (o, v) => o.AutosaveIntervalMs = v),
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
        // Not a vmangos key: the switch for its custom "World"/"China" channel names, read when a channel is created.
        LiveSocial("VmangosChannelExtensions", o => o.VmangosChannelExtensions, (o, v) => o.VmangosChannelExtensions = v),
    ];

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

    private static WorldConfigKey Fixed(string path, Func<WorldConfigView, object?> read)
        => new($"{Root}:{path}", read, null, null);

    private static WorldConfigKey FixedListener(string path, Func<WorldOptions, object?> read)
        => new($"{Root}:{path}", v => v.Listener is { } listener ? read(listener) : null, null, null);
}
