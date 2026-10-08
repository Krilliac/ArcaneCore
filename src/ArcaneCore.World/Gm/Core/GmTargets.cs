using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.Core;

/// <summary>Target resolution shared by the GM commands.</summary>
public static class GmTargets
{
    /// <summary>The reply when the owner account of an offline character cannot be read.</summary>
    public const string AccountLookupFailed = "The account database is unavailable; nothing was changed. See the server log.";

    /// <summary>
    /// vmangos ExtractPlayerTarget for online players: a name or <c>|Hplayer:|</c> link from
    /// <paramref name="args"/> when present, else the selected player (the invoker when nothing is
    /// selected). Replies "Player not found!" and returns false when there is none.
    /// </summary>
    public static bool TryPlayer(CommandContext context, CommandArgs args, out Player target)
    {
        if (PlayerTargetResolver.TryExtract(args, name => context.World.FindOnlinePlayer(name), context.SelectedPlayerOrSelf, out Player? found, out _)
            && found is not null)
        {
            target = found;
            return true;
        }

        context.Reply(GmStrings.PlayerNotFound);
        target = null!;
        return false;
    }

    /// <summary><see cref="TryPlayer(CommandContext, CommandArgs, out Player)"/> for an already extracted name argument (null: the selection).</summary>
    public static bool TryPlayer(CommandContext context, string? nameArg, out Player target)
        => TryPlayer(context, new CommandArgs(nameArg ?? string.Empty), out target);

    /// <summary>
    /// The security of account <paramref name="accountId"/>, the owner of an offline character (vmangos
    /// HasLowerSecurity(NULL, guid) reads it through the guid's account, Chat.cpp:1521-1538); null when the
    /// account does not exist. Database work: call it off the world thread.
    /// </summary>
    public static async Task<AccountSecurity?> FindAccountSecurityAsync(IServiceProvider services, int accountId)
    {
        IReadOnlyDictionary<int, string> names = await services.GetRequiredService<IAccountAdmin>()
            .GetUsernamesAsync([accountId]).ConfigureAwait(false);
        Account? owner = names.TryGetValue(accountId, out string? username)
            ? await services.GetRequiredService<IAccountStore>().FindByUsernameAsync(username).ConfigureAwait(false)
            : null;
        return owner?.Security;
    }

    /// <summary>
    /// <see cref="CommandContext.CanActOn"/> for a character that is not online: runs <paramref name="act"/> on the
    /// world thread when the invoker may act on an account of account <paramref name="accountId"/>'s security. An
    /// invoker no account can outrank, or the character's own account, needs no lookup and acts at once; otherwise the
    /// owner's security is read off the world thread and the answer (the action, "You have low security level for
    /// this.", or "Player not found!") arrives later.
    /// </summary>
    public static void ActOnOffline(CommandContext context, int accountId, Action act)
        => ActOnOffline(context, [accountId], act, unknownAccountIsNotFound: true);

    /// <summary>
    /// <see cref="ActOnOffline(CommandContext, int, Action)"/> for several offline characters at once (the members of a
    /// guild being disbanded): <paramref name="act"/> runs only when the invoker may act on every one of
    /// <paramref name="accountIds"/>. An account that no longer exists answers "Player not found!" when
    /// <paramref name="unknownAccountIsNotFound"/> is set; otherwise it has no security to protect and is skipped.
    /// </summary>
    public static void ActOnOffline(CommandContext context, IReadOnlyCollection<int> accountIds, Action act, bool unknownAccountIsNotFound)
    {
        int[] others = [.. accountIds.Where(id => id != context.Session.AccountId).Distinct()];
        if (others.Length == 0
            || !GmSecurity.HasLowerSecurity(context.Security, AccountSecurity.Administrator, strong: false, context.Commands.Gm))
        {
            act();
            return;
        }

        IServiceScopeFactory scopes = context.Session.Services.GetRequiredService<IServiceScopeFactory>();
        ILogger logger = context.Session.Logger;
        _ = Task.Run(async () =>
        {
            var owners = new List<AccountSecurity?>(others.Length);
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                foreach (int accountId in others)
                {
                    owners.Add(await FindAccountSecurityAsync(scope.ServiceProvider, accountId).ConfigureAwait(false));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogError(ex, "reading the security of accounts {AccountIds} for a GM command failed", string.Join(",", others));
                context.World.Post(() => context.Reply(AccountLookupFailed));
                return;
            }

            context.World.Post(() =>
            {
                if (unknownAccountIsNotFound && owners.Contains(null))
                {
                    context.Reply(GmStrings.PlayerNotFound);
                }
                else if (owners.Any(owner => owner is { } security
                    && GmSecurity.HasLowerSecurity(context.Security, security, strong: false, context.Commands.Gm)))
                {
                    context.Reply(GmStrings.SecurityTooLow);
                }
                else
                {
                    act();
                }
            });
        });
    }
}
