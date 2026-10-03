using System.Globalization;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;

namespace ArcaneCore.World.HotCode;

/// <summary>
/// <c>.hotcode status | refresh | freeze | thaw</c>, Administrator only. Observe and pause only:
/// none of these loads code (the runtime applies edits; this server adds no way to feed it any).
/// </summary>
/// <remarks>
/// Not an <see cref="ICommandGroup"/> on purpose: groups are discovered and registered
/// unconditionally, while this root must exist only when <c>World:HotCode:Enabled</c> is set.
/// <see cref="HotCodeHost"/> appends it to the live command table at start.
/// </remarks>
public static class HotCodeCommands
{
    public const string RootName = "hotcode";

    public static ChatCommand Create(HotCodeRefresh refresh, HotCodeAudit audit)
        => new(RootName, AccountSecurity.Administrator, "Code hot reload (development runner).", Children:
        [
            new ChatCommand("status", AccountSecurity.Administrator, "Syntax: .hotcode status — generation, frozen/degraded state and whether the process differs from the build.",
                (context, args) =>
                {
                    context.Reply(FormatStatus(refresh.State.Snapshot()));
                    return true;
                }),
            new ChatCommand("refresh", AccountSecurity.Administrator, "Syntax: .hotcode refresh — rescan opcode handlers, chat commands and default map updaters now.",
                (context, args) =>
                {
                    string who = context.Player.Name;
                    audit.Record("hotcode-command", $"{who} refresh");
                    Task<RefreshResult> task = refresh.RefreshNowAsync($"requested by {who}");

                    // The commit is posted to this very (world) thread: never block on it. Answer from the next tick.
                    _ = task.ContinueWith(
                        finished => context.World.Post(() => context.Reply(finished.IsCompletedSuccessfully
                            ? $"Refresh {finished.Result.Status}: {finished.Result.Detail}"
                            : $"Refresh failed: {finished.Exception?.GetBaseException().Message}")),
                        TaskScheduler.Default);
                    context.Reply("Refresh requested.");
                    return true;
                }),
            new ChatCommand("freeze", AccountSecurity.Administrator, "Syntax: .hotcode freeze — stop registry refreshes (applied code edits stay applied).",
                (context, args) =>
                {
                    audit.Record("hotcode-command", $"{context.Player.Name} freeze");
                    refresh.State.SetFrozen(true);
                    context.Reply("Hot code refresh is frozen.");
                    return true;
                }),
            new ChatCommand("thaw", AccountSecurity.Administrator, "Syntax: .hotcode thaw — resume registry refreshes.",
                (context, args) =>
                {
                    audit.Record("hotcode-command", $"{context.Player.Name} thaw");
                    refresh.State.SetFrozen(false);
                    context.Reply("Hot code refresh is active.");
                    return true;
                }),
        ]);

    public static string FormatStatus(HotCodeStatus status)
    {
        string diverged = status.DivergedFromBuild
            ? $"yes, {status.MetadataUpdates} code edits applied in memory (no rollback; restart to return to the built state)"
            : "no";
        string degraded = status.Degraded ? $"yes, the last refresh was rejected: {status.DegradedReason}" : "no";
        string last = status.LastRefreshUtc is { } at ? at.ToString("u", CultureInfo.InvariantCulture) : "never";
        return $"Code hot reload: generation {status.Generation}, applied {status.AppliedGeneration}, frozen: {(status.Frozen ? "yes" : "no")}, degraded: {degraded}\n"
            + $"Refreshes applied: {status.Applied}, rejected: {status.Rejected}, last: {last}\n"
            + $"Process diverged from build: {diverged}";
    }
}
