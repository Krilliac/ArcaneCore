using System.Numerics;
using ArcaneCore.Cryptography;
using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
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
builder.Services.AddAuthDatabase(builder.Configuration);
using IHost host = builder.Build();

await host.Services.GetRequiredService<AuthDbInitializer>().InitializeAsync().ConfigureAwait(false);

using IServiceScope scope = host.Services.CreateScope();
IAccountStore accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

string command = args[0].ToLowerInvariant();
switch (command)
{
    case "create":
        return await CreateAsync();
    case "set-password":
        return await SetPasswordAsync();
    case "set-gmlevel":
        return await SetGmLevelAsync();
    case "list":
        return await ListAsync();
    default:
        PrintUsage();
        return 1;
}

async Task<int> CreateAsync()
{
    if (!TryReadPassword(out string password))
    {
        Console.Error.WriteLine("usage: arcane-account create <username> [<password> | --password-stdin]   (no password argument: ARCANE_ACCOUNT_PASSWORD)");
        return 1;
    }

    string username = args[1].ToUpperInvariant();

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
    if (!TryReadPassword(out string password))
    {
        Console.Error.WriteLine("usage: arcane-account set-password <username> [<password> | --password-stdin]   (no password argument: ARCANE_ACCOUNT_PASSWORD)");
        return 1;
    }

    string username = args[1].ToUpperInvariant();

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

// The password never has to be on a command line (where other processes can read it): pass
// --password-stdin and write it on standard input, or pass neither and set ARCANE_ACCOUNT_PASSWORD.
bool TryReadPassword(out string password)
{
    password = string.Empty;
    if (args.Length < 2 || args.Length > 3)
    {
        return false;
    }

    string? value = args.Length == 3
        ? args[2] == "--password-stdin" ? Console.In.ReadLine() : args[2]
        : Environment.GetEnvironmentVariable("ARCANE_ACCOUNT_PASSWORD");
    if (string.IsNullOrEmpty(value))
    {
        return false;
    }

    password = value;
    return true;
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
    Console.Error.WriteLine("  arcane-account create <username> [<password> | --password-stdin]");
    Console.Error.WriteLine("  arcane-account set-password <username> [<password> | --password-stdin]");
    Console.Error.WriteLine("  (without a password argument the password is read from the ARCANE_ACCOUNT_PASSWORD environment variable;");
    Console.Error.WriteLine("   --password-stdin reads it from the first line of standard input. Neither puts it on a command line.)");
    Console.Error.WriteLine("  arcane-account set-gmlevel <username> <0-3|player|moderator|gamemaster|administrator>");
    Console.Error.WriteLine("  arcane-account list");
}
