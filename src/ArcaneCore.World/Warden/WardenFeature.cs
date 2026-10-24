using System.Collections.Concurrent;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.Announce;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Warden;

/// <summary>
/// Warden for build 5875 clients (docs: <see cref="WardenOptions"/>). Off unless <c>Warden:Enabled</c>. Each authenticated, non-exempt
/// session of a 5875 client gets a <see cref="WardenSession"/> as soon as AUTH_OK is out (vmangos WorldSession::InitWarden); its
/// CMSG_WARDEN_DATA bodies arrive on the session task and the world tick drives deadlines and scans. A verdict is logged and then acted
/// on (vmangos Warden::ApplyPenalty): Log keeps the session, Kick disconnects it, Ban bans the account through <see cref="IBanStore"/>
/// with author "Warden" and disconnects. Never scanned: managed playerbot sessions, staff at or above
/// <see cref="WardenOptions.ExemptSecurity"/>, other client builds.
/// <para>
/// The verdicts of one driver call (one CMSG_WARDEN_DATA, one tick update, one flush) are one batch: each is logged and raised through
/// <see cref="Verdict"/>, but the batch is acted on once, at its strongest action, so a scan reply with several failed scans sends one
/// staff line and starts one ban. The staff line goes to staff directly (outside World:Announce) and at most once per account and
/// action per <see cref="WardenOptions.StaffNoticeCooldownSeconds"/>; the held-back count follows as one line.
/// </para>
/// </summary>
public sealed partial class WardenFeature(IServiceProvider services, ILogger<WardenFeature> logger) : IWorldFeature
{
    public const string BanAuthor = "Warden";

    private const string StaffPrefix = "|cffff4040[Warden]|r ";

    private readonly ConcurrentDictionary<WorldSession, Tracked> _sessions = new(ReferenceEqualityComparer.Instance);
    private WardenOptions _options = new();
    private StaffNoticeLimiter _staffLimiter = StaffNoticeLimiter.On(TimeProvider.System);
    private volatile IReadOnlyList<WardenCheckOptions> _checks = [];
    private WorldRuntime? _world;
    private bool _usable;

    public WardenOptions Options => _options;

    /// <summary>Verdicts reported since start (tests and diagnostics).</summary>
    public event Action<WorldSession, WardenVerdict>? Verdict;

    /// <summary>The cooldown of the direct staff lines (tests, diagnostics).</summary>
    public StaffNoticeLimiter StaffLimiter => _staffLimiter;

    public void Attach(WorldRuntime world)
    {
        _world = world;
        _staffLimiter = StaffNoticeLimiter.On(services.GetService<TimeProvider>() ?? TimeProvider.System);
        WardenOptions configured = services.GetService<IOptions<WardenOptions>>()?.Value ?? new WardenOptions();
        IReadOnlyList<string> problems = configured.Validate();
        if (problems.Count > 0)
        {
            logger.LogError("Warden options are invalid ({Problems}); Warden stays off", string.Join(" ", problems));
            configured = new WardenOptions();
        }

        _options = configured;
        _usable = _options.Enabled && WardenModuleProfile.Module is not null;
        if (_options.Enabled && !_usable)
        {
            logger.LogError("Warden is enabled but the build 5875 module resource is missing or fails its digest check; Warden stays off");
        }

        _checks = [.. _options.Checks];
        if (_usable && _options.LoadFromDatabase)
        {
            _ = LoadChecksAsync();
        }

        world.WorldTick += OnWorldTick;
        logger.LogInformation("Warden {State} (scan action {Action}, protocol action {ProtocolAction}, {Checks} configured scan(s))",
            _usable ? "enabled" : "disabled", _options.Action, _options.ProtocolAction, _options.Checks.Count);
    }

    /// <summary>Report every session's held protocol verdicts, so no count is lost at shutdown.</summary>
    public Task StopAsync()
    {
        foreach ((WorldSession session, Tracked tracked) in _sessions)
        {
            Drive(session, tracked, static (w, _) => w.Flush(), 0u);
        }

        return Task.CompletedTask;
    }

    /// <summary>The scans new sessions run: the table's for build 5875 merged with <c>Warden:Checks</c>.</summary>
    public IReadOnlyList<WardenCheckOptions> Checks => _checks;

    /// <summary>Replace the scans from table rows (the database load, tests).</summary>
    public void ApplyRows(IEnumerable<Data.World.Warden.WardenCheckRow> rows)
    {
        int skipped = 0;
        _checks = WardenCheckRows.Merge(rows, _options.Checks, WardenModuleProfile.Build, (row, problem) =>
        {
            skipped++;
            logger.LogDebug("Warden: warden_checks row {Id} skipped: {Problem}", row.Id, problem);
        });
        logger.LogInformation("Warden: {Count} scan(s) for build {Build} ({Skipped} table row(s) skipped)", _checks.Count, WardenModuleProfile.Build, skipped);
    }

    private async Task LoadChecksAsync()
    {
        try
        {
            if (services.GetService<Microsoft.EntityFrameworkCore.IDbContextFactory<Data.Content.WorldDbContext>>() is not { } factory)
            {
                logger.LogInformation("Warden: no world database; running the configured scans only");
                return;
            }

            await using Data.Content.WorldDbContext db = await factory.CreateDbContextAsync().ConfigureAwait(false);
            ApplyRows(await Data.World.Warden.WardenCheckStore.LoadAsync(db).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("warden.load-checks", ex);
            AnomalyLedger.Shared.Report("warden", "check-fault", "load-checks", $"error={ex.GetType().Name}: {ex.Message}");
            logger.LogFault("warden.load-checks", ex, "Warden: loading warden_checks failed; running the configured scans only");
        }
    }

    /// <summary>The Warden of <paramref name="session"/>, if it has one.</summary>
    public WardenSession? Find(WorldSession session) => _sessions.GetValueOrDefault(session)?.Warden;

    /// <summary>Start Warden for a session that just received AUTH_OK (session task). <paramref name="sessionKey"/> is not retained.</summary>
    public void OnAuthenticated(WorldSession session, uint build, ReadOnlySpan<byte> sessionKey)
    {
        if (!_usable || session.IsManaged || session.Security >= _options.ExemptSecurity || build != WardenModuleProfile.Build || sessionKey.Length != 40)
        {
            return;
        }

        var pending = new VerdictQueue();
        var warden = new WardenSession(sessionKey, _options, body => session.Send(WorldOpcode.SmsgWardenData, body), pending.Add, checks: _checks);
        var tracked = new Tracked(warden, pending);
        if (_sessions.TryAdd(session, tracked))
        {
            Drive(session, tracked, static (w, _) => w.Start(), 0u);
        }
    }

    /// <summary>CMSG_WARDEN_DATA (session task).</summary>
    public void Handle(WorldSession session, byte[] payload)
    {
        if (_sessions.GetValueOrDefault(session) is not { } tracked)
        {
            return;
        }

        Drive(session, tracked, static (w, body) => w.Handle(body), payload);
        if (session.State == SessionState.Closed)
        {
            // A packet that lands after the world tick already flushed this session (or before it removes it) may have throttled a Log
            // verdict; nothing would report that count later, so report it now. Flush is idempotent.
            _sessions.TryRemove(session, out _);
            Drive(session, tracked, static (w, _) => w.Flush(), 0u);
        }
    }

    private void OnWorldTick(uint diffMs)
    {
        foreach ((WorldSession session, Tracked tracked) in _sessions)
        {
            try
            {
                if (session.State == SessionState.Closed)
                {
                    if (_sessions.TryRemove(session, out Tracked? removed))
                    {
                        Drive(session, removed, static (w, _) => w.Flush(), 0u); // the held protocol verdict count is reported, not dropped with the session
                    }

                    continue;
                }

                Drive(session, tracked, static (w, diff) => w.Update(diff), diffMs);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("warden.tick", ex);
                AnomalyLedger.Shared.Report("warden", "check-fault", $"account {session.AccountId}", $"tick error={ex.GetType().Name}: {ex.Message}");
                logger.LogFault("warden.tick", ex, "Warden: world tick update faulted for account {AccountId}", session.AccountId);
            }
        }

        // A staff line window that ended with lines held back: one summary line per account and action (world thread).
        WorldRuntime? world = _world;
        if (world is not null)
        {
            try
            {
                foreach (SuppressedNotices held in _staffLimiter.TakeExpired())
                {
                    SendToStaff(world, $"{StaffPrefix}{StaffNoticeLimiter.SummaryFor(held)}");
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("warden.staff-flush", ex);
                AnomalyLedger.Shared.Report("warden", "check-fault", "staff-flush", $"error={ex.GetType().Name}: {ex.Message}");
                logger.LogFault("warden.tick", ex, "Warden: staff notice flush faulted");
            }
        }
    }

    /// <summary>
    /// One driver call of <paramref name="tracked"/> (any thread), then act on the verdicts it reported as one batch. The call and the
    /// taking of its verdicts share the session's drive gate, so a tick update and a packet on another thread never split a batch; the
    /// verdicts are acted on outside it.
    /// </summary>
    private void Drive<TState>(WorldSession session, Tracked tracked, Action<WardenSession, TState> call, TState state)
    {
        List<WardenVerdict>? batch;
        lock (tracked.Gate)
        {
            call(tracked.Warden, state);
            batch = tracked.Pending.TakeAll();
        }

        if (batch is not null)
        {
            OnVerdicts(session, batch);
        }
    }

    /// <summary>One batch: every verdict is logged and raised; the batch is acted on once, at its strongest action.</summary>
    private void OnVerdicts(WorldSession session, List<WardenVerdict> batch)
    {
        List<WardenVerdict>? acting = null;
        WardenAction action = WardenAction.Log;
        foreach (WardenVerdict verdict in batch)
        {
            logger.LogWarning("Warden: account {Account} ({AccountId}): {Reason}; action {Action}", session.AccountName, session.AccountId, verdict.Reason, verdict.Action);
            if (Verdict is { } handlers)
            {
                try
                {
                    handlers.Invoke(session, verdict);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("warden.verdict", ex);
                    AnomalyLedger.Shared.Report("warden", "check-fault", $"account {session.AccountId}", $"verdict subscriber error={ex.GetType().Name}: {ex.Message}");
                    logger.LogFault("warden.verdict", ex, "Warden: Verdict subscriber threw for account {AccountId}", session.AccountId);
                }
            }

            if (verdict.Action != WardenAction.Log)
            {
                (acting ??= []).Add(verdict);
                if (verdict.Action > action)
                {
                    action = verdict.Action;
                }
            }
        }

        if (acting is null)
        {
            return;
        }

        string reason = ReasonOf(acting);
        switch (action)
        {
            case WardenAction.Kick:
                Announce(session, action, reason, "Warden disconnected a client", AnnounceSeverity.Warning);
                session.Kick();
                break;
            case WardenAction.Ban:
                Announce(session, action, reason, "Warden banned an account", AnnounceSeverity.Error);
                _ = BanAsync(session.AccountId, reason);
                session.Kick();
                break;
        }
    }

    /// <summary>The batch's reasons in one line: up to three, then a count of the rest.</summary>
    private static string ReasonOf(List<WardenVerdict> acting)
    {
        static string One(WardenVerdict v) => v.IsProtocolFailure ? $"{v.Reason} (protocol failure)" : v.Reason;
        if (acting.Count == 1)
        {
            return One(acting[0]);
        }

        string shown = string.Join("; ", acting.Take(3).Select(One));
        return acting.Count > 3 ? $"{acting.Count} failures: {shown}; +{acting.Count - 3} more" : $"{acting.Count} failures: {shown}";
    }

    // Staff get the full line (the account, the reason) directly, outside World:Announce and its shared rate bucket, at most once per account
    // and action per Warden:StaffNoticeCooldownSeconds, so a reconnect loop of one account cannot flood them; the held count follows. The
    // announcer adds the optional player-safe world line (no names, no reason) and the log/metric.
    private void Announce(WorldSession session, WardenAction action, string reason, string text, AnnounceSeverity severity)
    {
        string who = $"account {session.AccountName} ({session.AccountId})";
        string detail = $"{who}: {reason}";
        int cooldown = (int)_options.StaffNoticeCooldownSeconds;
        WorldRuntime? world = _world;
        if (_staffLimiter.TryPass($"{session.AccountId}:{action}", $"{who}: {action.ToString().ToLowerInvariant()}", cooldown, out int suppressed))
        {
            string line = $"{StaffPrefix}{text}: {detail}{StaffNoticeLimiter.SuffixFor(suppressed, cooldown)}";
            world?.Post(() => SendToStaff(world, line));
        }

        services.GetService<IOpsAnnouncer>()?.Announce(
            AnnounceCategory.AntiCheat,
            severity,
            text,
            detail,
            dedupKey: $"warden:{session.AccountId}:{action}",
            staffHandled: true);
    }

    private static void SendToStaff(WorldRuntime world, string text)
    {
        byte[] packet = Packets.ChatPackets.BuildSystemMessage(text);
        foreach (Game.Entities.Player player in world.OnlinePlayers)
        {
            if (player.Security > AccountSecurity.Player)
            {
                player.Session.Send(WorldOpcode.SmsgMessagechat, packet);
            }
        }
    }

    private sealed record Tracked(WardenSession Warden, VerdictQueue Pending)
    {
        public Lock Gate { get; } = new();
    }

    /// <summary>The verdicts a <see cref="WardenSession"/> reported during one driver call (it reports under its own lock, on either thread).</summary>
    private sealed class VerdictQueue
    {
        private readonly Lock _gate = new();
        private List<WardenVerdict>? _items;

        public void Add(WardenVerdict verdict)
        {
            lock (_gate)
            {
                (_items ??= []).Add(verdict);
            }
        }

        public List<WardenVerdict>? TakeAll()
        {
            lock (_gate)
            {
                List<WardenVerdict>? items = _items;
                _items = null;
                return items;
            }
        }
    }

    private async Task BanAsync(int accountId, string reason)
    {
        try
        {
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<IBanStore>() is not { } bans)
            {
                logger.LogWarning("Warden: ban of account {Account} skipped: no ban store", accountId);
                return;
            }

            await bans.BanAccountAsync(new BanRequest(accountId, _options.BanSeconds, $"Warden: {reason}", BanAuthor)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("warden.ban", ex);
            AnomalyLedger.Shared.Report("warden", "check-fault", $"account {accountId}", $"ban error={ex.GetType().Name}: {ex.Message}");
            logger.LogFault("warden.ban", ex, "Warden: ban of account {Account} failed", accountId);
        }
    }
}

/// <summary>CMSG_WARDEN_DATA: the encrypted Warden body, in any authenticated state.</summary>
public sealed class WardenHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
        => table.OnSession(WorldOpcode.CmsgWardenData, SessionStates.Authenticated, (session, payload) =>
        {
            session.Services.GetService<WardenFeature>()?.Handle(session, payload);
            return Task.CompletedTask;
        });
}
