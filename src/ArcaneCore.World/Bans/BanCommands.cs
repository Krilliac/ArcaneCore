using System.Net;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Bans;

/// <summary>
/// <c>.ban</c>, <c>.unban</c>, <c>.baninfo</c> and <c>.banlist</c> for account, character and ip, ported from
/// vmangos game/Commands/AccountCommands.cpp:516-1010 and World.cpp:2461-2665. Mechanics kept as retail: the
/// duration goes through TimeStringToSecs (a bad one is a PERMANENT ban unless <c>Bans:RejectUnparseableDuration</c>),
/// arguments are ExtractArg tokens (the reason is ONE token), the invoker's own account is never kicked by their
/// ban, and the database work runs asynchronously with the reply sent when it completes (vmangos BanQueryHolder).
/// The kick itself happens through <see cref="AccountStatusEvents"/>, shared with every other ban writer.
/// <para>
/// Security: every row of the classic-db <c>command</c> table for ban/unban/baninfo/banlist is level 3, which is
/// <see cref="AccountSecurity.Administrator"/> on ArcaneCore's four-level scale. Without a last_ip column
/// <c>.ban ip</c> kicks the live sessions from that address and always reports success (retail kicks accounts whose
/// last_ip matches and prints "ip X not found" when none do).
/// </para>
/// </summary>
public sealed class BanCommands : ICommandGroup
{
    private const string DatabaseError = "The ban database is unavailable; see the server log.";
    private const int MaxAccountNameLength = 16;

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("ban", AccountSecurity.Administrator, "Ban an account, character or IP address.", Children:
        [
            new ChatCommand("account", AccountSecurity.Administrator, "Syntax: .ban account $Name $bantime $reason — $bantime is like 1d2h3m4s, 0 or an unknown format is permanent; the reason is one word or quoted.", BanAccount),
            new ChatCommand("character", AccountSecurity.Administrator, "Syntax: .ban character $Name $bantime $reason — bans the character's account.", BanCharacter),
            new ChatCommand("ip", AccountSecurity.Administrator, "Syntax: .ban ip $Ip $bantime $reason", BanIp),
        ]),
        new ChatCommand("unban", AccountSecurity.Administrator, "Lift a ban.", Children:
        [
            new ChatCommand("account", AccountSecurity.Administrator, "Syntax: .unban account $Name $message", UnbanAccount),
            new ChatCommand("character", AccountSecurity.Administrator, "Syntax: .unban character $Name $message", UnbanCharacter),
            new ChatCommand("ip", AccountSecurity.Administrator, "Syntax: .unban ip $Ip $message", UnbanIp),
        ]),
        new ChatCommand("baninfo", AccountSecurity.Administrator, "Show ban history.", Children:
        [
            new ChatCommand("account", AccountSecurity.Administrator, "Syntax: .baninfo account $accountid|$name", BanInfoAccount),
            new ChatCommand("character", AccountSecurity.Administrator, "Syntax: .baninfo character $Name", BanInfoCharacter),
            new ChatCommand("ip", AccountSecurity.Administrator, "Syntax: .baninfo ip $Ip", BanInfoIp),
        ]),
        new ChatCommand("banlist", AccountSecurity.Administrator, "List bans.", Children:
        [
            new ChatCommand("account", AccountSecurity.Administrator, "Syntax: .banlist account [$Name] — accounts with a ban whose name starts with $Name.", BanListAccount),
            new ChatCommand("character", AccountSecurity.Administrator, "Syntax: .banlist character $Name — banned accounts owning a character whose name starts with $Name.", BanListCharacter),
            new ChatCommand("ip", AccountSecurity.Administrator, "Syntax: .banlist ip [$Ip] — banned addresses starting with $Ip.", BanListIp),
        ]),
    ];

    private enum Kind { Account, Character, Ip }

    // --- .ban ---------------------------------------------------------------------

    private static bool BanAccount(CommandContext context, string args) => Ban(context, args, Kind.Account);

    private static bool BanCharacter(CommandContext context, string args) => Ban(context, args, Kind.Character);

    private static bool BanIp(CommandContext context, string args) => Ban(context, args, Kind.Ip);

    private static bool Ban(CommandContext context, string args, Kind kind)
    {
        // HandleBanHelper (AccountCommands.cpp:588-638): target, duration, reason — each an ExtractArg token.
        string rest = args;
        if (BanCommandText.ExtractArg(ref rest) is not { } target
            || BanCommandText.ExtractArg(ref rest) is not { } duration
            || BanCommandText.ExtractArg(ref rest) is not { } reason)
        {
            return false;
        }

        BanOptions options = OptionsOf(context);
        if (options.RejectUnparseableDuration && !BanCommandText.IsWellFormedDuration(duration))
        {
            return false;
        }

        uint seconds = BanCommandText.TimeStringToSecs(duration);
        string display;
        switch (kind)
        {
            case Kind.Account:
                display = target.ToUpperInvariant();
                if (display.Length > MaxAccountNameLength)
                {
                    context.Reply(string.Format(BanCommandText.AccountNotExist, display));
                    return true;
                }

                break;
            case Kind.Character:
                display = Capitalize(target);
                break;
            default:
                if (!IsIpAddress(target))
                {
                    return false;
                }

                display = AccountBanEvaluator.NormalizeIp(target)!;
                break;
        }

        Run(context, async services =>
        {
            IBanStore bans = services.GetRequiredService<IBanStore>();
            string author = context.Player.Name;
            int authorAccount = context.Session.AccountId;

            if (kind == Kind.Ip)
            {
                // The IpBanned event kicks the sessions from that address (the invoker's own account is skipped).
                await bans.BanIpAsync(new IpBanRequest(display, seconds, reason, author, authorAccount)).ConfigureAwait(false);
                ReplyBanned(context, display, seconds, reason);
                return;
            }

            (int AccountId, AccountSecurity Security)? owner = await ResolveAccountAsync(services, context, kind, display).ConfigureAwait(false);
            if (owner is null)
            {
                context.Reply(string.Format(BanCommandText.BanNotFound, kind == Kind.Account ? "account" : "character", display));
                return;
            }

            await bans.BanAccountAsync(new BanRequest(owner.Value.AccountId, seconds, reason, author, authorAccount, options.RealmId)).ConfigureAwait(false);
            ReplyBanned(context, display, seconds, reason);
        });
        return true;
    }

    private static void ReplyBanned(CommandContext context, string display, uint seconds, string reason)
        => context.Reply(seconds > 0
            ? string.Format(BanCommandText.YouBanned, display, BanCommandText.SecsToTimeString(seconds), reason)
            : string.Format(BanCommandText.YouPermBanned, display, reason));

    // --- .unban -------------------------------------------------------------------

    private static bool UnbanAccount(CommandContext context, string args) => Unban(context, args, Kind.Account);

    private static bool UnbanCharacter(CommandContext context, string args) => Unban(context, args, Kind.Character);

    private static bool UnbanIp(CommandContext context, string args) => Unban(context, args, Kind.Ip);

    private static bool Unban(CommandContext context, string args, Kind kind)
    {
        // HandleUnBanHelper (AccountCommands.cpp:670-734): the message argument is required.
        string rest = args;
        if (BanCommandText.ExtractArg(ref rest) is not { } target || BanCommandText.ExtractQuotedOrLiteralArg(ref rest) is not { } message)
        {
            return false;
        }

        string display;
        switch (kind)
        {
            case Kind.Account:
                display = target.ToUpperInvariant();
                break;
            case Kind.Character:
                display = Capitalize(target);
                break;
            default:
                if (!IsIpAddress(target))
                {
                    return false;
                }

                display = AccountBanEvaluator.NormalizeIp(target)!;
                break;
        }

        Run(context, async services =>
        {
            IBanStore bans = services.GetRequiredService<IBanStore>();
            if (kind == Kind.Ip)
            {
                await bans.UnbanIpAsync(display).ConfigureAwait(false); // retail deletes and always reports success
                context.Reply(string.Format(BanCommandText.Unbanned, display));
                return;
            }

            (int AccountId, AccountSecurity Security)? owner = await ResolveAccountAsync(services, context, kind, display).ConfigureAwait(false);
            if (owner is null)
            {
                context.Reply(string.Format(BanCommandText.UnbanError, display)); // RemoveBanAccount returned false
                return;
            }

            await bans.UnbanAccountAsync(owner.Value.AccountId, context.Player.Name, message).ConfigureAwait(false);
            context.Reply(string.Format(BanCommandText.Unbanned, display));
        });
        return true;
    }

    // --- .baninfo -----------------------------------------------------------------

    private static bool BanInfoAccount(CommandContext context, string args)
    {
        string rest = args;
        if (BanCommandText.ExtractLiteralArg(ref rest) is not { } who)
        {
            return false;
        }

        Run(context, async services =>
        {
            (int Id, string Name)? account = await FindAccountByNameOrIdAsync(services, who).ConfigureAwait(false);
            if (account is null)
            {
                context.Reply(string.Format(BanCommandText.AccountNotExist, who.ToUpperInvariant()));
                return;
            }

            await ReplyHistoryAsync(services, context, account.Value.Id, account.Value.Name).ConfigureAwait(false);
        });
        return true;
    }

    private static bool BanInfoCharacter(CommandContext context, string args)
    {
        string rest = args;
        if (BanCommandText.ExtractLiteralArg(ref rest) is not { } name)
        {
            return false;
        }

        CharacterIdentity? identity = context.Session.Services.GetRequiredService<CharacterDirectory>().FindByName(name);
        if (identity is null)
        {
            context.Reply(BanCommandText.PlayerNotFound);
            return true;
        }

        Run(context, async services =>
        {
            IReadOnlyDictionary<int, string> names = await services.GetRequiredService<IAccountAdmin>()
                .GetUsernamesAsync([identity.AccountId]).ConfigureAwait(false);
            if (!names.TryGetValue(identity.AccountId, out string? accountName))
            {
                context.Reply(BanCommandText.BanInfoNoCharacter);
                return;
            }

            await ReplyHistoryAsync(services, context, identity.AccountId, accountName).ConfigureAwait(false);
        });
        return true;
    }

    private static async Task ReplyHistoryAsync(IServiceProvider services, CommandContext context, int accountId, string accountName)
    {
        IReadOnlyList<AccountBanRecord> history = await services.GetRequiredService<IBanStore>().GetHistoryAsync(accountId).ConfigureAwait(false);
        if (history.Count == 0)
        {
            context.Reply(string.Format(BanCommandText.BanInfoNoAccountBan, accountName));
            return;
        }

        IReadOnlyList<RealmEntry> realms = services.GetService<IRealmStore>() is { } realmStore
            ? await realmStore.GetRealmsAsync().ConfigureAwait(false)
            : [];
        long now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();

        context.Reply(string.Format(BanCommandText.BanInfoHistory, accountName));
        foreach (AccountBanRecord row in history)
        {
            // AccountCommands.cpp:781-803. Note retail's own comparison: active here means unbandate >= now.
            long length = row.UnbanDate - row.BanDate;
            bool permanent = length == 0;
            bool active = row.Active && (permanent || row.UnbanDate >= now);
            string realm = realms.FirstOrDefault(r => r.Id == row.Realm)?.Name ?? "NoRealm";
            context.Reply(string.Format(
                BanCommandText.BanInfoEntry,
                BanCommandText.FromUnixTime(row.BanDate),
                permanent ? BanCommandText.Infinite : BanCommandText.SecsToTimeString((ulong)length),
                active ? BanCommandText.Yes : BanCommandText.No,
                row.Reason,
                $"{row.BannedBy} ({realm})"));
        }
    }

    private static bool BanInfoIp(CommandContext context, string args)
    {
        string rest = args;
        if (BanCommandText.ExtractQuotedOrLiteralArg(ref rest) is not { } ip || !IsIpAddress(ip))
        {
            return false;
        }

        string key = AccountBanEvaluator.NormalizeIp(ip)!;
        Run(context, async services =>
        {
            IpBanRecord? ban = await services.GetRequiredService<IBanStore>().GetActiveIpBanAsync(key).ConfigureAwait(false);
            if (ban is null)
            {
                context.Reply(BanCommandText.BanInfoNoIp);
                return;
            }

            long now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
            context.Reply(string.Format(
                BanCommandText.BanInfoIpEntry,
                ban.Ip,
                BanCommandText.FromUnixTime(ban.BanDate),
                ban.IsPermanent ? BanCommandText.Never : BanCommandText.FromUnixTime(ban.UnbanDate),
                ban.IsPermanent ? BanCommandText.Infinite : BanCommandText.SecsToTimeString((ulong)Math.Max(0, ban.UnbanDate - now)),
                ban.Reason,
                ban.BannedBy));
        });
        return true;
    }

    // --- .banlist -----------------------------------------------------------------

    private static bool BanListAccount(CommandContext context, string args)
    {
        string rest = args;
        string filter = BanCommandText.ExtractLiteralArg(ref rest) ?? string.Empty;
        Run(context, async services =>
        {
            IBanStore bans = services.GetRequiredService<IBanStore>();
            await bans.PurgeExpiredAsync().ConfigureAwait(false); // retail clears expired ip_banned rows first

            int[] ids = [.. (await bans.ListActiveAccountBansAsync().ConfigureAwait(false)).Select(r => r.AccountId).Distinct().Order()];
            IReadOnlyDictionary<int, string> names = await services.GetRequiredService<IAccountAdmin>().GetUsernamesAsync(ids).ConfigureAwait(false);
            string[] matching = [.. ids.Where(names.ContainsKey).Select(i => names[i])
                .Where(n => n.StartsWith(filter, StringComparison.OrdinalIgnoreCase))];
            if (matching.Length == 0)
            {
                context.Reply(BanCommandText.BanListNoAccount);
                return;
            }

            context.Reply(BanCommandText.BanListMatchingAccount);
            context.Reply(string.Join('\n', matching));
        });
        return true;
    }

    private static bool BanListCharacter(CommandContext context, string args)
    {
        string rest = args;
        if (BanCommandText.ExtractLiteralArg(ref rest) is not { } filter)
        {
            return false;
        }

        Run(context, async services =>
        {
            IBanStore bans = services.GetRequiredService<IBanStore>();
            await bans.PurgeExpiredAsync().ConfigureAwait(false);

            IReadOnlyList<CharacterIdentity> all = await services.GetRequiredService<ICharacterStore>().GetAllIdentitiesAsync().ConfigureAwait(false);
            int[] accountIds = [.. all.Where(c => c.Name.StartsWith(filter, StringComparison.OrdinalIgnoreCase)).Select(c => c.AccountId).Distinct().Order()];
            if (accountIds.Length == 0)
            {
                context.Reply(BanCommandText.BanListNoCharacter);
                return;
            }

            // HandleBanListHelper: the header, then the name of every such account that has any ban row.
            context.Reply(BanCommandText.BanListMatchingAccount);
            IReadOnlyDictionary<int, string> names = await services.GetRequiredService<IAccountAdmin>().GetUsernamesAsync(accountIds).ConfigureAwait(false);
            var lines = new List<string>();
            foreach (int id in accountIds)
            {
                if (names.TryGetValue(id, out string? name) && (await bans.GetHistoryAsync(id).ConfigureAwait(false)).Count > 0)
                {
                    lines.Add(name);
                }
            }

            if (lines.Count > 0)
            {
                context.Reply(string.Join('\n', lines));
            }
        });
        return true;
    }

    private static bool BanListIp(CommandContext context, string args)
    {
        string rest = args;
        string filter = BanCommandText.ExtractLiteralArg(ref rest) ?? string.Empty;
        Run(context, async services =>
        {
            IBanStore bans = services.GetRequiredService<IBanStore>();
            await bans.PurgeExpiredAsync().ConfigureAwait(false);
            IReadOnlyList<IpBanRecord> rows = await bans.ListIpBansAsync(filter).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                context.Reply(BanCommandText.BanListNoIp);
                return;
            }

            context.Reply(BanCommandText.BanListMatchingIp);
            context.Reply(string.Join('\n', rows.Select(r => r.Ip)));
        });
        return true;
    }

    // --- helpers ------------------------------------------------------------------

    /// <summary>
    /// Run the store work off the world thread on its own DI scope (the session's scope is not safe to share with
    /// the session task) and reply when it completes; a failure is logged and answered, never thrown into the world.
    /// </summary>
    private static void Run(CommandContext context, Func<IServiceProvider, Task> work)
    {
        IServiceScopeFactory scopes = context.Session.Services.GetRequiredService<IServiceScopeFactory>();
        ILogger logger = context.Session.Logger;
        _ = Task.Run(async () =>
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                await work(scope.ServiceProvider).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A ban command failed");
                context.Reply(DatabaseError);
            }
        });
    }

    private static BanOptions OptionsOf(CommandContext context)
        => context.Session.Services.GetService<IOptions<BanOptions>>()?.Value ?? new BanOptions();

    /// <summary>The account named (or owning the character named) <paramref name="display"/>, with its security.</summary>
    private static async Task<(int AccountId, AccountSecurity Security)?> ResolveAccountAsync(
        IServiceProvider services, CommandContext context, Kind kind, string display)
    {
        if (kind == Kind.Account)
        {
            Account? account = await services.GetRequiredService<IAccountStore>().FindByUsernameAsync(display).ConfigureAwait(false);
            return account is null ? null : (account.Id, account.Security);
        }

        CharacterIdentity? identity = context.Session.Services.GetRequiredService<CharacterDirectory>().FindByName(display);
        if (identity is null)
        {
            return null;
        }

        IReadOnlyDictionary<int, string> names = await services.GetRequiredService<IAccountAdmin>().GetUsernamesAsync([identity.AccountId]).ConfigureAwait(false);
        if (!names.TryGetValue(identity.AccountId, out string? username))
        {
            return null;
        }

        Account? owner = await services.GetRequiredService<IAccountStore>().FindByUsernameAsync(username).ConfigureAwait(false);
        return owner is null ? null : (owner.Id, owner.Security);
    }

    /// <summary>ExtractAccountId: a number is an account id, anything else an (uppercased) account name.</summary>
    private static async Task<(int Id, string Name)?> FindAccountByNameOrIdAsync(IServiceProvider services, string who)
    {
        if (uint.TryParse(who, out uint id) && id <= int.MaxValue)
        {
            IReadOnlyDictionary<int, string> names = await services.GetRequiredService<IAccountAdmin>().GetUsernamesAsync([(int)id]).ConfigureAwait(false);
            return names.TryGetValue((int)id, out string? name) ? ((int)id, name) : null;
        }

        string upper = who.ToUpperInvariant();
        Account? account = await services.GetRequiredService<IAccountStore>().FindByUsernameAsync(upper).ConfigureAwait(false);
        return account is null ? null : (account.Id, account.Username);
    }

    private static bool IsIpAddress(string text)
        => text.Length > 0 && IPAddress.TryParse(text, out _) && AccountBanEvaluator.NormalizeIp(text) is not null;

    /// <summary>vmangos normalizePlayerName: first letter upper case, the rest lower case.</summary>
    private static string Capitalize(string name)
        => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
}
