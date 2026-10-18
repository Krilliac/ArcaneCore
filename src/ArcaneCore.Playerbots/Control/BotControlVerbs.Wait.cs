using ArcaneCore.World.Net;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Commands;
using static ArcaneCore.World.Playerbots.Control.BotControlDispatch;

namespace ArcaneCore.World.Playerbots.Control;

/// <summary><c>.botctl wait</c>, <c>sleep</c> and <c>run</c>: the scripting verbs.</summary>
internal static partial class BotControlVerbs
{
    private const int PollMs = 100;
    private const double DefaultWaitSeconds = 30;
    private const double MaxWaitSeconds = 1800;

    /// <summary>A parsed wait condition: true when it holds for the bot; <c>describe</c> says where the bot stands.</summary>
    private sealed record Condition(string Text, Func<BotView, long, bool> Holds, Func<BotView, string> Describe);

    private static bool Compare(double value, string op, double wanted) => op switch
    {
        "<" => value < wanted,
        "<=" => value <= wanted,
        ">" => value > wanted,
        ">=" => value >= wanted,
        "!=" => Math.Abs(value - wanted) > 0.0001,
        _ => Math.Abs(value - wanted) <= 0.0001,
    };

    private static bool IsOp(string word) => word is "<" or "<=" or ">" or ">=" or "=" or "==" or "!=";

    private static string Pos(Player player) => BotController.Fmt(new Vector3(player.X, player.Y, player.Z));

    private static string? ParseCondition(BotCall call, string[] words, out Condition? condition, out int used)
    {
        condition = null;
        used = 1;
        string word = words[0].ToLowerInvariant();
        string Arg(int i) => i < words.Length ? words[i] : string.Empty;
        switch (word)
        {
            case "arrived":
                condition = new("arrived", (v, m) => v.Controller?.MotionState == BotMotionState.Arrived,
                    v => "motion " + (v.Controller?.MotionState.ToString() ?? "none") + " at " + Pos(v.Player!) + (v.Controller?.MotionDetail is { } d ? " (" + d + ")" : string.Empty));
                return null;
            case "idle":
                condition = new("idle", (v, m) => v.Controller?.MotionState is BotMotionState.Idle or BotMotionState.Arrived or BotMotionState.Failed or BotMotionState.Lost or null,
                    v => "motion " + (v.Controller?.MotionState.ToString() ?? "none"));
                return null;
            case "following":
                condition = new("following", (v, m) => v.Controller?.MotionState == BotMotionState.Following, v => "motion " + (v.Controller?.MotionState.ToString() ?? "none"));
                return null;
            case "combat":
                condition = new("combat", (v, m) => v.Player!.Combat.IsInCombat, _ => "not in combat");
                return null;
            case "nocombat":
                condition = new("nocombat", (v, m) => !v.Player!.Combat.IsInCombat, _ => "in combat");
                return null;
            case "alive":
                condition = new("alive", (v, m) => v.Player!.IsAlive, _ => "dead");
                return null;
            case "casting":
                condition = new("casting", (v, m) => BotControlState.Casting(v.Session!, v.Player!) != 0, _ => "not casting");
                return null;
            case "notcasting":
                condition = new("notcasting", (v, m) => BotControlState.Casting(v.Session!, v.Player!) == 0, v => I($"casting {BotControlState.Casting(v.Session!, v.Player!)}"));
                return null;
            case "invited":
                condition = new("invited", (v, mark) => v.Controller?.HasEvent(mark, e => e.Kind == "group-invite") == true, _ => "no invite yet");
                return null;
            case "targetdead":
                condition = new("targetdead", (v, m) => v.Player!.Map?.FindObject(v.Player.Selection) is not Unit u || !u.IsAlive,
                    v => v.Player!.Map?.FindObject(v.Player.Selection) is Unit u ? I($"target {BotControlUnits.NameOf(u)} at {u.Health}/{u.MaxHealth}") : "no target");
                return null;
            case "dead":
            {
                if (words.Length > 1 && !IsKeyword(words[1]))
                {
                    used = 2;
                    string spec = words[1];
                    WorldObject? fixedUnit = null;
                    condition = new("dead " + spec, (v, m) =>
                    {
                        fixedUnit ??= BotControlUnits.Resolve(call.World, v.Player!, spec, out _);
                        return fixedUnit is Unit u && (!u.IsAlive || u.Map is null);
                    }, v => fixedUnit is Unit u ? I($"{BotControlUnits.NameOf(u)} at {u.Health}/{u.MaxHealth}") : "no such unit in sight");
                    return null;
                }

                condition = new("dead", (v, m) => !v.Player!.IsAlive, _ => "alive");
                return null;
            }

            case "hp" or "health" or "power" or "mana":
            {
                if (!IsOp(Arg(1)) || !double.TryParse(Arg(2).TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
                    return word + " <op> <percent>";
                used = 3;
                string op = Arg(1);
                bool hp = word is "hp" or "health";
                condition = new(I($"{word} {op} {pct}"), (v, m) => Compare(Percent(v.Player!, hp), op, pct), v => I($"{word} {Percent(v.Player!, hp):F0}%"));
                return null;
            }

            case "level":
            {
                string op = IsOp(Arg(1)) ? Arg(1) : ">=";
                int at = IsOp(Arg(1)) ? 2 : 1;
                if (!uint.TryParse(Arg(at), NumberStyles.None, CultureInfo.InvariantCulture, out uint level)) return "level [op] <n>";
                used = at + 1;
                condition = new(I($"level {op} {level}"), (v, m) => Compare(v.Player!.Level, op, level), v => I($"level {v.Player!.Level}"));
                return null;
            }

            case "map":
                if (!uint.TryParse(Arg(1), NumberStyles.None, CultureInfo.InvariantCulture, out uint map)) return "map <id>";
                used = 2;
                condition = new(I($"map {map}"), (v, m) => v.Player!.MapId == map && v.Player.Map is not null && !BotControlState.Teleporting(v.Session!, v.Player), v => I($"map {v.Player!.MapId}"));
                return null;
            case "quest":
            {
                if (!uint.TryParse(Arg(1), NumberStyles.None, CultureInfo.InvariantCulture, out uint quest) || Arg(2).Length == 0) return "quest <id> <none|incomplete|complete|failed|rewarded|taken>";
                used = 3;
                string wanted = Arg(2).ToLowerInvariant();
                condition = new(I($"quest {quest} {wanted}"), (v, m) =>
                {
                    string status = BotControlState.QuestStatusOf(v.Session!, v.Player!, quest).ToLowerInvariant();
                    return wanted == "taken" ? status is "incomplete" or "complete" or "failed" : status == wanted;
                }, v => I($"quest {quest} is {BotControlState.QuestStatusOf(v.Session!, v.Player!, quest)}"));
                return null;
            }

            case "item":
            {
                if (!uint.TryParse(Arg(1), NumberStyles.None, CultureInfo.InvariantCulture, out uint entry)) return "item <entry> [op] <count>";
                string op = IsOp(Arg(2)) ? Arg(2) : ">=";
                int at = IsOp(Arg(2)) ? 3 : 2;
                uint count = 1;
                if (Arg(at).Length > 0 && uint.TryParse(Arg(at), NumberStyles.None, CultureInfo.InvariantCulture, out uint n)) { count = n; used = at + 1; }
                else used = IsOp(Arg(2)) ? 3 : 2;
                condition = new(I($"item {entry} {op} {count}"), (v, m) => Compare(BotControlState.ItemCount(v.Player!, entry), op, count),
                    v => I($"{BotControlState.ItemCount(v.Player!, entry)} of item {entry}"));
                return null;
            }

            case "aura" or "noaura":
            {
                if (!uint.TryParse(Arg(1), NumberStyles.None, CultureInfo.InvariantCulture, out uint spell)) return word + " <spell>";
                used = 2;
                bool want = word == "aura";
                condition = new(I($"{word} {spell}"), (v, m) => BotControlState.HasAura(v.Session!, v.Player!, spell) == want,
                    _ => want ? I($"no aura {spell}") : I($"aura {spell} is on"));
                return null;
            }

            case "event":
            {
                if (Arg(1).Length == 0) return "event <kind prefix>";
                used = 2;
                string kind = Arg(1);
                condition = new("event " + kind, (v, mark) => v.Controller?.HasEvent(mark, e => e.Kind.StartsWith(kind, StringComparison.OrdinalIgnoreCase)) == true,
                    _ => "no '" + kind + "' event yet");
                return null;
            }

            case "near":
            {
                float radius = 3f;
                if (words.Length >= 4 && TryFloat(words[1], out float x) && TryFloat(words[2], out float y) && TryFloat(words[3], out float z))
                {
                    used = 4;
                    if (words.Length > 4 && TryFloat(words[4], out float r)) { radius = r; used = 5; }
                    var point = new Vector3(x, y, z);
                    condition = new(I($"near {BotController.Fmt(point)} {radius}"), (v, m) => Vector3.Distance(new(v.Player!.X, v.Player.Y, v.Player.Z), point) <= radius,
                        v => I($"{Vector3.Distance(new(v.Player!.X, v.Player.Y, v.Player.Z), point):F1} yd away at {Pos(v.Player)}"));
                    return null;
                }

                if (Arg(1).Length == 0) return "near <x> <y> <z> [r] | near <unit> [r]";
                string spec = Arg(1);
                used = 2;
                if (words.Length > 2 && TryFloat(words[2], out float ur)) { radius = ur; used = 3; }
                condition = new(I($"near {spec} {radius}"), (v, m) => BotControlUnits.Resolve(call.World, v.Player!, spec, out _) is { } u && ReferenceEquals(u.Map, v.Player!.Map) && BotControlUnits.Distance(v.Player, u) <= radius,
                    v => BotControlUnits.Resolve(call.World, v.Player!, spec, out _) is { } u ? I($"{BotControlUnits.Distance(v.Player!, u):F1} yd from {spec}") : spec + " is not in sight");
                return null;
            }

            default:
                return "unknown condition '" + words[0] + "'";
        }
    }

    private static bool IsKeyword(string word) => word.ToLowerInvariant() is "timeout" or "and";

    private static double Percent(Player player, bool hp)
    {
        if (hp) return player.MaxHealth == 0 ? 0 : 100.0 * player.Health / player.MaxHealth;
        (uint power, uint max) = BotControlState.Power(player);
        return max == 0 ? 0 : 100.0 * power / max;
    }

    private static readonly string[] AttachedOps = ["<=", ">=", "!=", "==", "<", ">"];

    private static IEnumerable<string> ExpandAttachedOperators(string[] args)
    {
        foreach (string arg in args)
        {
            if (IsOp(arg))
            {
                yield return arg;
                continue;
            }

            string? matchedOp = null;
            int idx = -1;
            foreach (string op in AttachedOps)
            {
                idx = arg.IndexOf(op, StringComparison.Ordinal);
                if (idx > 0)
                {
                    matchedOp = op;
                    break;
                }
            }

            if (matchedOp is not null)
            {
                yield return arg[..idx];
                yield return matchedOp;
                string rest = arg[(idx + matchedOp.Length)..];
                if (rest.Length > 0) yield return rest;
            }
            else
            {
                yield return arg;
            }
        }
    }

    private static async Task<BotControlResult> Wait(BotCall call)
    {
        var result = new BotControlResult("wait");
        double timeout = DefaultWaitSeconds;
        var conditions = new List<Condition>();
        string[] words = [.. ExpandAttachedOperators(call.Args)];
        for (int i = 0; i < words.Length;)
        {
            string word = words[i].ToLowerInvariant();
            if (word == "and") { i++; continue; }
            if (word == "timeout")
            {
                if (i + 1 >= words.Length || !double.TryParse(words[i + 1].TrimEnd('s'), NumberStyles.Float, CultureInfo.InvariantCulture, out timeout) || timeout <= 0)
                    return result.Fail("syntax", "timeout <seconds>");
                timeout = Math.Min(timeout, MaxWaitSeconds);
                i += 2;
                continue;
            }

            if (ParseCondition(call, words[i..], out Condition? condition, out int used) is { } problem) return result.Fail("syntax", problem);
            conditions.Add(condition!);
            i += used;
        }

        if (conditions.Count == 0) return result.Fail("syntax", "no condition");
        bool any = call.Has("--any");
        bool failOnError = call.Has("--failonerror");
        long mark = call.Mark;
        List<Picked> bots = [];
        string? selectError = await call.World.InvokeAsync(() => Select(call, out bots), call.Token).ConfigureAwait(false);
        if (selectError is not null) return result.Fail(selectError.Split(':')[0], selectError);
        string what = string.Join(" and ", conditions.Select(c => c.Text));
        var clock = Stopwatch.StartNew();
        var done = new Dictionary<Guid, string>();
        var failed = new Dictionary<Guid, (string Code, string Detail)>();
        var last = new Dictionary<Guid, string>();
        while (true)
        {
            await call.World.InvokeAsync(() =>
            {
                foreach (Picked bot in bots)
                {
                    if (done.ContainsKey(bot.BotId) || failed.ContainsKey(bot.BotId)) continue;
                    BotController? controller = call.Hub.Find(bot.BotId);
                    WorldSession? session = call.Feature.FindSession(bot.BotId);
                    Player? player = session?.Player;
                    if (player is null || session is null || player.Map is null && !BotControlState.Teleporting(session, player))
                    {
                        failed[bot.BotId] = ("not-in-world", "the bot is not in the world");
                        continue;
                    }

                    controller?.Pump();
                    var view = new BotView(bot, controller, session, player);
                    if (failOnError && controller is not null && controller.Events(mark, 1, errorsOnly: true) is { Count: > 0 } errors)
                    {
                        failed[bot.BotId] = ("server-error", errors[0].Kind + ": " + errors[0].Text);
                        continue;
                    }

                    if (player.Map is null) { last[bot.BotId] = "teleporting"; continue; }
                    if (conditions.All(c => c.Holds(view, mark)))
                    {
                        done[bot.BotId] = I($"{what} after {clock.Elapsed.TotalSeconds:F1} s");
                        continue;
                    }

                    if (conditions.Any(c => c.Text == "arrived") && controller?.MotionState is BotMotionState.Failed or BotMotionState.Lost)
                    {
                        failed[bot.BotId] = ("motion-" + controller.MotionState.ToString().ToLowerInvariant(), controller.MotionDetail ?? "the walk failed");
                        continue;
                    }

                    last[bot.BotId] = string.Join("; ", conditions.Where(c => !c.Holds(view, mark)).Select(c => c.Describe(view)));
                }

                return true;
            }, call.Token).ConfigureAwait(false);

            bool finished = any ? done.Count > 0 || failed.Count == bots.Count : done.Count + failed.Count == bots.Count;
            if (finished || clock.Elapsed.TotalSeconds >= timeout) break;
            await Task.Delay(PollMs, call.Token).ConfigureAwait(false);
        }

        foreach (Picked bot in bots)
        {
            BotOutcome outcome = result.Add(bot.Name);
            if (done.TryGetValue(bot.BotId, out string? met)) outcome.Success(met);
            else if (failed.TryGetValue(bot.BotId, out (string Code, string Detail) f)) outcome.Failure(f.Code, f.Detail);
            else if (any && done.Count > 0) outcome.Success("not needed (--any): " + (last.GetValueOrDefault(bot.BotId) ?? "pending"));
            else outcome.Failure("timeout", I($"{what} not met in {timeout:0.#} s: ") + (last.GetValueOrDefault(bot.BotId) ?? "pending"));
        }

        result.Data = new JsonObject { ["condition"] = what, ["elapsedMs"] = (long)clock.Elapsed.TotalMilliseconds };
        if (any && done.Count > 0) return result.Say(I($"{what}: {done.Count} of {bots.Count} bots"));
        return result;
    }

    private static async Task<BotControlResult> Sleep(BotCall call)
    {
        if (!uint.TryParse(call.Args[0].TrimEnd('m', 's'), NumberStyles.None, CultureInfo.InvariantCulture, out uint ms) || ms > MaxWaitSeconds * 1000)
            return new BotControlResult("sleep").Fail("syntax", "sleep <milliseconds>");
        await Task.Delay((int)ms, call.Token).ConfigureAwait(false);
        return new BotControlResult("sleep").Say(I($"slept {ms} ms"));
    }

    private static async Task<BotControlResult> Run(BotCall call)
    {
        var result = new BotControlResult("run");
        bool keepGoing = call.Has("--continue");
        string[] steps = [.. call.Rest.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.Replace(" --continue", string.Empty, StringComparison.OrdinalIgnoreCase).Trim()).Where(s => s.Length > 0 && s != "--continue")];
        if (steps.Length == 0) return result.Fail("syntax", "run <bots> <step> ; <step> ...");
        var log = new JsonArray();
        int okSteps = 0;
        string? firstError = null;
        for (int i = 0; i < steps.Length; i++)
        {
            string step = steps[i].StartsWith('.') ? steps[i][1..] : steps[i];
            if (step.StartsWith("botctl ", StringComparison.OrdinalIgnoreCase)) step = step[7..].TrimStart();
            int space = step.IndexOf(' ');
            string name = space < 0 ? step : step[..space];
            string args = space < 0 ? string.Empty : step[(space + 1)..];
            BotControlResult stepResult;
            if (Find(name) is not { } verb || verb.Name == "run")
            {
                stepResult = new BotControlResult(name).Fail("unknown-verb", "no verb '" + name + "'" + (name == "run" ? " (runs do not nest)" : string.Empty));
            }
            else if (call.Context.Security < verb.Security)
            {
                stepResult = new BotControlResult(name).Fail("security", "the verb needs " + verb.Security);
            }
            else
            {
                string text = verb.NeedsSelector ? call.Selector + " " + args : args;
                if (call.Json) text += " --json";
                stepResult = TryParse(call.Context, call.Hub, verb, text, out BotCall? stepCall, out string? problem)
                    ? await RunAsync(stepCall!).ConfigureAwait(false)
                    : new BotControlResult(name).Fail("syntax", problem + ". Syntax: .botctl " + verb.Name + " " + verb.Syntax);
            }

            JsonObject json = stepResult.ToJson();
            json["step"] = i + 1;
            log.Add(json);
            if (!call.Json)
            {
                foreach (string line in stepResult.ToLines(json: false)) call.Context.Reply(I($"[{i + 1}/{steps.Length}] ") + line);
            }

            if (stepResult.Ok) okSteps++;
            else
            {
                firstError ??= I($"step {i + 1} '{step}': ") + (stepResult.Reason ?? "failed");
                if (!keepGoing) break;
            }

            if (call.Token.IsCancellationRequested) break;
        }

        if (call.Json) result.Data = new JsonObject { ["steps"] = log };
        if (firstError is not null) return result.Fail("step-failed", firstError + I($" ({okSteps}/{steps.Length} steps ok)"));
        return result.Say(I($"{okSteps}/{steps.Length} steps ok"));
    }
}