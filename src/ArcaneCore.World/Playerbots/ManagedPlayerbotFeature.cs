using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Diagnostics;
using ArcaneCore.Cryptography;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Playerbots;

/// <summary>Owns persistent autonomous P0 players, their ordinary sessions and save barriers.</summary>
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
    private WorldRuntime? _world;
    private bool _stopping;
    private int _cursor;
    private uint _checkpointMs;
    private Task _checkpoint = Task.CompletedTask;
    private PlayerbotLocalPlanner? _planner;
    private readonly CancellationTokenSource _planningStop = new();

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
        Volatile.Write(ref _snapshot, stopped.ToArray());
        if (_options.RestoreOnStartup)
            foreach (ManagedPlayerbot bot in registered.Where(b => b.DesiredEnabled).Take(_options.MaxBots))
                await StartAsync(bot.BotId.ToString(), cancellationToken).ConfigureAwait(false);
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
            return bot is null ? null : PlayerbotInspector.Capture(bot.Session, bot.Brain);
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
            if ((await store.LoadAllAsync(cancellationToken).ConfigureAwait(false)).Count >= _options.MaxBots)
                return new(false, "playerbot-capacity");
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

    public Task<PlayerbotOperationResult> StartAsync(string idOrName, CancellationToken cancellationToken = default)
        => StartCoreAsync(idOrName, null, cancellationToken);

    /// <summary>
    /// Start a bot in scripted mode: <paramref name="controller"/> replaces <see cref="PlayerbotBrain"/> from the first
    /// tick (the brain never runs). A bot that is already running is refused with "already-running"; switch it with
    /// <see cref="SetControllerAsync"/> instead.
    /// </summary>
    public Task<PlayerbotOperationResult> StartScriptedAsync(string idOrName, IPlayerbotController controller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controller);
        return StartCoreAsync(idOrName, controller, cancellationToken);
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
            if (previous is not null && !ReferenceEquals(previous, controller)) previous.Detached(botId);
            return true;
        });
    }

    /// <summary>Whether a running bot is driven by a controller instead of the brain.</summary>
    public bool IsScripted(Guid botId) => _active.TryGetValue(botId, out ActiveBot? active) && active.Controller is not null;

    /// <summary>The ordinary session of a running bot (scenario harness; null when not running).</summary>
    internal WorldSession? FindSession(Guid botId)
        => _active.TryGetValue(botId, out ActiveBot? active) && !active.Paused ? active.Session : null;

    private async Task<PlayerbotOperationResult> StartCoreAsync(string idOrName, IPlayerbotController? controller,
        CancellationToken cancellationToken)
    {
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
            if (_active.TryGetValue(bot.BotId, out ActiveBot? existing))
                return new(!existing.Paused, existing.Paused ? "stop-incomplete" : "already-running", bot.BotId);
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
            var active = new ActiveBot(bot, scope, session, new PlayerbotBrain(session, _options, _planner, _planningStop.Token))
            { Controller = controller };
            if (!_active.TryAdd(bot.BotId, active)) throw new InvalidOperationException("duplicate-bot");
            retained = true;
            logger.LogInformation("Started managed playerbot {BotId}, character {CharacterId}", bot.BotId, bot.CharacterId);
            return new(true, "started", bot.BotId, session.Player!.Name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning("Managed playerbot start failed ({Type})", ex.GetType().Name);
            if (bot is not null)
            {
                bot = await PersistAsync(scope.ServiceProvider, bot with { DesiredEnabled = false, State = ManagedPlayerbotState.Faulted, ErrorCode = "start-failed" }, CancellationToken.None).ConfigureAwait(false);
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

    private Task<WorldSession> NewSessionAsync(Account owner, int? characterId, IServiceProvider services, CancellationToken cancellationToken)
        => WorldSession.CreateManagedAsync(owner, characterId, services, opcodes, _world!, registry, sessionOptions.Value, logger, cancellationToken);

    private async Task StopCoreAsync(ManagedPlayerbot bot, bool preserveDesired, CancellationToken cancellationToken)
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
                    DesiredEnabled = preserveDesired && active.Record.DesiredEnabled, State = ManagedPlayerbotState.Stopped,
                    Goal = active.Brain.Goal, TargetEntry = active.Brain.TargetEntry, QuestId = active.Brain.QuestId,
                }, cancellationToken).ConfigureAwait(false);
                active.Record = bot;
                _active.TryRemove(new KeyValuePair<Guid, ActiveBot>(bot.BotId, active));
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
        ActiveBot[] bots = _active.Values.OrderBy(b => b.Record.BotId).ToArray();
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
                    else
                    {
                        active.Session.ManagedBudget = budget;
                        active.Brain.Update(sinceLast);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { logger.LogWarning("Playerbot {BotId} action failed ({Type})", active.Record.BotId, ex.GetType().Name); active.Session.Kick(); }
            }
            if (active.Session.Player is { } player) { active.Name = player.Name; active.MapId = player.Map?.MapId ?? player.MapId; }
        }
        if (bots.Length > 0) _cursor = (_cursor + 1) % bots.Length;
        lock (_snapshotGate)
        {
            PlayerbotStatus[] current = Volatile.Read(ref _snapshot);
            Volatile.Write(ref _snapshot, current.Where(s => !_active.ContainsKey(s.BotId)).Concat(bots.Where(b => _active.ContainsKey(b.Record.BotId)).Select(b => new PlayerbotStatus(
                b.Record.BotId, b.Name, b.Session.State == SessionState.Closed ? ManagedPlayerbotState.Faulted : b.Record.State,
                b.Record.DesiredEnabled, b.Brain.Goal, b.Brain.TargetEntry, b.Brain.QuestId, b.MapId, b.Session.Player?.Health ?? 0,
                b.Session.State == SessionState.Closed ? "session-closed" : b.Record.ErrorCode))).ToArray());
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
                { await StopCoreAsync(active.Record, false, CancellationToken.None).ConfigureAwait(false); continue; }
                active.Record = await PersistAsync(active.Scope.ServiceProvider, active.Record with
                { Goal = active.Brain.Goal, TargetEntry = active.Brain.TargetEntry, QuestId = active.Brain.QuestId }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { logger.LogWarning("Managed playerbot checkpoint failed ({Type})", ex.GetType().Name); }
        finally { _operations.Release(); }
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
        }
    }

    private sealed class ActiveBot(ManagedPlayerbot record, AsyncServiceScope scope, WorldSession session, PlayerbotBrain brain)
    {
        public ManagedPlayerbot Record = record;
        public AsyncServiceScope Scope { get; } = scope;
        public WorldSession Session { get; } = session;
        public PlayerbotBrain Brain { get; } = brain;
        public string Name = session.Player!.Name;
        public uint MapId = session.Player!.Map!.MapId;
        public uint LastUpdateMs = session.World.NowMs;
        public volatile bool Paused;
        public IPlayerbotController? Controller;
        public PlayerbotControllerContext ControllerContext { get; } = new(record.BotId, session);
    }
}
