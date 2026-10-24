using System.Globalization;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Gm.Death;
using ArcaneCore.World.Gm.Teleport;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Social;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Fleet control of the managed playerbots under <c>.playerbot</c>: <c>summon</c>, <c>send</c>, <c>follow</c>, <c>stay</c>,
/// <c>dismiss</c>, <c>near</c> and <c>revive</c>, each on one bot, a name prefix (<c>Wfbot*</c>) or <c>all</c>
/// (docs/areas/playbots.md, Bot commands). Added through <see cref="ICommandExtension"/>, so the existing children keep their
/// order and abbreviations; <c>start</c> and <c>stop</c> stay in <see cref="PlayerbotCommands"/> and delegate to
/// <see cref="StartStopMany"/>.
/// <para>
/// Threading: handlers run on the world thread (so do the teleports, leashes and revives). Only the bulk start/stop awaits worker
/// tasks and replies later. A command that implies "to me" refuses from the console (<see cref="GmStrings.ConsoleNeedsPlayer"/>)
/// unless <c>to $player</c> names an online player. Only bots in <see cref="IPlayerbotService.Snapshot"/> can ever be touched.
/// </para>
/// </summary>
public sealed partial class PlayerbotFleetCommands : ICommandExtension
{
    /// <summary>The most per-bot lines a bulk reply lists before "... and N more."</summary>
    internal const int MaxDetailLines = 15;

    /// <summary>The most bots <c>.playerbot near</c> lists.</summary>
    internal const int MaxNearLines = 20;

    /// <summary>Default and greatest radius of <c>.playerbot near</c>, in yards.</summary>
    internal const float DefaultNearRadius = 50f;

    internal const float MaxNearRadius = 500f;

    public string Path => "playerbot";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("summon", AccountSecurity.Administrator,
            "Syntax: .playerbot summon $name|$prefix*|all [#count] [force] [to $player]\nTeleport running bots to you (or to the named online player), in rings around the spot. With a count: the nearest bots on that player's map first. 'all' or a count above the cap needs 'force'.",
            Summon),
        new ChatCommand("send", AccountSecurity.Administrator,
            "Syntax: .playerbot send $name|$prefix*|all $tele|here [force] [to $player]\n.playerbot send $name|$prefix*|all at $map $x $y $z [force]\nTeleport running bots to a game_tele place, to your position (here, or the named player's), or to the given map and coordinates (at: no player needed, so the server console and RA can use it; each bot's height is the collision ground found from the given z on the loaded continent map, as for here, else the given z), in rings.",
            Send),
        new ChatCommand("follow", AccountSecurity.Administrator,
            "Syntax: .playerbot follow $name|$prefix*|all [to $player]\nThe bots follow you (or the named player) without a party and fight back. .playerbot dismiss, or the master's whisper 'leave', sets them free.",
            Follow),
        new ChatCommand("stay", AccountSecurity.Administrator,
            "Syntax: .playerbot stay $name|$prefix*|all [to $player]\nThe bots hold their position (they fight only what attacks them) and obey the player's whispered orders.",
            Stay),
        new ChatCommand("dismiss", AccountSecurity.Administrator,
            "Syntax: .playerbot dismiss $name|$prefix*|all\nEnd a follow or stay: the bots go back to their own autonomous play.",
            Dismiss),
        new ChatCommand("near", AccountSecurity.GameMaster,
            "Syntax: .playerbot near [#radius]\nList the running bots within the radius (default 50, at most 500 yards) of you: level, class, health, state, goal and distance.",
            Near),
        new ChatCommand("revive", AccountSecurity.Administrator,
            "Syntax: .playerbot revive $name|$prefix*|all\nRevive the dead bots among the selection, as .revive does.",
            Revive),
        new ChatCommand("defend", AccountSecurity.Administrator,
            "Syntax: .playerbot defend $name|$prefix*|all here #radius [force]\n.playerbot defend $name|$prefix*|all at $map $x $y $z #radius [force]\n.playerbot defend $name|$prefix*|all clear\nThe running bots hold a post at your position (here) or at the given map and coordinates (at: no player needed, so the server console and RA can use it; the height is taken as given): they turn their PvP flag on, fight the PvP-flagged enemy players and help friendly creatures in combat within #radius yards (10 to 200), heal each other and walk back to the post. 'clear' ends it. More bots than the bulk cap need 'force'. Memory only: a restart ends it.",
            Defend),
        SiegeCommand(),
        new ChatCommand("fleet", AccountSecurity.Administrator,
            "Syntax: .playerbot fleet reset [confirm $code]\nDestructive fleet operations.",
            Children:
            [
                new ChatCommand("reset", AccountSecurity.Administrator,
                    "Syntax: .playerbot fleet reset\n.playerbot fleet reset confirm $code\nSimulate a fresh server launch: stop every managed bot and delete every character on a playerbot account (the bot accounts stay). The first form only prints what would happen and a confirm code valid for 60 seconds; the second one does it. Real players' characters are never touched.",
                    FleetReset),
            ]),
    ];

    // --- shared ---------------------------------------------------------------------------------------------------

    private sealed record Env(CommandContext Context, ManagedPlayerbotFeature Feature, PlayerbotOptions Options, TeleportFeature Tele,
        GroupManagerView Groups);

    /// <summary>The group queries the commands need; null groups (no social feature) mean nobody is grouped.</summary>
    private sealed class GroupManagerView(ArcaneCore.Game.Groups.GroupManager? groups)
    {
        public bool SameGroup(Player a, Player b) => groups?.AreInSameGroup(a.Guid, b.Guid) == true;

        public bool HasRealPlayer(Player bot, CommandContext context)
        {
            if (groups?.GetGroup(bot.Guid) is not { } group) return false;
            foreach (ArcaneCore.Game.Groups.GroupMemberSlot slot in group.Members)
                if (slot.Guid != bot.Guid && context.World.FindOnlinePlayer(slot.Guid) is { Session: WorldSession { IsManaged: false } })
                    return true;
            return false;
        }
    }

    private static Env? Services(CommandContext context)
    {
        IServiceProvider services = context.Session.Services;
        if (services.GetService<ManagedPlayerbotFeature>() is not { } feature || services.GetService<TeleportFeature>() is not { } tele)
        {
            context.Reply("Playerbot service is unavailable.");
            return null;
        }

        PlayerbotOptions options = services.GetService<IOptions<PlayerbotOptions>>()?.Value ?? new PlayerbotOptions();
        return new Env(context, feature, options, tele, new GroupManagerView(services.GetService<SocialFeature>()?.Context.Groups));
    }

    /// <summary>The player a command acts "to": the named online player, else the invoker (never the console).</summary>
    private static Player? Anchor(CommandContext context, string? to)
    {
        if (to is null)
        {
            if (!context.IsConsole) return context.Player;
            context.Reply(GmStrings.ConsoleNeedsPlayer);
            return null;
        }

        if (context.World.FindOnlinePlayer(to) is { IsInWorld: true, Map: not null } found) return found;
        context.Reply(GmStrings.PlayerNotFound);
        return null;
    }

    /// <summary>An explicit, validated position of an <c>at $map $x $y $z</c> form: no player is involved.</summary>
    private readonly record struct FleetSpot(uint MapId, bool IsBattleground, Vector3 Position);

    /// <summary>
    /// Check the four words of an <c>at</c> form: a map id the registry knows, then finite numbers, then a position inside the map
    /// (<see cref="GridDefines.IsValidMapCoord(float, float, float, float)"/>, the check .go xyz and .tele apply). The height is taken as
    /// given: nothing here snaps it to the terrain or the navmesh. Replies with the reason and returns null when a word is wrong.
    /// </summary>
    private static FleetSpot? ResolveSpot(CommandContext context, TeleportFeature tele, string verb, IReadOnlyList<string> words)
    {
        if (words.Count != 4 || !uint.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint mapId))
        {
            context.Reply($"Playerbot {verb}: the map must be a map id.");
            return null;
        }

        if (tele.Maps.Registry.Find(mapId) is not { } template)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"Playerbot {verb}: unknown map {mapId}."));
            return null;
        }

        if (!float.TryParse(words[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) || !float.IsFinite(x)
            || !float.TryParse(words[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) || !float.IsFinite(y)
            || !float.TryParse(words[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) || !float.IsFinite(z))
        {
            context.Reply($"Playerbot {verb}: x, y and z must be finite numbers.");
            return null;
        }

        if (!GridDefines.IsValidMapCoord(x, y, z, 0f))
        {
            context.Reply($"Playerbot {verb}: the position is outside the map.");
            return null;
        }

        return new FleetSpot(mapId, template.IsBattleground, new Vector3(x, y, z));
    }

    /// <summary>The running managed bot behind a status line, or the reason it is not one.</summary>
    private static string? Running(Env env, PlayerbotStatus status, out Player bot)
    {
        bot = null!;
        WorldSession? session = env.Feature.FindSession(status.BotId);
        if (session is null || session.State != SessionState.InWorld || session.Player is not { IsInWorld: true, Map: not null } player)
            return "not-running";
        if (!session.IsManaged) return "not-managed";
        bot = player;
        return null;
    }

    private static bool SecurityBlocks(Env env, Player bot) => SecurityBlocks(env.Context, bot);

    internal static bool SecurityBlocks(CommandContext context, Player bot)
        => !context.IsTrueConsole() && !context.IsInvoker(bot)
            && GmSecurity.HasLowerSecurity(context.Security, bot.Security, false, context.Commands.Gm);

    /// <summary>One line of a bulk reply: a bot and what happened to it.</summary>
    private readonly record struct Outcome(string Name, string Reason);

    private static void ReportSkips(CommandContext context, string verb, int matched, string doneWord, int done, List<Outcome> skipped,
        string leftWord = "skipped")
    {
        string detail = skipped.Count == 0 ? string.Empty
            : " (" + string.Join(' ', skipped.GroupBy(s => s.Reason).Select(g => $"{g.Key}={g.Count()}")) + ")";
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"Playerbot {verb}: matched={matched} {doneWord}={done} {leftWord}={skipped.Count}{detail}"));
        foreach (Outcome line in skipped.Take(MaxDetailLines)) context.Reply($"{line.Name}: {line.Reason}");
        if (skipped.Count > MaxDetailLines) context.Reply(string.Create(CultureInfo.InvariantCulture, $"... and {skipped.Count - MaxDetailLines} more."));
        context.Session.Logger.LogInformation("Playerbot {Verb} by {Invoker}: matched={Matched} {Done}={Count} skipped={Skipped}",
            verb, context.InvokerName, matched, doneWord, done, skipped.Count);
    }

    // --- summon / send ---------------------------------------------------------------------------------------------

    /// <summary>Where a teleport batch goes. <see cref="Anchor"/> is the player it is relative to (null for a game_tele place).</summary>
    private sealed record Destination(uint MapId, Vector3 Position, float Orientation, Player? Anchor, Func<float, float, float, float> Ground,
        Func<Vector3, bool> Clear);

    private static bool Summon(CommandContext context, string text)
    {
        if (!PlayerbotFleetSelection.TryParse(text, FleetVerb.Summon, out FleetRequest request)) return false;
        if (Services(context) is not { } env || Anchor(context, request.ToPlayer) is not { } anchor) return true;
        return Relocate(env, "summon", request, DestinationOf(anchor));
    }

    private static bool Send(CommandContext context, string text)
    {
        if (!PlayerbotFleetSelection.TryParse(text, FleetVerb.Send, out FleetRequest request)) return false;
        if (Services(context) is not { } env) return true;
        if (request.At is { } words)
        {
            // A game_tele place that FindGameTele matches for "at $a $b $c $d" (exact, else a name containing it) shadows the position form.
            if (env.Tele.Maps.FindGameTele(string.Join(' ', words.Prepend("at"))) is not null)
                request = request with { At = null, Head = string.Join(' ', words.Prepend("at")) };
            else
            {
                if (ResolveSpot(context, env.Tele, "send", words) is not { } spot) return true;
                if (spot.IsBattleground)
                {
                    context.Reply(TeleportCommands.CannotTeleToBattlegroundText);
                    return true;
                }

                // Slot heights as the here form finds them: the collision ground (terrain and models) searched from the given z, on the
                // continent map when it is loaded; on an unloaded map, or where it knows no ground, the given z is kept.
                Func<float, float, float, float> ground = context.World.FindMap(spot.MapId) is { } loaded
                    ? (x, y, z) => loaded.Collision.GetHeight(x, y, z)
                    : (_, _, z) => z;
                return Relocate(env, "send", request, new Destination(spot.MapId, spot.Position, 0f, null, ground, _ => true));
            }
        }

        if (request.Head.Equals("here", StringComparison.OrdinalIgnoreCase))
        {
            return Anchor(context, request.ToPlayer) is not { } anchor || Relocate(env, "send", request, DestinationOf(anchor));
        }

        WorldMaps maps = env.Tele.Maps;
        GameTele? tele = maps.FindGameTele(request.Head);
        // A place whose name ends in "to <word>" ("Path to Ironforge") is not a target player: try the whole tail as the name.
        if (tele is null && request.ToPlayer is not null && maps.FindGameTele(request.RawTail) is { } whole)
        {
            tele = whole;
            request = request with { ToPlayer = null };
        }

        if (tele is null)
        {
            context.Reply(GmStrings.TeleNotFound);
            return true;
        }

        MapTemplate? template = maps.Registry.Find(tele.MapId);
        if (template is null || !GridDefines.IsValidMapCoord(tele.X, tele.Y, tele.Z, tele.Orientation))
        {
            TeleportCommands.ReplyInvalid(context, tele.X, tele.Y, tele.MapId);
            return true;
        }

        if (template.IsBattleground)
        {
            context.Reply(TeleportCommands.CannotTeleToBattlegroundText);
            return true;
        }

        // The place is not tied to a loaded map: its ground comes from the terrain files, the way .go xyz asks for it.
        TerrainInfo terrain = maps.Terrain.For(tele.MapId);
        var destination = new Destination(tele.MapId, new Vector3(tele.X, tele.Y, tele.Z), tele.Orientation, null,
            (x, y, z) => terrain.GetHeight(x, y, z), _ => true);
        return Relocate(env, "send", request, destination);
    }

    private static Destination DestinationOf(Player anchor)
    {
        Map map = anchor.Map!;
        MapCollision collision = map.Collision;
        return new Destination(anchor.MapId, new Vector3(anchor.X, anchor.Y, anchor.Z), anchor.Orientation, anchor,
            (x, y, z) => collision.GetHeight(x, y, z),
            // From the anchor's eye to the slot raised by the default 2 yd, the eye of the bot that will stand there (vmangos IsWithinLOS).
            slot => collision.IsWithinLineOfSight(anchor, slot.X, slot.Y, slot.Z));
    }

    /// <summary>
    /// Why <paramref name="bot"/> cannot be moved to <paramref name="destination"/> now, or null. The order is the reply's
    /// vocabulary (docs/areas/playbots.md, Bot commands).
    /// </summary>
    private static string? SkipReason(Env env, Guid botId, Player bot, Destination destination)
    {
        if (destination.Anchor is { } anchor && ReferenceEquals(bot, anchor)) return "is-target";
        if (!bot.IsAlive) return "dead";
        if (env.Feature.IsScripted(botId)) return "scripted";
        if (env.Tele.Teleports.IsBeingTeleported(bot)) return "teleporting";
        MapTemplate? template = env.Tele.Maps.Registry.Find(destination.MapId);
        Map own = bot.Map!;
        if (template is { Instanceable: true })
        {
            // vmangos HandleGroupgoCommand (TeleportCommands.cpp:181-266): summoning into an instance needs the target's group
            // (LANG_CANNOT_SUMMON_TO_INST); a bot already inside the very instance stays there (a near teleport).
            bool together = destination.Anchor is { } a
                ? env.Groups.SameGroup(a, bot) || ReferenceEquals(own, a.Map)
                : own.MapId == destination.MapId;
            if (!together) return "instance";
            // Within one map id the teleport service goes near and keeps the bot's own instance (as PlayerbotPartyAI.TeleportToMaster).
            if (own.MapId == destination.MapId && destination.Anchor?.Map is { } there && !ReferenceEquals(own, there)) return "other-instance";
        }
        else if (!PlayerbotMapPolicy.Allows(env.Options.AllowedMaps, destination.MapId, template, current: false))
        {
            return "map-not-allowed";
        }

        return SecurityBlocks(env, bot) ? "security" : null;
    }

    private static bool Relocate(Env env, string verb, FleetRequest request, Destination destination)
    {
        CommandContext context = env.Context;
        List<PlayerbotStatus> matches = PlayerbotFleetSelection.Match(request, env.Feature.Snapshot());
        if (matches.Count == 0)
        {
            context.Reply(PlayerbotFleetSelection.NothingMatches(request));
            return true;
        }

        if (PlayerbotFleetSelection.NeedsForce(matches.Count, request.Count, request.Force, env.Options.MaxBulkCommand))
        {
            context.Reply(PlayerbotFleetSelection.ForceMessage(verb, matches.Count, env.Options.MaxBulkCommand));
            return true;
        }

        var skipped = new List<Outcome>();
        var running = new Dictionary<Guid, Player>();
        var candidates = new List<FleetCandidate>(matches.Count);
        Player? near = destination.Anchor;
        foreach (PlayerbotStatus status in matches)
        {
            if (Running(env, status, out Player bot) is { } reason)
            {
                skipped.Add(new Outcome(status.Name, reason));
                continue;
            }

            running[status.BotId] = bot;
            bool sameMap = near is not null && ReferenceEquals(bot.Map, near.Map);
            float distance = sameMap ? Vector2.Distance(new Vector2(bot.X, bot.Y), new Vector2(near!.X, near.Y)) : 0f;
            candidates.Add(new FleetCandidate(status.BotId, status.Name, sameMap, distance));
        }

        // The nearest first, so that a count takes the bots already close; a bot that cannot move is skipped and the next one takes its place.
        var chosen = new List<(FleetCandidate Candidate, Player Bot)>();
        foreach (FleetCandidate candidate in PlayerbotFleetSelection.Order(candidates))
        {
            if (request.Count is { } wanted && chosen.Count >= wanted) break;
            Player bot = running[candidate.BotId];
            if (SkipReason(env, candidate.BotId, bot, destination) is { } reason) skipped.Add(new Outcome(candidate.Name, reason));
            else chosen.Add((candidate, bot));
        }

        IReadOnlyList<FleetSlot> slots = PlayerbotFleetPlacement.Ring(destination.Position, destination.Orientation, chosen.Count,
            destination.Ground, destination.Clear);
        int moved = 0;
        for (int i = 0; i < chosen.Count; i++)
        {
            (FleetCandidate candidate, Player bot) = chosen[i];
            if (i >= slots.Count) { skipped.Add(new Outcome(candidate.Name, "no-free-spot")); continue; }
            FleetSlot slot = slots[i];
            GmTeleports.BeginCommandTeleport(context, bot);
            if (env.Tele.Teleports.TeleportTo(bot, destination.MapId, slot.X, slot.Y, slot.Z, slot.Orientation)) moved++;
            else skipped.Add(new Outcome(candidate.Name, "teleport-refused"));
        }

        ReportSkips(context, verb, matches.Count, "moved", moved, skipped);
        return true;
    }

    // --- follow / stay / dismiss -----------------------------------------------------------------------------------

    private static bool Follow(CommandContext context, string text) => Leash(context, text, stay: false);

    private static bool Stay(CommandContext context, string text) => Leash(context, text, stay: true);

    private static bool Leash(CommandContext context, string text, bool stay)
    {
        if (!PlayerbotFleetSelection.TryParse(text, FleetVerb.Leash, out FleetRequest request)) return false;
        string verb = stay ? "stay" : "follow";
        if (Services(context) is not { } env || Anchor(context, request.ToPlayer) is not { } master) return true;
        if (master.Session is not WorldSession { IsManaged: false })
        {
            context.Reply($"Playerbot {verb}: {master.Name} is a bot; name a real player.");
            return true;
        }

        List<PlayerbotStatus> matches = PlayerbotFleetSelection.Match(request, env.Feature.Snapshot());
        if (matches.Count == 0)
        {
            context.Reply(PlayerbotFleetSelection.NothingMatches(request));
            return true;
        }

        var skipped = new List<Outcome>();
        int done = 0;
        foreach (PlayerbotStatus status in matches)
        {
            string? reason = Running(env, status, out Player bot);
            if (reason is null && ReferenceEquals(bot, master)) reason = "is-target";
            else if (reason is null && !bot.IsAlive) reason = "dead";
            else if (reason is null && env.Groups.HasRealPlayer(bot, context)) reason = "in-player-group";
            else if (reason is null && SecurityBlocks(env, bot)) reason = "security";
            if (reason is null)
            {
                PlayerbotOperationResult result = env.Feature.Leash(status.BotId, master, stay);
                if (result.Success) done++;
                else reason = result.Code;
            }

            if (reason is not null) skipped.Add(new Outcome(status.Name, reason));
        }

        ReportSkips(context, verb, matches.Count, stay ? "staying" : "following", done, skipped);
        return true;
    }

    private static bool Dismiss(CommandContext context, string text)
    {
        if (!PlayerbotFleetSelection.TryParse(text, FleetVerb.Plain, out FleetRequest request)) return false;
        if (Services(context) is not { } env) return true;
        List<PlayerbotStatus> matches = PlayerbotFleetSelection.Match(request, env.Feature.Snapshot());
        if (matches.Count == 0)
        {
            context.Reply(PlayerbotFleetSelection.NothingMatches(request));
            return true;
        }

        var skipped = new List<Outcome>();
        int done = 0;
        foreach (PlayerbotStatus status in matches)
        {
            PlayerbotOperationResult result = env.Feature.Unleash(status.BotId);
            if (result.Success) done++;
            else skipped.Add(new Outcome(status.Name, result.Code));
        }

        ReportSkips(context, "dismiss", matches.Count, "dismissed", done, skipped);
        return true;
    }

    // --- revive ----------------------------------------------------------------------------------------------------

    private static bool Revive(CommandContext context, string text)
    {
        if (!PlayerbotFleetSelection.TryParse(text, FleetVerb.Plain, out FleetRequest request)) return false;
        if (Services(context) is not { } env) return true;
        List<PlayerbotStatus> matches = PlayerbotFleetSelection.Match(request, env.Feature.Snapshot());
        if (matches.Count == 0)
        {
            context.Reply(PlayerbotFleetSelection.NothingMatches(request));
            return true;
        }

        var skipped = new List<Outcome>();
        int done = 0;
        foreach (PlayerbotStatus status in matches)
        {
            string? reason = Running(env, status, out Player bot);
            if (reason is null && bot.IsAlive) reason = "alive";
            else if (reason is null && SecurityBlocks(env, bot)) reason = "security";
            if (reason is not null) { skipped.Add(new Outcome(status.Name, reason)); continue; }
            DeathGmCommands.ReviveInPlace(bot);
            done++;
        }

        ReportSkips(context, "revive", matches.Count, "revived", done, skipped);
        return true;
    }

    // --- defend ----------------------------------------------------------------------------------------------------

    private static int s_nextPostId;

    private static bool Defend(CommandContext context, string text)
    {
        if (!PlayerbotFleetSelection.TryParse(text, FleetVerb.Defend, out FleetRequest request)) return false;
        if (Services(context) is not { } env) return true;
        bool clear = request.Head == "clear";
        Player? anchor = null;
        FleetSpot? spot = null;
        if (!clear)
        {
            if (request.At is { } words)
            {
                spot = ResolveSpot(context, env.Tele, "defend", words);
                if (spot is null) return true;
                if (spot.Value.IsBattleground)
                {
                    context.Reply(TeleportCommands.CannotTeleToBattlegroundText);
                    return true;
                }
            }
            else
            {
                anchor = Anchor(context, null);
                if (anchor is null) return true;
            }
        }

        List<PlayerbotStatus> matches = PlayerbotFleetSelection.Match(request, env.Feature.Snapshot());
        if (matches.Count == 0)
        {
            context.Reply(PlayerbotFleetSelection.NothingMatches(request));
            return true;
        }

        int cap = env.Options.MaxBulkCommand;
        if (!clear && PlayerbotFleetSelection.NeedsForce(matches.Count, null, request.Force, cap))
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"Playerbot defend: {matches.Count} bots match; add 'force' above {cap}."));
            return true;
        }

        PlayerbotDefendPost? post = anchor is not null
            ? new PlayerbotDefendPost(anchor.MapId, new Vector3(anchor.X, anchor.Y, anchor.Z),
                PlayerbotDefend.ClampRadius(request.Radius), Interlocked.Increment(ref s_nextPostId))
            : spot is { } there
                ? new PlayerbotDefendPost(there.MapId, there.Position, PlayerbotDefend.ClampRadius(request.Radius), Interlocked.Increment(ref s_nextPostId))
                : null;
        var skipped = new List<Outcome>();
        var chosen = new List<Guid>(matches.Count);
        var names = new Dictionary<Guid, string>(matches.Count);
        foreach (PlayerbotStatus status in matches)
        {
            string? reason = Running(env, status, out Player bot);
            if (reason is null && !clear)
            {
                if (env.Groups.HasRealPlayer(bot, context)) reason = "in-player-group";
                else if (SecurityBlocks(env, bot)) reason = "security";
            }

            if (reason is not null) skipped.Add(new Outcome(status.Name, reason));
            else
            {
                chosen.Add(status.BotId);
                names[status.BotId] = status.Name;
            }
        }

        int done = 0;
        foreach (PlayerbotOperationResult result in env.Feature.SetDefend(chosen, post))
        {
            if (result.Success) done++;
            else skipped.Add(new Outcome(result.BotId is { } id && names.TryGetValue(id, out string? name) ? name : result.Name ?? "?", result.Code));
        }

        ReportSkips(context, "defend", matches.Count, clear ? "cleared" : "defending", done, skipped);
        return true;
    }

    // --- near ------------------------------------------------------------------------------------------------------

    private static bool Near(CommandContext context, string text)
    {
        float radius = DefaultNearRadius;
        string arg = text.Trim();
        if (arg.Length != 0
            && (!float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out radius) || !float.IsFinite(radius) || radius <= 0
                || radius > MaxNearRadius))
            return false;
        if (context.IsConsole)
        {
            context.Reply(GmStrings.ConsoleNeedsPlayer);
            return true;
        }

        if (Services(context) is not { } env) return true;
        Player me = context.Player;
        var found = new List<(PlayerbotStatus Status, Player Bot, float Distance)>();
        foreach (PlayerbotStatus status in env.Feature.Snapshot())
        {
            if (Running(env, status, out Player bot) is not null || !ReferenceEquals(bot.Map, me.Map)) continue;
            float distance = Vector3.Distance(new Vector3(bot.X, bot.Y, bot.Z), new Vector3(me.X, me.Y, me.Z));
            if (distance <= radius) found.Add((status, bot, distance));
        }

        found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        context.Reply(string.Create(CultureInfo.InvariantCulture, $"Playerbots within {radius:0.#} yards: {found.Count}."));
        if (found.Count == 0)
        {
            context.Reply("None.");
            return true;
        }

        foreach ((PlayerbotStatus status, Player bot, float distance) in found.Take(MaxNearLines))
            context.Reply(NearLine(status, bot, distance));
        if (found.Count > MaxNearLines) context.Reply(string.Create(CultureInfo.InvariantCulture, $"... and {found.Count - MaxNearLines} more."));
        return true;
    }

    /// <summary><c>Name L60 Warrior hp=80% alive goal=Explore d=12.3</c> (the goal is the bot's status line's: its party AI's while that drives it).</summary>
    internal static string NearLine(PlayerbotStatus status, Player bot, float distance)
    {
        uint percent = bot.MaxHealth == 0 ? 0 : (uint)((ulong)bot.Health * 100 / bot.MaxHealth);
        string state = bot.IsAlive ? "alive" : (bot.Flags & PlayerFlags.Ghost) != 0 ? "ghost" : "dead";
        return string.Create(CultureInfo.InvariantCulture,
            $"{bot.Name} L{bot.Level} {ClassName(bot.Class)} hp={percent}% {state} goal={status.Goal} d={distance:F1}");
    }

    private static string ClassName(Class value) => Enum.IsDefined(value) ? value.ToString() : $"class={(byte)value}";

    // --- fleet reset -----------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>.playerbot fleet reset</c> prints the census and a confirm code; <c>... confirm $code</c> runs it
    /// (<see cref="ManagedPlayerbotFeature.ResetFleetAsync"/>). Administrator or the server console; the work runs on a worker and
    /// reports back line by line.
    /// </summary>
    private static bool FleetReset(CommandContext context, string text)
    {
        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool confirm = words.Length == 2 && words[0].Equals("confirm", StringComparison.OrdinalIgnoreCase);
        if (words.Length != 0 && !confirm) return false;
        if (context.Session.Services.GetService<ManagedPlayerbotFeature>() is not { } feature)
        {
            context.Reply("Playerbot service is unavailable.");
            return true;
        }

        string invoker = context.InvokerName;
        if (confirm) _ = Task.Run(() => FleetResetConfirmAsync(context, feature, invoker, words[1]));
        else _ = Task.Run(() => FleetResetPreviewAsync(context, feature, invoker));
        return true;
    }

    private static async Task FleetResetPreviewAsync(CommandContext context, ManagedPlayerbotFeature feature, string invoker)
    {
        try
        {
            if (feature.ResetInProgress) { Say(context, "Playerbot fleet reset: one is already running."); return; }
            FleetResetCensus census = await feature.TakeFleetCensusAsync().ConfigureAwait(false);
            if (census.Refusal is { } refusal)
            {
                context.Session.Logger.LogWarning("Playerbot fleet reset preview by {Invoker} REFUSED: {Reason}", invoker, refusal);
                Say(context, "Playerbot fleet reset refused: " + refusal + ". Nothing was changed.");
                return;
            }

            if (census.Accounts == 0) { Say(context, "Playerbot fleet reset: there are no playerbot accounts."); return; }
            string code = feature.ResetConfirmation.Issue(invoker, new FleetResetPromise(census.Characters, census.Accounts, census.CharacterIds));
            context.Session.Logger.LogInformation(
                "Playerbot fleet reset previewed by {Invoker}: {Characters} characters on {Accounts} bot accounts, {Running} running; confirm code issued",
                invoker, census.Characters, census.Accounts, census.Running);
            Say(context, string.Create(CultureInfo.InvariantCulture,
                $"Playerbot fleet reset would stop {census.Running} running bots and delete {census.Characters} bot characters on {census.Accounts} bot accounts. The emptied bot accounts are kept and reused for new bots; real players' characters are not touched."));
            if (feature.RefillWarning is { } warning) Say(context, warning);
            if (census.GroupedWithPlayers != 0 || census.TradingWithPlayers != 0)
                Say(context, string.Create(CultureInfo.InvariantCulture,
                    $"{census.GroupedWithPlayers} bots are grouped with real players (they leave their groups as their characters are deleted) and {census.TradingWithPlayers} are in a trade (cancelled)."));
            Say(context, string.Create(CultureInfo.InvariantCulture,
                $"To proceed within {(int)FleetResetConfirmation.Lifetime.TotalSeconds} seconds: .playerbot fleet reset confirm {code}"));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.fleet-commands", error);
            context.Session.Logger.LogWarning(error, "Playerbot fleet reset preview failed ({Type}: {Message})", error.GetType().Name, error.Message);
            Say(context, $"Playerbot fleet reset preview failed: {error.GetType().Name}.");
        }
    }

    private static async Task FleetResetConfirmAsync(CommandContext context, ManagedPlayerbotFeature feature, string invoker, string code)
    {
        try
        {
            FleetResetCodeResult verdict = feature.ResetConfirmation.TryRedeem(invoker, code, out FleetResetPromise? promise);
            if (verdict != FleetResetCodeResult.Accepted || promise is null)
            {
                context.Session.Logger.LogWarning("Playerbot fleet reset confirm by {Invoker} refused: {Verdict}", invoker, verdict);
                Say(context, verdict switch
                {
                    FleetResetCodeResult.Expired => "Playerbot fleet reset: the confirm code has expired. Run .playerbot fleet reset again.",
                    FleetResetCodeResult.WrongCode => "Playerbot fleet reset: wrong confirm code.",
                    FleetResetCodeResult.OtherInvoker => "Playerbot fleet reset: the pending preview belongs to someone else.",
                    FleetResetCodeResult.TooManyAttempts => "Playerbot fleet reset: too many wrong codes. Run .playerbot fleet reset again.",
                    _ => "Playerbot fleet reset: nothing to confirm. Run .playerbot fleet reset first.",
                });
                return;
            }

            context.Session.Logger.LogInformation(
                "Playerbot fleet reset CONFIRMED by {Invoker}: {Characters} characters on {Accounts} bot accounts", invoker, promise.Characters, promise.Accounts);
            FleetResetResult result = await feature.ResetFleetAsync(promise, line => Say(context, line)).ConfigureAwait(false);
            Say(context, result.Success ? "Playerbot fleet reset done: " + result.Message
                : $"Playerbot fleet reset {(result.Code == "reset-incomplete" ? "incomplete" : "refused")} ({result.Code}): {result.Message}");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.fleet-commands", error);
            context.Session.Logger.LogError(error, "Playerbot fleet reset failed ({Type}: {Message})", error.GetType().Name, error.Message);
            Say(context, $"Playerbot fleet reset failed: {error.GetType().Name}: {error.Message}");
        }
    }

    // A reply from a worker thread: the console always has a sink, a player only while still in the world.
    private static void Say(CommandContext context, string line)
    {
        if (context.CanReplyLater) context.Reply(line);
    }

    // --- start / stop many -----------------------------------------------------------------------------------------

    /// <summary>
    /// <c>.playerbot start|stop</c> with a prefix or <c>all</c> (called by <see cref="PlayerbotCommands"/>): the matching registered
    /// bots are started or stopped one after another on a worker, and one summary comes back with each failure's code (for example
    /// <c>playerbot-capacity</c>). More than <see cref="PlayerbotOptions.MaxBulkCommand"/> bots need <c>force</c>.
    /// </summary>
    internal static bool StartStopMany(CommandContext context, string text, bool start)
    {
        if (!PlayerbotFleetSelection.TryParse(text, FleetVerb.Plain, out FleetRequest request)) return false;
        string verb = start ? "start" : "stop";
        if (Services(context) is not { } env) return true;
        List<PlayerbotStatus> matches = PlayerbotFleetSelection.Match(request, env.Feature.Snapshot());
        if (matches.Count == 0)
        {
            context.Reply(PlayerbotFleetSelection.NothingMatches(request));
            return true;
        }

        if (PlayerbotFleetSelection.NeedsForce(matches.Count, null, request.Force, env.Options.MaxBulkCommand))
        {
            context.Reply(PlayerbotFleetSelection.ForceMessage(verb, matches.Count, env.Options.MaxBulkCommand));
            return true;
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture, $"Playerbot {verb} accepted ({matches.Count})."));
        ManagedPlayerbotFeature feature = env.Feature;
        _ = Task.Run(() => RunManyAsync(context, feature, matches, start));
        return true;
    }

    private static async Task RunManyAsync(CommandContext context, ManagedPlayerbotFeature feature, List<PlayerbotStatus> matches, bool start)
    {
        string verb = start ? "start" : "stop";
        var failed = new List<Outcome>();
        int ok = 0;
        foreach (PlayerbotStatus status in matches)
        {
            try
            {
                PlayerbotOperationResult result = await (start ? feature.StartAsync(status.BotId.ToString()) : feature.StopAsync(status.BotId.ToString()))
                    .ConfigureAwait(false);
                if (result.Success) ok++;
                else failed.Add(new Outcome(status.Name, result.Code));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("playerbot.fleet-commands", error);
                feature.ReportCommandFault(start ? "fleet start" : "fleet stop", error);
                failed.Add(new Outcome(status.Name, error.GetType().Name));
            }
        }

        if (!context.CanReplyLater) return;
        ReportSkips(context, verb, matches.Count, start ? "started" : "stopped", ok, failed, "failed");
    }
}
