using ArcaneCore.Data.ClientData;
using ArcaneCore.Data.Content;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.ClientData;

/// <summary>
/// <c>.arcane dbc</c> (GameMaster): the client data start-up report, one line per DBC the daemon reads (loaded N records, missing or
/// format mismatch, and where the path came from). <c>.arcane dbc validate</c> (Administrator, it scans the world database): the
/// world database's references into the client DBCs and how many ids dangle (<see cref="DbcCrossReferences"/>); the same report as
/// <c>arcane-db dbc</c>. No reference core has either, so they live under ArcaneCore's own <c>.arcane</c> root. Read-only.
/// </summary>
public sealed class ClientDataCommands : ICommandExtension
{
    /// <summary>The most lines <c>.arcane dbc validate</c> sends after its summary.</summary>
    public const int MaxValidateLines = 40;

    public string Path => "arcane";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("dbc", AccountSecurity.GameMaster,
            "Syntax: .arcane dbc\nShow how each client DBC the server reads was resolved (loaded, missing or format mismatch) and where from.", Status, Children:
            [
                new ChatCommand("validate", AccountSecurity.Administrator,
                    "Syntax: .arcane dbc validate\nCheck the world database's spell, map, area, faction, display ... ids against the DBCs in ClientData:DbcDirectory and report the dangling ones.", Validate),
            ]),
    ];

    private static ClientDataReport ReportOf(CommandContext context)
        => context.Session.Services.GetService<ClientDataReport>() ?? ClientDataReport.Empty;

    private static bool Status(CommandContext context, string args)
    {
        if (args.Length > 0)
        {
            return false;
        }

        foreach (ClientDataLine line in ReportOf(context).Lines())
        {
            context.Reply((line.Warning ? "WARN " : string.Empty) + line.Text);
        }

        return true;
    }

    private static bool Validate(CommandContext context, string args)
    {
        if (args.Length > 0)
        {
            return false;
        }

        ClientDataReport report = ReportOf(context);
        if (!report.DirectoryExists)
        {
            context.Reply(report.DirectoryConfigured
                ? $"ClientData:DbcDirectory {report.Options.DbcDirectory} does not exist: nothing to validate against."
                : "ClientData:DbcDirectory is not set: nothing to validate against.");
            return true;
        }

        IServiceProvider services = context.Session.Services;
        IDbContextFactory<WorldDbContext>? factory = services.GetService<IDbContextFactory<WorldDbContext>>();
        if (factory is null)
        {
            context.Reply("No world database is configured.");
            return true;
        }

        ILogger logger = context.Session.Logger;
        string directory = report.Options.DbcDirectory;
        context.Reply("Validating the world database against the client DBCs...");
        _ = Task.Run(async () =>
        {
            try
            {
                await using WorldDbContext db = await factory.CreateDbContextAsync().ConfigureAwait(false);
                IReadOnlyList<DbcReferenceResult> results = await DbcCrossReferences.RunAsync(db.Database.GetDbConnection(), directory).ConfigureAwait(false);
                IReadOnlyList<string> lines = DbcCrossReferences.Lines(results);
                foreach (string line in lines)
                {
                    logger.LogInformation("{DbcValidate}", line);
                }

                context.World.Post(() =>
                {
                    context.Reply(lines[0]);
                    foreach (string line in lines.Skip(1).Take(MaxValidateLines))
                    {
                        context.Reply(line);
                    }

                    if (lines.Count - 1 > MaxValidateLines)
                    {
                        context.Reply($"... {lines.Count - 1 - MaxValidateLines} more lines in the server log.");
                    }
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, ".arcane dbc validate failed");
                context.World.Post(() => context.Reply("DBC validation failed; see the server log."));
            }
        });
        return true;
    }
}
