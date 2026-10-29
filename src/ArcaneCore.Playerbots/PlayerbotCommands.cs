using System.Globalization;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using Microsoft.Extensions.DependencyInjection;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Playerbots;

/// <summary>Administrator lifecycle controls for server-managed playerbots.</summary>
public sealed class PlayerbotCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("playerbot", AccountSecurity.GameMaster,
            "Syntax: .playerbot <create|start|stop|status|splitbrain|list|inspect|invite|home|chat|groups|battlegrounds|travel|scenario|summon|send|follow|stay|dismiss|near|revive|fleet|population|qa|chaos|social>\nManage server-owned autonomous players.",
            Children:
            [
                new ChatCommand("create", AccountSecurity.Administrator,
                    "Syntax: .playerbot create $name [#race #class]\nCreate a persistent bot character.", Create),
                new ChatCommand("start", AccountSecurity.Administrator,
                    "Syntax: .playerbot start $id|$name|$prefix*|all [force]\nStart a persistent bot, or every bot whose name starts with the prefix (or all; more than the bulk cap needs force).", Start),
                new ChatCommand("stop", AccountSecurity.Administrator,
                    "Syntax: .playerbot stop $id|$name|$prefix*|all [force]\nStop a persistent bot, or every bot whose name starts with the prefix (or all; more than the bulk cap needs force).", Stop),
                new ChatCommand("status", AccountSecurity.GameMaster,
                    "Syntax: .playerbot status [$id|$name]\nShow bot lifecycle status.", Status),
                new ChatCommand("splitbrain", AccountSecurity.Administrator,
                    "Syntax: .playerbot splitbrain [clear]\nShow whether this world stopped writing playerbot rows because another world process changed them. Stop the other world first, then clear: it re-reads the stored rows, reports them and lifts the latch so playerbot writes resume. The population manager (if enabled) then restarts its members over its next passes; every other bot stays stopped until you start it, and quarantined bots stay Faulted until you start them.", SplitBrainCommand),
                new ChatCommand("list", AccountSecurity.GameMaster,
                    "Syntax: .playerbot list\nList managed bots.", List),
                new ChatCommand("inspect", AccountSecurity.GameMaster,
                    "Syntax: .playerbot inspect $id|$name\nRead actual target, victim, cast and nearby trainer facts.", Inspect),
                new ChatCommand("invite", AccountSecurity.GameMaster,
                    "Syntax: .playerbot invite $id|$name\nPut a running bot into your group; it follows you and takes your commands.", Invite),
                new ChatCommand("home", AccountSecurity.Administrator,
                    "Syntax: .playerbot home $name|$prefix* <$tele|here> [#radius] [zone|nozone]\n.playerbot home $name|$prefix* clear\nAnchor bots to an area: a game_tele place (or each bot's own position with here) and a radius in yards (50 to 5000, default 600), clipped to the zone of the centre unless nozone is given. A prefix such as Wfbot* names every bot whose name starts with it. clear removes the home.", Home),
                new ChatCommand("chat", AccountSecurity.GameMaster,
                    "Syntax: .playerbot chat <status|flags|pardon>\nThe bots' chat replies and their safety screening.",
                    Children:
                    [
                        new ChatCommand("status", AccountSecurity.GameMaster,
                            "Syntax: .playerbot chat status\nShow bot chat: on/off, the spend estimate, and per provider its kind, model, whether its key variable is set, replies this hour and the last error.",
                            ChatStatus),
                        new ChatCommand("flags", AccountSecurity.GameMaster,
                            "Syntax: .playerbot chat flags [$player]\nShow the latest lines the bot chat safety flagged (all players, or one player's with their strikes, cut-off and AI choice).",
                            ChatFlags),
                        new ChatCommand("pardon", AccountSecurity.GameMaster,
                            "Syntax: .playerbot chat pardon $player\nClear a player's bot chat strikes and cut-off (an automatic mute is lifted with .unmute).",
                            ChatPardon),
                    ]),
                new ChatCommand("groups", AccountSecurity.GameMaster,
                    "Syntax: .playerbot groups\nList the bot-led groups (goal, members and roles, state) and the bots waiting for partners.", GroupsCommand),
                new ChatCommand("battlegrounds", AccountSecurity.GameMaster,
                    "Syntax: .playerbot battlegrounds\nList the bots with battleground business (type, phase, role, activity), the totals and the refused types.", BattlegroundsCommand),
                new ChatCommand("travel", AccountSecurity.Administrator,
                    "Syntax: .playerbot travel $id|$name #map #x #y #z\nSend a running bot on a journey to the place by its own means (walking, flight paths, the Deeprun Tram and lifts, boats, zeppelins, its hearthstone, mage teleports and portals) and print the planned legs.", TravelCommand),
                Scenarios.PlayerbotScenarioCommands.Command,
            ])
    ];

    /// <summary>
    /// <c>.playerbot chat status</c>: the chat service's state. A key is never shown, only its variable's name and whether it is set.
    /// </summary>
    private static bool ChatStatus(CommandContext context, string text)
    {
        if (text.Trim().Length != 0) return false;
        ManagedPlayerbotFeature? feature = context.Session.Services.GetService<ManagedPlayerbotFeature>();
        if (feature?.Chat is not { } chat)
        {
            context.Reply("Playerbot chat is unavailable.");
            return true;
        }

        foreach (string line in ChatStatusLines(chat.Status())) context.Reply(line);
        if (feature.ChatInProcess?.StatusLine() is { } local) context.Reply(local);
        return true;
    }

    /// <summary>The most flag records <c>.playerbot chat flags</c> shows.</summary>
    internal const int MaxFlagLines = 15;

    /// <summary><c>.playerbot chat flags [player]</c>: the newest flag records (one player's, with their standing, when named).</summary>
    private static bool ChatFlags(CommandContext context, string text)
    {
        string name = text.Trim();
        if (name.Contains(' ', StringComparison.Ordinal)) return false;
        if (context.Session.Services.GetService<ManagedPlayerbotFeature>()?.Chat is not { } chat)
        {
            context.Reply("Playerbot chat is unavailable.");
            return true;
        }

        foreach (string line in ChatFlagLines(chat.Safety, name.Length == 0 ? null : name)) context.Reply(line);
        return true;
    }

    internal static IEnumerable<string> ChatFlagLines(Chat.PlayerbotChatSafety safety, string? player)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        if (player is not null)
        {
            if (safety.Player(player) is { } standing)
            {
                string choice = standing.AiReplies switch { true => "on", false => "off", _ => "default" };
                yield return string.Create(c,
                    $"{standing.PlayerName}: strikes={standing.Strikes} cut-off={(standing.CutoffSeconds > 0 ? Math.Ceiling(standing.CutoffSeconds / 60d) + "m" : "no")} ai={choice}");
            }
            else
            {
                yield return $"{player}: no strikes, no cut-off.";
            }
        }

        IReadOnlyList<Chat.BotChatFlag> flags = safety.Flags(player);
        if (flags.Count == 0)
        {
            yield return "No flagged lines.";
            yield break;
        }

        foreach (Chat.BotChatFlag flag in flags.Take(MaxFlagLines))
        {
            string excerpt = flag.Excerpt is null ? string.Empty : $" \"{flag.Excerpt.Replace('|', '/')}\"";
            yield return string.Create(c,
                $"{flag.At:yyyy-MM-dd HH:mm:ss} {flag.PlayerName} (guid {flag.PlayerGuid}, account {flag.AccountId}) -> {flag.Bot}: {flag.Category} ({Chat.PlayerbotChatSafety.Words(flag.Source)}){excerpt}");
        }

        if (flags.Count > MaxFlagLines) yield return string.Create(c, $"... and {flags.Count - MaxFlagLines} older.");
    }

    /// <summary><c>.playerbot chat pardon &lt;player&gt;</c>: clear the player's strikes and cut-off.</summary>
    private static bool ChatPardon(CommandContext context, string text)
    {
        string name = text.Trim();
        if (name.Length == 0 || name.Contains(' ', StringComparison.Ordinal)) return false;
        if (context.Session.Services.GetService<ManagedPlayerbotFeature>()?.Chat is not { } chat)
        {
            context.Reply("Playerbot chat is unavailable.");
            return true;
        }

        ulong? guid = context.World.FindOnlinePlayer(name)?.Guid.Value;
        context.Reply(chat.Safety.Pardon(name, guid)
            ? $"{name}: bot chat strikes and cut-off cleared."
            : $"{name} has no bot chat strikes or cut-off.");
        return true;
    }

    internal static IEnumerable<string> ChatStatusLines(Chat.BotChatStatus status)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        string cap = status.MaxDailySpendUsd > 0 ? Chat.PlayerbotChat.Usd(status.MaxDailySpendUsd) : "none";
        yield return string.Create(c,
            $"Bot chat: enabled={(status.Enabled ? "yes" : "no")} channels={status.Channels} queued={status.Queued} answered={status.Answered} unanswered={status.Dropped} spend-today={Chat.PlayerbotChat.Usd(status.SpentTodayUsd)} cap={cap}");
        foreach (Chat.BotChatProviderStatus p in status.Providers)
        {
            string key = p.KeyVariable is null ? "none" : $"{p.KeyVariable}:{(p.KeyPresent ? "present" : "missing")}";
            yield return string.Create(c,
                $"#{p.Index} {p.Kind} model={p.Model} key={key} replies-hour={p.RepliesThisHour}/{p.MaxRepliesPerHour} replies={p.Replies} errors={p.Errors} last-error={p.LastError ?? "none"} cooldown={p.CooldownSeconds}s priced={(p.Priced ? "yes" : "no")}");
        }

        Chat.BotChatSafetyStatus s = status.Safety;
        yield return string.Create(c,
            $"Bot chat safety: screening={(s.Screening ? "on" : "off")} screened={s.Screened} flagged={s.Flagged} moderation-flagged={s.ModerationFlagged} output-flagged={s.OutputFlagged} builtin-only={s.BuiltinOnly} timed-out={s.TimedOut} cut-offs={s.Cutoffs} cut-off-now={s.CutOffNow} auto-mutes={s.AutoMutes} opted-out={s.OptedOut} opted-in={s.OptedIn} disclosed={s.Disclosed} filter={s.Terms} terms/{s.Patterns} patterns");
    }

    private static bool Create(CommandContext context, string text)
    {
        CommandArgs args = new(text);
        string? name = args.ExtractArg();
        if (name is null || !TryOptionalRaceClass(args, out byte race, out byte characterClass)) return false;
        return StartOperation(context, service => service.CreateAsync(name, race, characterClass), "create");
    }

    private static bool Start(CommandContext context, string text)
    {
        if (PlayerbotFleetSelection.IsMulti(text)) return PlayerbotFleetCommands.StartStopMany(context, text, start: true);
        if (!TryRequiredId(text, out string id)) return false;
        return StartOperation(context, service => service.StartAsync(id), "start");
    }

    private static bool Stop(CommandContext context, string text)
    {
        if (PlayerbotFleetSelection.IsMulti(text)) return PlayerbotFleetCommands.StartStopMany(context, text, start: false);
        if (!TryRequiredId(text, out string id)) return false;
        return StartOperation(context, service => service.StopAsync(id), "stop");
    }

    private static bool Status(CommandContext context, string text)
    {
        if (string.IsNullOrWhiteSpace(text) && context.Session.Services.GetService<ManagedPlayerbotFeature>()?.SplitBrain == true)
            context.Reply("WARNING: split brain - another world process is writing the playerbot rows; this world writes none (.playerbot splitbrain).");
        IPlayerbotService? service = context.Session.Services.GetService<IPlayerbotService>();
        if (service is null)
        {
            context.Reply("Playerbot service is unavailable.");
            return true;
        }

        PlayerbotStatus? status = null;
        if (!string.IsNullOrWhiteSpace(text) && !TryFind(service, text, out status))
        {
            context.Reply("Playerbot not found.");
            return true;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            IReadOnlyList<PlayerbotStatus> all = service.Snapshot();
            foreach (PlayerbotStatus item in all) context.Reply(Format(item));
            // The terminator: tools that wait for the whole reply (tools/ops/OpsLib.ps1) match it, so a bot that is missing does not hide the end.
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"{StatusTerminator}{all.Count}"));
        }
        else if (status is { } found)
        {
            context.Reply(Format(found));
        }

        return true;
    }

    /// <summary><c>.playerbot splitbrain [clear]</c>: the split-brain latch of <see cref="ManagedPlayerbotFeature"/>.</summary>
    private static bool SplitBrainCommand(CommandContext context, string text)
    {
        string argument = text.Trim();
        if (argument.Length != 0 && !argument.Equals("clear", StringComparison.OrdinalIgnoreCase)) return false;
        if (context.Session.Services.GetService<ManagedPlayerbotFeature>() is not { } feature)
        {
            context.Reply("Playerbot service is unavailable.");
            return true;
        }

        if (argument.Length == 0)
            context.Reply(feature.SplitBrain
                ? "Split brain: another world process is writing the playerbot rows; this world writes none. Stop the other world, then .playerbot splitbrain clear."
                : "No split brain: this world is writing the playerbot rows.");
        else
        {
            context.Reply("Split brain clear accepted (re-reading the stored rows).");
            _ = Task.Run(async () =>
            {
                try
                {
                    ManagedPlayerbotFeature.SplitBrainClear cleared = await feature.ClearSplitBrainAsync().ConfigureAwait(false);
                    context.Reply(cleared.WasSet
                        ? $"Split brain cleared; playerbot writes resume. Stored rows re-read: {cleared.Rows} bots, {cleared.Enabled} wanted running, {cleared.Faulted} faulted. All are stopped here; the population manager (if enabled) restarts its members over its next passes, any other bot stays stopped until you start it."
                        : "No split brain was set.");
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("playerbot.commands", error);
                    feature.ReportCommandFault("splitbrain-clear", error);
                    context.Reply($"Split brain clear failed: {error.GetType().Name}.");
                }
            });
        }

        return true;
    }

    private static bool List(CommandContext context, string text)
    {
        if (text.Trim().Length != 0) return false;
        return Status(context, string.Empty);
    }

    private static bool Inspect(CommandContext context, string text)
    {
        if (!TryRequiredId(text, out string id)) return false;
        ManagedPlayerbotFeature? service = context.Session.Services.GetService<ManagedPlayerbotFeature>();
        if (service is null) { context.Reply("Playerbot inspection is unavailable."); return true; }
        _ = InspectAndReplyAsync(context, service, id);
        return true;
    }

    /// <summary>
    /// <c>.playerbot invite</c> (vmangos <c>.partybot add</c>): the invoker invites the bot through the ordinary group invite and the
    /// bot accepts at once, whatever its invite policy. Runs on the world thread, where commands run.
    /// </summary>
    private static bool Invite(CommandContext context, string text)
    {
        if (!TryRequiredId(text, out string id)) return false;
        ManagedPlayerbotFeature? service = context.Session.Services.GetService<ManagedPlayerbotFeature>();
        if (service is null) { context.Reply("Playerbot service is unavailable."); return true; }
        if (context.IsConsole || context.Player.Map is null)
        {
            context.Reply("Playerbot failed: invoker is not in the world.");
            return true;
        }

        PlayerbotOperationResult result = service.InviteToGroup(context.Player, id);
        context.Reply($"Playerbot {(result.Success ? "ok" : "failed")}: {result.Code} ({result.Name ?? id}).");
        return true;
    }

    /// <summary>
    /// <c>.playerbot travel $id|$name #map #x #y #z</c>: plan a journey for a running managed bot through <see cref="IPlayerbotTravel.TryBegin"/>
    /// (docs/areas/playbots.md, Travel) and print its legs, one line each; the brain drives it from the bot's next think. A bot the brain does
    /// not drive now (<see cref="ManagedPlayerbotFeature.HeldFromBrain"/>: a controller, a battleground, a bot-led group, its party) is
    /// refused with that reason; other refusals print the planner's reason. World thread, where commands run.
    /// </summary>
    private static bool TravelCommand(CommandContext context, string text)
    {
        CommandArgs args = new(text);
        string? id = args.ExtractArg();
        if (id is null || !args.ExtractUInt32(out uint mapId) || !args.ExtractFloat(out float x) || !args.ExtractFloat(out float y)
            || !args.ExtractFloat(out float z) || !args.IsEmpty)
            return false;
        IServiceProvider services = context.Session.Services;
        if (services.GetService<ManagedPlayerbotFeature>() is not { } feature || services.GetService<IPlayerbotTravel>() is not { } travel)
        {
            context.Reply("Playerbot service is unavailable.");
            return true;
        }

        if (!TryFind(feature, id, out PlayerbotStatus? status) || status is null)
        {
            context.Reply("Playerbot not found.");
            return true;
        }

        if (feature.FindSession(status.BotId) is not { IsManaged: true } session || session.Player is not { IsInWorld: true, Map: not null } bot)
        {
            context.Reply($"Playerbot travel failed: {status.Name} is not running.");
            return true;
        }

        // Only the brain drives a journey: a bot another driver holds would have it cancelled at its next tick, so it is refused here.
        if (feature.HeldFromBrain(status.BotId) is { } held)
        {
            context.Reply($"Playerbot travel refused: {status.Name}: {held}.");
            return true;
        }

        var goal = new System.Numerics.Vector3(x, y, z);
        if (!travel.TryBegin(session, bot, mapId, goal, out string? why))
        {
            context.Reply($"Playerbot travel refused: {status.Name}: {why}");
            return true;
        }

        CultureInfo c = CultureInfo.InvariantCulture;
        if (PlayerbotTravel.JourneyOf(bot) is not { State: JourneyState.Active } journey || journey.Goal.DistanceTo(new TravelPlace(mapId, goal)) > PlayerbotTravelPlanner.ArrivedYards)
        {
            context.Reply($"Playerbot travel: {status.Name} already stands at {PlayerbotTravel.Describe(new TravelPlace(mapId, goal))}.");
            return true;
        }

        TravelRoute route = journey.Planned;
        context.Reply(string.Create(c,
            $"Playerbot travel: {status.Name} to {PlayerbotTravel.Describe(journey.Goal)}: {route.Legs.Count} leg(s), about {route.Seconds:F0} s, {route.Copper} copper."));
        for (int i = 0; i < route.Legs.Count; i++) context.Reply(string.Create(c, $"{i + 1}. {PlayerbotTravel.Describe(route.Legs[i])}"));
        return true;
    }

    /// <summary><c>.playerbot groups</c>: the coordinator's report (world thread, where commands run).</summary>
    private static bool GroupsCommand(CommandContext context, string text)
    {
        if (text.Trim().Length != 0) return false;
        if (context.Session.Services.GetService<Groups.PlayerbotGroupCoordinator>() is not { } coordinator)
        {
            context.Reply("Playerbot groups are unavailable.");
            return true;
        }

        foreach (string line in coordinator.Report()) context.Reply(line);
        return true;
    }

    /// <summary>
    /// <c>.playerbot home $name|$prefix* &lt;$tele|here&gt; [#radius] [zone|nozone]</c> and <c>... clear</c> (Administrator; world thread,
    /// where commands run). The selector is one bot or every bot whose name starts with the prefix, running or not. A tele name is a
    /// <c>game_tele</c> row (the place's map and position become the centre); <c>here</c> is each matched running bot's own position.
    /// A trailing number is the radius in yards; <c>nozone</c> turns off the zone clip (on by default: the area is clipped to the zone
    /// of its centre, <see cref="PlayerbotHomeArea"/>), and a clipped home whose centre has no zone the terrain knows is refused. The
    /// command is accepted at once and the homes are stored (characters schema 56, zone clip 58) and applied to the running brains off
    /// the world thread, as the other lifecycle commands are.
    /// </summary>
    private static bool Home(CommandContext context, string text)
    {
        string[] tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return false;
        ManagedPlayerbotFeature? service = context.Session.Services.GetService<ManagedPlayerbotFeature>();
        if (service is null || !service.HomesAvailable)
        {
            context.Reply("Playerbot homes are unavailable.");
            return true;
        }

        if (!service.TryMatchBots(tokens[0], out IReadOnlyList<PlayerbotStatus> matches))
        {
            context.Reply("Playerbot home: name a bot, give its id, or use a prefix of 2 to 11 letters with one trailing * (Wfbot*).");
            return true;
        }

        if (matches.Count == 0)
        {
            context.Reply("Playerbot not found.");
            return true;
        }

        string[] rest = tokens[1..];
        if (rest.Length == 1 && rest[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            Guid[] ids = [.. matches.Select(match => match.BotId)];
            context.Reply("Playerbot home accepted.");
            _ = Task.Run(async () =>
            {
                try { context.Reply($"Playerbot home: {await service.ClearHomesAsync(ids).ConfigureAwait(false)} cleared."); }
                catch (ManagedPlayerbotFeature.PlayerbotSplitBrainException error) { context.Reply($"Playerbot home refused: split brain is latched (see .playerbot splitbrain); {error.Done} cleared before it."); }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("playerbot.commands", error);
                    service.ReportCommandFault("home clear", error);
                    context.Reply($"Playerbot home failed: {error.GetType().Name}.");
                }
            });
            return true;
        }

        // Trailing options, in either order: the radius and zone|nozone (the zone clip, on by default).
        float radius = PlayerbotHomeBounds.DefaultRadius;
        bool zoneClipped = true, radiusGiven = false, zoneGiven = false;
        while (rest.Length >= 2)
        {
            if (!zoneGiven && rest[^1].Equals("zone", StringComparison.OrdinalIgnoreCase)) zoneGiven = true;
            else if (!zoneGiven && rest[^1].Equals("nozone", StringComparison.OrdinalIgnoreCase)) { zoneGiven = true; zoneClipped = false; }
            else if (!radiusGiven && float.TryParse(rest[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
            { radiusGiven = true; radius = parsed; }
            else break;
            rest = rest[..^1];
        }

        if (!float.IsFinite(radius) || radius < PlayerbotHomeBounds.MinRadius || radius > PlayerbotHomeBounds.MaxRadius)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture,
                $"Playerbot home: the radius must be between {PlayerbotHomeBounds.MinRadius:F0} and {PlayerbotHomeBounds.MaxRadius:F0} yards."));
            return true;
        }

        string place = string.Join(' ', rest);
        var targets = new List<ManagedPlayerbotFeature.HomeTarget>(matches.Count);
        int skipped = 0, unzoned = 0;
        // A zone-clipped home whose centre has no known zone would contain nothing (PlayerbotHomeArea.ZoneUnknown): refused here, on
        // the world thread where the terrain is read, before anything is stored.
        bool ZoneKnown(uint map, System.Numerics.Vector3 centre)
        {
            if (!zoneClipped || service.ZoneAt(map, centre) != 0) return true;
            unzoned++;
            return false;
        }

        string? placeName;
        string description;
        if (place.Equals("here", StringComparison.OrdinalIgnoreCase))
        {
            foreach (PlayerbotStatus match in matches)
            {
                if (service.TryGetPosition(match.BotId, out uint map, out System.Numerics.Vector3 position)
                    && float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z)
                    && service.AllowsHomeMap(map))
                {
                    if (ZoneKnown(map, position)) targets.Add(new(match.BotId, map, position));
                }
                else
                    skipped++;
            }

            placeName = null;
            description = string.Create(CultureInfo.InvariantCulture, $"here, radius {radius:F0}, {(zoneClipped ? "zone clipped" : "no zone clip")}");
            if (targets.Count == 0)
            {
                context.Reply($"Playerbot home: 0 set, {skipped} skipped (not running){Unzoned(unzoned)}.");
                return true;
            }
        }
        else
        {
            ArcaneCore.Kernel.WorldData.GameTele? tele;
            try { tele = context.Session.Services.GetService<Teleport.TeleportFeature>()?.Maps.FindGameTele(place); }
            catch (InvalidOperationException) { context.Reply("Playerbot homes are unavailable."); return true; }
            if (tele is null)
            {
                context.Reply(Gm.Core.GmStrings.TeleNotFound);
                return true;
            }

            if (!float.IsFinite(tele.X) || !float.IsFinite(tele.Y) || !float.IsFinite(tele.Z))
            {
                context.Reply("Playerbot home: the teleport location has invalid coordinates.");
                return true;
            }

            if (!service.AllowsHomeMap(tele.MapId))
            {
                context.Reply($"Playerbot home: map {tele.MapId} is not a map playerbots may enter.");
                return true;
            }

            var centre = new System.Numerics.Vector3(tele.X, tele.Y, tele.Z);
            if (!ZoneKnown(tele.MapId, centre))
            {
                context.Reply($"Playerbot home: the terrain knows no zone at {tele.Name}; a zone-clipped home there would contain nothing (add nozone).");
                return true;
            }

            foreach (PlayerbotStatus match in matches)
                targets.Add(new(match.BotId, tele.MapId, centre));
            placeName = tele.Name;
            string zoneText = zoneClipped ? $"zone {service.ZoneAt(tele.MapId, centre)}" : "no zone clip";
            description = string.Create(CultureInfo.InvariantCulture,
                $"{tele.Name} map {tele.MapId} at {tele.X:F1},{tele.Y:F1},{tele.Z:F1} radius {radius:F0}, {zoneText}");
        }

        context.Reply("Playerbot home accepted.");
        _ = Task.Run(async () =>
        {
            try
            {
                ManagedPlayerbotFeature.HomeOutcome outcome = await service.SetHomesAsync(targets, radius, placeName, default, zoneClipped)
                    .ConfigureAwait(false);
                context.Reply($"Playerbot home: {outcome.Set} set ({description}), {skipped} skipped (not running)"
                    + Unzoned(unzoned) + (outcome.Failed != 0 ? $", {outcome.Failed} failed." : "."));
            }
            catch (ManagedPlayerbotFeature.PlayerbotSplitBrainException error) { context.Reply($"Playerbot home refused: split brain is latched (see .playerbot splitbrain); {error.Done} set before it."); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("playerbot.commands", error);
                service.ReportCommandFault("home set", error);
                context.Reply($"Playerbot home failed: {error.GetType().Name}.");
            }
        });
        return true;
    }

    private static string Unzoned(int count)
        => count == 0 ? string.Empty : $", {count} refused (no zone known at the bot's position; add nozone)";

    /// <summary><c>.playerbot battlegrounds</c>: the battlegrounds coordinator's report (world thread, where commands run).</summary>
    private static bool BattlegroundsCommand(CommandContext context, string text)
    {
        if (text.Trim().Length != 0) return false;
        if (context.Session.Services.GetService<Battlegrounds.PlayerbotBattlegroundCoordinator>() is not { } coordinator)
        {
            context.Reply("Playerbot battlegrounds are unavailable.");
            return true;
        }

        foreach (string line in coordinator.Report()) context.Reply(line);
        return true;
    }

    private static async Task InspectAndReplyAsync(CommandContext context, ManagedPlayerbotFeature service, string id)
    {
        try
        {
            await InspectReplyCoreAsync(context, service, id).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            context.Reply("Playerbot inspection is temporarily unavailable.");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.commands", error);
            service.ReportCommandFault("inspect", error);
            context.Reply("Playerbot inspection is temporarily unavailable.");
        }
    }

    private static async Task InspectReplyCoreAsync(CommandContext context, ManagedPlayerbotFeature service, string id)
    {
        PlayerbotInspection? value = await service.InspectAsync(id).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        if (value is null) { context.Reply("Playerbot is not running or was not found."); return; }
        context.Reply(FormattableString.Invariant($"BOTINSPECT {value.Name} goal={value.Goal} report={value.ReportedTarget} quest={value.QuestId} map={value.MapId} level={value.Level} hp={value.Health}/{value.MaxHealth} money={value.Money} combat={value.InCombat} ghost={value.Ghost}"));
        context.Reply(FormattableString.Invariant($"BOTINSPECT death={value.DeathState} pos={value.PlayerX:F2},{value.PlayerY:F2},{value.PlayerZ:F2}"));
        context.Reply(FormattableString.Invariant($"BOTINSPECT movement=flags:{(uint)value.MovementFlags:X8} stand:{value.StandState} time:{value.MovementTimeMs} following:{value.Following} loops:{value.LoopsGivenUp}"));
        context.Reply($"BOTINSPECT stall={value.Stall ?? "none"}");
        context.Reply($"BOTINSPECT {value.Risk ?? "decision=none"}");
        context.Reply($"BOTINSPECT {value.Group ?? "group=none"}");
        context.Reply($"BOTINSPECT home={value.Home ?? "none"}");
        context.Reply($"BOTINSPECT defend={value.Defend ?? "none"}");
        context.Reply($"BOTINSPECT budget={value.Budget ?? "none"}");
        context.Reply($"BOTINSPECT economy={value.Economy ?? "none"}");
        context.Reply(value.Master is null ? "BOTINSPECT party=none"
            : $"BOTINSPECT party=master:{value.Master} mode:{value.PartyMode?.ToString().ToLowerInvariant()}");
        context.Reply($"BOTINSPECT anomalies={value.Anomalies ?? "none"}");
        if (value.Corpse is { } corpse)
        {
            string distance = corpse.Distance?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable";
            context.Reply(FormattableString.Invariant($"BOTINSPECT corpse={corpse.Guid:X} map={corpse.MapId} pos={corpse.X:F2},{corpse.Y:F2},{corpse.Z:F2} distance={distance}"));
            context.Reply($"BOTINSPECT delay_remaining_s={corpse.ReclaimDelayRemainingSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"}");
        }
        else
            context.Reply("BOTINSPECT corpse=none");
        context.Reply($"BOTINSPECT target={Facts(value.Target)} victim={Facts(value.Victim)}");
        context.Reply($"BOTINSPECT attackers=count={value.AttackerCount} shown={value.Attackers.Count}");
        foreach (PlayerbotUnitFacts attacker in value.Attackers)
            context.Reply($"BOTINSPECT attacker={Facts(attacker)}");
        context.Reply(FormattableString.Invariant($"BOTINSPECT equipment=mainhand={ItemFacts(value.Equipment.MainHand)} mainhand_skill={value.Equipment.MainHandWeaponSkill?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"} armor={value.Equipment.TotalArmor?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"} feet={ItemFacts(value.Equipment.Feet)}"));
        context.Reply($"BOTINSPECT cast={value.CurrentCast} melee={value.MeleeCast} knownCount={value.KnownSpells.Count}");
        foreach (uint[] batch in value.KnownSpells.Chunk(24))
            context.Reply($"BOTINSPECT known={string.Join(',', batch)}");
        foreach (PlayerbotNpcFacts npc in value.NearbyServices)
        {
            context.Reply(FormattableString.Invariant($"BOTINSPECT npc={npc.Entry} guid={npc.Guid:X} flags={npc.Flags:X} class={npc.TrainerClass} type={npc.TrainerType} hostile={npc.Hostile} combat={npc.InCombat} distance={npc.Distance:F1}"));
            foreach (PlayerbotTeacherFacts teacher in npc.Teaching)
                context.Reply($"BOTINSPECT teach={teacher.TeachingSpell} learned={teacher.LearnedSpell} known={teacher.Known} fit={teacher.FitsClassRace} level={teacher.RequiredLevel} cost={teacher.Cost}");
        }
    }

    private static string Facts(PlayerbotUnitFacts? unit) => unit is null ? "none"
        : $"{unit.Kind}/{unit.Guid:X}/entry:{unit.Entry}/npc:{unit.NpcFlags:X}/faction:{unit.Faction}/type:{unit.CreatureType}";

    private static string ItemFacts(PlayerbotItemFacts? item) => item is null ? "none"
        : $"entry:{item.Entry}/durability:{item.Durability}/{item.MaxDurability}";

    private static bool StartOperation(CommandContext context, Func<IPlayerbotService, Task<PlayerbotOperationResult>> operation,
        string verb)
    {
        IPlayerbotService? service = context.Session.Services.GetService<IPlayerbotService>();
        if (service is null)
        {
            context.Reply("Playerbot service is unavailable.");
            return true;
        }

        context.Reply($"Playerbot {verb} accepted.");
        _ = Task.Run(() => CompleteAsync(context, service, operation));
        return true;
    }

    private static async Task CompleteAsync(CommandContext context, IPlayerbotService service,
        Func<IPlayerbotService, Task<PlayerbotOperationResult>> operation)
    {
        try
        {
            PlayerbotOperationResult result = await operation(service).ConfigureAwait(false);
            string identity = result.Name is null ? result.BotId?.ToString() ?? "unknown" : result.Name;
            context.Reply($"Playerbot {(result.Success ? "ok" : "failed")}: {result.Code} ({identity}).");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.commands", error);
            if (service is ManagedPlayerbotFeature managed) managed.ReportCommandFault("operation", error);
            context.Reply($"Playerbot failed: {error.GetType().Name}.");
        }
    }

    private static bool TryFind(IPlayerbotService service, string text, out PlayerbotStatus? status)
    {
        status = null;
        string id = text.Trim();
        if (Guid.TryParse(id, out Guid botId))
            status = service.Snapshot().FirstOrDefault(item => item.BotId == botId);
        else
            status = service.Snapshot().FirstOrDefault(item => item.Name.Equals(id, StringComparison.OrdinalIgnoreCase));
        return status is not null;
    }

    private static bool TryRequiredId(string text, out string id)
    {
        CommandArgs args = new(text);
        id = args.ExtractArg() ?? string.Empty;
        return id.Length != 0 && args.IsEmpty;
    }

    private static bool TryOptionalRaceClass(CommandArgs args, out byte race, out byte characterClass)
    {
        race = 1;
        characterClass = 1;
        if (args.IsEmpty) return true;
        if (!args.ExtractUInt32(out uint rawRace) || rawRace > byte.MaxValue
            || !args.ExtractUInt32(out uint rawClass) || rawClass > byte.MaxValue || !args.IsEmpty)
            return false;
        race = (byte)rawRace;
        characterClass = (byte)rawClass;
        return true;
    }

    /// <summary>The last line of the full <c>.playerbot status</c>/<c>list</c> reply, followed by the bot count.</summary>
    internal const string StatusTerminator = "Playerbot status end: ";

    private static string Format(PlayerbotStatus status)
        => string.Create(CultureInfo.InvariantCulture,
            $"{status.BotId} {status.Name} state={status.State} desired={status.DesiredEnabled} goal={status.Goal} "
            + $"target={status.TargetEntry} quest={status.QuestId} map={status.MapId} health={status.Health} "
            + $"error={status.ErrorCode ?? "none"}{(status.Risk is { } risk ? " " + risk : string.Empty)}"
            + $"{(status.Group is { } group ? " " + group : string.Empty)}"
            + $"{(status.Home is { } home ? " home=" + home : string.Empty)}");
}
