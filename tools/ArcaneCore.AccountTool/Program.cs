using System.Numerics;
using ArcaneCore.AccountTool;
using ArcaneCore.Cryptography;
using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using ArcaneCore.Kernel.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// "arcane-account db <command>" is arcane-db (the database upgrade tool) on this tool's configuration. It runs before
// the auth schema is initialized below: status, plan and check must not upgrade the database they report on.
if (string.Equals(args[0], "db", StringComparison.OrdinalIgnoreCase))
{
    DatabaseOptions database = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
    return await DbUpgradeCli.RunAsync(args[1..], database, Console.Out, Console.Error, CancellationToken.None).ConfigureAwait(false);
}

builder.Services.AddAuthDatabase(builder.Configuration);
using IHost host = builder.Build();

int startup = await DatabaseStartup.InitializeAsync(
    () => host.Services.GetRequiredService<AuthDbInitializer>().InitializeAsync(), host.Services, Console.Error).ConfigureAwait(false);
if (startup != 0)
{
    return startup;
}

using IServiceScope scope = host.Services.CreateScope();
IAccountStore accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

const string ConsoleAuthor = "CONSOLE";
const string RecheckWarning =
    "note: a running world daemon disconnects the account at its next ban re-check (Bans:RecheckIntervalSeconds, " +
    "60 seconds by default); with the re-check off (0) the ban applies at the account's next login.";

string command = args[0].ToLowerInvariant();
switch (command)
{
    case "create":
        return await CreateAsync();
    case "set-password":
        return await SetPasswordAsync();
    case "set-gmlevel":
        return await SetGmLevelAsync();
    case "set-pin":
        return await SetFactorAsync(AccountLockFlags.FixedPin);
    case "set-totp":
        return await SetFactorAsync(AccountLockFlags.Totp);
    case "clear-pin":
    case "clear-totp":
        return await ClearFactorAsync(command == "clear-pin" ? AccountLockFlags.FixedPin : AccountLockFlags.Totp);
    case "set-ip-lock":
        return await SetIpLockAsync();
    case "list":
        return await ListAsync();
    case "ban":
        return await BanAsync();
    case "unban":
        return await UnbanAsync();
    case "baninfo":
        return await BanInfoAsync();
    case "banlist":
        return await BanListAsync();
    default:
        PrintUsage();
        return 1;
}

async Task<int> CreateAsync()
{
    if (!TryReadCredentials(out string username, out string password))
    {
        return 1;
    }

    if (await accounts.FindByUsernameAsync(username).ConfigureAwait(false) is not null)
    {
        Console.Error.WriteLine($"account '{username}' already exists");
        return 1;
    }

    (byte[] salt, byte[] verifier) = MakeCredentials(username, password);
    await accounts.CreateAsync(new Account
    {
        Username = username,
        Salt = salt,
        Verifier = verifier,
        Status = AccountStatus.Active,
    }).ConfigureAwait(false);

    Console.WriteLine($"created account '{username}'");
    return 0;
}

async Task<int> SetPasswordAsync()
{
    if (!TryReadCredentials(out string username, out string password))
    {
        return 1;
    }

    if (await accounts.FindByUsernameAsync(username).ConfigureAwait(false) is null)
    {
        Console.Error.WriteLine($"account '{username}' does not exist");
        return 1;
    }

    (byte[] salt, byte[] verifier) = MakeCredentials(username, password);
    await accounts.UpdateCredentialsAsync(username, salt, verifier).ConfigureAwait(false);

    Console.WriteLine($"updated password for '{username}'");
    return 0;
}

async Task<int> SetGmLevelAsync()
{
    if (args.Length != 3 || !TryParseSecurity(args[2], out AccountSecurity security))
    {
        Console.Error.WriteLine("usage: arcane-account set-gmlevel <username> <0-3|player|moderator|gamemaster|administrator>");
        return 1;
    }

    string username = args[1].ToUpperInvariant();
    if (!await accounts.UpdateSecurityAsync(username, security).ConfigureAwait(false))
    {
        Console.Error.WriteLine($"account '{username}' does not exist");
        return 1;
    }

    Console.WriteLine($"'{username}' is now {security} ({(byte)security}); it applies from the account's next world login");
    return 0;
}

async Task<int> SetFactorAsync(AccountLockFlags method)
{
    if (args.Length is < 2 or > 3 || args.Length == 3 && args[2] != "--secret-stdin")
    {
        Console.Error.WriteLine("usage: arcane-account set-pin|set-totp <username> [--secret-stdin]");
        return 1;
    }
    string? secret = ReadSecret(args.Length == 3 || Console.IsInputRedirected);
    if (secret is null || !AccountLoginSecurityPolicy.IsValid(method | AccountLockFlags.AlwaysEnforce, secret)
        || method == AccountLockFlags.Totp && !Totp.TryDecodeSecret(secret, out _))
    {
        Console.Error.WriteLine("invalid secret (PIN: 4-10 decimal digits; TOTP: Base32 without padding, 16-103 characters)");
        return 1;
    }
    IAccountLoginSecurityStore store = scope.ServiceProvider.GetRequiredService<IAccountLoginSecurityStore>();
    Account? account = await accounts.FindByUsernameAsync(args[1]).ConfigureAwait(false);
    if (account is null || !await store.SetAsync(args[1],
            (account.LockFlags & AccountLockFlags.IpLock) | method | AccountLockFlags.AlwaysEnforce,
            secret.ToUpperInvariant()).ConfigureAwait(false))
    {
        Console.Error.WriteLine("account does not exist");
        return 1;
    }
    Console.WriteLine($"{method} enabled for '{account.Username}'; prior session key revoked");
    return 0;
}

async Task<int> ClearFactorAsync(AccountLockFlags method)
{
    if (args.Length != 2)
    {
        Console.Error.WriteLine("usage: arcane-account clear-pin|clear-totp <username>");
        return 1;
    }
    Account? account = await accounts.FindByUsernameAsync(args[1]).ConfigureAwait(false);
    if (account is null)
    {
        Console.Error.WriteLine("account does not exist");
        return 1;
    }
    if (!account.LockFlags.HasFlag(method))
    {
        Console.Error.WriteLine("that factor is not enabled");
        return 1;
    }
    // Remove IP_LOCK too: keeping it without a factor can lock out a moved account.
    await scope.ServiceProvider.GetRequiredService<IAccountLoginSecurityStore>()
        .SetAsync(args[1], AccountLockFlags.None, string.Empty).ConfigureAwait(false);
    Console.WriteLine($"login factor cleared for '{account.Username}'; prior session key revoked");
    return 0;
}

async Task<int> SetIpLockAsync()
{
    if (args.Length != 3 || args[2] is not ("on" or "off"))
    {
        Console.Error.WriteLine("usage: arcane-account set-ip-lock <username> on|off");
        return 1;
    }
    Account? account = await accounts.FindByUsernameAsync(args[1]).ConfigureAwait(false);
    if (account is null)
    {
        Console.Error.WriteLine("account does not exist");
        return 1;
    }
    if (args[2] == "on" && account.LastIp.Length == 0
        && (account.LockFlags & (AccountLockFlags.FixedPin | AccountLockFlags.Totp)) == 0)
    {
        Console.Error.WriteLine("the account must first log in from its trusted address or have a PIN/TOTP factor");
        return 1;
    }
    AccountLockFlags flags = args[2] == "on"
        ? account.LockFlags | AccountLockFlags.IpLock
        : account.LockFlags & ~AccountLockFlags.IpLock;
    await scope.ServiceProvider.GetRequiredService<IAccountLoginSecurityStore>()
        .SetAsync(args[1], flags, account.SecurityInfo).ConfigureAwait(false);
    Console.WriteLine($"IP lock {args[2]} for '{account.Username}'");
    return 0;
}

static string? ReadSecret(bool fromStdin)
{
    if (fromStdin) return Console.ReadLine();
    Console.Error.Write("Secret: ");
    var value = new System.Text.StringBuilder();
    while (true)
    {
        ConsoleKeyInfo key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (value.Length > 0) value.Length--;
        }
        else if (!char.IsControl(key.KeyChar) && value.Length < 103) value.Append(key.KeyChar);
    }
    Console.Error.WriteLine();
    return value.ToString();
}

async Task<int> ListAsync()
{
    List<Account> all = await db.Accounts.AsNoTracking().OrderBy(a => a.Id).ToListAsync().ConfigureAwait(false);
    if (all.Count == 0)
    {
        Console.WriteLine("(no accounts)");
        return 0;
    }

    foreach (Account account in all)
    {
        Console.WriteLine($"{account.Id,6}  {account.Username,-16}  {account.Status,-9}  {account.Security}");
    }

    return 0;
}

bool TryReadCredentials(out string username, out string password)
{
    username = string.Empty;
    password = string.Empty;
    PasswordRequest request = PasswordSource.Parse(args, Environment.GetEnvironmentVariable, Console.IsInputRedirected);
    if (!request.IsValid)
    {
        Console.Error.WriteLine(request.Error);
        return false;
    }

    if (request.Mode == PasswordMode.Argv)
    {
        Console.Error.WriteLine(PasswordSource.ArgvWarning);
    }

    string? read = PasswordSource.Read(request, Environment.GetEnvironmentVariable, Console.In, PasswordSource.PromptNoEcho);
    if (read is null)
    {
        Console.Error.WriteLine("no password supplied (empty, or the two prompts did not match)");
        return false;
    }

    username = request.Username!.ToUpperInvariant();
    password = read;
    return true;
}

// --- bans (the same rows as .ban in game: account_banned, written through IBanStore) ---------------

async Task<int> BanAsync()
{
    if (args.Length != 4)
    {
        Console.Error.WriteLine("usage: arcane-account ban <username> <duration|0> <reason>   (duration like 1d2h3m4s; 0 or an unknown format is permanent, as in retail)");
        return 1;
    }

    Account? account = await accounts.FindByUsernameAsync(args[1].ToUpperInvariant()).ConfigureAwait(false);
    if (account is null)
    {
        Console.Error.WriteLine($"account '{args[1].ToUpperInvariant()}' does not exist");
        return 1;
    }

    if (!BanTime.TryTimeStringToSecs(args[2], out uint seconds))
    {
        Console.Error.WriteLine($"duration '{args[2]}' is too long (more than {uint.MaxValue} seconds); refusing instead of wrapping");
        return 1;
    }

    int realm = int.TryParse(builder.Configuration["Bans:RealmId"], out int configured) ? configured : 1;
    IBanStore bans = scope.ServiceProvider.GetRequiredService<IBanStore>();
    await bans.BanAccountAsync(new BanRequest(account.Id, seconds, args[3], ConsoleAuthor, null, realm)).ConfigureAwait(false);

    Console.WriteLine(seconds > 0
        ? $"'{account.Username}' is banned for {BanTime.SecsToTimeString(seconds)}. Reason: {args[3]}."
        : $"'{account.Username}' is banned permanently for {args[3]}.");
    Console.WriteLine(RecheckWarning);
    return 0;
}

async Task<int> UnbanAsync()
{
    if (args.Length != 3)
    {
        Console.Error.WriteLine("usage: arcane-account unban <username> <message>");
        return 1;
    }

    Account? account = await accounts.FindByUsernameAsync(args[1].ToUpperInvariant()).ConfigureAwait(false);
    if (account is null)
    {
        Console.Error.WriteLine($"account '{args[1].ToUpperInvariant()}' does not exist");
        return 1;
    }

    bool lifted = await scope.ServiceProvider.GetRequiredService<IBanStore>()
        .UnbanAccountAsync(account.Id, ConsoleAuthor, args[2]).ConfigureAwait(false);
    Console.WriteLine(lifted ? $"'{account.Username}' unbanned." : $"'{account.Username}' had no ban in force (an audit row was written).");
    return 0;
}

async Task<int> BanInfoAsync()
{
    if (args.Length != 2)
    {
        Console.Error.WriteLine("usage: arcane-account baninfo <username>");
        return 1;
    }

    Account? account = await accounts.FindByUsernameAsync(args[1].ToUpperInvariant()).ConfigureAwait(false);
    if (account is null)
    {
        Console.Error.WriteLine($"account '{args[1].ToUpperInvariant()}' does not exist");
        return 1;
    }

    IReadOnlyList<AccountBanRecord> history = await scope.ServiceProvider.GetRequiredService<IBanStore>()
        .GetHistoryAsync(account.Id).ConfigureAwait(false);
    if (history.Count == 0)
    {
        Console.WriteLine($"Account {account.Username} has never been banned");
        return 0;
    }

    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    Console.WriteLine($"Ban history for account {account.Username}:");
    foreach (AccountBanRecord row in history)
    {
        bool active = AccountBanEvaluator.IsActive(row, now);
        string length = row.IsPermanent ? "Inf." : BanTime.SecsToTimeString((ulong)(row.UnbanDate - row.BanDate));
        Console.WriteLine($"Ban Date: {DateTimeOffset.FromUnixTimeSeconds(row.BanDate).ToLocalTime():yyyy-MM-dd HH:mm:ss} Bantime: {length} Still active: {(active ? "Yes" : "No")}  Reason: {row.Reason} Set by: {row.BannedBy} (realm {row.Realm})");
    }

    return 0;
}

async Task<int> BanListAsync()
{
    IBanStore bans = scope.ServiceProvider.GetRequiredService<IBanStore>();
    await bans.PurgeExpiredAsync().ConfigureAwait(false);
    IReadOnlyList<AccountBanRecord> rows = await bans.ListActiveAccountBansAsync().ConfigureAwait(false);
    IReadOnlyList<IpBanRecord> ips = await bans.ListIpBansAsync(string.Empty).ConfigureAwait(false);
    if (rows.Count == 0 && ips.Count == 0)
    {
        Console.WriteLine("(no bans in force)");
        return 0;
    }

    List<int> bannedIds = [.. rows.Select(r => r.AccountId).Distinct()];
    Dictionary<int, string> names = await db.Accounts.AsNoTracking()
        .Where(a => bannedIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Username).ConfigureAwait(false);
    foreach (AccountBanRecord row in rows)
    {
        Console.WriteLine($"account {names.GetValueOrDefault(row.AccountId, "?"),-16} {(row.IsPermanent ? "permanent" : "until " + DateTimeOffset.FromUnixTimeSeconds(row.UnbanDate).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))}  {row.BannedBy}: {row.Reason}");
    }

    foreach (IpBanRecord ip in ips)
    {
        Console.WriteLine($"ip      {ip.Ip,-16} {(ip.IsPermanent ? "permanent" : "until " + DateTimeOffset.FromUnixTimeSeconds(ip.UnbanDate).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))}  {ip.BannedBy}: {ip.Reason}");
    }

    return 0;
}

static (byte[] Salt, byte[] Verifier) MakeCredentials(string username, string password)
{
    byte[] salt = WowSrp6.GenerateSalt();
    BigInteger verifier = WowSrp6.ComputeVerifier(salt, username, password);
    return (salt, WowSrp6.ToFixedLittleEndian(verifier, WowSrp6.KeyLength));
}

static bool TryParseSecurity(string text, out AccountSecurity security)
{
    if (byte.TryParse(text, out byte level) && Enum.IsDefined((AccountSecurity)level))
    {
        security = (AccountSecurity)level;
        return true;
    }

    return Enum.TryParse(text, ignoreCase: true, out security) && Enum.IsDefined(security);
}

static void PrintUsage()
{
    Console.Error.WriteLine("ArcaneCore account tool");
    Console.Error.WriteLine("usage:");
    Console.Error.WriteLine("  arcane-account create <username> [--password-stdin]");
    Console.Error.WriteLine("  arcane-account set-password <username> [--password-stdin]");
    Console.Error.WriteLine("    the password comes from a no-echo prompt, from stdin (--password-stdin or piped),");
    Console.Error.WriteLine("    or from the ARCANE_ACCOUNT_PASSWORD environment variable; '<username> <password>' still");
    Console.Error.WriteLine("    works but exposes the password in process listings and shell history (warned on stderr)");
    Console.Error.WriteLine("  arcane-account set-gmlevel <username> <0-3|player|moderator|gamemaster|administrator>");
    Console.Error.WriteLine("  arcane-account set-pin|set-totp <username> [--secret-stdin]   (no-echo prompt or stdin)");
    Console.Error.WriteLine("  arcane-account clear-pin|clear-totp <username>");
    Console.Error.WriteLine("  arcane-account set-ip-lock <username> on|off");
    Console.Error.WriteLine("  arcane-account list");
    Console.Error.WriteLine("  arcane-account ban <username> <duration|0> <reason>");
    Console.Error.WriteLine("  arcane-account unban <username> <message>");
    Console.Error.WriteLine("  arcane-account baninfo <username>");
    Console.Error.WriteLine("  arcane-account banlist");
    Console.Error.WriteLine("  arcane-account db <status|plan|check|upgrade|backup-info> [options]   (database upgrade tool, see arcane-db --help)");
}
