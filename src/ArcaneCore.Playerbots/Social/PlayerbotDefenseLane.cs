using ArcaneCore.Game;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Game.Channels;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Combat;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// The social pass's "defense" lane (war-mode-defense design, section 1.3; <c>World:Playerbots:Pvp:Defense</c>): the announce part. It sweeps the call board
/// (<see cref="PlayerbotDefenseBoard"/>) once per pass and, for each open call not yet announced, has the freshest live caller that is in the zone's
/// "LocalDefense - &lt;zone&gt;" channel post one line there ("&lt;place&gt; is under attack!", with the map coordinates when WorldMapArea bounds are
/// known). One line per call, and at most one per (map, zone, team) per <see cref="PlayerbotPvpDefenseOptions.ZoneAnnounceSeconds"/>.
/// <para>
/// The line goes through <see cref="PlayerbotChat.TrySpeak"/>, so its screens apply (chat and speech on, safety, the bot's and the channel's limits, a real
/// listener). Coordination never depends on the line: a refused, dropped or unheard line changes nothing on the board, and with
/// <see cref="PlayerbotPvpDefenseOptions.Announce"/> off the lane only sweeps. The retail "is under attack" line is a client-formatted zone packet, not
/// chat; this chat line is a deliberate addition behind <see cref="PlayerbotPvpDefenseOptions.Enabled"/>.
/// </para>
/// World thread only (a pass lane); the state is memory only and bounded: the announced-call set is cut to the open calls and the zone times expire.
/// </summary>
internal sealed class PlayerbotDefenseLane
{
    /// <summary>The most calls looked at for an announcement per pass.</summary>
    internal const int CallsPerPass = 8;

    /// <summary>The most callers of one call tried for a line per pass (the freshest first).</summary>
    internal const int CallersTried = 3;

    /// <summary>The most bots examined for a response per pass (a cursor walks the rest at later passes).</summary>
    internal const int CandidatesPerPass = 256;

    /// <summary>The topic of a call line (the speech limiter and the log name it; never the text).</summary>
    internal const string Topic = "defense-call";

    private readonly Dictionary<(uint Map, uint Zone, Team Team), long> _zoneAnnouncedAt = [];
    private readonly HashSet<int> _announced = [];
    private readonly HashSet<int> _openIds = [];
    private readonly List<DefenseCall> _calls = [];
    private readonly List<(ulong Guid, long LastMs)> _callers = [];
    private readonly List<Pick> _picks = [];
    private readonly int[] _arrived = new int[CallsPerPass];
    private readonly int[] _pickedDefenders = new int[CallsPerPass];
    private readonly int[] _pickedJoiners = new int[CallsPerPass];
    private readonly int[] _attackerMin = new int[CallsPerPass];
    private readonly int[] _attackerMax = new int[CallsPerPass];
    private int _cursor;
    private IReadOnlyDictionary<uint, WorldMapAreaBounds>? _bounds;
    private string? _boundsPath;

    /// <summary>Call lines queued so far (tests and status).</summary>
    internal int Announced { get; private set; }

    /// <summary>Responders assigned so far (tests and status).</summary>
    internal int Assigned { get; private set; }

    /// <summary>Calls the lane remembers as announced or given up on (tests: it is cut to the open calls).</summary>
    internal int Remembered => _announced.Count;

    internal void Run(PlayerbotSocialPass pass)
    {
        if (pass.DefenseCalls is not { } board) return;
        PlayerbotPvpDefenseOptions options = pass.Bot.Pvp.Defense;
        if (!options.Enabled) return;

        long now = pass.NowMs;
        board.Sweep();
        _calls.Clear();
        _openIds.Clear();
        try
        {
            board.OpenCalls(_calls);
            foreach (DefenseCall call in _calls) _openIds.Add(call.Id);
            _announced.RemoveWhere(id => !_openIds.Contains(id));
            long window = options.ZoneAnnounceSeconds * 1000L;
            foreach (var (key, at) in _zoneAnnouncedAt)
            {
                if (now - at >= window) _zoneAnnouncedAt.Remove(key);
            }

            if (options.Announce && pass.Chat is { IsActive: true } chat) Announce(pass, chat, options, now);
            if (options.MaxRespondersPerSide > 0) Respond(pass, board, options);
        }
        finally
        {
            _calls.Clear();
            _openIds.Clear();
            _callers.Clear();
        }
    }

    private void Announce(PlayerbotSocialPass pass, PlayerbotChat chat, PlayerbotPvpDefenseOptions options, long now)
    {
        AreaTable areas = WorldMaps.Of(pass.World).Areas;
        int looked = 0;
        foreach (DefenseCall call in _calls)
        {
            if (_announced.Contains(call.Id)) continue;
            if (++looked > CallsPerPass) break;

            if (areas.GetById(call.ZoneId) is not { } zone || string.IsNullOrWhiteSpace(zone.Name))
            {
                _announced.Add(call.Id); // a zone with no name never gets one: no line for this call
                continue;
            }

            var key = (call.MapId, call.ZoneId, call.CallerTeam);
            if (_zoneAnnouncedAt.TryGetValue(key, out long at) && now - at < options.ZoneAnnounceSeconds * 1000L) continue; // tried again at a later pass while the call is open

            string channelName = "LocalDefense - " + zone.Name;
            string text = PlayerbotDefenseText.Format(PlaceName(areas, call, zone), CoordinatesOf(pass, options, call));
            switch (TrySay(pass, chat, call, channelName, text))
            {
                case Said.Yes:
                    _announced.Add(call.Id);
                    _zoneAnnouncedAt[key] = now;
                    Announced++;
                    break;
                case Said.Never:
                    _announced.Add(call.Id);
                    break;
            }
        }
    }

    private readonly record struct Pick(int Call, int Tier, float Yards, ulong Guid, PlayerbotSocialBot Bot, DefenseSide Side);

    private static readonly Comparison<Pick> ByPick = static (a, b) =>
    {
        int c = a.Call.CompareTo(b.Call);
        if (c != 0) return c;
        c = a.Tier.CompareTo(b.Tier);
        if (c != 0) return c;
        c = a.Yards.CompareTo(b.Yards);
        return c != 0 ? c : a.Guid.CompareTo(b.Guid);
    };

    /// <summary>
    /// Responder selection (design 1.4): at most <see cref="CallsPerPass"/> open calls and <see cref="CandidatesPerPass"/> bots per pass, round robin over the pass's bots
    /// with a cursor, deterministic (no random draw): the order is call, activity tier (explore, grind, home before quest, gather, mount; vendor and train bots are never
    /// taken), distance, guid. A candidate is free (<see cref="PlayerbotHelpLane.IsFree"/>, which also leaves out a responder), has no defend post, stay or engagement,
    /// is not in a group with a real player, stands on the call's map instance within <see cref="PlayerbotPvpDefenseOptions.RespondRadiusYards"/> of its centre without water between,
    /// and may flag (<see cref="PlayerbotHelpRules.MayFlag"/>). Same team as the caller: a defender, level window against the caller. Other team: a joiner, only with War Mode
    /// or <see cref="PlayerbotPvpDefenseOptions.BothSides"/>, level window against the attackers. Defenders are capped at <see cref="PlayerbotPvpDefenseOptions.MaxRespondersPerSide"/>;
    /// joiners at the lesser of that and (defenders that have arrived + live callers + 1), so reinforcements do not gank the caller before a defender is there.
    /// </summary>
    private void Respond(PlayerbotSocialPass pass, PlayerbotDefenseBoard board, PlayerbotPvpDefenseOptions options)
    {
        IReadOnlyList<PlayerbotSocialBot> bots = pass.Bots;
        int calls = Math.Min(CallsPerPass, _calls.Count);
        if (bots.Count == 0 || calls == 0) return;
        bool warMode = pass.Options.WarMode.Enabled;
        bool joiners = warMode || options.BothSides;
        long now = board.NowMs;
        for (int c = 0; c < calls; c++)
        {
            DefenseCall call = _calls[c];
            _pickedDefenders[c] = _pickedJoiners[c] = 0;
            int arrived = 0;
            foreach (ulong guid in call.Defenders)
            {
                if (pass.ByCharacter.TryGetValue((uint)(guid & 0xFFFFFFFFUL), out PlayerbotSocialBot? defender) && defender.Brain is { DefenseArrived: true }) arrived++;
            }

            _arrived[c] = arrived + call.LiveCallerCount(now) + 1;
            _attackerMin[c] = int.MaxValue;
            _attackerMax[c] = -1;
            if (!joiners) continue;
            foreach (ulong guid in call.Attackers)
            {
                if (pass.World.FindOnlinePlayer(new ObjectGuid(guid)) is not { IsAlive: true } attacker) continue;
                _attackerMin[c] = Math.Min(_attackerMin[c], attacker.Level);
                _attackerMax[c] = Math.Max(_attackerMax[c], attacker.Level);
            }
        }

        _picks.Clear();
        int examined = Math.Min(CandidatesPerPass, bots.Count);
        for (int i = 0; i < examined; i++)
        {
            PlayerbotSocialBot bot = bots[(_cursor + i) % bots.Count];
            if (!Eligible(bot, out int tier)) continue;
            Player player = bot.Player;
            for (int c = 0; c < calls; c++)
            {
                if (!Candidate(options, warMode, _calls[c], c, player, out DefenseSide side, out float yards)) continue;
                _picks.Add(new Pick(c, tier, yards, player.Guid.Value, bot, side));
                break;
            }
        }

        _cursor = (_cursor + examined) % bots.Count;
        _picks.Sort(ByPick);
        foreach (Pick pick in _picks)
        {
            DefenseCall call = _calls[pick.Call];
            if (pick.Side == DefenseSide.Defender)
            {
                if (call.Defenders.Count >= options.MaxRespondersPerSide) continue;
            }
            else if (call.Joiners.Count >= Math.Min(options.MaxRespondersPerSide, _arrived[pick.Call])) continue;

            PlayerbotBrain brain = pick.Bot.Brain!;
            if (!brain.RequestDefense(call.Id, pick.Side)) continue;
            if (!board.Assign(call.Id, pick.Guid, pick.Side))
            {
                brain.CancelDefense(call.Id);
                continue;
            }

            Assigned++;
        }

        _picks.Clear();
    }

    /// <summary>The bot-side facts that do not depend on the call: free, no post, stay or engagement, no real player in its group, and a tier that may leave.</summary>
    private static bool Eligible(PlayerbotSocialBot bot, out int tier)
    {
        tier = 0;
        if (!PlayerbotHelpLane.IsFree(bot) || bot.Brain is not { } brain) return false;
        if (brain.Defend is not null || brain.Stay is not null || brain.EngagedPlayer is not null || brain.InGroupWithRealPlayer(bot.Player)) return false;
        switch (brain.Goal)
        {
            case PlayerbotGoalKind.Explore or PlayerbotGoalKind.Grind or PlayerbotGoalKind.Home:
                return true;
            case PlayerbotGoalKind.Quest or PlayerbotGoalKind.Gather or PlayerbotGoalKind.Mount:
                tier = 1;
                return true;
            default:
                return false; // Vendor and Train (tier 2) stay where they are
        }
    }

    private bool Candidate(PlayerbotPvpDefenseOptions options, bool warMode, DefenseCall call, int index, Player player, out DefenseSide side, out float yards)
    {
        side = DefenseSide.Defender;
        yards = 0f;
        if (player.Map is not { } map || player.MapId != call.MapId || map.InstanceId != call.InstanceId) return false;
        float dx = player.X - call.Centre.X, dy = player.Y - call.Centre.Y;
        yards = MathF.Sqrt((dx * dx) + (dy * dy));
        if (yards > options.RespondRadiusYards) return false;
        if (ArcaneCore.World.Playerbots.Population.PopulationTownProgression.SeparatedByWater(call.MapId, player.X, player.Y, call.MapId, call.Centre.X, call.Centre.Y)) return false;
        if (!PlayerbotHelpRules.MayFlag(player, warMode)) return false;
        if (player.Team == call.CallerTeam)
        {
            if (player.Level + options.MaxLevelBelow < call.Level || player.Level > call.Level + options.MaxLevelAbove) return false;
            if (call.Defenders.Count + _pickedDefenders[index] >= options.MaxRespondersPerSide) return false;
            _pickedDefenders[index]++;
            return true;
        }

        if (!(warMode || options.BothSides) || _attackerMax[index] < 0) return false;
        side = DefenseSide.Joiner;
        if (player.Level + options.MaxLevelBelow < _attackerMin[index] || player.Level > _attackerMax[index] + options.MaxLevelAbove) return false;
        if (call.Joiners.Count + _pickedJoiners[index] >= Math.Min(options.MaxRespondersPerSide, _arrived[index])) return false;
        _pickedJoiners[index]++;
        return true;
    }

    private enum Said : byte { No, Yes, Never }

    /// <summary>The freshest callers, one after another, until one is allowed to post the line. <see cref="Said.Never"/>: no other caller will do either (speech off, the line empty or flagged).</summary>
    private Said TrySay(PlayerbotSocialPass pass, PlayerbotChat chat, DefenseCall call, string channelName, string text)
    {
        _callers.Clear();
        foreach (var (guid, caller) in call.Callers) _callers.Add((guid, caller.LastMs));
        _callers.Sort(static (a, b) => b.LastMs.CompareTo(a.LastMs));

        int tried = 0;
        foreach ((ulong guid, long _) in _callers)
        {
            if (tried >= CallersTried) break;
            if (!pass.ByCharacter.TryGetValue((uint)(guid & 0xFFFFFFFFUL), out PlayerbotSocialBot? bot)) continue;
            Player player = bot.Player;
            if (!player.IsAlive || player.Map is not { } map || map.MapId != call.MapId || map.InstanceId != call.InstanceId || !InChannel(pass, player, channelName)) continue;

            tried++;
            switch (chat.TrySpeak(bot.Id, player.Guid, BotSpeech.InChannel(channelName, text, Topic, BotSpeechPriority.Normal)))
            {
                case BotSpeechAdmission.Queued:
                    return Said.Yes;
                case BotSpeechAdmission.Disabled or BotSpeechAdmission.Empty or BotSpeechAdmission.Flagged:
                    return Said.Never;
            }
        }

        return Said.No;
    }

    private static bool InChannel(PlayerbotSocialPass pass, Player player, string channelName)
    {
        foreach (Channel channel in pass.Social.Channels.JoinedBy(player))
        {
            if (channel.IsConstant && channel.ChannelId == BuiltInChannels.LocalDefenseId && string.Equals(channel.Name, channelName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>The subzone's name when the call's area differs from its zone, otherwise the zone's (the channel name already carries the zone).</summary>
    private static string PlaceName(AreaTable areas, DefenseCall call, AreaTemplate zone)
        => call.AreaId != 0 && call.AreaId != call.ZoneId && areas.GetById(call.AreaId) is { Name: { Length: > 0 } sub } ? sub : zone.Name;

    private (int X, int Y)? CoordinatesOf(PlayerbotSocialPass pass, PlayerbotPvpDefenseOptions options, DefenseCall call)
    {
        if (!string.Equals(_boundsPath, options.WorldMapAreaDbcPath, StringComparison.Ordinal) || _bounds is null)
        {
            // The table is small (a few dozen rows) and read again only when the path changes; a missing or bad file warns once and gives no coordinates.
            _boundsPath = options.WorldMapAreaDbcPath;
            _bounds = WorldMapAreaDbcReader.TryRead(_boundsPath, detail =>
                AnomalyLedger.Shared.Report("bot-social", "defense-map-bounds", "defense", detail, origin: pass.World.Identity));
        }

        return _bounds.TryGetValue(call.ZoneId, out WorldMapAreaBounds bounds)
            ? PlayerbotDefenseText.Coordinates(bounds, call.Centre.X, call.Centre.Y)
            : null;
    }
}
