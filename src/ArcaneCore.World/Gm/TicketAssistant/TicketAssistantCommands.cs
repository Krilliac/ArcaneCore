using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.TicketAssistant;

/// <summary>
/// <c>.ticket assistant status|on|off|run $id</c>: the GM ticket assistant's switch and test hook (ArcaneCore only). Every change is
/// written to the GM audit log under the invoking GM.
/// </summary>
public sealed class TicketAssistantCommands : ICommandExtension
{
    public string Path => "ticket";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("assistant", AccountSecurity.GameMaster,
            "Syntax: .ticket assistant status|on|off|run $id\nstatus: whether the ticket assistant is on and what it has done (Game Master). on/off/run: Administrator only; on/off switch it until the daemon restarts, run $id handles that open ticket now.",
            Assistant),
    ];

    /// <summary>Why a Game Master is refused on/off/run: they change the world for players (revive, unstuck, combat reset), so they need an Administrator.</summary>
    internal const string AdministratorOnly = "Only an Administrator can switch the ticket assistant or run it on a ticket. status is open to Game Masters.";

    private static bool Assistant(CommandContext context, string text)
    {
        string word = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries) is [var first, ..] ? first.ToLowerInvariant() : string.Empty;
        GmTicketAssistant? assistant = context.Session.Services.GetService<GmTicketAssistant>();
        if (assistant is null)
        {
            context.Reply("The ticket assistant is not available on this host.");
            return true;
        }

        switch (word)
        {
            case "status":
                TicketAssistantStatus status = assistant.Status();
                context.Reply($"Ticket assistant: {(status.Enabled ? "on" : "off")}, model {(status.ModelAvailable ? "in use" : "not in use (keywords only)")}.");
                context.Reply($"Handled {status.Handled}: fixed {status.Fixed}, answered {status.Answered}, left to a GM {status.Escalated} ({status.NeedsHuman} still open for a human).");
                return true;
            case "on":
            case "off":
                if (context.Security < AccountSecurity.Administrator)
                {
                    context.Reply(AdministratorOnly);
                    return true;
                }

                assistant.SetEnabled(word == "on");
                Record(context, $"ticket assistant {word}", $"{context.InvokerName} switched the ticket assistant {word}");
                context.Reply($"Ticket assistant switched {word}.");
                return true;
            case "run":
                if (context.Security < AccountSecurity.Administrator)
                {
                    context.Reply(AdministratorOnly);
                    return true;
                }

                string rest = text.Trim()[3..].Trim();
                if (!uint.TryParse(rest, out uint id) || id == 0 || id > int.MaxValue)
                {
                    return false;
                }

                if (!assistant.Enabled)
                {
                    context.Reply("The ticket assistant is off. Use .ticket assistant on first.");
                    return true;
                }

                Record(context, "ticket assistant run", $"{context.InvokerName} ran the ticket assistant on ticket {id}");
                Task<TicketOutcome?> run = assistant.ProcessTicketAsync((int)id, force: true);
                _ = run.ContinueWith(
                    done => context.World.Post(() => context.Reply(Describe((int)id, done))),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return true;
            default:
                return false;
        }
    }

    private static string Describe(int id, Task<TicketOutcome?> done)
    {
        if (done.IsFaulted || done.IsCanceled)
        {
            return $"The ticket assistant could not handle ticket {id}.";
        }

#pragma warning disable ARC0006 // faulted and canceled tasks returned above, so the task is finished and Result does not block
        return done.Result is { } outcome
            ? $"Ticket {id}: {outcome.Category} -> {outcome.Action}{(outcome.Closed ? " (closed)" : " (left open)")}: {outcome.Detail}."
            : $"Ticket {id} is not open.";
#pragma warning restore ARC0006
    }

    private static void Record(CommandContext context, string command, string line)
        => context.Session.Services.GetService<Audit.GmAuditFeature>()?.RecordCommand(context.Session.AccountId, context.InvokerName, command, line);
}
