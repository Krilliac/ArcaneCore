using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Kernel.Net;
using ArcaneCore.World.Ops.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// The web login of the public Atlas listener (docs/areas/atlas-public.md, "Login"): the ops <c>login</c>, <c>logout</c> and <c>characters</c>.
/// Atlas never opens the auth database and never computes a verifier; it sends the typed name, password and code over the web connection and
/// receives an opaque ticket (<see cref="MapFeedActorTicketStore"/>) or the single answer <c>login-failed</c>.
/// <para>
/// One answer for every cause of failure: wrong name, wrong password, banned or suspended account, banned address, locked out, an account below the
/// level floor, an account with an IP lock, a missing or wrong PIN/TOTP code, and a refusal by the per-address failure burst. The checks all run before
/// the answer is chosen, after the same equal-time verifier work, so the answer for a real staff account's correct password but missing code is
/// byte-identical to a wrong password's (no password oracle, and no <c>code-required</c> step). The level floor is the higher of the one Atlas states
/// and <see cref="MapFeedActionsOptions.WebLoginMinSecurity"/>. Failure bookkeeping lives here: a <see cref="NetGuard"/> failure burst per client address
/// and a <see cref="WebLoginLockout"/> per (account, network).
/// </para>
/// <para>
/// Roles (design 0.10): <c>login</c>, <c>characters</c> and a ticketed <c>logout</c> belong to a <c>web</c> connection; <c>logout</c> by account name (the
/// operator's revoke) belongs to a <c>local</c> one; anything else is <c>role-mismatch</c>. Thread affinity: handlers run on the connection's reader
/// task; the lockout, the guard and the TOTP step table are thread-safe.
/// </para>
/// </summary>
internal sealed class MapFeedLogin : IFeedExtension
{
    /// <summary>The one failure code of a login, whatever the cause.</summary>
    public const string LoginFailed = "login-failed";

    /// <summary>Longest code (PIN or TOTP digits) read.</summary>
    public const int MaxCodeLength = 16;

    private const string Kind = "login";

    private readonly IServiceProvider _services;
    private readonly MapFeedActionsOptions _options;
    private readonly MapFeedActorTicketStore _tickets;
    private readonly MapFeedWebAudit _audit;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly WebLoginLockout _lockout;
    private readonly NetGuard _guard;
    private readonly ConcurrentDictionary<int, long> _lastTotpStep = new();
    private readonly ConcurrentDictionary<string, int> _failureRuns = new(StringComparer.Ordinal);
    private volatile bool _loginOn;

    public MapFeedLogin(IServiceProvider services, IOptions<MapFeedActionsOptions> options, MapFeedActorTicketStore tickets, MapFeedWebAudit audit, ILoggerFactory loggers)
    {
        _services = services;
        _options = options.Value;
        _tickets = tickets;
        _audit = audit;
        _loggers = loggers;
        _logger = loggers.CreateLogger(MapFeedActorTicketStore.LogCategory);
        _time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        _lockout = new WebLoginLockout(_time);
        long Clock() => _time.GetTimestamp() * 1000 / _time.TimestampFrequency;
        _guard = new NetGuard(
            new NetProtectionOptions { AuthFailureBurstPerIp = 10, AuthFailuresPerMinutePerIp = 10 },
            () => 0, () => 0, _loggers.CreateLogger(MapFeedActorTicketStore.LogCategory), Clock);
    }

    /// <inheritdoc />
    public bool Handles(string op) => op is MapFeedExtensions.LoginOp or MapFeedExtensions.LogoutOp or MapFeedExtensions.CharactersOp;

    /// <inheritdoc />
    public void FeedStarted(WorldMapFeed feed)
    {
        // The channel decided at the same start whether web actors are accepted; a login is offered exactly then.
        _loginOn = _services.GetService<MapFeedActions>()?.Gate.Login == true;
        if (!_loginOn)
        {
            _logger.LogInformation("web login off: the action channel does not accept web actors (World:Control:MapFeed:Actions:Enabled and WebLogin)");
        }
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(MapFeedConnection connection, string op, ReadOnlyMemory<byte> line)
        => op switch
        {
            MapFeedExtensions.LoginOp => LoginAsync(connection, line),
            MapFeedExtensions.LogoutOp => LogoutAsync(connection, line),
            _ => CharactersAsync(connection, line),
        };

    /// <inheritdoc />
    public ValueTask ConnectionEndedAsync(MapFeedConnection connection) => ValueTask.CompletedTask;

    // ---- login -----------------------------------------------------------------------------------------------------------------

    private async Task<bool> LoginAsync(MapFeedConnection connection, ReadOnlyMemory<byte> line)
    {
        using JsonDocument document = FeedExtensionJson.Parse(line);
        JsonElement root = document.RootElement;
        if (!FeedExtensionJson.TryId(root, out long id))
        {
            await connection.QueueAsync(MapFeedJson.ErrorLine(MapFeedProtocol.BadArgs, "login needs an integer \"id\"")).ConfigureAwait(false);
            return false;
        }

        if (connection.Role != FeedRole.Web)
        {
            await connection.QueueAsync(FeedExtensionJson.Refused(id, Kind, MapFeedRoles.RoleMismatch, "login belongs to a web connection")).ConfigureAwait(false);
            return true;
        }

        if (!_loginOn)
        {
            await connection.QueueAsync(FeedExtensionJson.Refused(id, Kind, ActionCodes.WebLoginOff, "web login is off on this server")).ConfigureAwait(false);
            return true;
        }

        string? user = Str(root, "user"u8);
        string? password = Str(root, "password"u8);
        string? code = Str(root, "code"u8);
        string? ip = Str(root, "ip"u8);
        int floor = root.TryGetProperty("floor"u8, out JsonElement floorElement) && floorElement.ValueKind == JsonValueKind.Number && floorElement.TryGetInt32(out int f) ? f : 0;
        if (user is null || password is null || user.Length > AccountPasswordCheck.MaxNameLength || password.Length > AccountPasswordCheck.MaxPasswordLength
            || (code is not null && code.Length > MaxCodeLength) || (ip is not null && ip.Length > 64) || floor is < 0 or > (int)AccountSecurity.Administrator)
        {
            await connection.QueueAsync(FeedExtensionJson.Refused(id, Kind, MapFeedProtocol.BadArgs, "login needs user, password and optionally code, ip and floor (0 to 3) within their length limits")).ConfigureAwait(false);
            return true;
        }

        string reference = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
        string? clientIp = ip is null ? null : AccountBanEvaluator.NormalizeIp(ip);
        string network = WebLoginLockout.NetworkOf(clientIp);
        IpKey? key = IpKey.TryParse(clientIp ?? string.Empty, out IpKey parsed) ? parsed : IpKey.From(IPAddress.IPv6None);
        string name = user.Trim().ToUpperInvariant();
        bool burstOk = _guard.AllowsAuthAttempt(key);
        bool locked = _lockout.IsLocked(name, network);

        bool success = false;
        Account? account = null;
        string why = "refused";
        long totpStep = -1;
        try
        {
            await using AsyncServiceScope scope = _services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            IAccountStore? accounts = scope.ServiceProvider.GetService<IAccountStore>();
            IBanStore? bans = scope.ServiceProvider.GetService<IBanStore>();
            if (accounts is not null)
            {
                // Always the same work first, whatever the outcome.
                AccountCheckResult check = await AccountPasswordCheck.CheckAsync(accounts, bans, user, password, _time, connection.Lifetime).ConfigureAwait(false);
                IpBanRecord? ipBan = clientIp is null || bans is null ? null : await bans.GetActiveIpBanAsync(clientIp, connection.Lifetime).ConfigureAwait(false);
                bool ipBanned = ipBan is not null && AccountBanEvaluator.IsActive(ipBan, _time.GetUtcNow().ToUnixTimeSeconds());
                account = check.Account;
                bool floorOk = account is not null && account.Security >= (AccountSecurity)Math.Max(floor, (int)_options.WebLoginMinSecurity);
                bool ipLockFree = account is null || !account.LockFlags.HasFlag(AccountLockFlags.IpLock);
                bool codeOk = account is not null && CodeAccepted(account, code, out totpStep);
                success = check.Ok & burstOk & !locked & !ipBanned & floorOk & ipLockFree & codeOk;
                if (!success)
                {
                    why = !check.Ok ? check.Outcome.ToString() : !burstOk ? "address failure burst" : locked ? "locked out" : ipBanned ? "address banned"
                        : !floorOk ? "below the level floor" : !ipLockFree ? "IP-locked account" : "code";
                }
            }
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            _logger.LogError(ex, "web login: the account stores failed (ref={Ref})", reference);
            await connection.QueueAsync(FeedExtensionJson.Refused(id, Kind, ActionCodes.TicketUnavailable, "the login service failed; try again")).ConfigureAwait(false);
            return true;
        }

        if (!success)
        {
            await FailAsync(connection, id, name, clientIp, key, network, why, reference).ConfigureAwait(false);
            return true;
        }

        if (!_audit.Preflight())
        {
            // Every check has passed, so a distinct answer here would tell a stranger the password was right. The caller sees the ordinary
            // login-failed; the audit fault is logged here (and by Preflight) for the operator.
            _logger.LogError("web login refused for account={Account} ref={Ref}: the durable audit file cannot be written", LogSafe.Escape(name), reference);
            await FailAsync(connection, id, name, clientIp, key, network, "audit file unwritable", reference).ConfigureAwait(false);
            return true;
        }

        // The code's TOTP step is spent only now that every other check has passed, and the claim is atomic: of two logins racing with the same code, one wins.
        if (totpStep >= 0 && !ClaimTotpStep(account!.Id, totpStep))
        {
            await FailAsync(connection, id, name, clientIp, key, network, "code", reference).ConfigureAwait(false);
            return true;
        }

        string ticket = _tickets.Issue(account!, clientIp);
        _lockout.RecordSuccess(name, network);
        _failureRuns.TryRemove(name + "|" + network, out _);
        string actor = "atlas-web:" + account!.Username.ToUpperInvariant();
        await _audit.AppendAsync(new WebAuditRecord("login", actor, account.Id, ticket, clientIp, null, 0, "ok", null)).ConfigureAwait(false);
        _logger.LogInformation("login ok account={Account} ip={Ip} ref={Ref}", LogSafe.Escape(account.Username), clientIp ?? "unknown", reference);
        AccountSecurity security = account.Security;
        string issued = ticket;
        await connection.QueueAsync(FeedExtensionJson.Result(id, ok: true, Kind, writer =>
        {
            writer.WriteString("ticket"u8, issued);
            writer.WriteString("account"u8, account.Username.ToUpperInvariant());
            writer.WriteNumber("security"u8, (int)security);
            writer.WriteNumber("expiresIn"u8, (int)MapFeedActorTicketStore.AbsoluteLifetime.TotalSeconds);
        })).ConfigureAwait(false);
        return true;
    }

    private async Task FailAsync(MapFeedConnection connection, long id, string name, string? clientIp, IpKey? key, string network, string why, string reference)
    {
        _lockout.RecordFailure(name, network);
        _guard.RecordAuthFailure(key);
        int run = _failureRuns.AddOrUpdate(name + "|" + network, 1, static (_, n) => n + 1);
        if (_failureRuns.Count > 4096)
        {
            _failureRuns.Clear();
        }

        // Account names are the stranger's text: escaped, and the cause stays in the daemon's own log, never in the answer.
        if (run >= 3)
        {
            _logger.LogWarning("login failed account={Account} ip={Ip} ref={Ref} run={Run} ({Why})", LogSafe.Escape(name), clientIp ?? "unknown", reference, run, why);
        }
        else
        {
            _logger.LogInformation("login failed account={Account} ip={Ip} ref={Ref} ({Why})", LogSafe.Escape(name), clientIp ?? "unknown", reference, why);
        }

        await _audit.AppendAsync(new WebAuditRecord("login", "atlas-web:" + name, null, null, clientIp, null, 0, "failed", LoginFailed)).ConfigureAwait(false);
        TimeSpan delay = _lockout.DelayFor(name);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _time, connection.Lifetime).ConfigureAwait(false);
        }

        await connection.QueueAsync(FeedExtensionJson.Refused(id, Kind, LoginFailed, "the name, password or code is not accepted")).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether <paramref name="code"/> satisfies the account's second factor: a fixed PIN (checked first, as the realm does), else a TOTP code of the
    /// current 30 s step or one either side, never a step already used for this account. An account with neither flag takes any code (it is ignored).
    /// A TOTP step is only recorded as used by a successful login: this method does not record it, it returns the accepted step in
    /// <paramref name="totpStep"/> (-1 when the code was a PIN, the account has no second factor, or nothing was accepted) and the caller claims it
    /// with <see cref="ClaimTotpStep"/> once every other check has passed.
    /// </summary>
    private bool CodeAccepted(Account account, string? code, out long totpStep)
    {
        totpStep = -1;
        if (account.LockFlags.HasFlag(AccountLockFlags.FixedPin))
        {
            string pin = account.SecurityInfo;
            return code is { Length: > 0 } && pin.Length is >= 4 and <= 10 && pin.All(char.IsAsciiDigit)
                && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(code.Trim()), Encoding.UTF8.GetBytes(pin));
        }

        if (!account.LockFlags.HasFlag(AccountLockFlags.Totp))
        {
            return true;
        }

        if (code is not { Length: > 0 } || !Totp.TryDecodeSecret(account.SecurityInfo, out byte[] secret))
        {
            return false;
        }

        string typed = code.Trim();
        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        _lastTotpStep.TryGetValue(account.Id, out long lastUsed);
        bool accepted = false;
        for (int offset = -1; offset <= 1; offset++)
        {
            long step = (now / 30) + offset;
            if (step < 0)
            {
                continue;
            }

            string expected = Totp.Generate(secret, now, offset).ToString("D6", CultureInfo.InvariantCulture);
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(typed), Encoding.UTF8.GetBytes(expected)) & step > lastUsed)
            {
                accepted = true;
                totpStep = step;
            }
        }

        return accepted;
    }

    /// <summary>Records <paramref name="step"/> as the account's newest used step; false when that step (or a newer one) was already used, so a replay loses.</summary>
    private bool ClaimTotpStep(int accountId, long step)
    {
        while (true)
        {
            if (!_lastTotpStep.TryGetValue(accountId, out long last))
            {
                if (_lastTotpStep.TryAdd(accountId, step))
                {
                    return true;
                }

                continue;
            }

            if (step <= last)
            {
                return false;
            }

            if (_lastTotpStep.TryUpdate(accountId, step, last))
            {
                return true;
            }
        }
    }

    // ---- logout and revoke -----------------------------------------------------------------------------------------------------

    private async Task<bool> LogoutAsync(MapFeedConnection connection, ReadOnlyMemory<byte> line)
    {
        using JsonDocument document = FeedExtensionJson.Parse(line);
        JsonElement root = document.RootElement;
        if (!FeedExtensionJson.TryId(root, out long id))
        {
            await connection.QueueAsync(MapFeedJson.ErrorLine(MapFeedProtocol.BadArgs, "logout needs an integer \"id\"")).ConfigureAwait(false);
            return false;
        }

        string? ticket = Str(root, "ticket"u8);
        string? account = Str(root, "account"u8);
        if ((ticket is null) == (account is null))
        {
            await connection.QueueAsync(FeedExtensionJson.Refused(id, "logout", MapFeedProtocol.BadArgs, "logout needs either \"ticket\" or \"account\"")).ConfigureAwait(false);
            return true;
        }

        if (ticket is not null)
        {
            if (connection.Role != FeedRole.Web)
            {
                await connection.QueueAsync(FeedExtensionJson.Refused(id, "logout", MapFeedRoles.RoleMismatch, "a ticket logout belongs to a web connection")).ConfigureAwait(false);
                return true;
            }

            string reason = Str(root, "reason"u8) is "kick" ? "kick" : "logout";
            bool ended = ticket.Length <= 512 && await _tickets.EndAsync(ticket, reason, Str(root, "ip"u8)).ConfigureAwait(false);
            await connection.QueueAsync(FeedExtensionJson.Result(id, ok: true, "logout", w => w.WriteBoolean("ended"u8, ended))).ConfigureAwait(false);
            return true;
        }

        if (connection.Role != FeedRole.Local)
        {
            await connection.QueueAsync(FeedExtensionJson.Refused(id, "logout", MapFeedRoles.RoleMismatch, "revoking an account belongs to the local connection")).ConfigureAwait(false);
            return true;
        }

        if (account!.Length is 0 or > AccountPasswordCheck.MaxNameLength)
        {
            await connection.QueueAsync(FeedExtensionJson.Refused(id, "logout", MapFeedProtocol.BadArgs, "\"account\" is not an account name")).ConfigureAwait(false);
            return true;
        }

        if (!_audit.Preflight())
        {
            await connection.QueueAsync(FeedExtensionJson.Refused(id, "logout", ActionCodes.AuditUnavailable, "the audit file cannot be written; the revoke is refused")).ConfigureAwait(false);
            return true;
        }

        int dropped = await _tickets.RevokeAccountAsync(account, connection.Identity.ActorName).ConfigureAwait(false);
        await connection.QueueAsync(FeedExtensionJson.Result(id, ok: true, "logout", w => w.WriteNumber("revoked"u8, dropped))).ConfigureAwait(false);
        return true;
    }

    // ---- characters ------------------------------------------------------------------------------------------------------------

    /// <summary>One online character of the ticket's own account.</summary>
    private sealed record OwnCharacter(string Guid, string Name, uint Map, uint Instance, float X, float Y, float Z, float Orientation, int Level);

    private async Task<bool> CharactersAsync(MapFeedConnection connection, ReadOnlyMemory<byte> line)
    {
        using JsonDocument document = FeedExtensionJson.Parse(line);
        JsonElement root = document.RootElement;
        if (!FeedExtensionJson.TryId(root, out long id))
        {
            await connection.QueueAsync(MapFeedJson.ErrorLine(MapFeedProtocol.BadArgs, "characters needs an integer \"id\"")).ConfigureAwait(false);
            return false;
        }

        if (connection.Role != FeedRole.Web)
        {
            await connection.QueueAsync(FeedExtensionJson.Refused(id, "characters", MapFeedRoles.RoleMismatch, "characters belongs to a web connection")).ConfigureAwait(false);
            return true;
        }

        if (Str(root, "ticket"u8) is not { Length: > 0 and <= 512 } ticket)
        {
            await connection.QueueAsync(FeedExtensionJson.Refused(id, "characters", ActionCodes.NoTicket, "characters needs a ticket")).ConfigureAwait(false);
            return true;
        }

        FeedTicketLookup lookup;
        try
        {
            lookup = await _tickets.ResolveAsync(ticket, connection.Lifetime).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            _logger.LogError(ex, "web login: the ticket service failed for a characters request");
            await connection.QueueAsync(FeedExtensionJson.Refused(id, "characters", ActionCodes.TicketUnavailable, "the ticket service failed; try again")).ConfigureAwait(false);
            return true;
        }

        if (lookup.Status != FeedTicketStatus.Valid)
        {
            await connection.QueueAsync(FeedExtensionJson.Refused(id, "characters", ActionCodes.NoTicket, "the ticket is not valid; log in again")).ConfigureAwait(false);
            return true;
        }

        int accountId = lookup.AccountId;
        (OwnCharacter[]? own, FeedError? failure) = await connection.InvokeWorldAsync(() =>
            connection.Feed.World.OnlinePlayers.Where(p => p.AccountId == accountId).Take(16)
                .Select(p => new OwnCharacter(p.Guid.Value.ToString("X16", CultureInfo.InvariantCulture), p.Name, p.MapId, p.Map?.InstanceId ?? 0u, p.X, p.Y, p.Z, p.Orientation, p.Level))
                .ToArray()).ConfigureAwait(false);
        if (own is null)
        {
            await connection.QueueAsync(FeedExtensionJson.Refused(id, "characters", failure!.Value.Code, failure.Value.Message)).ConfigureAwait(false);
            return true;
        }

        await connection.QueueAsync(FeedExtensionJson.Result(id, ok: true, "characters", writer =>
        {
            writer.WriteStartArray("characters"u8);
            foreach (OwnCharacter c in own)
            {
                if (!float.IsFinite(c.X) || !float.IsFinite(c.Y) || !float.IsFinite(c.Z))
                {
                    continue;
                }

                writer.WriteStartObject();
                writer.WriteString("g"u8, c.Guid);
                writer.WriteString("n"u8, c.Name);
                writer.WriteNumber("map"u8, c.Map);
                writer.WriteNumber("instance"u8, c.Instance);
                writer.WriteNumber("x"u8, c.X);
                writer.WriteNumber("y"u8, c.Y);
                writer.WriteNumber("z"u8, c.Z);
                writer.WriteNumber("o"u8, float.IsFinite(c.Orientation) ? c.Orientation : 0f);
                writer.WriteNumber("level"u8, c.Level);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        })).ConfigureAwait(false);
        return true;
    }

    private static string? Str(JsonElement root, ReadOnlySpan<byte> name)
        => root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}

/// <summary>Registers the web login (docs/areas/atlas-public.md): the ticket service the action channel resolves web actors through, and the <c>login</c>, <c>logout</c> and <c>characters</c> ops.</summary>
internal static class MapFeedLoginRegistration
{
    /// <summary>Register <see cref="MapFeedActorTicketStore"/> (also as <see cref="IFeedActorTickets"/>) and <see cref="MapFeedLogin"/> and <see cref="MapFeedPublicLinks"/> as feed extensions. Needs <c>AddMapFeedActions</c> (the audit).</summary>
    public static IServiceCollection AddMapFeedWebLogin(this IServiceCollection services)
    {
        services.AddSingleton<MapFeedActorTicketStore>();
        services.AddSingleton<IFeedActorTickets>(provider => provider.GetRequiredService<MapFeedActorTicketStore>());
        services.AddSingleton<MapFeedLogin>();
        services.AddSingleton<IFeedExtension>(provider => provider.GetRequiredService<MapFeedLogin>());
        services.AddSingleton<MapFeedPublicLinks>();
        services.AddSingleton<IFeedExtension>(provider => provider.GetRequiredService<MapFeedPublicLinks>());
        return services;
    }
}
