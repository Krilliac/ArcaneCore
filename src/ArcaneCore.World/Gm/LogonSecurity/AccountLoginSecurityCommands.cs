using System.Security.Cryptography;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.LogonSecurity;

/// <summary>Administrator-only factor management. No secret is accepted in command text:
/// CommandTable logs that text before dispatch (GmCommandLog), so the server generates the
/// factor and replies privately to the invoker.</summary>
public sealed class AccountLoginSecurityCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("account", AccountSecurity.Administrator, "Account login security.", Children:
        [
            new ChatCommand("pin", AccountSecurity.Administrator, "Syntax: .account pin set|clear $account", Children:
            [
                new ChatCommand("set", AccountSecurity.Administrator, "Syntax: .account pin set $account", SetPin),
                new ChatCommand("clear", AccountSecurity.Administrator, "Syntax: .account pin clear $account", ClearPin),
            ]),
            new ChatCommand("totp", AccountSecurity.Administrator, "Syntax: .account totp set|clear $account", Children:
            [
                new ChatCommand("set", AccountSecurity.Administrator, "Syntax: .account totp set $account", SetTotp),
                new ChatCommand("clear", AccountSecurity.Administrator, "Syntax: .account totp clear $account", ClearTotp),
            ]),
            new ChatCommand("iplock", AccountSecurity.Administrator, "Syntax: .account iplock $account on|off", IpLock),
        ]),
    ];

    private static bool SetPin(CommandContext context, string text) => Set(context, text, AccountLockFlags.FixedPin);
    private static bool SetTotp(CommandContext context, string text) => Set(context, text, AccountLockFlags.Totp);
    private static bool ClearPin(CommandContext context, string text) => Clear(context, text, AccountLockFlags.FixedPin);
    private static bool ClearTotp(CommandContext context, string text) => Clear(context, text, AccountLockFlags.Totp);

    private static bool Set(CommandContext context, string text, AccountLockFlags method)
    {
        if (!TryAccount(text, out string name)) return false;
        Run(context, async services =>
        {
            Account? account = await services.GetRequiredService<IAccountStore>().FindByUsernameAsync(name).ConfigureAwait(false);
            if (account is null) { context.Reply("Account does not exist."); return; }
            string secret = method == AccountLockFlags.FixedPin
                ? RandomNumberGenerator.GetInt32(100000, 1000000).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : Totp.NewSecret();
            await services.GetRequiredService<IAccountLoginSecurityStore>().SetAsync(name,
                (account.LockFlags & AccountLockFlags.IpLock) | method | AccountLockFlags.AlwaysEnforce,
                secret).ConfigureAwait(false);
            context.Reply($"{method} for {name}: {secret}. Save it now; it will not be shown again.");
        });
        return true;
    }

    private static bool Clear(CommandContext context, string text, AccountLockFlags method)
    {
        if (!TryAccount(text, out string name)) return false;
        Run(context, async services =>
        {
            Account? account = await services.GetRequiredService<IAccountStore>().FindByUsernameAsync(name).ConfigureAwait(false);
            if (account is null) { context.Reply("Account does not exist."); return; }
            if (!account.LockFlags.HasFlag(method)) { context.Reply("That factor is not enabled."); return; }
            await services.GetRequiredService<IAccountLoginSecurityStore>().SetAsync(name,
                AccountLockFlags.None, string.Empty).ConfigureAwait(false);
            context.Reply($"Login factor cleared for {name}; prior session key revoked.");
        });
        return true;
    }

    private static bool IpLock(CommandContext context, string text)
    {
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !TryAccount(parts[0], out string name) || parts[1] is not ("on" or "off")) return false;
        Run(context, async services =>
        {
            Account? account = await services.GetRequiredService<IAccountStore>().FindByUsernameAsync(name).ConfigureAwait(false);
            if (account is null) { context.Reply("Account does not exist."); return; }
            if (parts[1] == "on" && account.LastIp.Length == 0
                && (account.LockFlags & (AccountLockFlags.FixedPin | AccountLockFlags.Totp)) == 0)
            {
                context.Reply("The account must first log in from its trusted address or have a PIN/TOTP factor.");
                return;
            }
            AccountLockFlags flags = parts[1] == "on"
                ? account.LockFlags | AccountLockFlags.IpLock
                : account.LockFlags & ~AccountLockFlags.IpLock;
            await services.GetRequiredService<IAccountLoginSecurityStore>().SetAsync(name, flags, account.SecurityInfo)
                .ConfigureAwait(false);
            context.Reply($"IP lock {parts[1]} for {name}.");
        });
        return true;
    }

    private static bool TryAccount(string text, out string name)
    {
        name = text.Trim().ToUpperInvariant();
        return name.Length is >= 1 and <= 16 && name.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9');
    }

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
                logger.LogError(ex, "Account login security command failed");
                context.Reply("Account security update failed; see the server log.");
            }
        });
    }
}
