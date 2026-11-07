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
/// <see cref="AccountSecurityText.TryParse"/>, the same <see cref="IAccountStore"/> calls and messages),
/// <c>account delete</c> (two steps, refused unless the recorded character count is 0, which can be stale; see <see cref="DeleteAccountAsync"/>) and
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
    /// <summary>How long an armed <c>account delete</c> waits for its <c>confirm</c>.</summary>
    public static readonly TimeSpan DeleteConfirmWindow = TimeSpan.FromSeconds(60);

    private readonly object _pendingLock = new();
    private string? _pendingDeleteName;
    private DateTimeOffset _pendingDeleteExpires;

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
        say("  account delete <name>                       arm the deletion of an account whose recorded character count is 0 (then repeat with 'confirm' within 60 s; irreversible)");
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
            case "delete" when words.Length == 3:
                await DeleteAccountAsync(words[2].ToUpperInvariant(), confirm: false, say, cancellationToken).ConfigureAwait(false);
                return;
            case "delete" when words.Length == 4 && string.Equals(words[3], "confirm", StringComparison.OrdinalIgnoreCase):
                await DeleteAccountAsync(words[2].ToUpperInvariant(), confirm: true, say, cancellationToken).ConfigureAwait(false);
                return;
            default:
                say("usage: account create <name> <password> | account set-password <name> <password> | account set-gmlevel <name> <0-3|player|moderator|gamemaster|administrator> | account delete <name> [confirm]");
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

    /// <summary>
    /// <c>account delete &lt;name&gt;</c> then <c>account delete &lt;name&gt; confirm</c> within <see cref="DeleteConfirmWindow"/>. The realm
    /// console is the local operator's stdin, which is the highest authority this daemon has (there is no in-band account to check a GM
    /// level of), so the guardrails are the two steps and a character check that fails closed. This daemon cannot read the characters
    /// database, so its only evidence is the <c>realmcharacters</c> cache (written by the world), and a missing row is NOT evidence of 0
    /// (characters that predate the table, playerbot characters, a world with realm id 0 and a failed count write all
    /// leave no row). The delete is therefore refused unless the count store is configured, at least one realm is listed, and every listed
    /// realm has an explicit row of 0 for the account; any recorded character refuses it too. Deliberate deviation from vmangos
    /// AccountMgr.cpp:82-105, which removes the characters first and then deletes the account row (:104) and the <c>realmcharacters</c>
    /// rows (:105) in one transaction: here the account row goes first, then the count rows, as two separate statements (no transaction
    /// spans the stores). A failure in between leaves harmless count rows of a dead account id instead of a live account with no rows.
    /// Limit: a recorded 0 is what the world last wrote, not proof: a failed count write, realm id 0 or a character added outside the world's
    /// create, delete, list and player-dump import paths leaves a stale 0, and this command trusts it.
    /// Limit: the realm cannot kick a world session, so an account online at character select keeps its session until it ends.
    /// Bans, realms and other accounts are untouched.
    /// </summary>
    private async Task DeleteAccountAsync(string username, bool confirm, Action<string> say, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<IAccountAdmin>() is not { } admin)
        {
            say("account deletion is unavailable: no account admin store is configured");
            return;
        }

        IAccountStore accounts = scope.ServiceProvider.GetRequiredService<IAccountStore>();
        if (await accounts.FindByUsernameAsync(username, cancellationToken).ConfigureAwait(false) is not { } account)
        {
            ClearPendingDelete();
            say($"account '{username}' does not exist");
            return;
        }

        if (scope.ServiceProvider.GetService<IRealmCharacterCountStore>() is not { } counts)
        {
            ClearPendingDelete();
            say($"account '{username}' cannot be deleted: no character count store is configured, so its characters cannot be ruled out");
            return;
        }

        IReadOnlyList<RealmEntry> realms = await scope.ServiceProvider.GetRequiredService<IRealmStore>().GetRealmsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<int, int> perRealm = await counts.GetCountsAsync(account.Id, cancellationToken).ConfigureAwait(false);
        int characters = perRealm.Values.Sum();
        if (characters > 0)
        {
            ClearPendingDelete();
            say($"account '{username}' still has {characters} character(s); delete them on their realm first, then delete the account");
            return;
        }

        int[] unproven = [.. realms.Select(r => r.Id).Where(id => !perRealm.ContainsKey(id)).Order()];
        if (realms.Count == 0 || unproven.Length > 0)
        {
            ClearPendingDelete();
            say(realms.Count == 0
                ? $"account '{username}' cannot be deleted: no realm is listed, so its characters cannot be ruled out"
                : $"account '{username}' cannot be deleted: no character count is recorded for realm(s) {string.Join(", ", unproven)}, so its characters there cannot be ruled out (a world records it when the account lists characters there)");
            return;
        }

        if (!confirm)
        {
            lock (_pendingLock)
            {
                _pendingDeleteName = username;
                _pendingDeleteExpires = _time.GetUtcNow() + DeleteConfirmWindow;
            }

            say($"this permanently deletes account '{username}'; to proceed run: account delete {username} confirm (within {(int)DeleteConfirmWindow.TotalSeconds} s)");
            return;
        }

        bool armed;
        lock (_pendingLock)
        {
            armed = string.Equals(_pendingDeleteName, username, StringComparison.Ordinal) && _time.GetUtcNow() <= _pendingDeleteExpires;
            _pendingDeleteName = null;
        }

        if (!armed)
        {
            say($"no pending deletion of '{username}' (it expires after {(int)DeleteConfirmWindow.TotalSeconds} s); run: account delete {username}");
            return;
        }

        bool deleted = await admin.DeleteAsync(username, cancellationToken).ConfigureAwait(false);
        await counts.DeleteByAccountAsync(account.Id, cancellationToken).ConfigureAwait(false);
        say(deleted ? $"deleted account '{username}'" : $"account '{username}' does not exist");
    }

    private void ClearPendingDelete()
    {
        lock (_pendingLock)
        {
            _pendingDeleteName = null;
        }
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
