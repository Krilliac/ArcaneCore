using System.Globalization;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Realm.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ArcaneCore.Realm.Ops.ServerConsole;

/// <summary>
/// The realm console's commands (docs/areas/server-console.md): <c>help</c>, <c>info</c>, <c>realmlist</c>, <c>online</c>,
/// <c>account create|set-password|set-gmlevel</c> (the arcane-account tool's logic: <see cref="WowSrp6.CreateCredentials"/>,
/// <see cref="AccountSecurityText.TryParse"/>, the same <see cref="IAccountStore"/> calls and messages) and
/// <c>quit</c>/<c>shutdown</c>/<c>exit</c> (graceful host stop).
/// <para>
/// Threading: the realm has no world thread. <see cref="ExecuteAsync"/> runs on the console reader thread; each command opens
/// its own DI scope for the stores and awaits them there. Output lines are written through <c>output</c> with the
/// <see cref="Prefix"/>; passwords are never echoed.
/// </para>
/// </summary>
public sealed class RealmConsoleCommands(
    IServiceScopeFactory scopes, Func<int> activeSessions, TimeProvider time, IHostApplicationLifetime lifetime, RealmLivenessOptions? liveness = null)
{
    /// <summary>The prefix of every output line.</summary>
    public const string Prefix = "[console] ";

    private readonly IServiceScopeFactory _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
    private readonly Func<int> _activeSessions = activeSessions ?? throw new ArgumentNullException(nameof(activeSessions));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly RealmLivenessOptions _liveness = liveness ?? new RealmLivenessOptions();
    private readonly IHostApplicationLifetime _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
    private readonly DateTimeOffset _started = (time ?? throw new ArgumentNullException(nameof(time))).GetUtcNow();

    /// <summary>Run one console line. Never throws for a bad command (a usage line is printed); store failures propagate.</summary>
    public async Task ExecuteAsync(string line, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);
        string text = line.Trim();
        if (text.Length > 0 && text[0] is '.' or '!')
        {
            text = text[1..].TrimStart();
        }

        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return;
        }

        void Say(string message) => output.WriteLine(Prefix + message);

        switch (words[0].ToLowerInvariant())
        {
            case "help":
                PrintHelp(Say);
                return;
            case "info":
                await PrintInfoAsync(Say, cancellationToken).ConfigureAwait(false);
                return;
            case "realmlist":
                await PrintRealmListAsync(Say, cancellationToken).ConfigureAwait(false);
                return;
            case "online":
                Say($"{_activeSessions()} logon session(s) connected");
                return;
            case "account":
                await AccountAsync(words, Say, cancellationToken).ConfigureAwait(false);
                return;
            case "quit" or "exit" or "shutdown":
                Say("stopping the realm daemon");
                _lifetime.StopApplication();
                return;
            default:
                Say("unknown command '" + words[0] + "'");
                PrintHelp(Say);
                return;
        }
    }

    private static void PrintHelp(Action<string> say)
    {
        say("commands:");
        say("  help                                        this list");
        say("  info                                        uptime, logon sessions, realm count");
        say("  realmlist                                   the realms as served to clients");
        say("  online                                      connected logon sessions");
        say("  account create <name> <password>            create an account (the password is visible while typed; arcane-account prompts without echo)");
        say("  account set-password <name> <password>      change an account's password");
        say("  account set-gmlevel <name> <0-3|name>       set an account's GM level (applies from the next world login)");
        say("  quit | shutdown                             stop the realm daemon");
    }

    private async Task PrintInfoAsync(Action<string> say, CancellationToken cancellationToken)
    {
        TimeSpan uptime = _time.GetUtcNow() - _started;
        if (uptime < TimeSpan.Zero)
        {
            uptime = TimeSpan.Zero; // the wall clock stepped back (NTP, VM resume): a negative uptime would print "-01:-05:-22"
        }
        say("uptime: " + string.Create(CultureInfo.InvariantCulture, $"{(int)uptime.TotalDays}d {uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}"));
        say($"logon sessions: {_activeSessions()}");
        say($"realms: {(await LoadRealmsAsync(cancellationToken).ConfigureAwait(false)).Count}");
    }

    private async Task PrintRealmListAsync(Action<string> say, CancellationToken cancellationToken)
    {
        IReadOnlyList<RealmEntry> realms = await LoadRealmsAsync(cancellationToken).ConfigureAwait(false);
        say($"{realms.Count} realm(s)");
        DateTimeOffset now = _time.GetUtcNow();
        foreach (RealmEntry realm in realms)
        {
            // What a client below the realm's lock level is sent, now (RealmListProjection); flags is what the row stores.
            RealmView view = RealmListProjection.Evaluate(realm, AccountSecurity.Player, now, _liveness);
            string beat = view.HeartbeatAge is { } age ? string.Create(CultureInfo.InvariantCulture, $"{age.TotalSeconds:0}s ago") : "none";
            say(string.Create(CultureInfo.InvariantCulture,
                $"id {realm.Id}  {realm.Name}  {realm.Address}  type {realm.Type}  flags {realm.Flags}  population {realm.Population:0.##}  category {realm.Category}  state {realm.State}  heartbeat {beat}  wire {view.Flags}  lock {realm.AllowedSecurityLevel}"));
        }
    }

    private async Task<IReadOnlyList<RealmEntry>> LoadRealmsAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IRealmStore>().GetRealmsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AccountAsync(string[] words, Action<string> say, CancellationToken cancellationToken)
    {
        string verb = words.Length > 1 ? words[1].ToLowerInvariant() : string.Empty;
        switch (verb)
        {
            case "create" when words.Length == 4:
                await CreateAccountAsync(words[2].ToUpperInvariant(), words[3], say, cancellationToken).ConfigureAwait(false);
                return;
            case "set-password" when words.Length == 4:
                await SetPasswordAsync(words[2].ToUpperInvariant(), words[3], say, cancellationToken).ConfigureAwait(false);
                return;
            case "set-gmlevel" when words.Length == 4:
                await SetGmLevelAsync(words[2].ToUpperInvariant(), words[3], say, cancellationToken).ConfigureAwait(false);
                return;
            default:
                say("usage: account create <name> <password> | account set-password <name> <password> | account set-gmlevel <name> <0-3|player|moderator|gamemaster|administrator>");
                return;
        }
    }

    private async Task CreateAccountAsync(string username, string password, Action<string> say, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        IAccountStore accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        if (await accounts.FindByUsernameAsync(username, cancellationToken).ConfigureAwait(false) is not null)
        {
            say($"account '{username}' already exists");
            return;
        }

        (byte[] salt, byte[] verifier) = WowSrp6.CreateCredentials(username, password);
        await accounts.CreateAsync(
            new Account { Username = username, Salt = salt, Verifier = verifier, Status = AccountStatus.Active },
            cancellationToken).ConfigureAwait(false);
        say($"created account '{username}'");
    }

    private async Task SetPasswordAsync(string username, string password, Action<string> say, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        IAccountStore accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        if (await accounts.FindByUsernameAsync(username, cancellationToken).ConfigureAwait(false) is null)
        {
            say($"account '{username}' does not exist");
            return;
        }

        (byte[] salt, byte[] verifier) = WowSrp6.CreateCredentials(username, password);
        await accounts.UpdateCredentialsAsync(username, salt, verifier, cancellationToken).ConfigureAwait(false);
        say($"updated password for '{username}'");
    }

    private async Task SetGmLevelAsync(string username, string level, Action<string> say, CancellationToken cancellationToken)
    {
        if (!AccountSecurityText.TryParse(level, out AccountSecurity security))
        {
            say("usage: account set-gmlevel <name> <0-3|player|moderator|gamemaster|administrator>");
            return;
        }

        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        IAccountStore accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        if (!await accounts.UpdateSecurityAsync(username, security, cancellationToken).ConfigureAwait(false))
        {
            say($"account '{username}' does not exist");
            return;
        }

        say($"'{username}' is now {security} ({(byte)security}); it applies from the account's next world login");
    }
}
