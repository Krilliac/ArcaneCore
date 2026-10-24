using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// What an actor may run, decided before anything executes, from the path the command table resolves the line to
/// (<see cref="CommandTable.Lookup"/>): so an abbreviation (<c>.zadm</c> for <c>.zadmin</c>) is judged as the command it will run as. The checks, in order:
/// unknown command, Console-only (<see cref="ConsoleOnlyCommands"/>: no tool actor is the true console), the deny lists (<see cref="MapFeedActionsOptions.Deny"/>
/// for everyone, <see cref="MapFeedActionsOptions.PublicDeny"/> for web actors), and the actor's security against the command's level. Pure; any thread.
/// </summary>
internal sealed class MapFeedActionPolicy
{
    private readonly string[][] _deny;
    private readonly string[][] _publicDeny;

    public MapFeedActionPolicy(MapFeedActionsOptions options)
    {
        _deny = MapFeedActionsOptions.DenyWords(options.Deny);
        IEnumerable<string> publicDeny = options.PublicAllowModeration
            ? options.PublicDeny.Where(e => !IsModerationRoot(e))
            : options.PublicDeny;
        _publicDeny = MapFeedActionsOptions.DenyWords(publicDeny);
    }

    /// <summary>The command line as the console executor will run it (its <c>quit</c>, <c>exit</c> and <c>shutdown</c> rewrites applied), so the policy judges what runs.</summary>
    public static string Normalize(string line) => Ops.ServerConsole.WorldConsoleExecutor.Normalize(line);

    /// <summary>
    /// The refusal for <paramref name="commandText"/> (no prefix, already normalized) for an actor of <paramref name="security"/>, or null when the
    /// table would run it. <paramref name="found"/> is the lookup the verdict is about (its <see cref="CommandLookup.Path"/> is empty when unknown).
    /// </summary>
    public FeedError? Check(CommandTable table, string commandText, AccountSecurity security, bool isPublic, out CommandLookup found)
    {
        found = table.Lookup(commandText, security);
        if (found.Result == CommandLookupResult.Unknown)
        {
            return new FeedError(ActionCodes.UnknownCommand, "there is no such command");
        }

        if (found.Result == CommandLookupResult.UnknownSubcommand)
        {
            // Nothing runs for a group without a handler: the table only prints its help. The same deny rules still apply to the group itself.
            return Denied(found.Path, isPublic);
        }

        if (ConsoleOnlyCommands.IsConsoleOnly(found.Path))
        {
            return new FeedError(ActionCodes.ConsoleOnly, $"'{found.Path}' can only be run at the server console");
        }

        if (Denied(found.Path, isPublic) is { } denied)
        {
            return denied;
        }

        return found.Available ? null : new FeedError(ActionCodes.Security, "this command is not available at your security level");
    }

    private FeedError? Denied(string path, bool isPublic)
    {
        string[] words = path.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (Matches(_deny, words) || (isPublic && Matches(_publicDeny, words)))
        {
            return new FeedError(ActionCodes.Security, $"'{path}' is not allowed on this channel");
        }

        return null;
    }

    private static bool Matches(string[][] entries, string[] path)
    {
        foreach (string[] entry in entries)
        {
            if (entry.Length <= path.Length && entry.AsSpan().SequenceEqual(path.AsSpan(0, entry.Length)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsModerationRoot(string entry)
        => entry.Trim().TrimStart('.', '!').Equals("ban", StringComparison.OrdinalIgnoreCase)
            || entry.Trim().TrimStart('.', '!').Equals("unban", StringComparison.OrdinalIgnoreCase);
}
