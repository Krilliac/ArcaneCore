using ArcaneCore.Game.Channels;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>One bot as the world channels see it for a pass (world thread).</summary>
internal readonly record struct PlayerbotChannelBot(Guid Id, WorldSession Session, Player Player);

/// <summary>
/// Keeps each bot in its zone's world channels as a 1.12 client does (<see cref="PlayerbotSpeechOptions.WorldChannels"/>): on login and
/// on a zone change it leaves the old zone's General / LocalDefense / Trade and joins the new zone's, through its own CMSG_LEAVE_CHANNEL
/// and CMSG_JOIN_CHANNEL (ChannelHandlers, vmangos HandleJoinChannelOpcode). Run on the social pass (once a second); at most
/// <see cref="PlayerbotSpeechOptions.ChannelChangesPerPass"/> bots are changed per pass, and a bot whose zone, map and wanted set are
/// unchanged costs one dictionary look-up (its membership is re-read every <see cref="RecheckMs"/> so drift heals).
/// <para>
/// The names are the 1.12.1 client's (ChatChannels.dbc patterns, <see cref="ChatChannelCatalog.Builtin"/>): "General - &lt;zone&gt;" and
/// "LocalDefense - &lt;zone&gt;" in every open-world zone (the AreaTable zone name), "Trade - City" only in a capital (the zone's
/// <see cref="AreaFlags.Capital"/>), "LookingForGroup" (no zone) everywhere. Inside an instance or a battleground no zone channel is kept.
/// </para>
/// </summary>
internal sealed class PlayerbotChannelMembership
{
    /// <summary>How often a bot's channel membership is compared with what it should be even when nothing changed.</summary>
    internal const long RecheckMs = 5 * 60_000;

    /// <summary>The trade channel name a 1.12 client uses in every capital (the CITY_ONLY row's "%s" is the localized "City").</summary>
    internal const string TradeCity = "Trade - City";

    internal const string LookingForGroup = "LookingForGroup";

    private readonly Dictionary<Guid, State> _states = [];
    private long _prunedAtMs;

    /// <summary>Join and leave packets sent so far (tests and status).</summary>
    internal int Joins { get; private set; }

    internal int Leaves { get; private set; }

    /// <summary>
    /// The channels a bot in <paramref name="zoneName"/> should be in. <paramref name="capital"/>: the zone is a capital;
    /// <paramref name="openWorld"/>: not an instance or battleground.
    /// </summary>
    internal static IReadOnlyList<string> Desired(PlayerbotWorldChannels wanted, string? zoneName, bool capital, bool openWorld)
    {
        var names = new List<string>(4);
        if (openWorld && !string.IsNullOrWhiteSpace(zoneName))
        {
            if ((wanted & PlayerbotWorldChannels.General) != 0) names.Add("General - " + zoneName);
            if ((wanted & PlayerbotWorldChannels.Trade) != 0 && capital) names.Add(TradeCity);
            if ((wanted & PlayerbotWorldChannels.LocalDefense) != 0) names.Add("LocalDefense - " + zoneName);
        }

        if ((wanted & PlayerbotWorldChannels.LookingForGroup) != 0) names.Add(LookingForGroup);
        return names;
    }

    /// <summary>Whether <paramref name="channel"/> is one of the zone channels this class manages (never a custom or guild recruitment channel).</summary>
    internal static bool Manages(Channel channel)
        => channel.IsConstant && channel.ChannelId is BuiltInChannels.GeneralId or BuiltInChannels.TradeId or BuiltInChannels.LocalDefenseId
            or BuiltInChannels.LookingForGroupId;

    /// <summary>
    /// The zone channel names of <paramref name="player"/>'s current place: its zone from <see cref="Player.ZoneId"/> (kept up by the server
    /// as the bot moves) and the zone's capital flag from <paramref name="areas"/>.
    /// </summary>
    internal static IReadOnlyList<string> DesiredFor(Player player, AreaTable? areas, PlayerbotWorldChannels wanted)
    {
        uint zoneId = player.ZoneId;
        AreaTemplate? zone = zoneId == 0 ? null : areas?.GetById(zoneId);
        bool openWorld = player.Map?.Template is not { Instanceable: true };
        bool capital = zone is not null && (zone.Flags & (uint)AreaFlags.Capital) != 0;
        return Desired(wanted, zone?.Name, capital, openWorld);
    }

    /// <summary>One pass over the running bots (world thread).</summary>
    internal void Run(IReadOnlyList<PlayerbotChannelBot> bots, ChannelManager channels, AreaTable? areas, PlayerbotSpeechOptions options, long nowMs)
    {
        PlayerbotWorldChannels wanted = options.Enabled ? options.WorldChannels : PlayerbotWorldChannels.None;
        int changes = 0;
        foreach (PlayerbotChannelBot bot in bots)
        {
            Player player = bot.Player;

            // A 1.12 client keeps its channels for the whole flight and updates the local ones once it is off the taxi, so a bot in the air
            // is left as it is (its state too: the landing zone differs from the recorded one, which makes the next pass change it).
            // Reference: vmangos Player::UpdateZone (src/game/Objects/Player.cpp) skips UpdateLocalChannels while IsTaxiFlying(),
            // "we don't change channels in preBC". This guard is wider: it also holds wanted-set changes (LookingForGroup, speech off)
            // until the bot lands.
            if (PlayerbotTaxi.IsFlying(player))
            {
                if (_states.TryGetValue(bot.Id, out State? flying)) flying.SeenMs = nowMs;
                continue;
            }

            uint map = player.MapId;
            if (_states.TryGetValue(bot.Id, out State? state) && ReferenceEquals(state.Player, player) && state.Zone == player.ZoneId
                && state.Map == map && state.Wanted == wanted && nowMs - state.CheckedMs < RecheckMs)
            {
                state.SeenMs = nowMs;
                continue;
            }

            if (changes >= options.ChannelChangesPerPass) continue; // the next pass takes it
            changes++;
            Apply(bot, channels, DesiredFor(player, areas, wanted));
            _states[bot.Id] = new State { Player = player, Zone = player.ZoneId, Map = map, Wanted = wanted, CheckedMs = nowMs, SeenMs = nowMs };
        }

        if (nowMs - _prunedAtMs >= 60_000)
        {
            _prunedAtMs = nowMs;
            foreach (KeyValuePair<Guid, State> entry in _states.ToArray())
                if (nowMs - entry.Value.SeenMs >= 60_000) _states.Remove(entry.Key);
        }
    }

    /// <summary>Leave the managed channels the bot should not be in, join the ones it should, through its own packets.</summary>
    private void Apply(PlayerbotChannelBot bot, ChannelManager channels, IReadOnlyList<string> desired)
    {
        var current = new List<string>(4);
        foreach (Channel channel in channels.JoinedBy(bot.Player))
            if (Manages(channel)) current.Add(channel.Name);
        foreach (string name in current)
        {
            if (desired.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (BotSocial.PlayerbotSocialPackets.Send(bot.Session, WorldOpcode.CmsgLeaveChannel, BotSocial.PlayerbotSocialPackets.OneString(name))) Leaves++;
        }

        foreach (string name in desired)
        {
            if (current.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (BotSocial.PlayerbotSocialPackets.Send(bot.Session, WorldOpcode.CmsgJoinChannel, JoinPacket(name))) Joins++;
        }
    }

    /// <summary>CMSG_JOIN_CHANNEL: CString name, CString password (none).</summary>
    internal static byte[] JoinPacket(string name)
    {
        var writer = new PacketWriter(name.Length + 2);
        writer.WriteCString(name);
        writer.WriteCString(string.Empty);
        return writer.ToArray();
    }

    private sealed class State
    {
        public Player? Player;
        public uint Zone;
        public uint Map;
        public PlayerbotWorldChannels Wanted;
        public long CheckedMs;
        public long SeenMs;
    }
}
