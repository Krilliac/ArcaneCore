using ArcaneCore.Game.Maps;
using System.Globalization;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Playerbots.Control;

/// <summary>
/// <c>.botctl</c>: direct, headless control of playerbots for operators and AI agents, from the console, the control pipe, RA or in game
/// (docs/ops/botctl.md). <c>.botctl &lt;verb&gt; &lt;bots&gt; [args] [--json]</c>: take bots over (their own AI pauses), drive them with
/// ordinary client actions, read their state, wait for conditions and run scripted sequences. Read-only verbs need GameMaster, the
/// others Administrator. Every command ends with exactly one summary line (<c>OK ...</c> / <c>ERR code ...</c>, or one JSON object).
/// </summary>
public sealed class BotControlCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("botctl", AccountSecurity.GameMaster,
            "Syntax: .botctl <verb> <bots> [args] [--json]\nDirectly control playerbots (headless testing): take/release, move/follow/tele, target/attack/cast/use, interact/gossip/quest/loot, buy/sell/train/equip, chat/emote, group, battlegrounds, state/quests/inventory/auras/events/errors, wait-for conditions, run sequences, stream events. <bots>: a name, bot id, Prefix*, all, controlled, @alias, where:key=value&... or a comma list. See .botctl help and docs/ops/botctl.md.",
            Children: [.. BotControlVerbs.All.Select(v => new ChatCommand(v.Name, v.Security, "Syntax: .botctl " + v.Name + " " + v.Syntax + "\n" + v.Summary,
                (context, text) => BotControlDispatch.Handle(context, v, text)))]),
    ];
}

/// <summary>One <c>.botctl</c> verb.</summary>
internal sealed record BotVerb(string Name, AccountSecurity Security, string Syntax, string Summary, bool NeedsSelector,
    Func<BotCall, Task<BotControlResult>> Run, int MinArgs = 0);

/// <summary>One parsed <c>.botctl</c> command line.</summary>
internal sealed class BotCall
{
    public required CommandContext Context { get; init; }

    public required BotControlHub Hub { get; init; }

    public required BotVerb Verb { get; init; }

    public required string Selector { get; init; }

    public required string[] Args { get; init; }

    /// <summary>The argument text after the selector, flags removed (chat text keeps its spacing).</summary>
    public required string Rest { get; init; }

    public required HashSet<string> Flags { get; init; }

    public required bool Json { get; init; }

    /// <summary>The <see cref="BotController.CurrentSequence"/> when the command started ("events since this command").</summary>
    public long Mark { get; init; }

    public ManagedPlayerbotFeature Feature => Hub.Feature;

    public WorldRuntime World => Context.World;

    public CancellationToken Token => Context.Lifetime;

    public bool Has(string flag) => Flags.Contains(flag);

    public string Arg(int index) => index < Args.Length ? Args[index] : string.Empty;

    /// <summary>The same command for another verb (the steps of <c>run</c>), with the same selector and output format.</summary>
    public BotCall With(BotVerb verb, string selector, string[] args, string rest, HashSet<string> flags) => new()
    {
        Context = Context, Hub = Hub, Verb = verb, Selector = selector, Args = args, Rest = rest, Flags = flags,
        Json = Json || flags.Contains("--json"), Mark = BotController.CurrentSequence,
    };
}

/// <summary>Parsing, dispatch, bot selection and the per-bot helpers of <c>.botctl</c>.</summary>
internal static class BotControlDispatch
{
    private sealed class Prefs
    {
        public bool Json;
    }

    private static readonly ConditionalWeakTable<WorldSession, Prefs> Preferences = new();

    internal static bool JsonByDefault(WorldSession session) => Preferences.TryGetValue(session, out Prefs? prefs) && prefs.Json;

    internal static void SetJsonByDefault(WorldSession session, bool json) => Preferences.GetOrCreateValue(session).Json = json;

    /// <summary>The command handler (world thread): parse, then run the verb off the world thread, holding the command open until it ends.</summary>
    internal static bool Handle(CommandContext context, BotVerb verb, string text)
    {
        if (context.Session.Services.GetService<ManagedPlayerbotFeature>() is not { } feature)
        {
            context.Fail("unavailable");
            context.Reply("ERR unavailable " + verb.Name + ": the playerbot service is not running (World:Playerbots:Enabled).");
            return true;
        }

        if (!TryParse(context, BotControlHub.For(feature), verb, text, out BotCall? call, out string? problem))
        {
            context.Fail("syntax");
            context.Reply("ERR syntax " + verb.Name + ": " + problem + ". Syntax: .botctl " + verb.Name + " " + verb.Syntax);
            return true;
        }

        IDisposable hold = context.Defer();
        _ = Task.Run(async () =>
        {
            try
            {
                BotControlResult result = await RunAsync(call!).ConfigureAwait(false);
                Emit(call!, result);
            }
            finally
            {
                hold.Dispose();
            }
        });
        return true;
    }

    /// <summary>Run a parsed call; never throws (a fault becomes an <c>exception</c> result).</summary>
    internal static async Task<BotControlResult> RunAsync(BotCall call)
    {
        try
        {
            return await call.Verb.Run(call).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (call.Token.IsCancellationRequested)
        {
            return new BotControlResult(call.Verb.Name).Fail("cancelled", "the invoker went away");
        }
        catch (OperationCanceledException)
        {
            return new BotControlResult(call.Verb.Name).Fail("world-stopping", "the world stopped before the command ran");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            call.Feature.ReportCommandFault("botctl " + call.Verb.Name, error);
            return new BotControlResult(call.Verb.Name).Fail("exception", error.GetType().Name + ": " + error.Message);
        }
    }

    internal static void Emit(BotCall call, BotControlResult result)
    {
        if (!result.Ok) call.Context.Fail(result.Reason ?? "failed");
        if (!call.Context.CanReplyLater || call.Token.IsCancellationRequested) return;
        foreach (string line in result.ToLines(call.Json)) call.Context.Reply(line);
    }

    internal static bool TryParse(CommandContext context, BotControlHub hub, BotVerb verb, string text, out BotCall? call, out string? problem)
    {
        call = null;
        problem = null;
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        foreach (string word in (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.StartsWith("--", StringComparison.Ordinal) && word.Length > 2) flags.Add(word.ToLowerInvariant());
            else kept.Add(word);
        }

        string selector = string.Empty;
        if (verb.NeedsSelector)
        {
            if (kept.Count == 0)
            {
                problem = "no bots named";
                return false;
            }

            selector = kept[0];
            kept.RemoveAt(0);
        }

        if (kept.Count < verb.MinArgs)
        {
            problem = "missing arguments";
            return false;
        }

        string rest = RestAfter(text ?? string.Empty, verb.NeedsSelector ? 1 : 0);
        bool json = flags.Contains("--json") || (!flags.Contains("--text") && JsonByDefault(context.Session));
        call = new BotCall
        {
            Context = context, Hub = hub, Verb = verb, Selector = selector, Args = [.. kept], Rest = rest, Flags = flags, Json = json,
            Mark = BotController.CurrentSequence,
        };
        return true;
    }

    /// <summary>The text after the first <paramref name="skip"/> words, with every <c>--flag</c> word removed.</summary>
    internal static string RestAfter(string text, int skip)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Where(w => !(w.StartsWith("--", StringComparison.Ordinal) && w.Length > 2)).Skip(skip));
    }

    // --- selection ---------------------------------------------------------------------------------------------

    internal readonly record struct Picked(Guid BotId, string Name);

    /// <summary>
    /// The bots <paramref name="selector"/> names (docs/ops/botctl.md, "Naming bots"), or the error. World thread (the filters read
    /// the live players). More than <see cref="PlayerbotOptions.MaxBulkCommand"/> bots need <c>--force</c>.
    /// </summary>
    internal static string? Select(BotCall call, out List<Picked> bots)
    {
        bots = [];
        string? error = Resolve(call.Hub, call.Selector, bots, 0);
        if (error is not null) return error;
        if (bots.Count == 0) return "no-match: no playerbot matches '" + call.Selector + "'";
        int cap = call.Context.Session.Services.GetService<IOptions<PlayerbotOptions>>()?.Value.MaxBulkCommand ?? 50;
        if (bots.Count > cap && !call.Has("--force"))
            return string.Create(CultureInfo.InvariantCulture, $"too-many: {bots.Count} bots match; the cap is {cap} (World:Playerbots:MaxBulkCommand); add --force");
        return null;
    }

    private static string? Resolve(BotControlHub hub, string selector, List<Picked> bots, int depth)
    {
        ManagedPlayerbotFeature feature = hub.Feature;
        IReadOnlyList<PlayerbotStatus> snapshot = feature.Snapshot();
        var seen = new HashSet<Guid>(bots.Select(b => b.BotId));
        void Add(Guid id, string name)
        {
            if (seen.Add(id)) bots.Add(new Picked(id, name));
        }

        foreach (string raw in selector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string part = raw;
            if (part.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (PlayerbotStatus s in snapshot.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)) Add(s.BotId, s.Name);
            }
            else if (part.Equals("controlled", StringComparison.OrdinalIgnoreCase) || part.Equals("mine", StringComparison.OrdinalIgnoreCase))
            {
                foreach (BotController c in hub.Controlled.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)) Add(c.BotId, c.Name);
            }
            else if (part.Equals("running", StringComparison.OrdinalIgnoreCase))
            {
                foreach (PlayerbotStatus s in snapshot.Where(s => feature.IsActive(s.BotId)).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)) Add(s.BotId, s.Name);
            }
            else if (part.StartsWith('@'))
            {
                if (depth > 3) return "bad-selector: aliases nest too deep";
                if (hub.Group(part[1..]) is not { } members) return "no-alias: no bot alias '" + part + "' (.botctl alias)";
                foreach (string member in members)
                {
                    string? inner = Resolve(hub, member, bots, depth + 1);
                    if (inner is not null) return inner;
                }

                seen = [.. bots.Select(b => b.BotId)];
            }
            else if (part.StartsWith("where:", StringComparison.OrdinalIgnoreCase))
            {
                string? problem = Filter(feature, hub, snapshot, part[6..], Add);
                if (problem is not null) return problem;
            }
            else if (part.EndsWith('*'))
            {
                string prefix = part[..^1];
                if (prefix.Length == 0) return "bad-selector: a prefix needs at least one letter before '*'";
                foreach (PlayerbotStatus s in snapshot.Where(s => s.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)) Add(s.BotId, s.Name);
            }
            else if (feature.TryResolveBotId(part, out Guid id, out string name))
            {
                Add(id, name);
            }
            else
            {
                return "no-match: no playerbot '" + part + "'";
            }
        }

        return null;
    }

    /// <summary><c>where:key=value&amp;key=value</c>: map, zone, level (n or a-b), class, race, controlled (0/1), running (0/1), state, goal.</summary>
    private static string? Filter(ManagedPlayerbotFeature feature, BotControlHub hub, IReadOnlyList<PlayerbotStatus> snapshot, string filter, Action<Guid, string> add)
    {
        var terms = new List<(string Key, string Value)>();
        foreach (string term in filter.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = term.IndexOf('=');
            if (eq <= 0) return "bad-selector: filter term '" + term + "' is not key=value";
            string key = term[..eq].ToLowerInvariant();
            if (key is not ("map" or "zone" or "level" or "class" or "race" or "controlled" or "running" or "state" or "goal" or "name"))
                return "bad-selector: unknown filter key '" + key + "' (map, zone, level, class, race, controlled, running, state, goal, name)";
            terms.Add((key, term[(eq + 1)..]));
        }

        foreach (PlayerbotStatus status in snapshot.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            Player? player = feature.FindSession(status.BotId)?.Player;
            bool all = true;
            foreach ((string key, string value) in terms)
            {
                bool match = key switch
                {
                    "map" => player is not null ? Num(value, player.MapId) : Num(value, status.MapId),
                    "zone" => player is { Map: not null } p && Num(value, feature.ZoneAt(p.MapId, new(p.X, p.Y, p.Z))),
                    "level" => player is not null && Range(value, player.Level),
                    "class" => player is not null && player.Class.ToString().Equals(value, StringComparison.OrdinalIgnoreCase),
                    "race" => player is not null && player.Race.ToString().Equals(value, StringComparison.OrdinalIgnoreCase),
                    "controlled" => (hub.Find(status.BotId) is not null) == Flag(value),
                    "running" => feature.IsActive(status.BotId) == Flag(value),
                    "state" => status.State.ToString().Equals(value, StringComparison.OrdinalIgnoreCase),
                    "goal" => status.Goal.ToString().Equals(value, StringComparison.OrdinalIgnoreCase),
                    "name" => status.Name.Contains(value, StringComparison.OrdinalIgnoreCase),
                    _ => throw new FormatException(key),
                };
                if (!match)
                {
                    all = false;
                    break;
                }
            }

            if (all) add(status.BotId, status.Name);
        }

        return null;

        static bool Flag(string value) => value is "1" or "true" or "yes" or "on";
        static bool Num(string value, uint actual) => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint n) && n == actual;
        static bool Range(string value, uint actual)
        {
            int dash = value.IndexOf('-');
            if (dash < 0) return Num(value, actual);
            return uint.TryParse(value[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out uint low)
                && uint.TryParse(value[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out uint high)
                && actual >= low && actual <= high;
        }
    }

    // --- per-bot helpers ---------------------------------------------------------------------------------------

    /// <summary>One bot as an action sees it (world thread).</summary>
    internal readonly record struct BotView(Picked Bot, BotController? Controller, WorldSession? Session, Player? Player);

    /// <summary>
    /// Run <paramref name="act"/> for every selected bot on the world thread. With <paramref name="control"/> a bot must be under
    /// <c>.botctl</c> control (taken); with <paramref name="inWorld"/> it must be in the world.
    /// </summary>
    internal static Task<BotControlResult> OnBots(BotCall call, Func<BotView, BotOutcome, BotOutcome> act, bool control = true, bool inWorld = true)
        => call.World.InvokeAsync(() =>
        {
            var result = new BotControlResult(call.Verb.Name);
            if (Select(call, out List<Picked> bots) is { } error)
            {
                int colon = error.IndexOf(':');
                return result.Fail(colon < 0 ? error : error[..colon], colon < 0 ? error : error[(colon + 1)..].Trim());
            }

            foreach (Picked bot in bots)
            {
                BotOutcome outcome = result.Add(bot.Name);
                BotController? controller = call.Hub.Find(bot.BotId);
                WorldSession? session = call.Feature.FindSession(bot.BotId);
                Player? player = session?.Player;
                if (controller is not null && session is not null) controller.EnsureAttached(session);
                if (control && controller is null)
                {
                    outcome.Failure("not-controlled", "take it first: .botctl take " + bot.Name);
                    continue;
                }

                if (inWorld && player is not { IsInWorld: true })
                {
                    outcome.Failure("not-in-world", call.Feature.IsActive(bot.BotId) ? "the bot is between maps" : "the bot is not running");
                    continue;
                }

                try
                {
                    act(new BotView(bot, controller, session, player), outcome);
                }
                catch (Exception error2) when (error2 is not OutOfMemoryException)
                {
                    call.Feature.ReportCommandFault("botctl " + call.Verb.Name, error2);
                    outcome.Failure("exception", error2.GetType().Name + ": " + error2.Message);
                }
            }

            return result;
        }, call.Token);

    /// <summary>
    /// Send one client opcode as the bot (its ordinary handler, the brain's own path; never refused for lack of the brain's action budget),
    /// then report what the server answered at once: an error event fails the outcome (<c>server-error</c>), other events are its detail.
    /// </summary>
    internal static BotOutcome Act(BotView view, BotOutcome outcome, string what, params (Protocol.WorldOpcode Opcode, byte[] Payload)[] packets)
    {
        BotController controller = view.Controller!;
        WorldSession session = view.Session!;
        long mark = BotController.CurrentSequence;
        controller.Pump();
        mark = BotController.CurrentSequence;
        foreach ((Protocol.WorldOpcode opcode, byte[] payload) in packets)
        {
            session.ManagedBudget = null;
            bool sent = session.TryManagedAction(opcode, payload);
            controller.Record("action", Protocol.WorldOpcodeNames.GetName(opcode) + (sent ? string.Empty : " refused"), error: !sent);
            if (!sent)
            {
                controller.Pump();
                return outcome.Failure("refused", what + ": " + Protocol.WorldOpcodeNames.GetName(opcode) + " was refused (the bot's state does not allow it)");
            }
        }

        return Answer(controller, outcome, what, mark);
    }

    /// <summary>The server's immediate answer to an action since <paramref name="mark"/> (see <see cref="Act"/>).</summary>
    internal static BotOutcome Answer(BotController controller, BotOutcome outcome, string what, long mark)
    {
        controller.Pump();
        IReadOnlyList<BotControlEvent> seen = controller.Events(mark, 32);
        BotControlEvent[] errors = [.. seen.Where(e => e.Error)];
        if (errors.Length > 0) return outcome.Failure("server-error", what + ": " + string.Join("; ", errors.Select(e => e.Kind + " " + e.Text)));
        BotControlEvent[] answers = [.. seen.Where(e => e.Kind != "action")];
        return outcome.Success(answers.Length == 0 ? what : what + ": " + string.Join("; ", answers.Take(6).Select(e => e.Kind + " " + e.Text)));
    }

    internal static bool TryFloat(string text, out float value)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    internal static bool TryUInt(string text, out uint value)
        => uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}