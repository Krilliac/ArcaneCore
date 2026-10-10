using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Handlers;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Net;

/// <summary>The socketless session behind the server console (docs/areas/server-console.md).</summary>
public sealed partial class WorldSession
{
    /// <summary>The account name of the console session.</summary>
    internal const string ConsoleAccountName = "CONSOLE";

    /// <summary>
    /// The console's session: account 0, <see cref="AccountSecurity.Administrator"/>, no socket (the managed capture queue
    /// absorbs any packet a handler sends it). It is never registered in the <see cref="SessionRegistry"/>, so it is not
    /// counted as online, cannot be kicked and never receives broadcasts. <paramref name="scopeServices"/> is the scope the
    /// console owns; the caller disposes it.
    /// </summary>
    internal static WorldSession CreateConsole(IServiceProvider scopeServices, OpcodeTable opcodes, WorldRuntime world,
        SessionRegistry registry, WorldSessionOptions options, ILogger logger)
        => new(Stream.Null, "console", scopeServices, opcodes, world, registry, options, logger)
        {
            _managed = true,
            AccountId = 0,
            AccountName = ConsoleAccountName,
            Security = AccountSecurity.Administrator,
        };
}
