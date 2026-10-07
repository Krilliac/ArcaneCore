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
/// Security follows vmangos Chat.cpp:170-191, 1022-1024, 1263-1266 (SEC_TICKETMASTER 2, SEC_GAMEMASTER 3,
/// SEC_ADMINISTRATOR 6) mapped onto ArcaneCore's four levels: Moderator for the ban/baninfo/banlist parents and
/// baninfo/banlist account/character, GameMaster for ban account/character and baninfo/banlist ip, Administrator for
/// ban ip, ban allip and every unban. <c>.ban ip</c> kicks the live sessions from that address and always reports success
/// (retail kicks accounts whose last_ip matches and prints "ip X not found" when none do); <c>.ban allip</c> reads the
/// world's own last-address record (<see cref="IAccountAddressStore"/>).
/// </para>
/// </summary>
public sealed class BanCommands : ICommandGroup
{
    private const string DatabaseError = "The ban database is unavailable; see the server log.";
    private const int MaxAccountNameLength = 16;
    private const int HistoryBatch = 200; // accounts per history query in .banlist character

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("ban", AccountSecurity.Moderator, "Ban an account, character or IP address.", Children:
        [
            new ChatCommand("account", AccountSecurity.GameMaster, "Syntax: .ban account $Name $bantime $reason — $bantime is like 1d2h3m4s, 0 or an unknown format is permanent; the reason is one word or quoted.", BanAccount),
            new ChatCommand("character", AccountSecurity.GameMaster, "Syntax: .ban character $Name $bantime $reason — bans the character's account.", BanCharacter),
            new ChatCommand("ip", AccountSecurity.Administrator, "Syntax: .ban ip $Ip $bantime $reason", BanIp),
            new ChatCommand("allip", AccountSecurity.Administrator, "Syntax: .ban allip $IpPrefix [$reason] — permanently bans every account last seen on an address starting with $IpPrefix that has no character above level 10.", BanAllIp),
        ]),
        new ChatCommand("unban", AccountSecurity.Administrator, "Lift a ban.", Children:
        [
            new ChatCommand("account", AccountSecurity.Administrator, "Syntax: .unban account $Name $message", UnbanAccount),
            new ChatCommand("character", AccountSecurity.Administrator, "Syntax: .unban character $Name $message", UnbanCharacter),
            new ChatCommand("ip", AccountSecurity.Administrator, "Syntax: .unban ip $Ip $message", UnbanIp),
        ]),
        new ChatCommand("baninfo", AccountSecurity.Moderator, "Show ban history.", Children:
        [
            new ChatCommand("account", AccountSecurity.Moderator, "Syntax: .baninfo account $accountid|$name", BanInfoAccount),
            new ChatCommand("character", AccountSecurity.Moderator, "Syntax: .baninfo character $Name", BanInfoCharacter),
            new ChatCommand("ip", AccountSecurity.GameMaster, "Syntax: .baninfo ip $Ip", BanInfoIp),
        ]),
        new ChatCommand("banlist", AccountSecurity.Moderator, "List bans.", Children:
        [
            new ChatCommand("account", AccountSecurity.Moderator, "Syntax: .banlist account [$Name] — accounts with a ban whose name starts with $Name.", BanListAccount),
            new ChatCommand("character", AccountSecurity.Moderator, "Syntax: .banlist character $Name — accounts with a ban in force owning a character whose name starts with $Name.", BanListCharacter),
            new ChatCommand("ip", AccountSecurity.GameMaster, "Syntax: .banlist ip [$Ip] — banned addresses starting with $Ip.", BanListIp),
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

        if (!BanCommandText.TryTimeStringToSecs(duration, out uint seconds))
        {
            return false; // overflow: never wrap into a short or a permanent ban, whatever RejectUnparseableDuration says
        }

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
                if (!await bans.BanIpAsync(new IpBanRequest(display, seconds, reason, author, authorAccount)).ConfigureAwait(false))
                {
                    context.Reply(string.Format(BanCommandText.IpAlreadyBanned, display)); // the first row stays; nothing was written
                    return;
                }

                ReplyBanned(context, display, seconds, reason);
                return;
            }

            (int AccountId, AccountSecurity Security)? owner = await ResolveAccountAsync(services, context, kind, display).ConfigureAwait(false);
            if (owner is null)
            {
                context.Reply(string.Format(BanCommandText.BanNotFound, kind == Kind.Account ? "account" : "character", display));
                return;
            }

            if (options.ProtectHigherSecurity && owner.Value.AccountId != authorAccount && owner.Value.Security >= context.Security)
            {
                context.Reply(BanCommandText.TargetSecurityTooHigh);
                return;
            }

            await bans.BanAccountAsync(new BanRequest(owner.Value.AccountId, seconds, reason, author, authorAccount, options.RealmId)).ConfigureAwait(false);
            ReplyBanned(context, display, seconds, reason);
        });
        return true;
    }

    // --- .ban allip -------------------------------------------------------------------

    /// <summary>vmangos HandleBanAllIPCommand's level limit: an account with a character above it is spared.</summary>
    public const int AllIpMaxLevel = 10;

    /// <summary>vmangos HandleBanAllIPCommand's reason when none is given.</summary>
    public const string AllIpNoReason = "<no reason given>";

    /// <summary>
    /// <c>.ban allip $IpPrefix [$reason]</c> (vmangos HandleBanAllIPCommand, AccountCommands.cpp:531-585): every account
    /// whose last address starts with the prefix (<see cref="IAccountAddressStore"/>, vmangos <c>last_ip LIKE 'prefix%'</c>)
    /// and that has no character above level <see cref="AllIpMaxLevel"/> is banned permanently with the reason, unless it is
    /// already banned; one line per banned account, then the total. The level is the higher of the stored one and, for a
    /// character online now, the live one. Deviations: the invoker's own account is never included (vmangos would ban it
    /// when its characters are low level), and with <c>Bans:ProtectHigherSecurity</c> (default) accounts of equal or higher
    /// security are spared, where vmangos only hides ids below 100 from a non-administrator. The prefix must look like the
    /// start of an address (digits, hex letters, '.' and ':').
    /// </summary>
    private static bool BanAllIp(CommandContext context, string args)
    {
        string rest = args;
        if (BanCommandText.ExtractArg(ref rest) is not { Length: > 0 } prefix || !IsAddressPrefix(prefix))
        {
            return false;
        }

        string reason = BanCommandText.ExtractArg(ref rest) is { Length: > 0 } given ? given : AllIpNoReason;
        BanOptions options = OptionsOf(context);
        AccountSecurity invokerSecurity = context.Security;
        int invokerAccount = context.Session.AccountId;
        string author = context.Player.Name;

        // Live levels are read here, on the world thread; the store work runs off it.
        var onlineLevels = new Dictionary<int, int>();
        foreach (Game.Entities.Player online in context.World.OnlinePlayers)
        {
            onlineLevels[online.AccountId] = Math.Max(onlineLevels.GetValueOrDefault(online.AccountId), online.Level);
        }

        Run(context, async services =>
        {
            IReadOnlyList<AccountAddressRecord> found = services.GetService<IAccountAddressStore>() is { } addresses
                ? await addresses.FindByPrefixAsync(prefix).ConfigureAwait(false)
                : [];
            int[] onIp = [.. found.Select(f => f.AccountId).Distinct()];
            if (onIp.Length == 0)
            {
                context.Reply(string.Format(BanCommandText.AllIpNotFound, prefix));
                return;
            }

            IBanStore bans = services.GetRequiredService<IBanStore>();
            IAccountStore accounts = services.GetRequiredService<IAccountStore>();
            ICharacterStore characters = services.GetRequiredService<ICharacterStore>();
            IReadOnlyDictionary<int, string> names = await services.GetRequiredService<IAccountAdmin>().GetUsernamesAsync(onIp).ConfigureAwait(false);
            IReadOnlySet<int> alreadyBanned = await bans.FindBannedAccountsAsync(onIp).ConfigureAwait(false);
            int max = options.MaxListedEntries;
            var lines = new List<string>();
            int banned = 0;
            foreach (int id in onIp)
            {
                if (id == invokerAccount || !names.TryGetValue(id, out string? name) || alreadyBanned.Contains(id))
                {
                    continue;
                }

                int level = onlineLevels.GetValueOrDefault(id);
                foreach (CharacterRecord character in await characters.GetByAccountAsync(id).ConfigureAwait(false))
                {
                    level = Math.Max(level, character.Level);
                }

                if (level > AllIpMaxLevel)
                {
                    continue;
                }

                if (options.ProtectHigherSecurity
                    && await accounts.FindByUsernameAsync(name).ConfigureAwait(false) is { } account && account.Security >= invokerSecurity)
                {
                    continue;
                }

                await bans.BanAccountAsync(new BanRequest(id, 0, reason, author, invokerAccount, options.RealmId)).ConfigureAwait(false);
                banned++;
                if (max <= 0 || lines.Count < max)
                {
                    lines.Add(string.Format(BanCommandText.AllIpBanned, name, reason));
                }
            }

            if (max > 0 && banned > max)
            {
                lines.Add(string.Format(BanCommandText.ListTruncated, max));
            }

            lines.Add(string.Format(BanCommandText.AllIpSummary, banned, reason, onIp.Length));
            context.Reply(string.Join('\n', lines));
        });
        return true;
    }

    private static bool IsAddressPrefix(string text)
        => text.Length <= 45 && text.All(c => char.IsAsciiHexDigit(c) || c is '.' or ':');

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

    /// <summary>The first <see cref="BanOptions.MaxListedEntries"/> items (all when 0); <paramref name="truncated"/> says some were left out.</summary>
    private static List<T> Capped<T>(IEnumerable<T> items, int max, out bool truncated)
    {
        var kept = new List<T>();
        truncated = false;
        foreach (T item in items)
        {
            if (max > 0 && kept.Count >= max)
            {
                truncated = true;
                break;
            }

            kept.Add(item);
        }

        return kept;
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

        int max = OptionsOf(context).MaxListedEntries;
        List<AccountBanRecord> shown = Capped(history, max, out bool historyTruncated);
        context.Reply(string.Format(BanCommandText.BanInfoHistory, accountName));
        foreach (AccountBanRecord row in shown)
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

        if (historyTruncated)
        {
            context.Reply(string.Format(BanCommandText.ListTruncated, max));
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
            int max = OptionsOf(context).MaxListedEntries;
            List<string> matching = Capped(ids.Where(names.ContainsKey).Select(i => names[i])
                .Where(n => n.StartsWith(filter, StringComparison.OrdinalIgnoreCase)), max, out bool truncated);
            if (matching.Count == 0)
            {
                context.Reply(BanCommandText.BanListNoAccount);
                return;
            }

            context.Reply(BanCommandText.BanListMatchingAccount);
            if (truncated)
            {
                matching.Add(string.Format(BanCommandText.ListTruncated, max));
            }

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

            // The directory is loaded from the character store at startup and kept current by creation and deletion
            // (BanInfoCharacter and ResolveAccountAsync rely on it too), so no all-characters query is needed.
            int[] accountIds = services.GetRequiredService<CharacterDirectory>().AccountIdsByNamePrefix(filter);
            if (accountIds.Length == 0)
            {
                context.Reply(BanCommandText.BanListNoCharacter);
                return;
            }

            // HandleBanListHelper: the header, then the name of every such account that has a ban in force (by default,
            // as .banlist account) or, with Bans:BanListCharacterIncludesHistory, any ban row at all (vmangos). Accounts
            // are checked HistoryBatch at a time (one query per batch) and the walk ends as soon as one more than
            // Bans:MaxListedEntries names are known, so the work is bounded, not just the printed lines.
            context.Reply(BanCommandText.BanListMatchingAccount);
            IAccountAdmin admin = services.GetRequiredService<IAccountAdmin>();
            BanOptions options = OptionsOf(context);
            int max = options.MaxListedEntries;
            var lines = new List<string>();
            bool truncated = false;
            foreach (int[] batch in accountIds.Chunk(HistoryBatch))
            {
                IReadOnlySet<int> withHistory = options.BanListCharacterIncludesHistory
                    ? await bans.FindAccountsWithHistoryAsync(batch).ConfigureAwait(false)
                    : await bans.FindBannedAccountsAsync(batch).ConfigureAwait(false);
                int[] hits = [.. batch.Where(withHistory.Contains)];
                if (hits.Length == 0)
                {
                    continue;
                }

                IReadOnlyDictionary<int, string> names = await admin.GetUsernamesAsync(hits).ConfigureAwait(false);
                foreach (int id in hits)
                {
                    if (!names.TryGetValue(id, out string? name))
                    {
                        continue;
                    }

                    if (max > 0 && lines.Count >= max)
                    {
                        truncated = true;
                        break;
                    }

                    lines.Add(name);
                }

                if (truncated)
                {
                    break;
                }
            }

            if (truncated)
            {
                lines.Add(string.Format(BanCommandText.ListTruncated, max));
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

            int max = OptionsOf(context).MaxListedEntries;
            List<string> ips = Capped(rows.Select(r => r.Ip), max, out bool truncated);
            context.Reply(BanCommandText.BanListMatchingIp);
            if (truncated)
            {
                ips.Add(string.Format(BanCommandText.ListTruncated, max));
            }

            context.Reply(string.Join('\n', ips));
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
