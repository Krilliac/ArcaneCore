using System.Globalization;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;

namespace ArcaneCore.World.Commands;

/// <summary>
/// <c>.server faults [top N | show ID | reset]</c>: the server's fault ledger (<see cref="FaultLedger"/>) from the console or in game. Without arguments it
/// lists the ten most frequent faults; <c>show</c> prints one fault in full (its id may be abbreviated to four or more characters); <c>reset</c> clears
/// the ledger and needs Administrator authority (the console has it). Viewing needs GameMaster. The ledger is process-wide state read under its own locks,
/// so the command is safe from the world thread. Output is plain lines (no colour codes) so the console and the chat show it alike.
/// Also formats the one-line summary <c>.server info</c> adds (<see cref="InfoLine"/>).
/// </summary>
public sealed class ServerFaultCommands : ICommandExtension
{
    /// <summary>Faults listed when no count is given.</summary>
    public const int DefaultTop = 10;

    /// <summary>The most faults one listing shows.</summary>
    public const int MaxTop = 50;

    /// <summary>The authority <c>reset</c> needs.</summary>
    public const AccountSecurity ResetSecurity = AccountSecurity.Administrator;

    public string Path => "server";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("faults", AccountSecurity.GameMaster,
            "Syntax: .server faults [top #count | show $id | reset]\nList the exceptions the server caught, most frequent first; show one in full; reset (administrator) clears the list.",
            Faults),
    ];

    private static bool Faults(CommandContext context, string args)
        => Run(args, context.Security, FaultLedger.Shared, DateTimeOffset.UtcNow, context.Reply);

    /// <summary>The command's logic apart from the invoker (tests call it with their own ledger). False means a usage error.</summary>
    internal static bool Run(string args, AccountSecurity security, FaultLedger ledger, DateTimeOffset now, Action<string> reply)
    {
        string[] words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            List(ledger, DefaultTop, now, reply);
            return true;
        }

        switch (words[0].ToLowerInvariant())
        {
            case "top":
            {
                int count = DefaultTop;
                if (words.Length > 2 || words.Length == 2 && !int.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out count))
                {
                    return false;
                }

                List(ledger, Math.Clamp(count, 1, MaxTop), now, reply);
                return true;
            }

            case "show":
                return words.Length == 2 && Show(ledger, words[1], now, reply);

            case "reset":
                if (words.Length != 1)
                {
                    return false;
                }

                if (security < ResetSecurity)
                {
                    reply("Only an administrator can reset the fault ledger.");
                    return true;
                }

                long total = ledger.Total;
                int distinct = ledger.Distinct;
                ledger.Reset();
                reply(string.Create(CultureInfo.InvariantCulture, $"Fault ledger cleared ({distinct} distinct, {total} total)."));
                return true;

            default:
                return false;
        }
    }

    /// <summary>The <c>.server info</c> line: <c>faults: 12 (3 distinct, last 5 min ago)</c>.</summary>
    public static string InfoLine(FaultLedger ledger, DateTimeOffset now)
    {
        string last = ledger.LastSeen is { } at ? Age(now - at) + " ago" : "never";
        return string.Create(CultureInfo.InvariantCulture, $"faults: {ledger.Total} ({ledger.Distinct} distinct, last {last})");
    }

    private static void List(FaultLedger ledger, int top, DateTimeOffset now, Action<string> reply)
    {
        reply(InfoLine(ledger, now));
        IReadOnlyList<FaultSnapshot> all = ledger.Snapshot();
        int shown = 0;
        foreach (FaultSnapshot fault in all)
        {
            if (shown++ == top)
            {
                break;
            }

            reply(string.Create(CultureInfo.InvariantCulture,
                $"{shown,2}. {fault.Id} x{fault.Count} {fault.Site} {fault.ExceptionType}, last {Age(now - fault.LastSeen)} ago"));
        }

        if (all.Count > top)
        {
            reply(string.Create(CultureInfo.InvariantCulture, $"... {all.Count - top} more; .server faults top {Math.Min(MaxTop, all.Count)} lists more."));
        }

        if (all.Count > 0)
        {
            reply("Use .server faults show <id> for the frames and the message.");
        }
    }

    private static bool Show(FaultLedger ledger, string id, DateTimeOffset now, Action<string> reply)
    {
        string wanted = id.ToLowerInvariant();
        if (wanted.Length < 4 || !wanted.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        FaultSnapshot[] matches = [.. ledger.Snapshot().Where(f => f.Id.StartsWith(wanted, StringComparison.Ordinal))];
        if (matches.Length == 0)
        {
            reply("No fault with that id.");
            return true;
        }

        if (matches.Length > 1)
        {
            reply("That id matches " + string.Join(", ", matches.Select(f => f.Id)) + "; give more characters.");
            return true;
        }

        FaultSnapshot fault = matches[0];
        reply(string.Create(CultureInfo.InvariantCulture, $"Fault {fault.Id}: {fault.ExceptionType} at {fault.Site}"));
        reply(string.Create(CultureInfo.InvariantCulture,
            $"Count {fault.Count}; first {fault.FirstSeen.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC, last {fault.LastSeen.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC ({Age(now - fault.LastSeen)} ago)"));
        reply("Message: " + (fault.SampleMessage.Length == 0 ? "(none)" : fault.SampleMessage.ReplaceLineEndings(" ")));
        if (!string.IsNullOrEmpty(fault.SampleStack))
        {
            reply("Stack: " + fault.SampleStack);
        }

        if (!string.IsNullOrEmpty(fault.SampleChain) && fault.SampleChain.Contains(" <- ", StringComparison.Ordinal))
        {
            reply("Chain: " + fault.SampleChain);
        }

        if (fault.Frames.Count == 0)
        {
            reply("Frames: (none inside ArcaneCore)");
        }

        foreach (string frame in fault.Frames)
        {
            reply("  at " + frame);
        }

        return true;
    }

    private static string Age(TimeSpan span)
        => GmDuration.SecsToTimeString((long)Math.Max(0, span.TotalSeconds), shortText: true).Trim();
}
