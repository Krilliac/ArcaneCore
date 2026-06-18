using System.Numerics;
using ArcaneCore.Cryptography;
using ArcaneCore.Data;
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
builder.Services.AddArcaneCoreData(builder.Configuration);
builder.Services.AddSingleton<ArcaneCoreDbInitializer>();
using IHost host = builder.Build();

await host.Services.GetRequiredService<ArcaneCoreDbInitializer>().InitializeAsync().ConfigureAwait(false);

using IServiceScope scope = host.Services.CreateScope();
IAccountStore accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
ArcaneCoreDbContext db = scope.ServiceProvider.GetRequiredService<ArcaneCoreDbContext>();

string command = args[0].ToLowerInvariant();
switch (command)
{
    case "create":
        return await CreateAsync();
    case "set-password":
        return await SetPasswordAsync();
    case "list":
        return await ListAsync();
    default:
        PrintUsage();
        return 1;
}

async Task<int> CreateAsync()
{
    if (args.Length != 3)
    {
        Console.Error.WriteLine("usage: arcane-account create <username> <password>");
        return 1;
    }

    string username = args[1].ToUpperInvariant();
    string password = args[2];

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
    if (args.Length != 3)
    {
        Console.Error.WriteLine("usage: arcane-account set-password <username> <password>");
        return 1;
    }

    string username = args[1].ToUpperInvariant();
    string password = args[2];

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
        Console.WriteLine($"{account.Id,6}  {account.Username,-16}  {account.Status}");
    }

    return 0;
}

static (byte[] Salt, byte[] Verifier) MakeCredentials(string username, string password)
{
    byte[] salt = WowSrp6.GenerateSalt();
    BigInteger verifier = WowSrp6.ComputeVerifier(salt, username, password);
    return (salt, WowSrp6.ToFixedLittleEndian(verifier, WowSrp6.KeyLength));
}

static void PrintUsage()
{
    Console.Error.WriteLine("ArcaneCore account tool");
    Console.Error.WriteLine("usage:");
    Console.Error.WriteLine("  arcane-account create <username> <password>");
    Console.Error.WriteLine("  arcane-account set-password <username> <password>");
    Console.Error.WriteLine("  arcane-account list");
}
