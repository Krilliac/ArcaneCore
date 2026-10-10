using System.Collections.Concurrent;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
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
/// </summary>
public sealed class WardenFeature(IServiceProvider services, ILogger<WardenFeature> logger) : IWorldFeature
{
    public const string BanAuthor = "Warden";

    private readonly ConcurrentDictionary<WorldSession, WardenSession> _sessions = new(ReferenceEqualityComparer.Instance);
    private WardenOptions _options = new();
    private bool _usable;

    public WardenOptions Options => _options;

    /// <summary>Verdicts reported since start (tests and diagnostics).</summary>
    public event Action<WorldSession, WardenVerdict>? Verdict;

    public void Attach(WorldRuntime world)
    {
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

        world.WorldTick += OnWorldTick;
        logger.LogInformation("Warden {State} (scan action {Action}, protocol action {ProtocolAction}, {Checks} configured scan(s))",
            _usable ? "enabled" : "disabled", _options.Action, _options.ProtocolAction, _options.Checks.Count);
    }

    public Task StopAsync() => Task.CompletedTask;

    /// <summary>The Warden of <paramref name="session"/>, if it has one.</summary>
    public WardenSession? Find(WorldSession session) => _sessions.GetValueOrDefault(session);

    /// <summary>Start Warden for a session that just received AUTH_OK (session task). <paramref name="sessionKey"/> is not retained.</summary>
    public void OnAuthenticated(WorldSession session, uint build, ReadOnlySpan<byte> sessionKey)
    {
        if (!_usable || session.IsManaged || session.Security >= _options.ExemptSecurity || build != WardenModuleProfile.Build || sessionKey.Length != 40)
        {
            return;
        }

        var warden = new WardenSession(sessionKey, _options, body => session.Send(WorldOpcode.SmsgWardenData, body), verdict => OnVerdict(session, verdict));
        if (_sessions.TryAdd(session, warden))
        {
            warden.Start();
        }
    }

    /// <summary>CMSG_WARDEN_DATA (session task).</summary>
    public void Handle(WorldSession session, byte[] payload) => Find(session)?.Handle(payload);

    private void OnWorldTick(uint diffMs)
    {
        foreach ((WorldSession session, WardenSession warden) in _sessions)
        {
            if (session.State == SessionState.Closed)
            {
                _sessions.TryRemove(session, out _);
                continue;
            }

            warden.Update(diffMs);
        }
    }

    private void OnVerdict(WorldSession session, WardenVerdict verdict)
    {
        logger.LogWarning("Warden: account {Account} ({AccountId}): {Reason}; action {Action}", session.AccountName, session.AccountId, verdict.Reason, verdict.Action);
        Verdict?.Invoke(session, verdict);
        switch (verdict.Action)
        {
            case WardenAction.Kick:
                session.Kick();
                break;
            case WardenAction.Ban:
                _ = BanAsync(session.AccountId, verdict.Reason);
                session.Kick();
                break;
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
        catch (Exception ex)
        {
            logger.LogError(ex, "Warden: ban of account {Account} failed", accountId);
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
