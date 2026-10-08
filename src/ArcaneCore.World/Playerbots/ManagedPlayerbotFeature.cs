using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Diagnostics;
using ArcaneCore.Cryptography;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Owns persistent autonomous P0 players, their ordinary sessions and save barriers.
/// <para>
/// Faults: an exception out of a bot's update (brain, scripted controller or motion) is an action fault. It is logged with the
/// whole exception and the bot is quarantined, not disabled: its session closes, its character is saved, the record turns
/// Faulted with the fault as its error code but keeps DesiredEnabled, and after <see cref="PlayerbotOptions.FaultBackoffSeconds"/>
/// (doubled for each further fault) it logs in again, autonomous. Only <see cref="PlayerbotOptions.MaxFaults"/> faults within
/// <see cref="PlayerbotOptions.FaultWindowSeconds"/> turn DesiredEnabled off. One fault used to disable the bot for good
/// (Dawnrover and Ironwander, 2026-10-07). The fault history lives in this process; a world restart restores every desired bot.
/// </para>
/// </summary>
public sealed class ManagedPlayerbotFeature(IServiceProvider services, ILogger<ManagedPlayerbotFeature> logger) : IWorldFeature, IPlayerbotService
{
    private readonly PlayerbotOptions _options = services.GetService<IOptions<PlayerbotOptions>>()?.Value ?? new();
    private IServiceScopeFactory scopes => services.GetRequiredService<IServiceScopeFactory>();
    private SessionRegistry registry => services.GetRequiredService<SessionRegistry>();
    private OpcodeTable opcodes => services.GetService<OpcodeTable>() ?? WorldServiceCollectionExtensions.BuildOpcodeTable();
    private IOptions<WorldSessionOptions> sessionOptions => services.GetService<IOptions<WorldSessionOptions>>() ?? Options.Create(new WorldSessionOptions());
    private CharacterSaveQueue saves => services.GetRequiredService<CharacterSaveQueue>();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly ConcurrentDictionary<Guid, ActiveBot> _active = new();
    private PlayerbotStatus[] _snapshot = [];
    private readonly object _snapshotGate = new();
    // Allocation per tick (docs/integration/perf-limits-20261008.md): the running bots in BotId order are re-sorted only when the
    // set changes (_activeVersion), and the snapshot is rebuilt only when a running bot's status changed or PublishStopped
    // touched it (_snapshotEpoch). Before, both were rebuilt every tick: ~100 bytes per bot per tick in PlayerbotStatus alone.
    private int _activeVersion;
    private int _orderedVersion = -1;
    private ActiveBot[] _ordered = [];
    private int _snapshotEpoch;
    private int _builtEpoch = -1;
    private int _builtCount = -1;
    private WorldRuntime? _world;
    private bool _stopping;
    private int _cursor;
    private uint _checkpointMs;
    private Task _checkpoint = Task.CompletedTask;

    /// <summary>
    /// The last checkpoint started (world thread: read it there, after the tick that started it). Tests step the manual world clock
    /// one tick at a time and let each checkpoint finish before game time moves on.
    /// </summary>
    internal Task LastCheckpoint => _checkpoint;
    private PlayerbotLocalPlanner? _planner;
    private readonly CancellationTokenSource _planningStop = new();
    private readonly ConcurrentDictionary<Guid, Quarantine> _quarantine = new();
    private long _clockMs;

    /// <summary>World time this feature has seen, in ms (the fault backoff and window run on it).</summary>
    private long ClockMs => Interlocked.Read(ref _clockMs);

    public void Attach(WorldRuntime world)
    {
        _options.Validate();
        if (_options.AllowLocalLlm) _planner = new PlayerbotLocalPlanner(_options);
        _world = world;
        world.Updated += Update;
    }

    public async Task StartupAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return;
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        await ReconcileProvisioningAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ManagedPlayerbot> registered = await scope.ServiceProvider.GetRequiredService<IManagedPlayerbotStore>()
            .LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var stopped = new List<PlayerbotStatus>();
        foreach (ManagedPlayerbot bot in registered)
        {
            CharacterRecord? character = await scope.ServiceProvider.GetRequiredService<ICharacterStore>()
                .GetByIdAsync(bot.CharacterId, cancellationToken).ConfigureAwait(false);
            if (character is null) continue;
            stopped.Add(new(bot.BotId, character.Name, ManagedPlayerbotState.Stopped, bot.DesiredEnabled,
                bot.Goal, bot.TargetEntry, bot.QuestId, character.MapId, 0, bot.ErrorCode));
        }
        lock (_snapshotGate)
        {
            Volatile.Write(ref _snapshot, stopped.ToArray());
            _snapshotEpoch++;
        }
        // A restore that cannot log a bot in is a fault, not an operator decision: the bot stays desired and is retried (quarantine).
        if (_options.RestoreOnStartup)
            foreach (ManagedPlayerbot bot in registered.Where(b => b.DesiredEnabled).Take(_options.MaxBots))
                await StartCoreAsync(bot.BotId.ToString(), null, cancellationToken, StartKind.Restore).ConfigureAwait(false);
    }

    public IReadOnlyList<PlayerbotStatus> Snapshot() => Volatile.Read(ref _snapshot).ToArray();

    public Task<PlayerbotInspection?> InspectAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        WorldRuntime? world = _world;
        if (world is null) return Task.FromResult<PlayerbotInspection?>(null);
        cancellationToken.ThrowIfCancellationRequested();
        Guid? id = Guid.TryParse(idOrName, out Guid parsed) ? parsed : null;
        return world.InvokeAsync(() =>
        {
            ActiveBot? bot = id is { } key ? _active.GetValueOrDefault(key)
                : _active.Values.FirstOrDefault(value => value.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
            return bot is null ? null : PlayerbotInspector.Capture(bot.Session, bot.Brain, bot.PartyDriven ? bot.Party : null);
        }).WaitAsync(cancellationToken);
    }

    public async Task<PlayerbotOperationResult> CreateAsync(string name, byte race, byte characterClass,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || _stopping) return new(false, "playerbots-disabled");
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping) return new(false, "playerbots-stopping");
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            await ReconcileProvisioningAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            IManagedPlayerbotStore store = scope.ServiceProvider.GetRequiredService<IManagedPlayerbotStore>();
            // Registration is bounded by MaxRegisteredBots; MaxBots bounds only the bots running at once (StartCoreAsync), so a
            // stopped bot never takes a running slot (the live stress test of 2026-10-08 found 5 stopped bots blocking creation).
            if ((await store.LoadAllAsync(cancellationToken).ConfigureAwait(false)).Count >= _options.MaxRegisteredBots)
                return new(false, "playerbot-registry-full");
            ICharacterStore characters = scope.ServiceProvider.GetRequiredService<ICharacterStore>();
            if (await characters.IsNameTakenAsync(name, cancellationToken).ConfigureAwait(false)) return new(false, "name-in-use");
            // Credentials are random and discarded; only salt/verifier persist. These accounts cannot be adopted by name.
            string username = "PB" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            byte[] salt = RandomNumberGenerator.GetBytes(32);
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
            Guid botId = Guid.NewGuid();
            Account owner = await scope.ServiceProvider.GetRequiredService<IManagedPlayerbotProvisionStore>().CreateAsync(botId, new Account
            {
                Username = username, Salt = salt,
                Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, username, password), 32),
                Security = AccountSecurity.Player,
            }, cancellationToken).ConfigureAwait(false);
            WorldSession session = await NewSessionAsync(owner, null, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            try
            {
                var packet = new PacketWriter();
                packet.WriteCString(name); packet.WriteByte(race); packet.WriteByte(characterClass);
                for (int i = 0; i < 8; i++) packet.WriteByte(0);
                await session.DispatchManagedSessionAsync(WorldOpcode.CmsgCharCreate, packet.ToArray()).ConfigureAwait(false);
                CharacterRecord? character = (await characters.GetByAccountAsync(owner.Id, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
                if (character is null) throw new InvalidOperationException("character-create-refused");
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var bot = new ManagedPlayerbot(botId, owner.Id, character.Id, owner.Username, false,
                    ManagedPlayerbotState.Stopped, PlayerbotGoalKind.Explore, 0, 0, 0, now, now);
                await store.CreateAsync(bot, cancellationToken).ConfigureAwait(false);
                if (!await scope.ServiceProvider.GetRequiredService<IManagedPlayerbotProvisionStore>()
                    .CompleteAsync(botId, owner.Id, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("provision-completion-refused");
                PublishStopped(bot, character.Name, character.MapId);
                logger.LogInformation("Created managed playerbot {BotId}, character {CharacterId}", bot.BotId, bot.CharacterId);
                return new(true, "created", bot.BotId, character.Name);
            }
            finally
            {
                session.Kick();
                await session.ManagedClosed.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning("Managed playerbot creation failed ({Type})", ex.GetType().Name);
            await using AsyncServiceScope repair = scopes.CreateAsyncScope();
            try { await ReconcileProvisioningAsync(repair.ServiceProvider, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            { logger.LogError("Playerbot provisioning retained for reconciliation ({Type})", failure.GetType().Name); }
            if (ex is OperationCanceledException) throw;
            return new(false, "create-failed");
        }
        finally { _operations.Release(); }
    }

    /// <summary>Start a bot; an operator start also clears the bot's quarantine and fault history.</summary>
    public Task<PlayerbotOperationResult> StartAsync(string idOrName, CancellationToken cancellationToken = default)
        => StartCoreAsync(idOrName, null, cancellationToken, StartKind.Operator);

    /// <summary>
    /// Start a bot in scripted mode: <paramref name="controller"/> replaces <see cref="PlayerbotBrain"/> from the first
    /// tick (the brain never runs). A bot that is already running is refused with "already-running"; switch it with
    /// <see cref="SetControllerAsync"/> instead.
    /// </summary>
    public Task<PlayerbotOperationResult> StartScriptedAsync(string idOrName, IPlayerbotController controller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controller);
        return StartCoreAsync(idOrName, controller, cancellationToken, StartKind.Operator);
    }

    /// <summary>
    /// Attach (scripted mode) or detach (null: back to autonomous mode) the controller of a running bot. Runs on the
    /// world thread; false when the bot is not running.
    /// </summary>
    public Task<bool> SetControllerAsync(Guid botId, IPlayerbotController? controller)
    {
        if (_world is not { } world) return Task.FromResult(false);
        return world.InvokeAsync(() =>
        {
            if (!_active.TryGetValue(botId, out ActiveBot? active) || active.Paused) return false;
            IPlayerbotController? previous = active.Controller;
            active.Controller = controller;
            active.Session.ManagedBudget = null;
            if (controller is not null && active.Session.Player is { } player && (active.PartyDriven || active.Party.IsEngaged))
            {
                // The controller drives now: the party AI lets go, so no party goal, master or mode is reported while it does. When
                // the controller detaches, a bot still grouped is engaged afresh; an ungrouped one gets a fresh brain.
                active.Party.Disengage(player);
                if (active.PartyDriven) ReplaceBrain(active);
                active.PartyDriven = false;
            }
            if (previous is not null && !ReferenceEquals(previous, controller)) previous.Detached(botId);
            return true;
        });
    }

    /// <summary>Whether a running bot is driven by a controller instead of the brain.</summary>
    public bool IsScripted(Guid botId) => _active.TryGetValue(botId, out ActiveBot? active) && active.Controller is not null;

    /// <summary>Whether a running autonomous bot is driven by its party AI (it is in a real player's group) instead of the brain.</summary>
    public bool IsPartyDriven(Guid botId) => _active.TryGetValue(botId, out ActiveBot? active) && active.PartyDriven;

    /// <summary>The brain of a running bot (inspection and the scenario harness; null when not running).</summary>
    internal PlayerbotBrain? FindBrain(Guid botId) => _active.TryGetValue(botId, out ActiveBot? active) && !active.Paused ? active.Brain : null;

    /// <summary>The party AI of a running bot while it drives the bot (inspection and the scenario harness), otherwise null.</summary>
    internal PlayerbotPartyAI? FindParty(Guid botId)
        => _active.TryGetValue(botId, out ActiveBot? active) && !active.Paused && active.PartyDriven ? active.Party : null;

    /// <summary>
    /// <c>.playerbot invite</c> (the vmangos <c>.partybot add</c> analogue; world thread): <paramref name="inviter"/> invites the
    /// running autonomous bot <paramref name="idOrName"/> through the ordinary group invite, and the bot accepts at once whatever its
    /// <see cref="PlayerbotPartyOptions.InvitePolicy"/> says. The invite keeps every ordinary rule (faction, full group, leader or
    /// assistant only); a refused one is answered to the inviter by the group system and reported as "invite-refused".
    /// </summary>
    internal PlayerbotOperationResult InviteToGroup(Player inviter, string idOrName)
    {
        ArgumentNullException.ThrowIfNull(inviter);
        if (_world is not { } world || !world.IsWorldThread) throw new InvalidOperationException("playerbot-invite-thread");
        Guid? id = Guid.TryParse(idOrName, out Guid parsed) ? parsed : null;
        ActiveBot? bot = id is { } key ? _active.GetValueOrDefault(key)
            : _active.Values.FirstOrDefault(value => value.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
        if (bot is null || bot.Paused || bot.Session.State != SessionState.InWorld || bot.Session.Player is not { } player)
            return new(false, "bot-not-running");
        if (bot.Controller is not null) return new(false, "bot-scripted", bot.Record.BotId, player.Name);
        if (ReferenceEquals(player, inviter)) return new(false, "invite-refused", bot.Record.BotId, player.Name);
        if (services.GetService<Social.SocialFeature>()?.Context.Groups is not { } groups) return new(false, "groups-unavailable");
        if (groups.AreInSameGroup(inviter.Guid, player.Guid)) return new(true, "already-in-your-group", bot.Record.BotId, player.Name);
        if (groups.GetGroup(player.Guid) is not null) return new(false, "bot-already-grouped", bot.Record.BotId, player.Name);
        groups.Invite(inviter, player.Name);
        if (groups.GetInvite(player.Guid) is null) return new(false, "invite-refused", bot.Record.BotId, player.Name);
        bot.Session.ManagedBudget = null;
        bot.Session.TryManagedAction(WorldOpcode.CmsgGroupAccept, []);
        return groups.AreInSameGroup(inviter.Guid, player.Guid)
            ? new(true, "invited", bot.Record.BotId, player.Name)
            : new(false, "accept-refused", bot.Record.BotId, player.Name);
    }

    /// <summary>The ordinary session of a running bot (scenario harness; null when not running).</summary>
    internal WorldSession? FindSession(Guid botId)
        => _active.TryGetValue(botId, out ActiveBot? active) && !active.Paused ? active.Session : null;

    /// <summary>Who starts a bot: an operator (clears the quarantine), the startup restore, or a quarantine retry.</summary>
    private enum StartKind { Operator, Restore, QuarantineRetry }

    /// <param name="retrying">For <see cref="StartKind.QuarantineRetry"/>: the quarantine entry the retry was scheduled from.</param>
    private async Task<PlayerbotOperationResult> StartCoreAsync(string idOrName, IPlayerbotController? controller,
        CancellationToken cancellationToken, StartKind kind, Quarantine? retrying = null)
    {
        bool quarantineRetry = kind != StartKind.Operator;
        if (!_options.Enabled || _stopping) return new(false, "playerbots-disabled");
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        AsyncServiceScope scope = scopes.CreateAsyncScope();
        WorldSession? session = null;
        bool retained = false;
        ManagedPlayerbot? bot = null;
        try
        {
            if (_stopping) return new(false, "playerbots-stopping");
            bot = await ResolveAsync(scope.ServiceProvider, idOrName, cancellationToken).ConfigureAwait(false);
            if (bot is null) return new(false, "bot-not-found");
            if (kind == StartKind.Operator) _quarantine.TryRemove(bot.BotId, out _);
            // The retry was scheduled outside this lock; an operator stop (or start) that took the lock in between cleared the
            // quarantine and decided DesiredEnabled. Re-check under the lock so the retry never undoes it.
            if (kind == StartKind.QuarantineRetry && (!bot.DesiredEnabled
                || !_quarantine.TryGetValue(bot.BotId, out Quarantine? current) || !ReferenceEquals(current, retrying)))
                return new(false, "quarantine-cleared", bot.BotId);
            if (_active.TryGetValue(bot.BotId, out ActiveBot? existing))
                return new(!existing.Paused, existing.Paused ? "stop-incomplete" : "already-running", bot.BotId);
            // Running bots only (World:Playerbots:MaxBots, live): stopped and quarantined bots hold no slot.
            if (_active.Count >= _options.MaxBots) return new(false, "playerbot-capacity");
            Account? owner = await scope.ServiceProvider.GetRequiredService<IAccountStore>()
                .FindByUsernameAsync(bot.AccountName, cancellationToken).ConfigureAwait(false);
            if (owner?.Id != bot.AccountId || owner.Status != AccountStatus.Active
                || owner.Security != AccountSecurity.Player || owner.SessionKey is not null)
                return new(false, "owner-refused", bot.BotId);
            session = await NewSessionAsync(owner, bot.CharacterId, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            bot = await PersistAsync(scope.ServiceProvider, bot with { DesiredEnabled = true, State = ManagedPlayerbotState.Starting, ErrorCode = null }, cancellationToken).ConfigureAwait(false);
            var login = new PacketWriter(); login.WriteUInt64((ulong)(uint)bot.CharacterId);
            await session.DispatchManagedSessionAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray()).ConfigureAwait(false);
            bool entered = await _world!.InvokeAsync(() => session.State == SessionState.InWorld
                && session.Player is { Map: not null } player && _options.AllowedMaps.Contains(player.Map.MapId))
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            if (!entered) throw new InvalidOperationException("login-refused");
            bot = await PersistAsync(scope.ServiceProvider, bot with { State = ManagedPlayerbotState.Running }, cancellationToken).ConfigureAwait(false);
            var active = new ActiveBot(bot, scope, session, NewBrain(session), new PlayerbotPartyAI(session, _options))
            { Controller = controller };
            if (!_active.TryAdd(bot.BotId, active)) throw new InvalidOperationException("duplicate-bot");
            Interlocked.Increment(ref _activeVersion);
            retained = true;
            logger.LogInformation("Started managed playerbot {BotId}, character {CharacterId}", bot.BotId, bot.CharacterId);
            return new(true, "started", bot.BotId, session.Player!.Name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Managed playerbot start failed ({Type}: {Message})", ex.GetType().Name, ex.Message);
            if (bot is not null)
            {
                // An operator start that fails is reported and not retried; a quarantine retry that fails is one more fault.
                (bool desired, string code) = quarantineRetry ? RecordFault(bot.BotId, "start-failed: " + ex.Message) : (false, "start-failed");
                bot = await PersistAsync(scope.ServiceProvider, bot with { DesiredEnabled = desired, State = ManagedPlayerbotState.Faulted, ErrorCode = code }, CancellationToken.None).ConfigureAwait(false);
                CharacterRecord? character = await scope.ServiceProvider.GetRequiredService<ICharacterStore>().GetByIdAsync(bot.CharacterId, CancellationToken.None).ConfigureAwait(false);
                PublishStopped(bot, character?.Name ?? idOrName, character?.MapId ?? 0);
            }
            if (ex is OperationCanceledException) throw;
            return new(false, "start-failed", bot?.BotId);
        }
        finally
        {
            try
            {
                if (!retained)
                {
                    if (session is not null)
                    {
                        session.Kick();
                        await session.ManagedClosed.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
                        if (bot is not null) await saves.FlushCharacterAsync(bot.CharacterId).ConfigureAwait(false);
                    }
                    await scope.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally { _operations.Release(); }
        }
    }

    public async Task<PlayerbotOperationResult> StopAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            ManagedPlayerbot? bot = await ResolveAsync(scope.ServiceProvider, idOrName, cancellationToken).ConfigureAwait(false);
            if (bot is null) return new(false, "bot-not-found");
            _quarantine.TryRemove(bot.BotId, out _);
            await StopCoreAsync(bot, false, cancellationToken).ConfigureAwait(false);
            return new(true, "stopped", bot.BotId);
        }
        finally { _operations.Release(); }
    }

    public async Task ShutdownBeforeWorldStopAsync()
    {
        _stopping = true;
        await _planningStop.CancelAsync().ConfigureAwait(false);
        if (_world is not null) _world.Updated -= Update;
        await _checkpoint.ConfigureAwait(false);
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (ActiveBot active in _active.Values.ToArray())
                await StopCoreAsync(active.Record, true, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
        if (_planner is { } planner) { _planner = null; await planner.DisposeAsync().ConfigureAwait(false); }
    }

    public Task StopAsync() => ShutdownBeforeWorldStopAsync();

    private PlayerbotBrain NewBrain(WorldSession session) => new(session, _options, _planner, _planningStop.Token);

    /// <summary>The goal the bot reports and persists: its party AI's while that drives it, otherwise the brain's.</summary>
    private static (PlayerbotGoalKind Goal, uint TargetEntry, uint QuestId) GoalOf(ActiveBot active)
        => active.PartyDriven ? (active.Party.Goal, active.Party.TargetEntry, active.Brain.QuestId)
            : (active.Brain.Goal, active.Brain.TargetEntry, active.Brain.QuestId);

    private Task<WorldSession> NewSessionAsync(Account owner, int? characterId, IServiceProvider services, CancellationToken cancellationToken)
        => WorldSession.CreateManagedAsync(owner, characterId, services, opcodes, _world!, registry, sessionOptions.Value, logger, cancellationToken);

    private async Task StopCoreAsync(ManagedPlayerbot bot, bool preserveDesired, CancellationToken cancellationToken, string? faultCode = null)
    {
        if (_active.TryGetValue(bot.BotId, out ActiveBot? active))
        {
            active.Paused = true;
            active.Brain.Stop();
            if (Interlocked.Exchange(ref active.Controller, null) is { } controller) controller.Detached(bot.BotId);
            try
            {
                active.Record = await PersistAsync(active.Scope.ServiceProvider, active.Record with
                { State = ManagedPlayerbotState.Stopping, DesiredEnabled = preserveDesired && active.Record.DesiredEnabled }, cancellationToken).ConfigureAwait(false);
                active.Session.Kick();
                await active.Session.ManagedClosed.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                await saves.FlushCharacterAsync(bot.CharacterId, cancellationToken).ConfigureAwait(false);
                bot = await PersistAsync(active.Scope.ServiceProvider, active.Record with
                {
                    DesiredEnabled = preserveDesired && active.Record.DesiredEnabled,
                    State = faultCode is null ? ManagedPlayerbotState.Stopped : ManagedPlayerbotState.Faulted,
                    ErrorCode = faultCode ?? active.Record.ErrorCode,
                    Goal = GoalOf(active).Goal, TargetEntry = GoalOf(active).TargetEntry, QuestId = GoalOf(active).QuestId,
                }, cancellationToken).ConfigureAwait(false);
                active.Record = bot;
                if (_active.TryRemove(new KeyValuePair<Guid, ActiveBot>(bot.BotId, active))) Interlocked.Increment(ref _activeVersion);
                PublishStopped(bot, active.Name, active.MapId);
                await active.Scope.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                try
                {
                    active.Record = await PersistAsync(active.Scope.ServiceProvider, active.Record with
                    { State = ManagedPlayerbotState.Faulted, DesiredEnabled = preserveDesired && active.Record.DesiredEnabled,
                        ErrorCode = "stop-incomplete" }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception failure) when (failure is not OutOfMemoryException)
                { logger.LogError("Playerbot {BotId} retains an incomplete stop ({Type})", bot.BotId, failure.GetType().Name); }
                PublishStopped(active.Record, active.Name, active.MapId);
                throw;
            }
        }
        else
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            bot = await PersistAsync(scope.ServiceProvider, bot with { DesiredEnabled = preserveDesired && bot.DesiredEnabled, State = ManagedPlayerbotState.Stopped }, cancellationToken).ConfigureAwait(false);
            CharacterRecord? character = await scope.ServiceProvider.GetRequiredService<ICharacterStore>().GetByIdAsync(bot.CharacterId, cancellationToken).ConfigureAwait(false);
            PublishStopped(bot, character?.Name ?? bot.BotId.ToString(), character?.MapId ?? 0);
        }
    }

    private void Update(uint elapsedMs)
    {
        if (_stopping || !_options.Enabled) return;
        Interlocked.Add(ref _clockMs, elapsedMs);
        int version = Volatile.Read(ref _activeVersion);
        if (version != _orderedVersion)
        {
            _ordered = _active.Values.OrderBy(b => b.Record.BotId).ToArray();
            _orderedVersion = version;
        }

        ActiveBot[] bots = _ordered;
        // Movement first, every tick, for every bot: the motion is a client's own reporting and must not wait for a
        // think, the shared action budget or the per-tick time cap below (PlayerbotMotion).
        foreach (ActiveBot active in bots)
        {
            if (active.Paused || active.Session.State != SessionState.InWorld || active.Session.Player is not { } mover) continue;
            try { PlayerbotMotion.Pump(active.Session, mover, _world!.NowMs); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Fault(active, "movement", ex); continue; }
            // Party intake, every tick for every autonomous bot (a scripted controller drains its own queue): invitations,
            // chat, loot rolls and resurrection offers are answered as they come, not when the bot's turn in the budget comes.
            if (active.Controller is not null || active.Session.State != SessionState.InWorld) continue;
            try { active.Party.Intake(mover); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Fault(active, "party", ex); }
        }
        var budget = new ManagedActionBudget(_options.MaxActionsPerTick);
        long started = Stopwatch.GetTimestamp();
        for (int i = 0; i < bots.Length && budget.Remaining > 0; i++)
        {
            ActiveBot active = bots[(_cursor + i) % bots.Length];
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 8) break;
            if (!active.Paused && active.Session.State == SessionState.InWorld)
            {
                uint sinceLast = unchecked(_world!.NowMs - active.LastUpdateMs);
                active.LastUpdateMs = _world.NowMs;
                try
                {
                    if (active.Controller is { } controller)
                    {
                        // Scripted mode: the brain is suppressed and the shared action budget does not apply.
                        active.Session.ManagedBudget = null;
                        controller.Tick(active.ControllerContext, sinceLast);
                    }
                    else if (active.Session.Player is { } driven)
                    {
                        active.Session.ManagedBudget = budget;
                        // A bot in a real player's group follows its party AI; out of it (or after its master's timeout) the
                        // brain takes over again, a fresh one: the old one's routes and targets belong to another place.
                        bool party = active.Party.Drives(driven);
                        if (active.PartyDriven && !party) ReplaceBrain(active);
                        active.PartyDriven = party;
                        if (party) active.Party.Update(driven, sinceLast);
                        else
                        {
                            active.Brain.Update(sinceLast);
                            if (active.Brain.StallCount != active.StallsLogged && active.Brain.StallReport is { } stall)
                            {
                                active.StallsLogged = active.Brain.StallCount;
                                logger.LogWarning("Playerbot {BotId} ({Name}) {Stall}; it gives up that goal", active.Record.BotId, active.Name, stall);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Fault(active, "action", ex); }
            }
            if (active.Session.Player is { } player) { active.Name = player.Name; active.MapId = player.Map?.MapId ?? player.MapId; }
        }
        if (bots.Length > 0) _cursor = (_cursor + 1) % bots.Length;
        bool changed = false;
        int running = 0;
        foreach (ActiveBot b in bots)
        {
            if (!_active.ContainsKey(b.Record.BotId)) continue;
            running++;
            PlayerbotStatus status = StatusOf(b);
            if (!ReferenceEquals(status, b.Status)) { b.Status = status; changed = true; }
        }

        lock (_snapshotGate)
        {
            if (changed || running != _builtCount || _snapshotEpoch != _builtEpoch)
            {
                PlayerbotStatus[] current = Volatile.Read(ref _snapshot);
                Volatile.Write(ref _snapshot, current.Where(s => !_active.ContainsKey(s.BotId))
                    .Concat(bots.Where(b => _active.ContainsKey(b.Record.BotId)).Select(b => b.Status!)).ToArray());
                _builtCount = running;
                _builtEpoch = _snapshotEpoch;
            }
        }
        _checkpointMs += elapsedMs;
        if (_checkpointMs >= 5000 && _checkpoint.IsCompleted)
        { _checkpointMs = 0; _checkpoint = Task.Run(CheckpointAsync); }
    }

    private async Task CheckpointAsync()
    {
        if (!await _operations.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            foreach (ActiveBot active in _active.Values.ToArray())
            {
                if (active.Record.State == ManagedPlayerbotState.Faulted) continue;
                if (active.Session.State == SessionState.Closed)
                {
                    if (Volatile.Read(ref active.FaultCode) is { } fault)
                    {
                        (bool desired, string code) = RecordFault(active.Record.BotId, fault);
                        await StopCoreAsync(active.Record, desired, CancellationToken.None, code).ConfigureAwait(false);
                    }
                    else await StopCoreAsync(active.Record, false, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }
                active.Record = await PersistAsync(active.Scope.ServiceProvider, active.Record with
                { Goal = GoalOf(active).Goal, TargetEntry = GoalOf(active).TargetEntry, QuestId = GoalOf(active).QuestId }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { logger.LogWarning(ex, "Managed playerbot checkpoint failed ({Type})", ex.GetType().Name); }
        finally { _operations.Release(); }
        await RetryQuarantinedAsync().ConfigureAwait(false);
    }

    /// <summary>A running bot's status line: the one it had when nothing in it changed (no allocation), otherwise a new one.</summary>
    private static PlayerbotStatus StatusOf(ActiveBot b)
    {
        bool closed = b.Session.State == SessionState.Closed;
        ManagedPlayerbotState state = closed ? ManagedPlayerbotState.Faulted : b.Record.State;
        (PlayerbotGoalKind goal, uint target, uint quest) = GoalOf(b);
        uint health = b.Session.Player?.Health ?? 0;
        string? error = closed ? "session-closed" : b.Record.ErrorCode ?? StallOf(b);
        string? risk = RiskOf(b);
        if (b.Status is { } last && last.BotId == b.Record.BotId && last.State == state && last.DesiredEnabled == b.Record.DesiredEnabled
            && last.Goal == goal && last.TargetEntry == target && last.QuestId == quest && last.MapId == b.MapId && last.Health == health
            && string.Equals(last.Name, b.Name, StringComparison.Ordinal) && string.Equals(last.ErrorCode, error, StringComparison.Ordinal)
            && string.Equals(last.Risk, risk, StringComparison.Ordinal))
            return last;
        return new PlayerbotStatus(b.Record.BotId, b.Name, state, b.Record.DesiredEnabled, goal, target, quest, b.MapId, health, error, risk);
    }

    /// <summary>The risk line of a running bot (its party AI's while that drives it); none for a scripted bot.</summary>
    private static string? RiskOf(ActiveBot active) => active.Controller is not null ? null
        : active.PartyDriven ? active.Party.RiskReport : active.Brain.RiskReport;

    /// <summary>A running bot's current stall (<see cref="PlayerbotStallWatch"/>), shown where a fault would be.</summary>
    private static string? StallOf(ActiveBot active) => active.PartyDriven || active.Controller is not null ? null
        : active.Brain.StallReport is { } stall ? Code(stall) : null;

    private void ReplaceBrain(ActiveBot active)
    {
        active.Brain.Stop();
        active.Brain = NewBrain(active.Session);
    }

    /// <summary>
    /// An exception out of a bot's update (world thread): log all of it, remember the fault for the checkpoint, close the session.
    /// The next checkpoint quarantines the bot (<see cref="RecordFault"/>).
    /// </summary>
    private void Fault(ActiveBot active, string what, Exception ex)
    {
        logger.LogWarning(ex, "Playerbot {BotId} {What} failed ({Type}: {Message})", active.Record.BotId, what, ex.GetType().Name, ex.Message);
        Interlocked.CompareExchange(ref active.FaultCode, $"{what}: {ex.Message}", null);
        active.Session.Kick();
    }

    /// <summary>
    /// Count one fault of a bot and schedule its retry: whether the bot stays desired, and the error code to store. The
    /// <see cref="PlayerbotOptions.MaxFaults"/>-th fault within the window disables it (no retry).
    /// </summary>
    private (bool Desired, string ErrorCode) RecordFault(Guid botId, string fault)
    {
        long now = ClockMs;
        Quarantine quarantine = _quarantine.GetOrAdd(botId, _ => new Quarantine());
        lock (quarantine)
        {
            quarantine.Faults.RemoveAll(at => now - at >= _options.FaultWindowSeconds * 1000L);
            quarantine.Faults.Add(now);
            int count = quarantine.Faults.Count;
            if (count >= _options.MaxFaults)
            {
                _quarantine.TryRemove(botId, out _);
                logger.LogWarning("Playerbot {BotId} disabled after {Count} faults within {Window} s: {Fault}",
                    botId, count, _options.FaultWindowSeconds, fault);
                return (false, Code($"disabled after {count} faults: {fault}"));
            }

            long backoffMs = Math.Min(3_600_000L, (_options.FaultBackoffSeconds * 1000L) << Math.Min(count - 1, 20));
            quarantine.RetryAtMs = now + backoffMs;
            logger.LogWarning("Playerbot {BotId} quarantined for {Seconds} s (fault {Count} of {Max}): {Fault}",
                botId, backoffMs / 1000, count, _options.MaxFaults, fault);
            return (true, Code($"quarantined (fault {count}/{_options.MaxFaults}): {fault}"));
        }

    }

    /// <summary>
    /// The stored error code (at most 128 UTF-16 units, the column's width), echoed to chat by '.playerbot' status: control
    /// characters of an exception message (newlines, tabs) become spaces, and the cut never splits a surrogate pair.
    /// </summary>
    internal static string Code(string text)
    {
        const int Max = 128;
        string clean = string.Create(text.Length, text, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++) span[i] = char.IsControl(source[i]) ? ' ' : source[i];
        });
        if (clean.Length <= Max) return clean;
        int cut = char.IsHighSurrogate(clean[Max - 1]) ? Max - 1 : Max;
        return clean[..cut];
    }

    /// <summary>Log the quarantined bots whose backoff ran out back in (checkpoint thread, outside the operation lock).</summary>
    private async Task RetryQuarantinedAsync()
    {
        long now = ClockMs;
        foreach ((Guid botId, Quarantine quarantine) in _quarantine.ToArray())
        {
            if (_stopping) return;
            lock (quarantine)
            {
                if (quarantine.RetryAtMs > now || _active.ContainsKey(botId)) continue;
                quarantine.RetryAtMs = long.MaxValue; // one retry at a time; a failed start schedules the next
            }

            // Scheduled: from here on an operator stop or start that takes the operation lock first decides (StartCoreAsync re-checks).
            logger.LogInformation("Playerbot {BotId} quarantine retry starting", botId);
            PlayerbotOperationResult result;
            try { result = await StartCoreAsync(botId.ToString(), null, CancellationToken.None, StartKind.QuarantineRetry, quarantine).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogWarning(ex, "Playerbot {BotId} quarantine retry failed", botId);
                result = new(false, "start-failed", botId);
            }

            if (result.Success)
            {
                logger.LogInformation("Playerbot {BotId} left quarantine", botId);
            }
            else if (result.Code is "playerbot-capacity" or "playerbots-stopping" or "playerbots-disabled")
            {
                lock (quarantine) quarantine.RetryAtMs = ClockMs + (_options.FaultBackoffSeconds * 1000L);
            }
            else if (result.Code != "start-failed")
            {
                // Gone, refused, cleared by an operator or already running: nothing left to retry. Only this entry is removed;
                // a newer one (a fault after an operator start) keeps its own schedule.
                _quarantine.TryRemove(new KeyValuePair<Guid, Quarantine>(botId, quarantine));
                logger.LogWarning("Playerbot {BotId} quarantine retry ended ({Code})", botId, result.Code);
            }
        }
    }

    private sealed class Quarantine
    {
        public readonly List<long> Faults = [];
        public long RetryAtMs = long.MaxValue;
    }

    private async Task ReconcileProvisioningAsync(IServiceProvider provider, CancellationToken cancellationToken)
    {
        IManagedPlayerbotProvisionStore provisions = provider.GetRequiredService<IManagedPlayerbotProvisionStore>();
        ICharacterStore characters = provider.GetRequiredService<ICharacterStore>();
        IReadOnlyList<ManagedPlayerbot> registered = await provider.GetRequiredService<IManagedPlayerbotStore>().LoadAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (ManagedPlayerbotProvision pending in await provisions.LoadPendingAsync(cancellationToken).ConfigureAwait(false))
        {
            ManagedPlayerbot? completed = registered.SingleOrDefault(b => b.BotId == pending.BotId && b.AccountId == pending.AccountId && b.AccountName == pending.AccountName);
            if (completed is not null)
            {
                CharacterRecord? character = await characters.GetByIdAsync(completed.CharacterId, cancellationToken).ConfigureAwait(false);
                if (character?.AccountId != pending.AccountId || !await provisions.CompleteAsync(pending.BotId, pending.AccountId, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("provision-reconcile-refused");
                continue;
            }
            Account? owner = await provider.GetRequiredService<IAccountStore>().FindByUsernameAsync(pending.AccountName, cancellationToken).ConfigureAwait(false);
            if (owner?.Id != pending.AccountId) throw new InvalidOperationException("provision-owner-refused");
            WorldSession session = await NewSessionAsync(owner, null, provider, cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (CharacterRecord character in await characters.GetByAccountAsync(pending.AccountId, cancellationToken).ConfigureAwait(false))
                    if (!await CharacterDeletion.TryDeleteAsync(session, (ulong)(uint)character.Id).ConfigureAwait(false))
                        throw new InvalidOperationException("provision-character-cleanup-refused");
            }
            finally
            {
                session.Kick();
                await session.ManagedClosed.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }
            if ((await characters.GetByAccountAsync(pending.AccountId, cancellationToken).ConfigureAwait(false)).Count != 0
                || !await provisions.RollbackEmptyOwnerAsync(pending.BotId, pending.AccountId, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("provision-rollback-refused");
        }
    }

    private static async Task<ManagedPlayerbot?> ResolveAsync(IServiceProvider services, string idOrName, CancellationToken cancellationToken)
    {
        IReadOnlyList<ManagedPlayerbot> bots = await services.GetRequiredService<IManagedPlayerbotStore>().LoadAllAsync(cancellationToken).ConfigureAwait(false);
        if (Guid.TryParse(idOrName, out Guid id)) return bots.SingleOrDefault(b => b.BotId == id);
        ICharacterStore characters = services.GetRequiredService<ICharacterStore>();
        foreach (ManagedPlayerbot bot in bots)
            if ((await characters.GetByIdAsync(bot.CharacterId, cancellationToken).ConfigureAwait(false))?.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase) == true) return bot;
        return null;
    }

    private static async Task<ManagedPlayerbot> PersistAsync(IServiceProvider services, ManagedPlayerbot next, CancellationToken cancellationToken)
    {
        ManagedPlayerbot updated = next with { Revision = next.Revision + 1, UpdatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        if (!await services.GetRequiredService<IManagedPlayerbotStore>().UpdateAsync(updated, next.Revision, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("managed-revision-conflict");
        return updated;
    }

    private void PublishStopped(ManagedPlayerbot bot, string name, uint mapId)
    {
        PlayerbotStatus next = new(bot.BotId, name, bot.State, bot.DesiredEnabled, bot.Goal, bot.TargetEntry, bot.QuestId, mapId, 0, bot.ErrorCode);
        lock (_snapshotGate)
        {
            PlayerbotStatus[] current = Volatile.Read(ref _snapshot);
            Volatile.Write(ref _snapshot, current.Where(s => s.BotId != bot.BotId).Append(next).ToArray());
            _snapshotEpoch++;
        }
    }

    private sealed class ActiveBot(ManagedPlayerbot record, AsyncServiceScope scope, WorldSession session, PlayerbotBrain brain,
        PlayerbotPartyAI party)
    {
        public ManagedPlayerbot Record = record;
        public AsyncServiceScope Scope { get; } = scope;
        public WorldSession Session { get; } = session;
        public PlayerbotBrain Brain = brain;
        public PlayerbotPartyAI Party { get; } = party;
        public bool PartyDriven;
        public string Name = session.Player!.Name;
        public uint MapId = session.Player!.Map!.MapId;
        public uint LastUpdateMs = session.World.NowMs;
        public volatile bool Paused;
        public IPlayerbotController? Controller;
        public string? FaultCode;
        public int StallsLogged;
        public PlayerbotStatus? Status; // world thread: the last status line (StatusOf)
        public PlayerbotControllerContext ControllerContext { get; } = new(record.BotId, session);
    }
}
