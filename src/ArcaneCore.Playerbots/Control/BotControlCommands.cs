using ArcaneCore.Game.Maps;
using System.Globalization;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Commands;
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
        catch (OperationCanceledException ex)
        {
            FaultLedger.Shared.Record("playerbot.control", ex);
            AnomalyLedger.Shared.Report("bot-control", "command-fault", "botctl " + call.Verb.Name, "the world stopped before the command ran");
            return new BotControlResult(call.Verb.Name).Fail("world-stopping", "the world stopped before the command ran");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.control", error);
            AnomalyLedger.Shared.Report("bot-control", "command-fault", "botctl " + call.Verb.Name, $"{error.GetType().Name}: {error.Message}");
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
    /// The bots the selector of <paramref name="call"/> names (<see cref="BotCall.Selector"/>; docs/ops/botctl.md, "Naming bots"), or the
    /// error. World thread (the filters read the live players). More than <see cref="PlayerbotOptions.MaxBulkCommand"/> bots need <c>--force</c>.
    /// </summary>
    internal static string? Select(BotCall call, out List<Picked> bots)
    {
        bots = [];
        string? error = BotSelector.TryResolve(call.Selector, new FeatureBotSelectorSource(call.Hub.Feature, call.Hub), out BotSelection selection);
        if (error is not null) return error;
        bots = [.. selection.Bots.Select(b => new Picked(b.BotId, b.Name))];
        int cap = call.Context.Session.Services.GetService<IOptions<PlayerbotOptions>>()?.Value.MaxBulkCommand ?? 50;
        if (bots.Count > cap && !call.Has("--force"))
            return string.Create(CultureInfo.InvariantCulture, $"too-many: {bots.Count} bots match; the cap is {cap} (World:Playerbots:MaxBulkCommand); add --force");
        return null;
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
                    FaultLedger.Shared.Record("playerbot.control", error2);
                    AnomalyLedger.Shared.Report("bot-control", "command-fault", "botctl " + call.Verb.Name, $"{error2.GetType().Name}: {error2.Message}");
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
