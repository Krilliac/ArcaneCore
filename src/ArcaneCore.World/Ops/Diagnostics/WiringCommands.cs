using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Ops.Diagnostics;

/// <summary>
/// <c>.server wiring</c> (administrator, also at the server console): runs <see cref="WiringAudit"/> now and lists what is declared but not
/// wired (docs/ops/wiring-audit.md). Added to the builtin <c>server</c> group as an <see cref="ICommandExtension"/>; no vmangos equivalent.
/// </summary>
public sealed class WiringCommands : ICommandExtension
{
    /// <summary>Most findings listed in one reply; the rest are counted.</summary>
    public const int MaxListed = 40;

    public string Path => "server";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("wiring", AccountSecurity.Administrator,
            "Syntax: .server wiring\nList what the running server declares but does not wire: client opcodes that arrived with no handler (counted since start), options that a class asks for but nothing configures, config checks that check-config never runs, handlers registered for server-only opcodes.",
            Wiring, RetailLevel: 6),
    ];

    private static bool Wiring(CommandContext context, string args)
    {
        if (args.Trim().Length != 0)
        {
            return false;
        }

        if (context.Session.Services.GetService<WiringAudit>() is not { } audit)
        {
            context.Reply("The wiring audit is not available on this host.");
            return true;
        }

        foreach (string line in Format(audit.Run()))
        {
            context.Reply(line);
        }

        return true;
    }

    /// <summary>The reply lines of a report: the summary, then one line per finding (at most <see cref="MaxListed"/>).</summary>
    public static IReadOnlyList<string> Format(WiringReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string> { report.Summary };
        foreach (WiringFinding finding in report.Findings.Take(MaxListed))
        {
            lines.Add(finding.ToString());
        }

        if (report.Findings.Count > MaxListed)
        {
            lines.Add($"... and {report.Findings.Count - MaxListed} more.");
        }

        return lines;
    }
}
