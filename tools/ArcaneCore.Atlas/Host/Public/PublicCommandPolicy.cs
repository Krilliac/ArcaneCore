namespace ArcaneCore.Atlas.Host.Public;

/// <summary>
/// The command policy of the public listener (design 0.3.1, I9): an explicit allowlist. A command that is not named here is <c>not-available</c> whatever
/// its surfaces, so a command added to the registry tomorrow is denied until someone lists it. The test enumerates the registry to keep it that way.
/// Pure and thread-safe.
/// </summary>
internal static class PublicCommandPolicy
{
    /// <summary>What every visitor, guest or account, may run. <c>live.entities</c> and <c>live.find</c> are the projected public versions, not the Local commands of the same name.</summary>
    public static readonly IReadOnlySet<string> GuestCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "guest.status",
        "guest.maps",
        "guest.layers",
        "guest.help",
        "live.entities",
        "live.find",
        "pick.public",
        "bookmarks.list",
    };

    /// <summary>What a logged-in account may add (any level that could log in).</summary>
    public static readonly IReadOnlySet<string> AccountCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "me.characters",
    };

    /// <summary>What an account at or above <c>MinActionSecurity</c> may add: the action channel. Run through the world with the account's ticket, never as the daemon.</summary>
    public static readonly IReadOnlySet<string> ActionCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "action.run",
        "action.at",
        "action.on",
        "action.catalog",
        "action.confirm",
        "live.inspect",
        "live.highlight",
    };

    /// <summary>Whether <paramref name="command"/> is a name this policy lists at all (any tier). The only names a reply may echo: client text is never repeated otherwise.</summary>
    public static bool IsKnown(string command) => GuestCommands.Contains(command) || AccountCommands.Contains(command) || ActionCommands.Contains(command);

    /// <summary>Whether a visitor may run <paramref name="command"/>. False for every name not listed above, including names no command has.</summary>
    /// <param name="command">The command name as sent.</param>
    /// <param name="tier">Guest or Account.</param>
    /// <param name="security">The account's security level (0 for a guest).</param>
    /// <param name="options">The listener settings (<c>MinActionSecurity</c>, <c>AccountActions</c>).</param>
    public static bool IsAllowed(string command, PublicTier tier, int security, PublicOptions options)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(options);
        if (GuestCommands.Contains(command))
        {
            return true;
        }

        if (tier != PublicTier.Account)
        {
            return false;
        }

        if (AccountCommands.Contains(command))
        {
            return true;
        }

        return ActionCommands.Contains(command) && options.AccountActions && security >= Math.Max(1, (int)options.MinActionSecurity);
    }

    /// <summary>The names of every command a visitor with this tier and level may run, sorted (the body of <c>guest.help</c>).</summary>
    public static IReadOnlyList<string> AllowedFor(PublicTier tier, int security, PublicOptions options)
        => [.. GuestCommands.Concat(AccountCommands).Concat(ActionCommands).Where(c => IsAllowed(c, tier, security, options)).Order(StringComparer.Ordinal)];
}

/// <summary>
/// The seam for the action channel on the public listener (<c>action.*</c>, <c>live.inspect</c>, <c>live.highlight</c>): a host that offers the channel
/// supplies an implementation that runs the command in the world with the session's ticket. Without one these commands are <c>not-available</c>.
/// </summary>
internal interface IPublicActionRunner
{
    /// <summary>Runs an allowlisted action command for an account session, returning the data of a successful result; throws <see cref="Core.Commands.CommandException"/> on failure.</summary>
    Task<System.Text.Json.Nodes.JsonNode?> RunAsync(PublicSession session, string command, System.Text.Json.Nodes.JsonObject args, CancellationToken cancellationToken);
}
