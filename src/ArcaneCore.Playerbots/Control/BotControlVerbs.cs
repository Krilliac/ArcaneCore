using ArcaneCore.World.Net;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using static ArcaneCore.World.Playerbots.Control.BotControlDispatch;

namespace ArcaneCore.World.Playerbots.Control;

/// <summary>The <c>.botctl</c> verbs (docs/ops/botctl.md, "Verbs").</summary>
internal static partial class BotControlVerbs
{
    private const AccountSecurity Read = AccountSecurity.GameMaster;
    private const AccountSecurity Drive = AccountSecurity.Administrator;

    internal static IReadOnlyList<BotVerb> All { get; } =
    [
        new("help", Read, "[verb]", "List the verbs, or show one verb's syntax.", false, Help),
        new("list", Read, "[bots]", "List playerbots (all, or the selection): running, controlled, owner, map, level.", false, List),
        new("take", Drive, "<bots> [passive|defensive] [--start] [--force]", "Take the bots over: their own AI pauses and they do only what .botctl tells them. --start starts a stopped bot scripted; --force takes a bot another controller (a scenario) drives.", true, Take),
        new("release", Drive, "<bots>", "Hand the bots back to their own AI.", true, Release),
        new("mode", Drive, "<bots> passive|defensive", "How a controlled bot reacts on its own: passive (nothing) or defensive (attacks back whatever attacks it).", true, Mode, 1),
        new("stop", Drive, "<bots>", "End the motion task (move/follow) and stop where the bot stands.", true, Stop),
        new("move", Drive, "<bots> <x> <y> <z> [tolerance] | <bots> <unit> [tolerance]", "Walk to a point (or to a unit's position) on the bot's map with its own navigation. Then: .botctl wait <bots> arrived.", true, Move, 1),
        new("tele", Drive, "<bots> <map> <x> <y> <z> [o] | <bots> <unit|player>", "Teleport the bots (like .tele / .summon).", true, Tele, 1),
        new("follow", Drive, "<bots> <unit> [distance]", "Follow a unit (player, bot, creature) with its own navigation until stopped.", true, Follow, 1),
        new("target", Drive, "<bots> <unit>|clear", "Select a unit (CMSG_SET_SELECTION).", true, Target, 1),
        new("attack", Drive, "<bots> [unit]", "Auto-attack the unit (default: the current target).", true, Attack),
        new("stopattack", Drive, "<bots>", "Stop auto-attacking.", true, StopAttack),
        new("cast", Drive, "<bots> <spell> [unit|self|at <x> <y> <z>]", "Cast a spell (CMSG_CAST_SPELL) at a unit, the bot itself (default when no target) or a point. The server's refusal is the result.", true, Cast, 1),
        new("cancelaura", Drive, "<bots> <spell>", "Remove one of the bot's own auras (right-click a buff).", true, CancelAura, 1),
        new("use", Drive, "<bots> <item entry> [unit|self]", "Use a carried item (CMSG_USE_ITEM).", true, Use, 1),
        new("equip", Drive, "<bots> <item entry>", "Equip a carried item (CMSG_AUTOEQUIP_ITEM).", true, Equip, 1),
        new("interact", Drive, "<bots> <unit|object>", "Right-click: gossip / quest giver hello for a creature (vendor and trainer windows too), use for a game object.", true, Interact, 1),
        new("gossip", Drive, "<bots> <npc> <option index> [code]", "Pick a gossip option (index as listed by .botctl menu / the gossip event).", true, Gossip, 2),
        new("menu", Read, "<bots>", "The gossip menu the server holds open for the bot (options and quests).", true, Menu),
        new("quest", Drive, "<bots> accept|complete|reward|query|hello <npc> [quest] [choice] | abandon <quest>", "Quest giver actions: accept, complete (request the reward), reward (choose a reward), query, hello; abandon from the log.", true, Quest, 1),
        new("loot", Drive, "<bots> [unit|corpse] [--keep-open]", "Open a loot window, take the money and every item, close it.", true, Loot),
        new("buy", Drive, "<bots> <vendor> <item entry> [count]", "Buy from a vendor (CMSG_BUY_ITEM).", true, Buy, 2),
        new("sell", Drive, "<bots> <vendor> <item entry> [count]", "Sell a carried item to a vendor (CMSG_SELL_ITEM).", true, Sell, 2),
        new("train", Drive, "<bots> <trainer> <spell>", "Learn a spell from a trainer (CMSG_TRAINER_BUY_SPELL; the trainer's spell id).", true, Train, 2),
        new("say", Drive, "<bots> <text>", "Say (the bot's own language).", true, (c) => Chat(c, ChatType.Say), 1),
        new("yell", Drive, "<bots> <text>", "Yell.", true, (c) => Chat(c, ChatType.Yell), 1),
        new("party", Drive, "<bots> <text>", "Party chat.", true, (c) => Chat(c, ChatType.Party), 1),
        new("guild", Drive, "<bots> <text>", "Guild chat.", true, (c) => Chat(c, ChatType.Guild), 1),
        new("whisper", Drive, "<bots> <player> <text>", "Whisper a player.", true, (c) => Chat(c, ChatType.Whisper), 2),
        new("emote", Drive, "<bots> <text emote id> [unit]", "A text emote (EmotesText.dbc id, e.g. 101 wave).", true, Emote, 1),
        new("invite", Drive, "<bots> <player>", "Invite a player into the bot's group.", true, Invite, 1),
        new("accept", Drive, "<bots>", "Accept a pending group invite.", true, (c) => Simple(c, "accept invite", WorldOpcode.CmsgGroupAccept)),
        new("decline", Drive, "<bots>", "Decline a pending group invite.", true, (c) => Simple(c, "decline invite", WorldOpcode.CmsgGroupDecline)),
        new("leave", Drive, "<bots>", "Leave the group.", true, (c) => Simple(c, "leave group", WorldOpcode.CmsgGroupDisband)),
        new("bg", Drive, "<bots> join <battlemaster> <map> [group] | port <map> enter|leave | leave <map> | status", "Battleground queue: join at a battlemaster, enter or leave when invited, leave the battleground.", true, Battleground, 1),
        new("repop", Drive, "<bots>", "Release the spirit (CMSG_REPOP_REQUEST).", true, (c) => Simple(c, "release spirit", WorldOpcode.CmsgRepopRequest, [0])),
        new("reclaim", Drive, "<bots>", "Resurrect at the corpse (CMSG_RECLAIM_CORPSE; the ghost must be near it).", true, Reclaim),
        new("sit", Drive, "<bots>", "Sit down.", true, (c) => Simple(c, "sit", WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes((uint)StandState.Sit))),
        new("stand", Drive, "<bots>", "Stand up.", true, (c) => Simple(c, "stand", WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes((uint)StandState.Stand))),
        new("raw", AccountSecurity.Administrator, "<bots> <CMSG_NAME|opcode> [hex payload]", "Send any client opcode with a raw payload (debugging; the server's handler validates it).", true, Raw, 1),
        new("state", Read, "<bots> [--full]", "Position, health/power, combat, target, casting, motion task, last errors (--full: auras, quests, inventory).", true, State),
        new("quests", Read, "<bots>", "The quest log: id, title, status, objective counts.", true, (c) => Part(c, "quests")),
        new("inventory", Read, "<bots>", "Every carried and equipped item: entry, name, count, bag, slot, guid.", true, (c) => Part(c, "inventory")),
        new("auras", Read, "<bots>", "The bot's auras: spell, stacks, duration, positive, caster.", true, (c) => Part(c, "auras")),
        new("path", Read, "<bots>", "The motion task: kind, state, destination, distance, plan failures.", true, Path),
        new("errors", Read, "<bots> [count]", "The last errors the server sent the bot (refused casts, attacks, purchases, quests ...).", true, (c) => Events(c, errorsOnly: true)),
        new("events", Read, "<bots> [since <seq>] [limit <n>] [kind <prefix>]", "What the server sent and the bot did, newest last (seq numbers are global; pass the last one as since).", true, (c) => Events(c, errorsOnly: false)),
        new("stream", Read, "<bots> on|off [--errors]", "Push the bots' events to this console/RA connection as '#event' lines until off (or the connection closes).", true, Stream, 1),
        new("alias", Drive, "<name> <bots> | <name> clear | list", "Name a selection: '@name' then selects it.", false, Alias, 1),
        new("format", Read, "json|text", "The default output of this connection's .botctl commands (per session).", false, Format, 1),
        new("wait", Read, "<bots> <condition> [timeout <s>] [--any] [--failonerror]", "Wait until a condition holds for every bot (--any: one). Conditions: arrived, idle, following, combat, nocombat, alive, dead, dead <unit>, targetdead, hp|power <op> <pct>, quest <id> <status>, item <entry> [<op>] <n>, aura <spell>, noaura <spell>, level <op> <n>, map <id>, near <x> <y> <z> [r] | near <unit> [r], casting, notcasting, event <kind>, invited. Default timeout 30 s.", true, Wait, 1),
        new("sleep", Read, "<milliseconds>", "Pause (in .botctl run sequences).", false, Sleep, 1),
        new("run", Drive, "<bots> <step> ; <step> ; ... [--continue]", "Run verbs one after another for the same bots ('take ; move 1 2 3 ; wait arrived timeout 60 ; cast 133 target'); stops at the first failure unless --continue.", true, Run, 1),
    ];

    internal static BotVerb? Find(string name) => All.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string I(FormattableString text) => FormattableString.Invariant(text);

    // --- control -----------------------------------------------------------------------------------------------

    private static Task<BotControlResult> Help(BotCall call)
    {
        var result = new BotControlResult("help");
        if (call.Args.Length > 0)
        {
            if (Find(call.Args[0]) is not { } verb) return Task.FromResult(result.Fail("unknown-verb", "no verb '" + call.Args[0] + "'"));
            result.Data = new JsonObject { ["verb"] = verb.Name, ["syntax"] = ".botctl " + verb.Name + " " + verb.Syntax, ["summary"] = verb.Summary, ["security"] = verb.Security.ToString() };
            return Task.FromResult(result);
        }

        var verbs = new JsonArray();
        foreach (BotVerb verb in All.Where(v => call.Context.Security >= v.Security))
            verbs.Add(new JsonObject { ["verb"] = verb.Name, ["syntax"] = verb.Syntax });
        result.Data = new JsonObject { ["verbs"] = verbs };
        return Task.FromResult(result.Say("bots: name | bot id | Prefix* | all | running | controlled | @alias | where:map=0&level=10-20&class=mage | a,b,c"));
    }

    private static Task<BotControlResult> List(BotCall call) => call.World.InvokeAsync(() =>
    {
        var result = new BotControlResult("list");
        List<Picked> bots;
        if (call.Args.Length > 0)
        {
            BotCall selection = call.With(call.Verb, call.Args[0], [], string.Empty, [.. call.Flags, "--force"]);
            if (Select(selection, out bots) is { } error) return result.Fail(error.Split(':')[0], error);
        }
        else
        {
            bots = [.. call.Feature.Snapshot().OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s => new Picked(s.BotId, s.Name))];
        }

        foreach (Picked bot in bots)
        {
            BotOutcome outcome = result.Add(bot.Name);
            BotController? controller = call.Hub.Find(bot.BotId);
            Player? player = call.Feature.FindSession(bot.BotId)?.Player;
            bool running = call.Feature.IsActive(bot.BotId);
            string state = controller is not null ? "controlled by " + controller.Owner : running ? "autonomous" : "stopped";
            if (call.Json)
            {
                var data = new JsonObject
                {
                    ["botId"] = bot.BotId.ToString("D"),
                    ["running"] = running,
                    ["controlled"] = controller is not null,
                };
                if (controller is not null) data["owner"] = controller.Owner;
                if (player is not null)
                {
                    data["map"] = player.MapId;
                    data["level"] = player.Level;
                    data["class"] = player.Class.ToString();
                    data["alive"] = player.IsAlive;
                }

                outcome.Data = data;
                outcome.Success(state);
            }
            else
            {
                string summary = player is not null
                    ? I($"{state} map={player.MapId} level={player.Level} class={player.Class} alive={(player.IsAlive ? "true" : "false")} botId={bot.BotId:D}")
                    : I($"{state} botId={bot.BotId:D}");
                outcome.Success(summary);
            }
        }

        return result.Say(I($"{bots.Count} bots, {call.Hub.Controlled.Count} controlled"));
    }, call.Token);

    private static async Task<BotControlResult> Take(BotCall call)
    {
        BotControlMode? mode = call.Args.Length > 0 ? ParseMode(call.Args[0]) : BotControlMode.Passive;
        if (mode is null) return new BotControlResult("take").Fail("syntax", "mode is passive or defensive");
        var result = new BotControlResult("take");
        List<Picked> bots = [];
        string? error = await call.World.InvokeAsync(() => Select(call, out bots), call.Token).ConfigureAwait(false);
        if (error is not null) return result.Fail(error.Split(':')[0], error);
        foreach (Picked bot in bots)
        {
            BotOutcome outcome = result.Add(bot.Name);
            (BotController? controller, string? refusal) = await call.Hub.TakeAsync(bot.BotId, bot.Name, call.Context.InvokerName,
                call.Has("--start"), call.Has("--force")).ConfigureAwait(false);
            if (controller is null)
            {
                outcome.Failure(refusal!.Split(':')[0], refusal switch
                {
                    "not-running" => "the bot is not running (add --start to start it scripted)",
                    "scripted-elsewhere" => "another controller (a scenario) drives it (add --force)",
                    _ => refusal,
                });
                continue;
            }

            controller.Mode = mode.Value;
            outcome.Success("controlled (" + mode.Value.ToString().ToLowerInvariant() + "); its own AI is paused");
        }

        return result;
    }

    private static async Task<BotControlResult> Release(BotCall call)
    {
        var result = new BotControlResult("release");
        List<Picked> bots = [];
        string? error = await call.World.InvokeAsync(() => Select(call, out bots), call.Token).ConfigureAwait(false);
        if (error is not null) return result.Fail(error.Split(':')[0], error);
        foreach (Picked bot in bots)
        {
            BotOutcome outcome = result.Add(bot.Name);
            if (call.Hub.Find(bot.BotId) is null)
            {
                outcome.Success("was not controlled");
                continue;
            }

            if (await call.Hub.ReleaseAsync(bot.BotId).ConfigureAwait(false)) outcome.Success("its own AI drives it again");
            else outcome.Failure("not-running", "the bot stopped");
        }

        return result;
    }

    private static BotControlMode? ParseMode(string text) => text.ToLowerInvariant() switch
    {
        "passive" or "manual" or "hold" => BotControlMode.Passive,
        "defensive" or "defend" => BotControlMode.Defensive,
        _ => null,
    };

    private static Task<BotControlResult> Mode(BotCall call)
    {
        if (ParseMode(call.Args[0]) is not { } mode) return Task.FromResult(new BotControlResult("mode").Fail("syntax", "mode is passive or defensive (release hands a bot back to its own AI)"));
        return OnBots(call, (view, outcome) =>
        {
            view.Controller!.Mode = mode;
            view.Controller.Record("control", "mode " + mode.ToString().ToLowerInvariant(), error: false);
            return outcome.Success(mode.ToString().ToLowerInvariant());
        }, inWorld: false);
    }

    private static Task<BotControlResult> Stop(BotCall call) => OnBots(call, (view, outcome) =>
    {
        view.Controller!.StopMotion("stopped by " + call.Context.InvokerName);
        return outcome.Success("stopped at " + BotController.Fmt(new Vector3(view.Player!.X, view.Player.Y, view.Player.Z)));
    });

    // --- movement ----------------------------------------------------------------------------------------------

    private static Task<BotControlResult> Move(BotCall call)
    {
        bool point = call.Args.Length >= 3 && TryFloat(call.Args[0], out _) && TryFloat(call.Args[1], out _) && TryFloat(call.Args[2], out _);
        float tolerance = 1.5f;
        string tolText = point ? call.Arg(3) : call.Arg(1);
        if (tolText.Length > 0 && !TryFloat(tolText, out tolerance)) return Task.FromResult(new BotControlResult("move").Fail("syntax", "tolerance is a number of yards"));
        return OnBots(call, (view, outcome) =>
        {
            Player player = view.Player!;
            Vector3 destination;
            if (point)
            {
                TryFloat(call.Args[0], out float x);
                TryFloat(call.Args[1], out float y);
                TryFloat(call.Args[2], out float z);
                destination = new(x, y, z);
            }
            else
            {
                if (BotControlUnits.Resolve(call.World, player, call.Args[0], out string? missing) is not { } unit) return outcome.Failure("no-unit", missing!);
                if (!ReferenceEquals(unit.Map, player.Map)) return outcome.Failure("other-map", "the unit is on another map (use tele)");
                destination = new(unit.X, unit.Y, unit.Z);
            }

            view.Controller!.MoveTo(player, destination, tolerance);
            view.Controller.Record("motion", "move to " + BotController.Fmt(destination), error: false);
            return outcome.Success(I($"walking to {BotController.Fmt(destination)} ({Vector2.Distance(new(player.X, player.Y), new(destination.X, destination.Y)):F1} yd)"));
        });
    }

    private static Task<BotControlResult> Tele(BotCall call) => OnBots(call, (view, outcome) =>
    {
        Player player = view.Player!;
        if (view.Session!.Services.GetService<TeleportFeature>()?.Teleports is not { } teleports) return outcome.Failure("unavailable", "teleports are unavailable");
        uint map;
        float x, y, z, o = player.Orientation;
        if (call.Args.Length >= 4 && TryUInt(call.Args[0], out map) && TryFloat(call.Args[1], out x) && TryFloat(call.Args[2], out y) && TryFloat(call.Args[3], out z))
        {
            if (call.Args.Length >= 5 && !TryFloat(call.Args[4], out o)) return outcome.Failure("syntax", "orientation is a number");
        }
        else
        {
            if (BotControlUnits.Resolve(call.World, player, call.Rest, out string? missing) is not { } unit) return outcome.Failure("no-unit", missing!);
            map = unit.Map?.MapId ?? (unit as Player)?.MapId ?? player.MapId;
            (x, y, z, o) = (unit.X, unit.Y, unit.Z, unit.Orientation);
        }

        view.Controller!.StopMotion("teleport");
        if (!teleports.TeleportTo(player, map, x, y, z, o)) return outcome.Failure("refused", I($"the teleport to map {map} was refused (unknown map, or a teleport is under way)"));
        view.Controller.Record("motion", I($"teleport to map {map} {BotController.Fmt(new(x, y, z))}"), error: false);
        return outcome.Success(I($"teleporting to map {map} {BotController.Fmt(new(x, y, z))}"));
    });

    private static Task<BotControlResult> Follow(BotCall call)
    {
        float distance = 3f;
        if (call.Args.Length > 1 && !TryFloat(call.Args[1], out distance)) return Task.FromResult(new BotControlResult("follow").Fail("syntax", "distance is a number of yards"));
        return OnBots(call, (view, outcome) =>
        {
            Player player = view.Player!;
            if (BotControlUnits.Resolve(call.World, player, call.Args[0], out string? missing) is not { } unit) return outcome.Failure("no-unit", missing!);
            if (ReferenceEquals(unit, player)) return outcome.Failure("syntax", "a bot cannot follow itself");
            if (!ReferenceEquals(unit.Map, player.Map)) return outcome.Failure("other-map", "the unit is on another map");
            view.Controller!.Follow(player, unit.Guid, distance);
            view.Controller.Record("motion", "follow " + BotControlState.Hex(unit.Guid.Value), error: false);
            return outcome.Success(I($"following {BotControlUnits.NameOf(unit)} {BotControlState.Hex(unit.Guid.Value)} at {distance:F0} yd"));
        });
    }

    // --- combat and spells -------------------------------------------------------------------------------------

    private static Task<BotControlResult> Target(BotCall call) => OnBots(call, (view, outcome) =>
    {
        if (call.Args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
            return Act(view, outcome, "target cleared", (WorldOpcode.CmsgSetSelection, ScenarioPackets.Guid(0)));
        if (BotControlUnits.Resolve(call.World, view.Player!, call.Rest, out string? missing) is not { } unit) return outcome.Failure("no-unit", missing!);
        return Act(view, outcome, "target " + Describe(unit), (WorldOpcode.CmsgSetSelection, ScenarioPackets.Guid(unit.Guid.Value)));
    });

    private static Task<BotControlResult> Attack(BotCall call) => OnBots(call, (view, outcome) =>
    {
        WorldObject? unit = call.Args.Length == 0
            ? view.Player!.Map?.FindObject(view.Player.Selection)
            : BotControlUnits.Resolve(call.World, view.Player!, call.Rest, out _);
        if (unit is null) return outcome.Failure("no-unit", call.Args.Length == 0 ? "the bot has no target" : "no such unit in sight");
        return Act(view, outcome, "attack " + Describe(unit),
            (WorldOpcode.CmsgSetSelection, ScenarioPackets.Guid(unit.Guid.Value)), (WorldOpcode.CmsgAttackswing, ScenarioPackets.Guid(unit.Guid.Value)));
    });

    private static Task<BotControlResult> StopAttack(BotCall call)
        => OnBots(call, (view, outcome) => Act(view, outcome, "attack stopped", (WorldOpcode.CmsgAttackstop, [])));

    private static Task<BotControlResult> Cast(BotCall call)
    {
        if (!TryUInt(call.Args[0], out uint spell) || spell == 0) return Task.FromResult(new BotControlResult("cast").Fail("syntax", "the spell is a spell id"));
        return OnBots(call, (view, outcome) =>
        {
            Player player = view.Player!;
            SpellCastTargets targets;
            string at;
            if (call.Args.Length >= 5 && call.Args[1].Equals("at", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryFloat(call.Args[2], out float x) || !TryFloat(call.Args[3], out float y) || !TryFloat(call.Args[4], out float z))
                    return outcome.Failure("syntax", "at <x> <y> <z>");
                targets = new SpellCastTargets { Mask = SpellCastTargetFlags.DestLocation, Dest = (x, y, z) };
                at = "at " + BotController.Fmt(new(x, y, z));
            }
            else if (call.Args.Length == 1 || call.Args[1].Equals("self", StringComparison.OrdinalIgnoreCase))
            {
                targets = SpellCastTargets.ForSelf();
                at = "on itself";
            }
            else
            {
                if (BotControlUnits.Resolve(call.World, player, string.Join(' ', call.Args.Skip(1)), out string? missing) is not { } unit) return outcome.Failure("no-unit", missing!);
                targets = unit switch
                {
                    GameObject => new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = unit.Guid },
                    _ when ReferenceEquals(unit, player) => SpellCastTargets.ForSelf(),
                    _ => SpellCastTargets.ForUnit(unit.Guid),
                };
                at = "at " + Describe(unit);
            }

            long mark = BotController.CurrentSequence;
            BotOutcome answer = Act(view, outcome, I($"cast {spell} {at}"), (WorldOpcode.CmsgCastSpell, ScenarioPackets.CastSpell(spell, targets)));
            if (!answer.Ok) return answer;

            // The server answers a cast it accepts (spell start/go, cast result) and refuses most others; one it ignores outright (a spell the
            // bot does not know) leaves no trace, which is a failure too.
            bool answered = view.Controller!.HasEvent(mark, e => e.Kind is "cast-ok" or "spell-go" || e.Kind.Contains("spell_start", StringComparison.OrdinalIgnoreCase)
                || e.Kind.Contains("spell-start", StringComparison.OrdinalIgnoreCase));
            if (!answered && BotControlState.Casting(view.Session!, player) != spell)
            {
                string ignored = I($"the server ignored the cast of {spell} (the bot does not know it, or it is not castable this way)");
                view.Controller.Record("cast-ignored", ignored, error: true);
                return outcome.Failure("ignored", ignored);
            }
            return answer;
        });
    }

    private static Task<BotControlResult> CancelAura(BotCall call)
    {
        if (!TryUInt(call.Args[0], out uint spell)) return Task.FromResult(new BotControlResult("cancelaura").Fail("syntax", "the spell is a spell id"));
        return OnBots(call, (view, outcome) => Act(view, outcome, I($"cancel aura {spell}"), (WorldOpcode.CmsgCancelAura, ScenarioPackets.UInt32(spell))));
    }

    // --- items -------------------------------------------------------------------------------------------------

    private static Task<BotControlResult> Use(BotCall call)
    {
        if (!TryUInt(call.Args[0], out uint entry)) return Task.FromResult(new BotControlResult("use").Fail("syntax", "the item is an item entry"));
        return OnBots(call, (view, outcome) =>
        {
            Player player = view.Player!;
            if (BotControlState.FindItem(player, entry) is not { } item) return outcome.Failure("no-item", I($"the bot carries no item {entry}"));
            SpellCastTargets targets = SpellCastTargets.ForSelf();
            if (call.Args.Length > 1 && !call.Args[1].Equals("self", StringComparison.OrdinalIgnoreCase))
            {
                if (BotControlUnits.Resolve(call.World, player, string.Join(' ', call.Args.Skip(1)), out string? missing) is not { } unit) return outcome.Failure("no-unit", missing!);
                targets = unit is GameObject
                    ? new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = unit.Guid }
                    : SpellCastTargets.ForUnit(unit.Guid);
            }

            return Act(view, outcome, I($"use {item.Template.Name} ({entry})"),
                (WorldOpcode.CmsgUseItem, PlayerbotNavigation.UseItemPayload(item.BagSlot, item.Slot, targets)));
        });
    }

    private static Task<BotControlResult> Equip(BotCall call)
    {
        if (!TryUInt(call.Args[0], out uint entry)) return Task.FromResult(new BotControlResult("equip").Fail("syntax", "the item is an item entry"));
        return OnBots(call, (view, outcome) =>
        {
            if (BotControlState.FindItem(view.Player!, entry) is not { } item) return outcome.Failure("no-item", I($"the bot carries no item {entry}"));
            return Act(view, outcome, I($"equip {item.Template.Name} ({entry})"), (WorldOpcode.CmsgAutoequipItem, [item.BagSlot, item.Slot]));
        });
    }

    // --- NPCs, objects, quests ---------------------------------------------------------------------------------

    private static string Describe(WorldObject unit) => (BotControlUnits.NameOf(unit) is { Length: > 0 } name ? name + " " : string.Empty) + BotControlState.Hex(unit.Guid.Value);

    private static Task<BotControlResult> Interact(BotCall call) => OnBots(call, (view, outcome) =>
    {
        if (BotControlUnits.Resolve(call.World, view.Player!, call.Rest, out string? missing) is not { } unit) return outcome.Failure("no-unit", missing!);
        byte[] guid = ScenarioPackets.Guid(unit.Guid.Value);
        if (unit is GameObject) return Act(view, outcome, "use " + Describe(unit), (WorldOpcode.CmsgGameobjUse, guid));
        if (unit is not Creature creature) return outcome.Failure("syntax", "interact needs a creature or a game object");
        var flags = (NpcFlags)creature.Template.NpcFlags;
        var packets = new List<(WorldOpcode, byte[])>();
        if ((flags & NpcFlags.Gossip) != 0) packets.Add((WorldOpcode.CmsgGossipHello, guid));
        else if ((flags & NpcFlags.QuestGiver) != 0) packets.Add((WorldOpcode.CmsgQuestgiverHello, guid));
        if ((flags & NpcFlags.Vendor) != 0 && (flags & NpcFlags.Gossip) == 0) packets.Add((WorldOpcode.CmsgListInventory, guid));
        if ((flags & NpcFlags.Trainer) != 0 && (flags & NpcFlags.Gossip) == 0) packets.Add((WorldOpcode.CmsgTrainerList, guid));
        if ((flags & NpcFlags.BattleMaster) != 0 && (flags & NpcFlags.Gossip) == 0) packets.Add((WorldOpcode.CmsgBattlemasterHello, guid));
        if (packets.Count == 0) packets.Add((WorldOpcode.CmsgGossipHello, guid));
        return Act(view, outcome, "interact " + Describe(unit), [.. packets]);
    });

    private static Task<BotControlResult> Gossip(BotCall call)
    {
        if (!TryUInt(call.Args[1], out uint option)) return Task.FromResult(new BotControlResult("gossip").Fail("syntax", "the option is an index"));
        string code = string.Join(' ', call.Args.Skip(2));
        return OnBots(call, (view, outcome) =>
        {
            if (BotControlUnits.Resolve(call.World, view.Player!, call.Args[0], out string? missing) is not { } unit) return outcome.Failure("no-unit", missing!);
            var w = new PacketWriter(16 + code.Length);
            w.WriteUInt64(unit.Guid.Value);
            w.WriteUInt32(option);
            if (code.Length > 0) w.WriteCString(code);
            return Act(view, outcome, I($"gossip option {option} at {Describe(unit)}"), (WorldOpcode.CmsgGossipSelectOption, w.ToArray()));
        });
    }

    private static Task<BotControlResult> Menu(BotCall call) => OnBots(call, (view, outcome) =>
    {
        outcome.Data = BotControlState.Menu(view.Session!, view.Player!);
        return outcome.Success();
    }, control: false);

    private static async Task<BotControlResult> Quest(BotCall call)
    {
        string action = call.Args[0].ToLowerInvariant();
        if (action == "abandon")
        {
            if (!TryUInt(call.Arg(1), out uint abandoned)) return new BotControlResult("quest").Fail("syntax", "quest abandon <quest id>");
            return await OnBots(call, (view, outcome) =>
            {
                if (view.Session!.Services.GetService<Npc.QuestNpcFeature>()?.Services.StateOf(view.Player!) is not { } state) return outcome.Failure("unavailable", "no quest log");
                int slot = state.Quests.FindSlot(abandoned);
                if (slot < 0 || slot >= ArcaneCore.Game.Quests.QuestConstants.MaxQuestLogSize) return outcome.Failure("no-quest", I($"quest {abandoned} is not in the log"));
                BotOutcome answer = Act(view, outcome, I($"abandon quest {abandoned}"), (WorldOpcode.CmsgQuestlogRemoveQuest, [(byte)slot]));
                if (answer.Ok && state.Quests.FindSlot(abandoned) < ArcaneCore.Game.Quests.QuestConstants.MaxQuestLogSize) return outcome.Failure("not-abandoned", I($"quest {abandoned} was not abandoned"));
                return answer;
            }).ConfigureAwait(false);
        }

        if (action is not ("accept" or "complete" or "reward" or "query" or "hello") || call.Args.Length < 2)
            return new BotControlResult("quest").Fail("syntax", "quest accept|complete|reward|query|hello <npc> [quest] [choice] | abandon <quest>");
        uint quest = 0, choice = 0;
        if (action != "hello" && (call.Args.Length < 3 || !TryUInt(call.Args[2], out quest)))
            return new BotControlResult("quest").Fail("syntax", "the quest is a quest id");
        if (call.Args.Length > 3 && !TryUInt(call.Args[3], out choice)) return new BotControlResult("quest").Fail("syntax", "the reward choice is an index");
        if (action == "reward")
        {
            var pending = new List<(Picked Bot, BotOutcome Outcome, Npc.QuestNpcFeature Feature, int CharId, long Mark)>();
            BotControlResult result = await OnBots(call, (view, outcome) =>
            {
                if (BotControlUnits.Resolve(call.World, view.Player!, call.Args[1], out string? missing) is not { } giver) return outcome.Failure("no-unit", missing!);
                Npc.QuestNpcFeature? feature = view.Session!.Services.GetService<Npc.QuestNpcFeature>();
                if (feature?.Services.DiagnoseTurnInRefusal(view.Player!, giver.Guid, quest, choice, feature.OfferedInTrade(view.Player!)) is { } refusal)
                    return outcome.Failure("not-rewarded", refusal);
                long mark = BotController.CurrentSequence;
                BotOutcome sent = Act(view, outcome, I($"choose reward {choice} of quest {quest}"), (WorldOpcode.CmsgQuestgiverChooseReward, ScenarioPackets.QuestChooseReward(giver.Guid.Value, quest, choice)));
                if (sent.Ok && feature is not null)
                    pending.Add((view.Bot, outcome, feature, (int)view.Player!.Guid.Low, mark));
                return sent;
            }).ConfigureAwait(false);
            foreach ((Picked bot, BotOutcome outcome, Npc.QuestNpcFeature feature, int charId, long mark) in pending)
            {
                await feature.WaitForSettlementAsync(charId, call.Token).ConfigureAwait(false);
                await call.World.InvokeAsync(() =>
                {
                    BotController? controller = call.Hub.Find(bot.BotId);
                    WorldSession? session = call.Feature.FindSession(bot.BotId);
                    Player? player = session?.Player;
                    if (controller is null || session is null || player is null) return false;
                    BotOutcome answer = Answer(controller, outcome, I($"choose reward {choice} of quest {quest}"), mark);
                    if (answer.Ok && BotControlState.QuestStatusOf(session, player, quest) != "Rewarded")
                        outcome.Failure("not-rewarded", I($"quest {quest} was not rewarded"));
                    return true;
                }, call.Token).ConfigureAwait(false);
            }

            return result;
        }

        return await OnBots(call, (view, outcome) =>
        {
            if (BotControlUnits.Resolve(call.World, view.Player!, call.Args[1], out string? missing) is not { } giver) return outcome.Failure("no-unit", missing!);
            ulong g = giver.Guid.Value;
            long mark = BotController.CurrentSequence;
            switch (action)
            {
                case "accept":
                {
                    BotOutcome answer = Act(view, outcome, I($"accept quest {quest}"), (WorldOpcode.CmsgQuestgiverAcceptQuest, ScenarioPackets.GuidUInt32(g, quest)));
                    if (answer.Ok && BotControlState.QuestStatusOf(view.Session!, view.Player!, quest) is "None" or "unknown")
                        return outcome.Failure("not-accepted", I($"quest {quest} was not accepted (out of range, or the NPC does not offer it)"));
                    return answer;
                }
                case "complete":
                {
                    BotOutcome answer = Act(view, outcome, I($"complete quest {quest}"), (WorldOpcode.CmsgQuestgiverCompleteQuest, ScenarioPackets.GuidUInt32(g, quest)));
                    if (answer.Ok && !view.Controller!.HasEvent(mark, e => e.Kind is "quest-request-items" or "quest-offer-reward"))
                        return outcome.Failure("not-completed", I($"quest {quest}: no turn-in dialog opened"));
                    return answer;
                }
                case "query":
                {
                    BotOutcome answer = Act(view, outcome, I($"query quest {quest}"), (WorldOpcode.CmsgQuestgiverQueryQuest, ScenarioPackets.GuidUInt32(g, quest)));
                    if (answer.Ok && !view.Controller!.HasEvent(mark, e => e.Kind == "quest-details"))
                        return outcome.Failure("no-details", I($"quest {quest}: no quest details returned"));
                    return answer;
                }
                default:
                    return Act(view, outcome, "quest giver hello " + Describe(giver), (WorldOpcode.CmsgQuestgiverHello, ScenarioPackets.Guid(g)));
            }
        }).ConfigureAwait(false);
    }

    private static Task<BotControlResult> Loot(BotCall call) => OnBots(call, (view, outcome) =>
    {
        Player player = view.Player!;
        string spec = call.Args.Length == 0 ? "corpse" : call.Rest;
        if (BotControlUnits.Resolve(call.World, player, spec, out string? missing) is not { } source) return outcome.Failure("no-unit", missing!);
        BotController controller = view.Controller!;
        long mark = BotController.CurrentSequence;
        BotOutcome opened = Act(view, outcome, "loot " + Describe(source), (WorldOpcode.CmsgLoot, ScenarioPackets.Guid(source.Guid.Value)));
        if (!opened.Ok) return opened;
        if (controller.LastLoot is not { } loot || loot.Source != source.Guid.Value)
            return outcome.Failure("no-loot", "the server opened no loot window for " + Describe(source));
        var taken = new List<string>();
        WorldSession session = view.Session!;
        session.ManagedBudget = null;
        if (loot.Gold > 0 && session.TryManagedAction(WorldOpcode.CmsgLootMoney, [])) taken.Add(I($"{loot.Gold} copper"));
        foreach (LootItemView item in loot.Items)
        {
            session.ManagedBudget = null;
            if (session.TryManagedAction(WorldOpcode.CmsgAutostoreLootItem, ScenarioPackets.LootSlot(item.Slot))) taken.Add(I($"{item.ItemId}x{item.Count}"));
        }

        if (!call.Has("--keep-open"))
        {
            session.ManagedBudget = null;
            session.TryManagedAction(WorldOpcode.CmsgLootRelease, ScenarioPackets.Guid(source.Guid.Value));
        }

        BotOutcome answer = Answer(controller, outcome, "looted " + (taken.Count == 0 ? "nothing" : string.Join(", ", taken)), mark);
        outcome.Data = new JsonObject
        {
            ["source"] = BotControlState.Hex(loot.Source),
            ["gold"] = loot.Gold,
            ["items"] = new JsonArray([.. loot.Items.Select(i => (JsonNode)new JsonObject { ["slot"] = i.Slot, ["item"] = i.ItemId, ["count"] = i.Count })]),
        };
        return answer;
    });

    private static Task<BotControlResult> Buy(BotCall call)
    {
        if (!TryUInt(call.Args[1], out uint entry)) return Task.FromResult(new BotControlResult("buy").Fail("syntax", "the item is an item entry"));
        uint count = 1;
        if (call.Args.Length > 2 && (!TryUInt(call.Args[2], out count) || count is 0 or > 255)) return Task.FromResult(new BotControlResult("buy").Fail("syntax", "count is 1..255"));
        return OnBots(call, (view, outcome) =>
        {
            if (BotControlUnits.Resolve(call.World, view.Player!, call.Args[0], out string? missing) is not { } vendor) return outcome.Failure("no-unit", missing!);
            var w = new PacketWriter(14);
            w.WriteUInt64(vendor.Guid.Value);
            w.WriteUInt32(entry);
            w.WriteByte((byte)count);
            w.WriteByte(0);
            long mark = BotController.CurrentSequence;
            uint before = view.Player!.Money;
            BotOutcome answer = Act(view, outcome, I($"buy {count}x {entry} from {Describe(vendor)}"), (WorldOpcode.CmsgBuyItem, w.ToArray()));
            if (answer.Ok && !view.Controller!.HasEvent(mark, e => e.Kind == "bought") && view.Player.Money == before)
                return outcome.Failure("not-bought", I($"buy {entry}: the money did not change (not sold there, or out of range)"));
            return answer;
        });
    }

    private static Task<BotControlResult> Sell(BotCall call)
    {
        if (!TryUInt(call.Args[1], out uint entry)) return Task.FromResult(new BotControlResult("sell").Fail("syntax", "the item is an item entry"));
        uint count = 0;
        if (call.Args.Length > 2 && (!TryUInt(call.Args[2], out count) || count > 255)) return Task.FromResult(new BotControlResult("sell").Fail("syntax", "count is 0..255 (0: the whole stack)"));
        return OnBots(call, (view, outcome) =>
        {
            if (BotControlUnits.Resolve(call.World, view.Player!, call.Args[0], out string? missing) is not { } vendor) return outcome.Failure("no-unit", missing!);
            if (BotControlState.FindItem(view.Player!, entry) is not { } item) return outcome.Failure("no-item", I($"the bot carries no item {entry}"));
            var w = new PacketWriter(17);
            w.WriteUInt64(vendor.Guid.Value);
            w.WriteUInt64(item.Guid.Value);
            w.WriteByte((byte)count);
            long beforeCount = BotControlState.ItemCount(view.Player!, entry);
            BotOutcome answer = Act(view, outcome, I($"sell {item.Template.Name} ({entry}) to {Describe(vendor)}"), (WorldOpcode.CmsgSellItem, w.ToArray()));
            if (answer.Ok && BotControlState.ItemCount(view.Player!, entry) >= beforeCount)
                return outcome.Failure("not-sold", I($"sell {entry}: the item was not sold"));
            return answer;
        });
    }

    private static Task<BotControlResult> Train(BotCall call)
    {
        if (!TryUInt(call.Args[1], out uint spell)) return Task.FromResult(new BotControlResult("train").Fail("syntax", "the spell is the trainer's spell id"));
        return OnBots(call, (view, outcome) =>
        {
            if (BotControlUnits.Resolve(call.World, view.Player!, call.Args[0], out string? missing) is not { } trainer) return outcome.Failure("no-unit", missing!);
            uint spellToBuy = spell;
            if (trainer is Creature c && view.Session!.Services.GetService<Npc.QuestNpcFeature>()?.Services is { } npcServices
                && npcServices.Deps.Spells is { } spells)
            {
                IReadOnlyList<Kernel.Npc.TrainerSpell> offers = npcServices.Npcs.TrainerSpells(c.Entry);
                if (offers.All(t => t.Spell != spellToBuy))
                {
                    uint match = offers.FirstOrDefault(t => spells.DescribeTrainerSpell(t.Spell)?.LearnedSpell == spellToBuy)?.Spell ?? 0;
                    if (match != 0) spellToBuy = match;
                }
            }

            var w = new PacketWriter(12);
            w.WriteUInt64(trainer.Guid.Value);
            w.WriteUInt32(spellToBuy);
            long mark = BotController.CurrentSequence;
            BotOutcome answer = Act(view, outcome, I($"train {spellToBuy} at {Describe(trainer)}"), (WorldOpcode.CmsgTrainerBuySpell, w.ToArray()));
            if (answer.Ok && !view.Controller!.HasEvent(mark, e => e.Kind == "trained"))
                return outcome.Failure("not-trained", I($"train {spellToBuy}: no confirmation from server"));
            return answer;
        });
    }

    // --- social ------------------------------------------------------------------------------------------------

    private static Task<BotControlResult> Chat(BotCall call, ChatType type)
    {
        string? to = null;
        string text = call.Rest;
        if (type == ChatType.Whisper)
        {
            to = call.Args[0];
            text = DropFirstWord(text);
        }

        if (text.Length == 0) return Task.FromResult(new BotControlResult(call.Verb.Name).Fail("syntax", "no text"));
        return OnBots(call, (view, outcome) =>
        {
            Language language = view.Player!.Team == Team.Horde ? Language.Orcish : Language.Common;
            return Act(view, outcome, type.ToString().ToLowerInvariant() + (to is null ? string.Empty : " to " + to),
                (WorldOpcode.CmsgMessagechat, ScenarioPackets.Chat(type, language, text, to)));
        });
    }

    private static string DropFirstWord(string text)
    {
        string trimmed = text.TrimStart();
        int space = trimmed.IndexOf(' ');
        return space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();
    }

    private static Task<BotControlResult> Emote(BotCall call)
    {
        if (!TryUInt(call.Args[0], out uint emote)) return Task.FromResult(new BotControlResult("emote").Fail("syntax", "the emote is an EmotesText.dbc id"));
        return OnBots(call, (view, outcome) =>
        {
            ulong target = 0;
            if (call.Args.Length > 1)
            {
                if (BotControlUnits.Resolve(call.World, view.Player!, string.Join(' ', call.Args.Skip(1)), out string? missing) is not { } unit) return outcome.Failure("no-unit", missing!);
                target = unit.Guid.Value;
            }

            var w = new PacketWriter(16);
            w.WriteUInt32(emote);
            w.WriteUInt32(0);
            w.WriteUInt64(target);
            return Act(view, outcome, I($"emote {emote}"), (WorldOpcode.CmsgTextEmote, w.ToArray()));
        });
    }

    private static Task<BotControlResult> Invite(BotCall call)
        => OnBots(call, (view, outcome) => Act(view, outcome, "invite " + call.Args[0], (WorldOpcode.CmsgGroupInvite, ScenarioPackets.GroupInvite(call.Args[0]))));

    private static Task<BotControlResult> Simple(BotCall call, string what, WorldOpcode opcode, byte[]? payload = null)
        => OnBots(call, (view, outcome) => Act(view, outcome, what, (opcode, payload ?? [])));

    private static Task<BotControlResult> Reclaim(BotCall call) => OnBots(call, (view, outcome) =>
    {
        if (view.Player!.Combat.Corpse is not { } corpse) return outcome.Failure("no-corpse", "the bot has no corpse");
        return Act(view, outcome, "reclaim corpse", (WorldOpcode.CmsgReclaimCorpse, ScenarioPackets.Guid(corpse.Guid.Value)));
    });

    private static Task<BotControlResult> Battleground(BotCall call)
    {
        string action = call.Args[0].ToLowerInvariant();
        switch (action)
        {
            case "join" when call.Args.Length >= 3 && TryUInt(call.Args[2], out uint map):
                bool group = call.Args.Length > 3 && call.Args[3].Equals("group", StringComparison.OrdinalIgnoreCase);
                return OnBots(call, (view, outcome) =>
                {
                    if (BotControlUnits.Resolve(call.World, view.Player!, call.Args[1], out string? missing) is not { } master) return outcome.Failure("no-unit", missing!);
                    var w = new PacketWriter(17);
                    w.WriteUInt64(master.Guid.Value);
                    w.WriteUInt32(map);
                    w.WriteUInt32(0);
                    w.WriteByte(group ? (byte)1 : (byte)0);
                    return Act(view, outcome, I($"join the battleground queue of map {map}"), (WorldOpcode.CmsgBattlemasterJoin, w.ToArray()));
                });
            case "port" when call.Args.Length >= 3 && TryUInt(call.Args[1], out uint portMap):
                bool enter = !call.Args[2].Equals("leave", StringComparison.OrdinalIgnoreCase);
                return OnBots(call, (view, outcome) =>
                {
                    var w = new PacketWriter(5);
                    w.WriteUInt32(portMap);
                    w.WriteByte(enter ? (byte)1 : (byte)0);
                    return Act(view, outcome, I($"{(enter ? "enter" : "leave the queue of")} battleground map {portMap}"), (WorldOpcode.CmsgBattlefieldPort, w.ToArray()));
                });
            case "leave" when call.Args.Length >= 2 && TryUInt(call.Args[1], out uint leaveMap):
                return OnBots(call, (view, outcome) => Act(view, outcome, I($"leave battleground map {leaveMap}"), (WorldOpcode.CmsgLeaveBattlefield, ScenarioPackets.UInt32(leaveMap))));
            case "status":
                return OnBots(call, (view, outcome) => Act(view, outcome, "battlefield status", (WorldOpcode.CmsgBattlefieldStatus, [])));
            default:
                return Task.FromResult(new BotControlResult("bg").Fail("syntax", "bg join <battlemaster> <map> [group] | port <map> enter|leave | leave <map> | status"));
        }
    }

    private static Task<BotControlResult> Raw(BotCall call)
    {
        string name = call.Args[0];
        WorldOpcode? opcode = null;
        if (TryUInt(name, out uint number)) opcode = (WorldOpcode)number;
        else
        {
            foreach (WorldOpcode value in Enum.GetValues<WorldOpcode>())
            {
                if (WorldOpcodeNames.GetName(value).Equals(name, StringComparison.OrdinalIgnoreCase) || value.ToString().Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    opcode = value;
                    break;
                }
            }
        }

        if (opcode is not { } op) return Task.FromResult(new BotControlResult("raw").Fail("syntax", "unknown opcode '" + name + "'"));
        byte[] payload;
        try
        {
            payload = Convert.FromHexString(string.Concat(call.Args.Skip(1)));
        }
        catch (FormatException)
        {
            return Task.FromResult(new BotControlResult("raw").Fail("syntax", "the payload is hex"));
        }

        return OnBots(call, (view, outcome) => Act(view, outcome, I($"{WorldOpcodeNames.GetName(op)} ({payload.Length} bytes)"), (op, payload)));
    }

    // --- queries -----------------------------------------------------------------------------------------------

    private static Task<BotControlResult> State(BotCall call) => OnBots(call, (view, outcome) =>
    {
        outcome.Data = BotControlState.State(call.Feature, view.Controller, view.Bot.BotId, view.Session!, view.Player!, call.Has("--full"));
        return outcome.Success();
    }, control: false);

    private static Task<BotControlResult> Part(BotCall call, string part) => OnBots(call, (view, outcome) =>
    {
        JsonNode data = part switch
        {
            "quests" => BotControlState.Quests(view.Session!, view.Player!),
            "auras" => BotControlState.Auras(view.Session!, view.Player!),
            _ => BotControlState.Inventory(view.Player!),
        };
        outcome.Data = new JsonObject { [part] = data };
        return outcome.Success(I($"{((JsonArray)data).Count} {part}"));
    }, control: false);

    private static Task<BotControlResult> Path(BotCall call) => OnBots(call, (view, outcome) =>
    {
        outcome.Data = BotControlState.Path(view.Controller!, view.Player!);
        return outcome.Success();
    });

    private static Task<BotControlResult> Events(BotCall call, bool errorsOnly)
    {
        long since = 0;
        int limit = errorsOnly ? BotController.MaxErrors : 50;
        string? kind = null;
        for (int i = 0; i < call.Args.Length; i++)
        {
            string word = call.Args[i].ToLowerInvariant();
            if (errorsOnly && i == 0 && int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out int n)) limit = n;
            else if (word == "since" && i + 1 < call.Args.Length && long.TryParse(call.Args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out long s)) { since = s; i++; }
            else if (word == "limit" && i + 1 < call.Args.Length && int.TryParse(call.Args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int l)) { limit = l; i++; }
            else if (word == "kind" && i + 1 < call.Args.Length) { kind = call.Args[i + 1]; i++; }
            else return Task.FromResult(new BotControlResult(call.Verb.Name).Fail("syntax", "events [since <seq>] [limit <n>] [kind <prefix>]"));
        }

        limit = Math.Clamp(limit, 1, BotController.MaxEvents);
        return OnBots(call, (view, outcome) =>
        {
            view.Controller!.Pump();
            IEnumerable<BotControlEvent> events = view.Controller.Events(since, BotController.MaxEvents, errorsOnly);
            if (kind is not null) events = events.Where(e => e.Kind.StartsWith(kind, StringComparison.OrdinalIgnoreCase));
            BotControlEvent[] picked = [.. events.TakeLast(limit)];
            outcome.Data = new JsonObject
            {
                ["last"] = picked.Length == 0 ? since : picked[^1].Seq,
                ["dropped"] = view.Controller.DroppedPackets,
                ["events"] = new JsonArray([.. picked.Select(e => (JsonNode)BotControlOutput.EventJson(e))]),
            };
            return outcome.Success(I($"{picked.Length} events"));
        }, inWorld: false);
    }

    private static Task<BotControlResult> Stream(BotCall call)
    {
        bool on = !call.Args[0].Equals("off", StringComparison.OrdinalIgnoreCase);
        CommandContext context = call.Context;
        string key = context.InvokerName + "#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(context.Session).ToString(CultureInfo.InvariantCulture);
        CancellationToken lifetime = context.Lifetime;
        if (on && !context.IsConsole) return Task.FromResult(new BotControlResult("stream").Fail("console-only", "streams go to the console, the control pipe or RA"));
        return OnBots(call, (view, outcome) =>
        {
            if (!on) return outcome.Success(view.Controller!.Unsubscribe(key) ? "stream off" : "was not streaming");
            view.Controller!.Subscribe(new BotControlSubscriber(key, context.Reply, lifetime, call.Json, call.Has("--errors")));
            return outcome.Success("streaming events as '#event' lines");
        }, inWorld: false);
    }

    private static Task<BotControlResult> Alias(BotCall call)
    {
        var result = new BotControlResult("alias");
        if (call.Args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var data = new JsonObject();
            foreach ((string name, string[] members) in call.Hub.Groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)) data[name] = string.Join(',', members);
            result.Data = data;
            return Task.FromResult(result.Say(I($"{call.Hub.Groups.Count} aliases")));
        }

        string alias = call.Args[0].TrimStart('@');
        if (alias.Length == 0 || !alias.All(c => char.IsLetterOrDigit(c) || c is '_' or '-')) return Task.FromResult(result.Fail("syntax", "an alias is letters, digits, '_' and '-'"));
        if (call.Args.Length < 2) return Task.FromResult(result.Fail("syntax", "alias <name> <bots> | <name> clear | list"));
        if (call.Args[1].Equals("clear", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(call.Hub.RemoveGroup(alias) ? result.Say("@" + alias + " removed") : result.Fail("no-alias", "no alias @" + alias));
        call.Hub.SetGroup(alias, [string.Join(',', call.Args.Skip(1))]);
        return Task.FromResult(result.Say("@" + alias + " = " + string.Join(',', call.Args.Skip(1))));
    }

    private static Task<BotControlResult> Format(BotCall call)
    {
        string format = call.Args[0].ToLowerInvariant();
        if (format is not ("json" or "text")) return Task.FromResult(new BotControlResult("format").Fail("syntax", "format json|text"));
        SetJsonByDefault(call.Context.Session, format == "json");
        return Task.FromResult(new BotControlResult("format").Say(".botctl output is " + format + " for this session"));
    }
}