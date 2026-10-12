using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Diagnostics;
using ArcaneCore.Cryptography;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Playerbots.Battlegrounds;
using ArcaneCore.World.Playerbots.Groups;
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
public sealed partial class ManagedPlayerbotFeature(IServiceProvider services, ILogger<ManagedPlayerbotFeature> logger) : IWorldFeature, IPlayerbotService
{
    private readonly PlayerbotOptions _options = services.GetService<IOptions<PlayerbotOptions>>()?.Value ?? new();
    private IServiceScopeFactory scopes => services.GetRequiredService<IServiceScopeFactory>();
    private SessionRegistry registry => services.GetRequiredService<SessionRegistry>();
    private OpcodeTable opcodes => services.GetService<OpcodeTable>() ?? WorldServiceCollectionExtensions.BuildOpcodeTable();
    private IOptions<WorldSessionOptions> sessionOptions => services.GetService<IOptions<WorldSessionOptions>>() ?? Options.Create(new WorldSessionOptions());
    private CharacterSaveQueue saves => services.GetRequiredService<CharacterSaveQueue>();

    /// <summary>Bot-led groups for group content (<see cref="PlayerbotGroupCoordinator"/>; absent in a minimal host).</summary>
    private PlayerbotGroupCoordinator? coordinator => _coordinator ??= services.GetService<PlayerbotGroupCoordinator>();
    private PlayerbotGroupCoordinator? _coordinator;

    /// <summary>Bots in battlegrounds (<see cref="PlayerbotBattlegroundCoordinator"/>; absent in a minimal host).</summary>
    private PlayerbotBattlegroundCoordinator? battlegrounds => _battlegrounds ??= services.GetService<PlayerbotBattlegroundCoordinator>();
    private PlayerbotBattlegroundCoordinator? _battlegrounds;
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
    private readonly PlayerbotThinkRotation _rotation = new();
    // Per-think attribution (PlayerbotThinkProbe, PlayerbotSlowThinkLog): which bot, goal and phase a slow think spent its time in.
    private readonly PlayerbotThinkProbe _probe = new();
    private PlayerbotSlowThinkLog? _slowThinks;

    /// <summary>
    /// Called after every think with the bot and the think's probe (world thread; benchmarks and tests). The probe is valid only
    /// during the call.
    /// </summary>
    internal Action<Guid, PlayerbotThinkProbe>? ThinkObserver { get; set; }

    /// <summary>The slow-think log (null before the first think).</summary>
    internal PlayerbotSlowThinkLog? SlowThinks => _slowThinks;
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
    private Chat.PlayerbotChat? _chat;

    // The persisted home areas (managed_playerbot_home), by bot id: loaded at startup, written by .playerbot home.
    private readonly ConcurrentDictionary<Guid, PlayerbotHome> _homes = new();
    private readonly ConcurrentDictionary<Guid, PlayerbotSetAside[]> _loadedSetAsides = new();
    private readonly TimeProvider _setAsideTime = services.GetService<TimeProvider>() ?? TimeProvider.System;
    private int _savedSharedSetAsideVersion;
    private bool _homeStore;
    private Chat.HttpBotChatClient? _chatClient;

    /// <summary>World time this feature has seen, in ms (the fault backoff and window run on it).</summary>
    private long ClockMs => Interlocked.Read(ref _clockMs);

    /// <summary>The bots' chat service (null before <see cref="Attach"/> and after shutdown; <c>.playerbot chat status</c>).</summary>
    internal Chat.PlayerbotChat? Chat => _chat;

    public void Attach(WorldRuntime world)
    {
        _options.Validate();
        if (_options.AllowLocalLlm) _planner = new PlayerbotLocalPlanner(_options);
        _chat = new Chat.PlayerbotChat(_options,
            services.GetService<Chat.IBotChatClient>() ?? (_chatClient = new Chat.HttpBotChatClient()),
            services.GetService<Chat.IBotChatEnvironment>() ?? new Chat.ProcessBotChatEnvironment(),
            services.GetService<TimeProvider>() ?? TimeProvider.System, logger);
        if (_options.Enabled) _chat.LogConfiguration();
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
        if (scope.ServiceProvider.GetService<IManagedPlayerbotSetAsideStore>() is { } setAsides)
        {
            IReadOnlyList<PlayerbotSetAside> rows = await setAsides.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            foreach (IGrouping<Guid, PlayerbotSetAside> group in rows.Where(row => row.BotId != Guid.Empty).GroupBy(row => row.BotId))
                _loadedSetAsides[group.Key] = [.. group];
            await _world!.InvokeAsync(() =>
            {
                _sharedSetAsides.Restore(rows.Where(row => row.BotId == Guid.Empty), _world.NowMs,
                    _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds());
                return true;
            }, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _savedSharedSetAsideVersion, _sharedSetAsides.Version);
        }
        if (scope.ServiceProvider.GetService<IManagedPlayerbotHomeStore>() is { } homeStore)
        {
            _homeStore = true;
            foreach (PlayerbotHome home in await homeStore.LoadAllAsync(cancellationToken).ConfigureAwait(false))
                _homes[home.BotId] = home;
        }

        var stopped = new List<PlayerbotStatus>();
        var orphans = new HashSet<Guid>();
        foreach (ManagedPlayerbot bot in registered)
        {
            CharacterRecord? character = await scope.ServiceProvider.GetRequiredService<ICharacterStore>()
                .GetByIdAsync(bot.CharacterId, cancellationToken).ConfigureAwait(false);
            if (character is null)
            {
                // A registry row whose character is gone owns nothing; leaving it would hold a MaxRegisteredBots slot for good.
                await scope.ServiceProvider.GetRequiredService<IManagedPlayerbotStore>()
                    .DeleteAsync(bot.BotId, bot.AccountId, bot.CharacterId, cancellationToken).ConfigureAwait(false);
                _homes.TryRemove(bot.BotId, out _);
                orphans.Add(bot.BotId);
                continue;
            }
            stopped.Add(new(bot.BotId, character.Name, ManagedPlayerbotState.Stopped, bot.DesiredEnabled,
                bot.Goal, bot.TargetEntry, bot.QuestId, character.MapId, 0, bot.ErrorCode, Home: StoppedHome(bot.BotId)));
        }
        lock (_snapshotGate)
        {
            Volatile.Write(ref _snapshot, stopped.ToArray());
            _snapshotEpoch++;
        }
        // A restore that cannot log a bot in is a fault, not an operator decision: the bot stays desired and is retried (quarantine).
        if (_options.RestoreOnStartup)
            foreach (ManagedPlayerbot bot in registered.Where(b => b.DesiredEnabled && !orphans.Contains(b.BotId)).Take(_options.MaxBots))
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
            if (bot is null) return null;
            PlayerbotInspection? inspection = PlayerbotInspector.Capture(bot.Session, bot.Brain, bot.PartyDriven ? bot.Party : null);
            return inspection is null ? null : inspection with { Group = coordinator?.Describe(bot.Record.BotId) ?? battlegrounds?.Describe(bot.Record.BotId) };
        }).WaitAsync(cancellationToken);
    }

    public async Task<PlayerbotOperationResult> CreateAsync(string name, byte race, byte characterClass,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || _stopping) return new(false, "playerbots-disabled");
        if (ResetInProgress) return new(false, "fleet-reset-running");
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        Account? spare = null;
        try
        {
            if (_stopping) return new(false, "playerbots-stopping");
            if (ResetInProgress) return new(false, "fleet-reset-running");
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            await ReconcileProvisioningAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            IManagedPlayerbotStore store = scope.ServiceProvider.GetRequiredService<IManagedPlayerbotStore>();
            IReadOnlyList<ManagedPlayerbot> registered = await store.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            // Registration is bounded by MaxRegisteredBots; MaxBots bounds only the bots running at once (StartCoreAsync), so a
            // stopped bot never takes a running slot (the live stress test of 2026-10-08 found 5 stopped bots blocking creation).
            if (registered.Count >= _options.MaxRegisteredBots)
                return new(false, "playerbot-registry-full");
            ICharacterStore characters = scope.ServiceProvider.GetRequiredService<ICharacterStore>();
            if (await characters.IsNameTakenAsync(name, cancellationToken).ConfigureAwait(false)) return new(false, "name-in-use");
            Guid botId = Guid.NewGuid();
            // The empty account of a bot a fleet reset deleted is reused (the provisioning journal is for NEW accounts only).
            spare = await ClaimSpareAccountAsync(scope.ServiceProvider, registered, cancellationToken).ConfigureAwait(false);
            Account owner;
            if (spare is not null) owner = spare;
            else
            {
                // Credentials are random and discarded; only salt/verifier persist. These accounts cannot be adopted by name.
                string username = "PB" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
                byte[] salt = RandomNumberGenerator.GetBytes(32);
                string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
                owner = await scope.ServiceProvider.GetRequiredService<IManagedPlayerbotProvisionStore>().CreateAsync(botId, new Account
                {
                    Username = username, Salt = salt,
                    Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, username, password), 32),
                    Security = AccountSecurity.Player,
                }, cancellationToken).ConfigureAwait(false);
            }

            WorldSession session = await NewSessionAsync(owner, null, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            try
            {
                var packet = new PacketWriter();
                packet.WriteCString(name); packet.WriteByte(race); packet.WriteByte(characterClass);
                for (int i = 0; i < 8; i++) packet.WriteByte(0);
                // The character handler answers with SMSG_CHAR_CREATE: keep its result so a refusal can say why (name rule, limit, ...).
                int createResult = -1;
                session.ManagedPacketObserver = (opcode, payload) =>
                {
                    if (opcode == WorldOpcode.SmsgCharCreate && payload.Length > 0) Volatile.Write(ref createResult, payload[0]);
                };
                await session.DispatchManagedSessionAsync(WorldOpcode.CmsgCharCreate, packet.ToArray()).ConfigureAwait(false);
                session.ManagedPacketObserver = null;
                CharacterRecord? character = (await characters.GetByAccountAsync(owner.Id, cancellationToken).ConfigureAwait(false)).SingleOrDefault();
                if (character is null) throw new PlayerbotCreateRefusedException(DescribeCreateRefusal(Volatile.Read(ref createResult)));
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var bot = new ManagedPlayerbot(botId, owner.Id, character.Id, owner.Username, false,
                    ManagedPlayerbotState.Stopped, PlayerbotGoalKind.Explore, 0, 0, 0, now, now);
                await store.CreateAsync(bot, cancellationToken).ConfigureAwait(false);
                if (spare is null && !await scope.ServiceProvider.GetRequiredService<IManagedPlayerbotProvisionStore>()
                    .CompleteAsync(botId, owner.Id, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("provision-completion-refused");
                spare = null; // in use now
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
            // A refusal of the character handler names its reason; any other failure names the exception and its message (the reason
            // used to be the exception type alone, so "InvalidOperationException" was all an operator saw of a name with a digit).
            string reason = ex is PlayerbotCreateRefusedException refused ? refused.Reason : $"{ex.GetType().Name}: {ex.Message}";
            logger.LogWarning("Managed playerbot creation of '{Name}' failed: {Reason}", name, reason);
            // A reused account has no provisioning journal, so what this attempt put on it is removed here (cancellation included).
            if (spare is not null) await RetireOrRequeueSpareAsync(spare).ConfigureAwait(false);
            await using AsyncServiceScope repair = scopes.CreateAsyncScope();
            try { await ReconcileProvisioningAsync(repair.ServiceProvider, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            { logger.LogError("Playerbot provisioning retained for reconciliation ({Type}: {Message})", failure.GetType().Name, failure.Message); }
            if (ex is OperationCanceledException) throw;
            return new(false, ex is PlayerbotCreateRefusedException ? $"character-create-refused: {reason}" : $"create-failed: {reason}", null, name);
        }
        finally { _operations.Release(); }
    }

    /// <summary>
    /// A creation on a reused account failed. The account was empty when it was claimed, so any character on it came from this attempt
    /// and no registry row names it (unless one was stored after all, then nothing is touched): delete it like a player's delete
    /// would, then make the account spare again. If a character cannot be deleted the account is retired (not offered again) and the
    /// character is logged. Runs with no cancellation token on purpose: a shutdown mid-creation is exactly when this matters.
    /// </summary>
    private async Task RetireOrRequeueSpareAsync(Account spare)
    {
        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            IServiceProvider provider = scope.ServiceProvider;
            IReadOnlyList<CharacterRecord> held = await provider.GetRequiredService<ICharacterStore>().GetByAccountAsync(spare.Id, CancellationToken.None).ConfigureAwait(false);
            if (held.Count != 0)
            {
                IReadOnlyList<ManagedPlayerbot> registered = await provider.GetRequiredService<IManagedPlayerbotStore>().LoadAllAsync(CancellationToken.None).ConfigureAwait(false);
                Account? owner = await provider.GetRequiredService<IAccountStore>().FindByUsernameAsync(spare.Username, CancellationToken.None).ConfigureAwait(false);
                if (registered.Any(b => b.AccountId == spare.Id) || owner is null || owner.Id != spare.Id
                    || owner.Status != AccountStatus.Active || owner.Security != AccountSecurity.Player || owner.SessionKey is not null) return; // in use, or not ours: leave it alone
                WorldSession session = await NewSessionAsync(owner, null, provider, CancellationToken.None).ConfigureAwait(false);
                try
                {
                    foreach (CharacterRecord character in held)
                        if (!await CharacterDeletion.TryDeleteAsync(session, (ulong)(uint)character.Id).ConfigureAwait(false))
                            logger.LogError("Playerbot creation on reused account '{Account}' failed and character {CharacterId} could not be deleted", spare.Username, character.Id);
                }
                finally
                {
                    session.Kick();
                    try { await session.ManagedClosed.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false); }
                    catch (TimeoutException) { logger.LogWarning("Playerbot cleanup session of '{Account}' did not close within 10 s", spare.Username); }
                }

                if ((await provider.GetRequiredService<ICharacterStore>().GetByAccountAsync(spare.Id, CancellationToken.None).ConfigureAwait(false)).Count != 0)
                {
                    logger.LogError("Playerbot cleanup of reused account '{Account}' left a character behind; the account is retired", spare.Username);
                    return;
                }
            }

            _spareAccounts.Enqueue(new SpareAccount(spare.Id, spare.Username));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Playerbot cleanup of reused account '{Account}' failed; the account is retired ({Type}: {Message})", spare.Username, ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>
    /// The next account a fleet reset left empty that is still safe to reuse, or null. Every candidate is read again: the account must
    /// be the same plain, active player account without a login key, hold no character, and be named by no registry row. Operation lock held.
    /// </summary>
    private async Task<Account?> ClaimSpareAccountAsync(IServiceProvider provider, IReadOnlyList<ManagedPlayerbot> registered, CancellationToken cancellationToken)
    {
        if (_spareAccounts.IsEmpty) return null;
        IAccountStore accounts = provider.GetRequiredService<IAccountStore>();
        ICharacterStore characters = provider.GetRequiredService<ICharacterStore>();
        while (_spareAccounts.TryDequeue(out SpareAccount? candidate))
        {
            Account? account = await accounts.FindByUsernameAsync(candidate.Name, cancellationToken).ConfigureAwait(false);
            if (account is null || account.Id != candidate.AccountId || account.Status != AccountStatus.Active
                || account.Security != AccountSecurity.Player || account.SessionKey is not null) continue;
            if (registered.Any(b => b.AccountId == account.Id)) continue;
            if ((await characters.GetByAccountAsync(account.Id, cancellationToken).ConfigureAwait(false)).Count != 0) continue;
            return account;
        }

        return null;
    }

    /// <summary>The character handler refused the bot's character; <see cref="Reason"/> says why.</summary>
    private sealed class PlayerbotCreateRefusedException(string reason) : InvalidOperationException(reason)
    {
        public string Reason { get; } = reason;
    }

    /// <summary>Why <c>CMSG_CHAR_CREATE</c> was refused, from its <c>SMSG_CHAR_CREATE</c> result (-1: no answer came).</summary>
    internal static string DescribeCreateRefusal(int result)
    {
        if (result < 0) return "no SMSG_CHAR_CREATE answer";
        var code = (CharResult)result;
        string text = code switch
        {
            CharResult.CharNameNoName => "no name, or a name longer than the limit",
            CharResult.CharNameTooShort => "the name is too short",
            CharResult.CharNameTooLong => "the name is too long",
            CharResult.CharNameInvalidCharacter => "the name is not well-formed text",
            CharResult.CharNameMixedLanguages => "the name may contain only letters of the realm's alphabet (no digits, symbols or mixed alphabets)",
            CharResult.CharNameProfane => "the name is profane",
            CharResult.CharNameReserved => "the name is reserved",
            CharResult.CharNameInvalidApostrophe => "the name has an apostrophe where none is allowed",
            CharResult.CharNameMultipleApostrophes => "the name has more than one apostrophe",
            CharResult.CharNameThreeConsecutive => "the name has three identical letters in a row",
            CharResult.CharNameInvalidSpace => "the name has a space",
            CharResult.CharNameFailure => "the name is not allowed",
            CharResult.CharCreateNameInUse => "the name is in use",
            CharResult.CharCreateDisabled => "character creation is disabled",
            CharResult.CharCreatePvpTeamsViolation => "the race's faction is not allowed (PvP realm team rule)",
            CharResult.CharCreateServerLimit => "the server's character limit is reached",
            CharResult.CharCreateAccountLimit => "the account's character limit is reached",
            CharResult.CharCreateSuccess => "the handler reported success but stored no character",
            _ => "character creation failed",
        };
        return Enum.IsDefined(code) ? $"{text} ({code})" : $"{text} (result 0x{result:X2})";
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
            if (controller is not null && active.GroupDriven)
            {
                // A controller takes over a member of a bot-led group: it leaves the group (and the brain resumes when it detaches).
                coordinator?.Forget(botId);
                active.GroupDriven = false;
                active.Brain.ResumeAfterGroup();
            }
            if (controller is not null)
            {
                // A controller takes over a bot with battleground business: the coordinator lets it go where it stands (the server keeps
                // its queue or match; the controller decides), and the brain resumes when the controller detaches.
                battlegrounds?.Forget(botId);
                if (active.BattlegroundDriven)
                {
                    active.BattlegroundDriven = false;
                    active.Brain.ResumeAfterGroup();
                }
            }
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

    /// <summary>Whether the bot is in the active set (test seam; <see cref="FindBrain"/> also hides a paused one).</summary>
    internal bool IsActive(Guid botId) => _active.ContainsKey(botId);

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

    /// <summary>Whether homes can be set: the characters database provides <see cref="IManagedPlayerbotHomeStore"/>.</summary>
    internal bool HomesAvailable => _homeStore;

    /// <summary>The persisted home of a bot (tests and inspection), null when it has none.</summary>
    internal PlayerbotHome? HomeOf(Guid botId) => _homes.GetValueOrDefault(botId);

    /// <summary>Whether a home may be set on <paramref name="mapId"/>: the maps bots may enter (<see cref="PlayerbotOptions.AllowedMaps"/>).</summary>
    internal bool AllowsHomeMap(uint mapId) => PlayerbotMapPolicy.Allows(_options.AllowedMaps, mapId, null, current: false);

    /// <summary>
    /// The bots a <c>.playerbot home</c> selector names (<see cref="PlayerbotHomeSelector"/>): one bot by id or name, or every bot
    /// whose name starts with the prefix, running or not. False for a selector that is none of those.
    /// </summary>
    internal bool TryMatchBots(string selector, out IReadOnlyList<PlayerbotStatus> matches)
    {
        matches = [];
        if (!PlayerbotHomeSelector.TryParse(selector, out PlayerbotHomeSelector parsed)) return false;
        matches = [.. Snapshot().Where(parsed.Matches).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
        return true;
    }

    /// <summary>Where a running bot stands (world thread; false when it is not running or has no map).</summary>
    internal bool TryGetPosition(Guid botId, out uint mapId, out System.Numerics.Vector3 position)
    {
        mapId = 0;
        position = default;
        if (!_active.TryGetValue(botId, out ActiveBot? active) || active.Paused
            || active.Session.Player is not { Map: { } map } player) return false;
        mapId = map.MapId;
        position = new(player.X, player.Y, player.Z);
        return true;
    }

    /// <summary>One bot's home centre for <see cref="SetHomesAsync"/>.</summary>
    internal readonly record struct HomeTarget(Guid BotId, uint MapId, System.Numerics.Vector3 Centre);

    /// <summary>What <see cref="SetHomesAsync"/> did: homes stored and applied, and bots it could not store a home for.</summary>
    internal readonly record struct HomeOutcome(int Set, int Failed);

    /// <summary>
    /// The zone lookup of a map for the homes' zone clip (<see cref="PlayerbotZoneLookup"/>); null reads the world's terrain
    /// (<see cref="PlayerbotZoneLookup.FromTerrain"/>). Tests without terrain data set a fixture. World thread.
    /// </summary>
    internal Func<uint, PlayerbotZoneLookup>? ZoneLookups { get; set; }

    // One lookup per map, shared by the bots homed there; built on any thread, read on the world thread.
    private readonly ConcurrentDictionary<uint, PlayerbotZoneLookup> _zoneLookups = new();

    /// <summary>
    /// The AreaTable zone at a home centre (<see cref="PlayerbotZoneLookup"/>, the terrain lookup <c>Map.GetZoneAndAreaId</c> uses);
    /// 0 when the terrain does not know it. World thread: <c>.playerbot home</c> refuses a zone-clipped home whose centre has no
    /// known zone (it would contain nothing).
    /// </summary>
    internal uint ZoneAt(uint mapId, System.Numerics.Vector3 position) => ZoneLookupOf(mapId)?.ZoneAt(position.X, position.Y, position.Z) ?? 0;

    private PlayerbotZoneLookup? ZoneLookupOf(uint mapId)
    {
        if (_zoneLookups.TryGetValue(mapId, out PlayerbotZoneLookup? known)) return known;
        PlayerbotZoneLookup? lookup = ZoneLookups is { } fixture ? fixture(mapId)
            : _world is { } world ? PlayerbotZoneLookup.FromTerrain(ArcaneCore.Game.Maps.Templates.WorldMaps.Of(world).Terrain, mapId) : null;
        return lookup is null ? null : _zoneLookups.GetOrAdd(mapId, lookup);
    }

    /// <summary>
    /// The area-level lookup of a map for the danger check of an Auto walk home (<see cref="PlayerbotAreaLevelLookup"/>); null reads
    /// the world's terrain (<see cref="PlayerbotAreaLevelLookup.FromTerrain"/>). Tests without terrain data set a fixture. World thread.
    /// </summary>
    internal Func<uint, PlayerbotAreaLevelLookup>? AreaLevelLookups { get; set; }

    // One lookup per map, shared by the bots homed there; built on any thread, read on the world thread.
    private readonly ConcurrentDictionary<uint, PlayerbotAreaLevelLookup> _levelLookups = new();

    private PlayerbotAreaLevelLookup? AreaLevelLookupOf(uint mapId)
    {
        if (_levelLookups.TryGetValue(mapId, out PlayerbotAreaLevelLookup? known)) return known;
        PlayerbotAreaLevelLookup? lookup = AreaLevelLookups is { } fixture ? fixture(mapId)
            : _world is { } world ? PlayerbotAreaLevelLookup.FromTerrain(ArcaneCore.Game.Maps.Templates.WorldMaps.Of(world).Terrain, mapId) : null;
        return lookup is null ? null : _levelLookups.GetOrAdd(mapId, lookup);
    }

    /// <summary>
    /// Store the homes (characters schema 56; the zone clip, schema 58) and apply them to the running brains on the world thread. A
    /// home that cannot be stored is not applied (fail closed) and counted as failed. Throws when no home store is available or the
    /// radius is out of bounds. <paramref name="zoneClipped"/> clips each home to the zone of its centre (the command's default).
    /// </summary>
    internal async Task<HomeOutcome> SetHomesAsync(IReadOnlyList<HomeTarget> targets, float radius, string? place,
        CancellationToken cancellationToken = default, bool zoneClipped = true, bool logFailures = true)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (_world is not { } world) throw new InvalidOperationException("playerbot-home-unavailable");
        if (!float.IsFinite(radius) || radius < PlayerbotHomeBounds.MinRadius || radius > PlayerbotHomeBounds.MaxRadius)
            throw new ArgumentOutOfRangeException(nameof(radius));
        string? cleanPlace = place is null ? null : place.Length > PlayerbotHomeBounds.MaxPlaceLength ? place[..PlayerbotHomeBounds.MaxPlaceLength] : place;
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IManagedPlayerbotHomeStore store = scope.ServiceProvider.GetService<IManagedPlayerbotHomeStore>()
            ?? throw new InvalidOperationException("playerbot-home-unavailable");
        IManagedPlayerbotStore bots = scope.ServiceProvider.GetRequiredService<IManagedPlayerbotStore>();
        var saved = new List<PlayerbotHome>(targets.Count);
        int failed = 0;
        foreach (HomeTarget target in targets)
        {
            try
            {
                ManagedPlayerbot? bot = await bots.FindAsync(target.BotId, cancellationToken).ConfigureAwait(false);
                if (bot is null) { failed++; continue; }
                var home = new PlayerbotHome(target.BotId, bot.CharacterId, target.MapId, target.Centre.X, target.Centre.Y, target.Centre.Z,
                    radius, cleanPlace, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), zoneClipped);
                await store.SetAsync(home, cancellationToken).ConfigureAwait(false);
                saved.Add(home);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                failed++;
                if (logFailures) logger.LogWarning(ex, "Playerbot home of {BotId} was not stored ({Type}: {Message})", target.BotId, ex.GetType().Name, ex.Message);
            }
        }

        foreach (PlayerbotHome home in saved) _homes[home.BotId] = home;
        if (saved.Count != 0)
        {
            await world.InvokeAsync(() =>
            {
                foreach (PlayerbotHome home in saved)
                    if (_active.TryGetValue(home.BotId, out ActiveBot? active) && !active.Paused) active.Brain.Home = AreaOf(home.BotId);
                lock (_snapshotGate) _snapshotEpoch++;
                return true;
            }).ConfigureAwait(false);
        }

        return new(saved.Count, failed);
    }

    /// <summary>
    /// Raised before an operator's <see cref="ClearHomesAsync"/> removes homes, with the bots whose home is a population start home
    /// (<see cref="Population.PopulationStartAreas"/>): the population must not give those bots one again.
    /// </summary>
    internal event Action<IReadOnlyList<Guid>>? HomesClearedByOperator;

    /// <summary>Remove the homes of these bots (store and running brains); returns how many bots had one. <paramref name="byOperator"/>: the command, as opposed to the population's own graduation.</summary>
    internal async Task<int> ClearHomesAsync(IReadOnlyList<Guid> botIds, CancellationToken cancellationToken = default, bool byOperator = true)
    {
        ArgumentNullException.ThrowIfNull(botIds);
        if (byOperator && HomesClearedByOperator is { } handler)
        {
            Guid[] start = [.. botIds.Where(id => _homes.TryGetValue(id, out PlayerbotHome? h) && Population.PopulationStartAreas.TryParse(h.Place, out _, out _))];
            if (start.Length != 0) handler(start);
        }

        if (_world is not { } world) throw new InvalidOperationException("playerbot-home-unavailable");
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IManagedPlayerbotHomeStore store = scope.ServiceProvider.GetService<IManagedPlayerbotHomeStore>()
            ?? throw new InvalidOperationException("playerbot-home-unavailable");
        int cleared = 0;
        List<Guid> done = new(botIds.Count);
        try
        {
            foreach (Guid botId in botIds)
            {
                bool stored = await store.ClearAsync(botId, cancellationToken).ConfigureAwait(false);
                done.Add(botId);
                if (_homes.TryRemove(botId, out _) || stored) cleared++;
            }
        }
        finally
        {
            // A store failure partway must not leave the bots already cleared with a live brain home (they would keep walking home
            // while status shows no home=): the world-thread step always runs for the ids done so far; the failure then propagates.
            if (done.Count != 0)
                await world.InvokeAsync(() =>
                {
                    foreach (Guid botId in done)
                        if (_active.TryGetValue(botId, out ActiveBot? active)) active.Brain.Home = null;
                    lock (_snapshotGate) _snapshotEpoch++;
                    return true;
                }).ConfigureAwait(false);
        }

        return cleared;
    }

    /// <summary>
    /// The party AI of a running bot whether or not it drives the bot (null when not running): the persona and the chat delivery of the
    /// bot's own announcements (<see cref="BotSocial.PlayerbotSocialFeature"/>).
    /// </summary>
    internal PlayerbotPartyAI? FindPartyAI(Guid botId)
        => _active.TryGetValue(botId, out ActiveBot? active) && !active.Paused ? active.Party : null;

    /// <summary>The ordinary session of a running bot (scenario harness; null when not running).</summary>
    internal WorldSession? FindSession(Guid botId)
        => _active.TryGetValue(botId, out ActiveBot? active) && !active.Paused ? active.Session : null;

    /// <summary>
    /// <c>.playerbot follow|stay</c> (world thread): leash the running autonomous bot to <paramref name="master"/>, a real player, with
    /// no group (<see cref="PlayerbotPartyAI.Leash"/>). A member of a bot-led group leaves it first, exactly as when a controller takes
    /// the bot over (<see cref="SetControllerAsync"/>). The bot is marked party-driven at once, so the bot-led group matching of the
    /// same tick cannot take it back before its next think; a bot with battleground business is no longer free, so the battlegrounds
    /// coordinator releases it at its next update (leaving its queue or match), before the think that would follow it.
    /// </summary>
    internal PlayerbotOperationResult Leash(Guid botId, Player master, bool stay)
    {
        ArgumentNullException.ThrowIfNull(master);
        if (_world is not { } world || !world.IsWorldThread) throw new InvalidOperationException("playerbot-leash-thread");
        if (!_active.TryGetValue(botId, out ActiveBot? bot) || bot.Paused || bot.Session.State != SessionState.InWorld
            || bot.Session.Player is not { } player)
            return new(false, "bot-not-running", botId);
        if (bot.Controller is not null) return new(false, "bot-scripted", botId, player.Name);
        if (ReferenceEquals(player, master)) return new(false, "leash-refused", botId, player.Name);
        if (bot.GroupDriven)
        {
            coordinator?.Forget(botId);
            bot.GroupDriven = false;
            bot.Brain.ResumeAfterGroup();
        }

        bot.Session.ManagedBudget = null;
        bot.Party.Leash(player, master, stay);
        bot.PartyDriven = true;
        return new(true, stay ? "staying" : "following", botId, player.Name);
    }

    /// <summary>
    /// <c>.playerbot dismiss</c> (world thread): end a leash. The party AI lets go now; the bot's next think finds it neither
    /// grouped nor leashed and gives it a fresh brain (<see cref="UpdateAutonomous"/>).
    /// </summary>
    internal PlayerbotOperationResult Unleash(Guid botId)
    {
        if (_world is not { } world || !world.IsWorldThread) throw new InvalidOperationException("playerbot-leash-thread");
        if (!_active.TryGetValue(botId, out ActiveBot? bot) || bot.Paused || bot.Session.State != SessionState.InWorld
            || bot.Session.Player is not { } player)
            return new(false, "bot-not-running", botId);
        if (!bot.Party.IsLeashed) return new(false, "not-leashed", botId, player.Name);
        bot.Party.Unleash(player);
        return new(true, "dismissed", botId, player.Name);
    }

    /// <summary>Who starts a bot: an operator (clears the quarantine), the startup restore, or a quarantine retry.</summary>
    private enum StartKind { Operator, Restore, QuarantineRetry }

    /// <param name="retrying">For <see cref="StartKind.QuarantineRetry"/>: the quarantine entry the retry was scheduled from.</param>
    private async Task<PlayerbotOperationResult> StartCoreAsync(string idOrName, IPlayerbotController? controller,
        CancellationToken cancellationToken, StartKind kind, Quarantine? retrying = null)
    {
        bool quarantineRetry = kind != StartKind.Operator;
        if (!_options.Enabled || _stopping) return new(false, "playerbots-disabled");
        if (ResetInProgress) return new(false, "fleet-reset-running");
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        AsyncServiceScope scope = scopes.CreateAsyncScope();
        WorldSession? session = null;
        bool retained = false;
        ManagedPlayerbot? bot = null;
        try
        {
            if (_stopping) return new(false, "playerbots-stopping");
            if (ResetInProgress) return new(false, "fleet-reset-running");
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
                && session.Player is { Map: not null } player && PlayerbotMapPolicy.MayStayOnMap(player, _options))
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            if (!entered) throw new InvalidOperationException("login-refused");
            bot = await PersistAsync(scope.ServiceProvider, bot with { State = ManagedPlayerbotState.Running }, cancellationToken).ConfigureAwait(false);
            var party = new PlayerbotPartyAI(session, _options, _chat, bot.BotId)
            {
                // A bot-led group's invitations and loot rolls (PlayerbotGroupCoordinator); everything else is the party AI's own rules.
                AcceptsBotGroup = (member, inviter) => coordinator?.AcceptsInvite(member, inviter) == true,
                GroupLootVote = (member, item) => coordinator?.LootVote(member, item),
            };
            var active = new ActiveBot(bot, scope, session, NewBrain(session, bot.BotId), party)
            { Controller = controller };
            // The chat persona's goal: the bot-led group's while the coordinator drives the bot, otherwise the brain's (GoalOf).
            active.Party.BrainGoal = () => { var goal = GoalOf(active); return (goal.Goal, goal.QuestId); };
            if (!_active.TryAdd(bot.BotId, active)) throw new InvalidOperationException("duplicate-bot");
            _loadedSetAsides.TryRemove(bot.BotId, out _);
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
                        if (bot is not null) await FlushBoundedAsync(bot.CharacterId, CancellationToken.None).ConfigureAwait(false);
                    }
                    await scope.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally { _operations.Release(); }
        }
    }

    /// <summary>
    /// Wait for a bot character's save barrier, but not forever: the caller holds <c>_operations</c>, and while another writer holds the database the
    /// save queue retains the snapshot and retries it, which would otherwise stall every bot operation. The snapshot is not lost on a timeout.
    /// </summary>
    private async Task FlushBoundedAsync(int characterId, CancellationToken cancellationToken)
    {
        try
        {
            await saves.FlushCharacterAsync(characterId, cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Managed playerbot character {CharacterId}: the save barrier did not complete in 15 s (database busy?); continuing, the snapshot stays queued", characterId);
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

    /// <summary>
    /// Delete a stopped bot: its character through the ordinary character-deletion path (which also clears the registry, member and
    /// home rows in the same transaction in production), then whatever is left of the registry row, then the in-memory records. The
    /// character goes first, never the row: a row without a character is recoverable, a character without a row is not owned by
    /// anything. The owner account stays. Refused while the bot runs.
    /// </summary>
    public async Task<PlayerbotOperationResult> DeleteAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || _stopping) return new(false, "playerbots-disabled");
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping) return new(false, "playerbots-disabled");
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            ManagedPlayerbot? bot = await ResolveAsync(scope.ServiceProvider, idOrName, cancellationToken).ConfigureAwait(false);
            if (bot is null) return new(false, "bot-not-found");
            if (_active.ContainsKey(bot.BotId)) return new(false, "bot-running", bot.BotId);
            Account? owner = await scope.ServiceProvider.GetRequiredService<IAccountStore>()
                .FindByUsernameAsync(bot.AccountName, cancellationToken).ConfigureAwait(false);
            if (owner?.Id != bot.AccountId || owner.Status != AccountStatus.Active
                || owner.Security != AccountSecurity.Player || owner.SessionKey is not null)
                return new(false, "owner-refused", bot.BotId);

            if (await scope.ServiceProvider.GetRequiredService<ICharacterStore>()
                .GetByIdAsync(bot.CharacterId, cancellationToken).ConfigureAwait(false) is not null)
            {
                WorldSession session = await NewSessionAsync(owner, null, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!await CharacterDeletion.TryDeleteAsync(session, (ulong)(uint)bot.CharacterId).ConfigureAwait(false))
                        return new(false, "character-delete-refused", bot.BotId);
                }
                finally
                {
                    session.Kick();
                    // A close that is slow after a committed deletion must not skip the cleanup below.
                    try { await session.ManagedClosed.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false); }
                    catch (TimeoutException) { logger.LogWarning("Playerbot {BotId}: the deletion session did not close within 10 s", bot.BotId); }
                }
            }

            await scope.ServiceProvider.GetRequiredService<IManagedPlayerbotStore>()
                .DeleteAsync(bot.BotId, bot.AccountId, bot.CharacterId, cancellationToken).ConfigureAwait(false);
            _quarantine.TryRemove(bot.BotId, out _);
            _homes.TryRemove(bot.BotId, out _);
            lock (_snapshotGate)
            {
                Volatile.Write(ref _snapshot, Volatile.Read(ref _snapshot).Where(s => s.BotId != bot.BotId).ToArray());
                _snapshotEpoch++;
            }

            logger.LogInformation("Deleted managed playerbot {BotId}, character {CharacterId}", bot.BotId, bot.CharacterId);
            return new(true, "deleted", bot.BotId);
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
        List<Exception> failures = [];
        try
        {
            // Each stop waits for its own world-thread removal and character flush. Start a bounded
            // group together so those waits overlap, while the operation lock keeps other lifecycle
            // work out until all members of the group have finished.
            foreach (ActiveBot[] batch in _active.Values.ToArray().Chunk(32))
            {
                Task[] stops = [.. batch.Select(active => StopCoreAsync(active.Record, true, CancellationToken.None))];
                try { await Task.WhenAll(stops).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    foreach (Task stop in stops.Where(stop => stop.IsFaulted))
                        failures.AddRange(stop.Exception!.InnerExceptions);
                    foreach (Task stop in stops.Where(stop => stop.IsCanceled))
                        failures.Add(new TaskCanceledException(stop));
                }
            }
            try { await SaveSharedSetAsidesAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { logger.LogWarning(ex, "Managed playerbot shared set-aside shutdown save failed ({Type})", ex.GetType().Name); }
        }
        finally { _operations.Release(); }
        if (_planner is { } planner) { _planner = null; await planner.DisposeAsync().ConfigureAwait(false); }
        if (_chat is { } chat) { _chat = null; await chat.DisposeAsync().ConfigureAwait(false); }
        _chatClient?.Dispose();
        _chatClient = null;
        if (failures.Count > 0) throw new AggregateException("Managed playerbot shutdown failed", failures);
    }

    public Task StopAsync() => ShutdownBeforeWorldStopAsync();

    private PlayerbotBrain NewBrain(WorldSession session, Guid botId)
    {
        var brain = new PlayerbotBrain(session, _options, _planner, _planningStop.Token, _sharedSetAsides) { Home = AreaOf(botId) };
        if (session.Player is { IsInWorld: true } player) brain.ObserveKillingBlows(player);
        if (_loadedSetAsides.TryGetValue(botId, out PlayerbotSetAside[]? rows))
            brain.Suspensions.Restore(rows, session.World.NowMs, _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds());
        return brain;
    }

    /// <summary>
    /// The bot's home as a brain's area, or null when it has none. A zone-clipped home takes the zone of its centre from the terrain on
    /// first use (world thread); a centre whose zone is not known gives an area that contains nothing (fail closed), logged then.
    /// </summary>
    private PlayerbotHomeArea? AreaOf(Guid botId)
        => _homes.TryGetValue(botId, out PlayerbotHome? home)
            ? new PlayerbotHomeArea(home.MapId, new System.Numerics.Vector3(home.X, home.Y, home.Z), home.Radius, home.Place,
                home.ZoneClipped, home.ZoneClipped ? ZoneLookupOf(home.MapId) : null,
                area => logger.LogWarning("Playerbot home of {BotId} is zone-clipped but the terrain knows no zone at its centre "
                    + "(map {Map} {X:F1},{Y:F1}): the home contains nothing until it is set again, or set with nozone",
                    botId, area.MapId, area.Centre.X, area.Centre.Y))
            {
                AreaLevels = AreaLevelLookupOf(home.MapId),
                AllowsLeaveQuests = Population.PopulationStartAreas.StageOfPlace(home.Place) == Population.StartHomeStage.Zone,
                ValleyRadius = Population.PopulationStartAreas.ValleyRadiusOfPlace(home.Place),
                LeavePolicy = _options.Population,
            }
            : null;

    // What every bot sets aside after several of them stalled on it (PlayerbotSharedSetAsides).
    private readonly PlayerbotSharedSetAsides _sharedSetAsides = new();

    /// <summary>The goals set aside for every bot (inspection and tests).</summary>
    internal PlayerbotSharedSetAsides SharedSetAsides => _sharedSetAsides;

    /// <summary>The goal the bot reports and persists: its party AI's while that drives it, otherwise the brain's.</summary>
    private (PlayerbotGoalKind Goal, uint TargetEntry, uint QuestId) GoalOf(ActiveBot active)
    {
        GoalReadProbe?.Invoke();
        return GoalCore(active);
    }

    /// <summary>Test seam: runs on whichever thread reads a bot's goal, so a test can assert that thread is the world thread.</summary>
    internal Action? GoalReadProbe { get; set; }

    private (PlayerbotGoalKind Goal, uint TargetEntry, uint QuestId) GoalCore(ActiveBot active)
        => active.BattlegroundDriven && battlegrounds?.GoalOf(active.Record.BotId) is { } bg ? (bg.Goal, bg.TargetEntry, active.Brain.QuestId)
            : active.GroupDriven && coordinator?.GoalOf(active.Record.BotId) is { } group ? (group.Goal, group.TargetEntry, active.Brain.QuestId)
            : active.PartyDriven ? (active.Party.Goal, active.Party.TargetEntry, active.Brain.QuestId)
            : (active.Brain.Goal, active.Brain.TargetEntry, active.Brain.QuestId);

    /// <summary>
    /// <see cref="GoalOf(ActiveBot)"/> read where the coordinators' collections are owned. A stopped world has no tick left to mutate them,
    /// so it is read in place then (as the set-aside captures do).
    /// </summary>
    private async Task<(PlayerbotGoalKind Goal, uint TargetEntry, uint QuestId)> GoalOnWorldThreadAsync(ActiveBot active)
    {
        if (_world is not { } world || world.IsStopped || world.IsWorldThread) return GoalOf(active);
        try { return await world.InvokeAsync(() => GoalOf(active)).ConfigureAwait(false); }
        catch (TaskCanceledException) when (world.IsStopped) { return GoalOf(active); }
    }

    /// <summary>
    /// The goals of <paramref name="bots"/> (same order), read in one world-thread hop: the checkpoint runs on a pool thread, and the
    /// coordinators' member maps are mutated by the world thread. A stopped world is read in place (see <see cref="GoalOnWorldThreadAsync"/>).
    /// </summary>
    private async Task<(PlayerbotGoalKind Goal, uint TargetEntry, uint QuestId)[]> GoalsOnWorldThreadAsync(ActiveBot[] bots)
    {
        (PlayerbotGoalKind Goal, uint TargetEntry, uint QuestId)[] ReadAll()
        {
            var goals = new (PlayerbotGoalKind Goal, uint TargetEntry, uint QuestId)[bots.Length];
            for (int i = 0; i < bots.Length; i++) goals[i] = GoalOf(bots[i]);
            return goals;
        }

        if (bots.Length == 0 || _world is not { } world || world.IsStopped || world.IsWorldThread) return ReadAll();
        try { return await world.InvokeAsync(ReadAll).ConfigureAwait(false); }
        catch (TaskCanceledException) when (world.IsStopped) { return ReadAll(); }
    }

    private Task<WorldSession> NewSessionAsync(Account owner, int? characterId, IServiceProvider services, CancellationToken cancellationToken)
        => WorldSession.CreateManagedAsync(owner, characterId, services, opcodes, _world!, registry, sessionOptions.Value, logger, cancellationToken);

    private async Task StopCoreAsync(ManagedPlayerbot bot, bool preserveDesired, CancellationToken cancellationToken, string? faultCode = null)
    {
        if (_active.TryGetValue(bot.BotId, out ActiveBot? active))
        {
            active.Paused = true;
            try { await SaveBotSetAsidesAsync(active, force: true).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { logger.LogWarning(ex, "Managed playerbot {BotId} set-aside stop save failed ({Type})", bot.BotId, ex.GetType().Name); }
            if (_world is { } stopping) _ = stopping.InvokeAsync(() => { coordinator?.Forget(bot.BotId); battlegrounds?.Forget(bot.BotId); return true; });
            active.Brain.Stop();
            if (Interlocked.Exchange(ref active.Controller, null) is { } controller) controller.Detached(bot.BotId);
            try
            {
                active.Record = await PersistAsync(active.Scope.ServiceProvider, active.Record with
                { State = ManagedPlayerbotState.Stopping, DesiredEnabled = preserveDesired && active.Record.DesiredEnabled }, cancellationToken).ConfigureAwait(false);
                active.Session.Kick();
                await active.Session.ManagedClosed.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                await FlushBoundedAsync(bot.CharacterId, cancellationToken).ConfigureAwait(false);
                // Read on the world thread, queued behind this bot's own Forget: other bots' Forgets mutate the coordinators' collections.
                (PlayerbotGoalKind goal, uint targetEntry, uint questId) = await GoalOnWorldThreadAsync(active).ConfigureAwait(false);
                bot = await PersistAsync(active.Scope.ServiceProvider, active.Record with
                {
                    DesiredEnabled = preserveDesired && active.Record.DesiredEnabled,
                    State = faultCode is null ? ManagedPlayerbotState.Stopped : ManagedPlayerbotState.Faulted,
                    ErrorCode = faultCode ?? active.Record.ErrorCode,
                    Goal = goal, TargetEntry = targetEntry, QuestId = questId,
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
        DeliverChatReplies();
        // Bot-led groups (PlayerbotGroupCoordinator): their states, the needs and the matching, once per tick before any think.
        if (coordinator is { } groups)
        {
            var running = new List<PlayerbotGroupCoordinator.PlayerbotGroupBot>(bots.Length);
            foreach (ActiveBot active in bots)
                if (!active.Paused && active.Session.State == SessionState.InWorld && active.Session.Player is not null)
                    running.Add(new(active.Record.BotId, active.Session, active.Brain,
                        active.Controller is null && !active.PartyDriven && battlegrounds?.Holds(active.Record.BotId) != true));
            try { groups.Update(running, elapsedMs); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { logger.LogWarning(ex, "Playerbot group coordinator failed ({Type}: {Message})", ex.GetType().Name, ex.Message); }
            // A group that stalled (PlayerbotGroupCoordinator.WatchStall) is reported like a brain's stall, on its leader: the brains do not
            // think while a group drives them, so this is the only stall line such a group gets (bot QA parses it).
            foreach ((Guid botId, string name, string report) in groups.TakeStalls())
            {
                ActiveBot? leader = bots.FirstOrDefault(bot => bot.Record.BotId == botId);
                string? context = leader?.Session.Player is { } player ? leader.Brain.QaReportContext(player, report) : null;
                LogStall(logger, botId, name, stallCount: 1, stallsLogged: 0, report, outsideHome: false, everyone: null, context);
            }
        }
        // Battlegrounds (PlayerbotBattlegroundCoordinator): the queue machines, the demand and the free bots' choices, after the groups
        // so a bot a group took this tick is not free here.
        if (battlegrounds is { } fields)
        {
            var running = new List<PlayerbotBattlegroundCoordinator.PlayerbotBattlegroundBot>(bots.Length);
            foreach (ActiveBot active in bots)
                if (!active.Paused && active.Session.State == SessionState.InWorld && active.Session.Player is not null)
                    running.Add(new(active.Record.BotId, active.Session, active.Brain,
                        active.Controller is null && !active.PartyDriven && coordinator?.Involves(active.Record.BotId) != true));
            try { fields.Update(running, elapsedMs); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { logger.LogWarning(ex, "Playerbot battleground coordinator failed ({Type}: {Message})", ex.GetType().Name, ex.Message); }
        }

        var budget = new ManagedActionBudget(_options.MaxActionsPerTick);
        // A fair rotation (PlayerbotThinkRotation): the next tick starts after the last bot this one thought for, and a tick thinks
        // for no more bots once PlayerbotThinkRotation.TickBudgetMs went by (one think itself is bounded by MaxThinkSearchNodes).
        _rotation.TickWithin(bots.Length, PlayerbotThinkRotation.TickBudgetMs, index =>
        {
            if (budget.Remaining <= 0) return false;
            Think(bots[index], budget);
            return true;
        });
        PublishSnapshot(bots);
        _checkpointMs += elapsedMs;
        if (_checkpointMs >= 5000 && _checkpoint.IsCompleted)
        { _checkpointMs = 0; _checkpoint = Task.Run(CheckpointAsync); }
    }

    /// <summary>
    /// One bot's think (world thread), measured (<see cref="PlayerbotThinkProbe"/>) and with its fallback path searches held to
    /// <see cref="PlayerbotOptions.MaxThinkSearchNodes"/>: a think over <see cref="PlayerbotOptions.SlowThinkLogMs"/> is logged with its
    /// bot, driver, goal and phases (<see cref="PlayerbotSlowThinkLog"/>). Both are read from the shared options at each think, so
    /// <c>.reload config</c> changes them at once (WorldConfigKeys).
    /// </summary>
    private void Think(ActiveBot active, ManagedActionBudget budget)
    {
        _probe.NodeBudget = _options.MaxThinkSearchNodes;
        _probe.Begin();
        string driver;
        try { driver = ThinkCore(active, budget); }
        finally { _probe.End(); }
        double slowMs = _options.SlowThinkLogMs;
        bool slow = slowMs > 0 && _probe.ElapsedMs >= slowMs;
        if (!slow && ThinkObserver is null) return; // the common case: nothing to describe, nothing allocated
        string goal = GoalOf(active, driver);
        if (slow) (_slowThinks ??= new PlayerbotSlowThinkLog(logger)).Observe(_probe, slowMs, ClockMs, active.Record.BotId, active.Name, driver, goal);
        if (ThinkObserver is { } observer)
        {
            _probe.Subject = (driver, goal);
            observer(active.Record.BotId, _probe);
        }
    }

    /// <summary>
    /// The goal a think was about, for the slow-think line: the brain's goal, target entry and quest when the brain drove it; "n/a" for
    /// another driver (party AI, bot-led group, battleground, controller), which holds the brain suspended with the goal it had before.
    /// </summary>
    private static string GoalOf(ActiveBot active, string driver)
        => driver == "brain" ? $"{active.Brain.Goal} entry {active.Brain.TargetEntry} quest {active.Brain.QuestId}" : "n/a";

    /// <summary>One bot's think (world thread): its scripted controller, its bot-led group, its party AI or its brain. Returns which drove it.</summary>
    private string ThinkCore(ActiveBot active, ManagedActionBudget budget)
    {
        string driver = "idle";
        if (!active.Paused && active.Session.State == SessionState.InWorld)
        {
            uint sinceLast = unchecked(_world!.NowMs - active.LastUpdateMs);
            active.LastUpdateMs = _world.NowMs;
            try
            {
                if (active.Session.Player is { IsInWorld: true } observed) active.Brain.ObserveKillingBlows(observed);
                if (active.Controller is { } controller)
                {
                    // Scripted mode: the brain is suppressed and the shared action budget does not apply.
                    active.Session.ManagedBudget = null;
                    driver = "controller";
                    PlayerbotThinkProbe.Phase("controller");
                    controller.Tick(active.ControllerContext, sinceLast);
                }
                else if (active.Session.Player is { } driven)
                {
                    active.Session.ManagedBudget = budget;
                    // A bot the battlegrounds coordinator drives (to a battlemaster, into, through and out of a match) follows it, even
                    // inside a battleground raid with a real player in it (the party AI never takes it there); the brain resumes after.
                    if (battlegrounds?.Drives(active.Record.BotId) == true)
                    {
                        if (active.PartyDriven)
                        {
                            active.Party.Disengage(driven);
                            active.PartyDriven = false;
                        }

                        active.BattlegroundDriven = true;
                        driver = "battleground";
                        PlayerbotThinkProbe.Phase("battleground");
                        battlegrounds.UpdateMember(active.Record.BotId, driven, sinceLast);
                    }
                    // A member of a bot-led group follows its group AI; when the group lets it go the brain resumes, with its
                    // quests and memories (its routes and targets belong to another place).
                    else if (coordinator?.Drives(active.Record.BotId) == true)
                    {
                        if (active.PartyDriven)
                        {
                            active.Party.Disengage(driven);
                            active.PartyDriven = false;
                        }

                        active.GroupDriven = true;
                        driver = "group";
                        PlayerbotThinkProbe.Phase("group");
                        coordinator.UpdateMember(active.Record.BotId, driven, sinceLast);
                    }
                    else
                    {
                        if (active.GroupDriven)
                        {
                            active.GroupDriven = false;
                            active.Brain.ResumeAfterGroup();
                        }

                        if (active.BattlegroundDriven)
                        {
                            active.BattlegroundDriven = false;
                            active.Brain.ResumeAfterGroup();
                        }

                        driver = UpdateAutonomous(active, driven, sinceLast);
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Fault(active, "action", ex); }
        }
        if (active.Session.Player is { } player) { active.Name = player.Name; active.MapId = player.Map?.MapId ?? player.MapId; }
        return driver;
    }

    /// <summary>One tick of an autonomous bot outside a bot-led group: its party AI in a real player's group, its brain otherwise.</summary>
    private string UpdateAutonomous(ActiveBot active, Player driven, uint sinceLast)
    {
        // A bot in a real player's group follows its party AI; out of it (or after its master's timeout) the
        // brain takes over again, a fresh one: the old one's routes and targets belong to another place.
        bool party = active.Party.Drives(driven);
        if (active.PartyDriven && !party) ReplaceBrain(active);
        // The party AI follows its master: a tram trip of the brain ends (its entrance consent with it); off the tram map again under the
        // brain, a bot left there takes the nearest exit (PlayerbotTram.Drive).
        if (party && !active.PartyDriven)
        {
            PlayerbotTram.Cancel(driven);
            // A bot aboard a lift is never moved off the car mid-shaft: its ride ends at the next stop (PlayerbotElevator.Cancel), and the
            // party AI takes it only once it stands on the landing. Meanwhile the ride is driven here (PartyDriven stays false).
            PlayerbotElevator.Cancel(driven);
            if (PlayerbotElevator.HoldsForLanding(driven))
            {
                if (!PlayerbotMovementControl.Update(active.Session, driven))
                    PlayerbotElevator.Drive(active.Session, driven, _options, sinceLast, out _);
                return "elevator";
            }
        }
        active.PartyDriven = party;
        if (party)
        {
            PlayerbotThinkProbe.Phase("party");
            active.Party.Update(driven, sinceLast);
            return "party AI";
        }
        else
        {
            active.Brain.Update(sinceLast);
            string? report = active.Brain.StallReport;
            bool awaitRelease = report?.StartsWith("death loop:", StringComparison.Ordinal) == true && !driven.IsAlive
                && !driven.RepopDestinationResolved;
            (active.StallsLogged, bool sharedLogged) = LogStall(logger, active.Record.BotId, active.Name, active.Brain.StallCount,
                active.StallsLogged, report, active.Brain.StallOutsideHome, active.Brain.SharedSetAside,
                report is not null && !awaitRelease && active.Brain.StallCount != active.StallsLogged
                    ? active.Brain.QaReportContext(driven, report) : null,
                awaitRelease);
            if (sharedLogged) active.Brain.SharedSetAside = null;
            return "brain";
        }
    }

    /// <summary>
    /// The warnings of a bot's stall (bot QA parses them, PlayerbotQaSignalParser): a new stall once (<paramref name="stallCount"/> moved
    /// past <paramref name="stallsLogged"/>), marked when the bot stood outside its home (<paramref name="outsideHome"/>,
    /// PlayerbotBrain.StallOutsideHome), and a goal it set aside for every bot (<paramref name="everyone"/>) as soon as there is one, with
    /// where the bots behind it stalled. Before, the set-aside was logged only beside a new stall line: one completed by a further give-up
    /// of a stall already logged (PlayerbotStallWatch.GiveUps) waited for the bot's next stall, and was logged beside a stall on another
    /// goal (live 2026-10-11 04:23:57: quest 2160 beside an Explore stall). Returns the stalls now logged and whether
    /// <paramref name="everyone"/> was logged (the caller clears it then). A death loop's report is never marked outside the home.
    /// </summary>
    internal static (int StallsLogged, bool SharedLogged) LogStall(ILogger logger, Guid botId, string name, int stallCount, int stallsLogged,
        string? stall, bool outsideHome, PlayerbotSharedSetAside? everyone, string? qaContext = null, bool awaitRelease = false)
    {
        if (stallCount != stallsLogged && stall is not null && !awaitRelease)
        {
            stallsLogged = stallCount;
            // The mark belongs to a living stall: a death loop counts as a stall too and its report comes first, while the flag still
            // holds the last give-up (PlayerbotStallWatch.Report).
            if (outsideHome && stall.StartsWith("stalled ", StringComparison.Ordinal)) logger.LogWarning("Playerbot {BotId} ({Name}) {Stall}; it gives up that goal outside its home", botId, name, stall + qaContext);
            else logger.LogWarning("Playerbot {BotId} ({Name}) {Stall}; it gives up that goal", botId, name, stall + qaContext);
        }

        if (everyone is not { } shared) return (stallsLogged, false);
        logger.LogWarning("Playerbots: {Count} bots stalled on {Goal}; every bot sets it aside (stalls at {Stalls})",
            PlayerbotSharedSetAsides.BotsToShare, shared.Goal, shared.Stalls);
        return (stallsLogged, true);
    }

    /// <summary>
    /// The running bots' status lines (StatusOf), the snapshot rebuilt only when one of them changed, the running set changed or
    /// PublishStopped touched it (_snapshotEpoch).
    /// </summary>
    private void PublishSnapshot(ActiveBot[] bots)
    {
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
    }

    /// <summary>
    /// World thread: say the chat replies the workers finished, each through its bot's own CMSG_MESSAGECHAT (a client's answer: no
    /// action budget). A reply for a bot that stopped, is scripted or left the world is dropped.
    /// </summary>
    private void DeliverChatReplies()
    {
        if (_chat is not { } chat) return;
        foreach (Chat.BotChatMute mute in chat.DrainMutes()) ApplyChatMute(mute);
        foreach (Chat.BotChatReply reply in chat.DrainReplies())
        {
            if (!_active.TryGetValue(reply.BotId, out ActiveBot? active) || active.Paused || active.Controller is not null
                || active.Session.State != SessionState.InWorld || active.Session.Player is not { } speaker) continue;
            try { active.Party.DeliverChat(speaker, reply); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A chat reply is never worth a bot: it is dropped and the bot carries on.
                logger.LogWarning(ex, "Playerbot {BotId} ({Name}) could not say a chat reply", active.Record.BotId, active.Name);
            }
        }
    }

    /// <summary>
    /// World thread: the bot chat safety's automatic mute (World:Playerbots:Chat:Safety:AutoMute), through the account mute of
    /// <c>.mute</c> (persisted, lifted by <c>.unmute</c>). A longer mute already in force is kept.
    /// </summary>
    private void ApplyChatMute(Chat.BotChatMute mute)
    {
        if (services.GetService<Gm.Audit.GmAuditFeature>() is not { } audit)
        {
            logger.LogWarning("Bot chat safety could not mute {Player}: the GM audit feature (account mutes) is not running", mute.PlayerName);
            return;
        }

        long seconds = mute.Minutes * 60L;
        if (audit.MuteOf(mute.AccountId) is { } existing && existing.MutedUntil >= audit.NowUnixSeconds + seconds) return;
        const string reason = "Bot chat safety: repeated flagged messages to bots";
        audit.Mute(mute.AccountId, seconds, "Bot chat safety", Kernel.Accounts.AccountSecurity.GameMaster, reason);
        if (_world?.FindOnlinePlayer(new Game.ObjectGuid(mute.PlayerGuid)) is { } player)
            Gm.Core.GmReplies.SendSystemMessage(player, Gm.Audit.GmAuditStrings.YourChatDisabled(Gm.Audit.GmAuditStrings.Span(seconds), "Bot chat safety", reason));
    }

    private async Task CheckpointAsync()
    {
        if (!await _operations.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            ActiveBot[] bots = _active.Values.ToArray();
            // One world-thread hop for every bot's goal (the coordinators' collections are world-thread owned).
            (PlayerbotGoalKind Goal, uint TargetEntry, uint QuestId)[] goals = await GoalsOnWorldThreadAsync(bots).ConfigureAwait(false);
            // Only a bot whose persisted goal fields changed is written (UpdatedUnix alone is never a reason), and all of them
            // together: one transaction per chunk instead of one autocommit (one fsync) per bot per checkpoint.
            var live = new List<(ActiveBot Active, ManagedPlayerbot Next)>(bots.Length);
            for (int i = 0; i < bots.Length; i++)
            {
                ActiveBot active = bots[i];
                if (active.Record.State == ManagedPlayerbotState.Faulted) continue;
                if (active.Session.State == SessionState.Closed)
                {
                    // One bot's failed stop must not keep the other bots from being checkpointed.
                    try
                    {
                        if (Volatile.Read(ref active.FaultCode) is { } fault)
                        {
                            (bool desired, string code) = RecordFault(active.Record.BotId, fault);
                            await StopCoreAsync(active.Record, desired, CancellationToken.None, code).ConfigureAwait(false);
                        }
                        else await StopCoreAsync(active.Record, false, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    { logger.LogWarning(ex, "Playerbot {BotId} checkpoint stop failed ({Type}: {Message})", active.Record.BotId, ex.GetType().Name, ex.Message); }
                    continue;
                }
                live.Add((active, active.Record with { Goal = goals[i].Goal, TargetEntry = goals[i].TargetEntry, QuestId = goals[i].QuestId }));
            }

            var conflicted = new HashSet<ActiveBot>();
            var changed = live.Where(item => GoalFieldsDiffer(item.Active.Record, item.Next)).ToList();
            if (changed.Count > 0)
            {
                // A failed write (not a refused row) leaves these bots' records as they were, so the next checkpoint retries them,
                // and it must not keep the set-asides below from being saved.
                try
                {
                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                    IManagedPlayerbotStore store = scope.ServiceProvider.GetRequiredService<IManagedPlayerbotStore>();
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    for (int start = 0; start < changed.Count; start += ManagedPlayerbotUpdate.MaxBatch)
                    {
                        int count = Math.Min(ManagedPlayerbotUpdate.MaxBatch, changed.Count - start);
                        var updates = new ManagedPlayerbotUpdate[count];
                        for (int j = 0; j < count; j++)
                        {
                            ManagedPlayerbot next = changed[start + j].Next;
                            updates[j] = new ManagedPlayerbotUpdate(next with { Revision = next.Revision + 1, UpdatedUnix = now }, next.Revision);
                        }

                        IReadOnlyList<bool> applied = await store.UpdateManyAsync(updates, CancellationToken.None).ConfigureAwait(false);
                        if (applied.Count != count)
                            throw new InvalidOperationException($"managed-update-many-mismatch: {applied.Count} results for {count} updates");
                        // The committed rows first: nothing below may leave a written bot with a stale revision.
                        var refused = new List<ActiveBot>();
                        for (int j = 0; j < count; j++)
                        {
                            ActiveBot active = changed[start + j].Active;
                            if (applied[j]) active.Record = updates[j].Bot;
                            else refused.Add(active);
                        }

                        foreach (ActiveBot active in refused)
                        {
                            // Someone else changed or removed the row (in-process paths run under _operations and never do). Fault only this bot.
                            conflicted.Add(active);
                            logger.LogWarning("Playerbot {BotId} checkpoint refused: managed-revision-conflict", active.Record.BotId);
                            ManagedPlayerbot? stored;
                            try { stored = await store.FindAsync(active.Record.BotId, CancellationToken.None).ConfigureAwait(false); }
                            catch (Exception ex) when (ex is not OutOfMemoryException)
                            {
                                logger.LogWarning(ex, "Playerbot {BotId} conflicting row could not be read ({Type})", active.Record.BotId, ex.GetType().Name);
                                continue; // not faulted: the next checkpoint finds the same conflict and looks again
                            }

                            if (stored is null || stored.State == ManagedPlayerbotState.Faulted) { await DropFromActiveAsync(active, stored).ConfigureAwait(false); continue; }
                            // Adopt the other writer's whole record, so the stop (a revision-checked write) changes only what it means to
                            // on top of its values (a manual DesiredEnabled = false survives) and can leave _active cleanly.
                            active.Record = stored;
                            Interlocked.CompareExchange(ref active.FaultCode, "checkpoint: managed-revision-conflict", null);
                            active.Session.Kick();
                        }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { logger.LogWarning(ex, "Managed playerbot checkpoint write failed ({Type})", ex.GetType().Name); }
            }

            foreach ((ActiveBot active, _) in live)
                if (!conflicted.Contains(active)) await SaveBotSetAsidesAsync(active).ConfigureAwait(false);
            await SaveSharedSetAsidesAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { logger.LogWarning(ex, "Managed playerbot checkpoint failed ({Type})", ex.GetType().Name); }
        finally { _operations.Release(); }
        await RetryQuarantinedAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// A running bot whose registry row no longer exists (<paramref name="row"/> null, deleted out of band) or was faulted out of band
    /// (the checkpoint skips Faulted records, so it could never stop): it leaves <see cref="_active"/> without a revision-checked
    /// write instead of failing or lingering every checkpoint. Logged once. The faulted row is published as stopped.
    /// </summary>
    private async Task DropFromActiveAsync(ActiveBot active, ManagedPlayerbot? row)
    {
        Guid botId = active.Record.BotId;
        if (row is null) logger.LogWarning("Playerbot {BotId} has no registry row any more; dropping it from the active set", botId);
        else logger.LogWarning("Playerbot {BotId} was faulted out of band ({Code}); dropping it from the active set", botId, row.ErrorCode);
        active.Paused = true;
        // A row that exists keeps the bot's suspensions across a restart, as a stop does.
        if (row is not null)
        {
            try { await SaveBotSetAsidesAsync(active, force: true).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { logger.LogWarning(ex, "Playerbot {BotId} set-aside save failed ({Type})", botId, ex.GetType().Name); }
        }

        if (_world is { } world) _ = world.InvokeAsync(() => { coordinator?.Forget(botId); battlegrounds?.Forget(botId); return true; });
        active.Brain.Stop();
        try { if (Interlocked.Exchange(ref active.Controller, null) is { } controller) controller.Detached(botId); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { logger.LogWarning(ex, "Playerbot {BotId} controller detach failed ({Type})", botId, ex.GetType().Name); }
        active.Session.Kick();
        if (_active.TryRemove(new KeyValuePair<Guid, ActiveBot>(botId, active))) Interlocked.Increment(ref _activeVersion);
        if (row is not null) { active.Record = row; PublishStopped(row, active.Name, active.MapId); }
        else
        {
            // The same cleanup as an in-process delete: no status line, quarantine or home is kept for a bot with no row.
            _quarantine.TryRemove(botId, out _);
            _homes.TryRemove(botId, out _);
            lock (_snapshotGate)
            {
                Volatile.Write(ref _snapshot, Volatile.Read(ref _snapshot).Where(s => s.BotId != botId).ToArray());
                _snapshotEpoch++;
            }
        }

        try { await active.Scope.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { logger.LogWarning(ex, "Playerbot {BotId} scope disposal failed ({Type})", botId, ex.GetType().Name); }
    }

    /// <summary>Whether the checkpoint's persisted fields (the goal, its target and its quest) differ between two records.</summary>
    private static bool GoalFieldsDiffer(ManagedPlayerbot stored, ManagedPlayerbot next)
        => stored.Goal != next.Goal || stored.TargetEntry != next.TargetEntry || stored.QuestId != next.QuestId;

    /// <summary>A running bot's status line: the one it had when nothing in it changed (no allocation), otherwise a new one.</summary>
    private PlayerbotStatus StatusOf(ActiveBot b)
    {
        bool closed = b.Session.State == SessionState.Closed;
        ManagedPlayerbotState state = closed ? ManagedPlayerbotState.Faulted : b.Record.State;
        (PlayerbotGoalKind goal, uint target, uint quest) = GoalOf(b);
        uint health = b.Session.Player?.Health ?? 0;
        string? error = closed ? "session-closed" : b.Record.ErrorCode ?? StallOf(b);
        string? risk = RiskOf(b);
        string? group = coordinator?.Describe(b.Record.BotId) ?? battlegrounds?.Describe(b.Record.BotId);
        string? home = HomeLineOf(b);
        if (b.Status is { } last && last.BotId == b.Record.BotId && last.State == state && last.DesiredEnabled == b.Record.DesiredEnabled
            && last.Goal == goal && last.TargetEntry == target && last.QuestId == quest && last.MapId == b.MapId && last.Health == health
            && string.Equals(last.Name, b.Name, StringComparison.Ordinal) && string.Equals(last.ErrorCode, error, StringComparison.Ordinal)
            && string.Equals(last.Risk, risk, StringComparison.Ordinal) && string.Equals(last.Group, group, StringComparison.Ordinal)
            && string.Equals(last.Home, home, StringComparison.Ordinal))
            return last;
        return new PlayerbotStatus(b.Record.BotId, b.Name, state, b.Record.DesiredEnabled, goal, target, quest, b.MapId, health, error, risk,
            group, home);
    }

    /// <summary>
    /// The home line of a running bot: none without a home, <c>suspended</c> while a scripted controller, a party AI, a bot-led group or
    /// the battlegrounds coordinator drives it (they ignore the home), otherwise the brain's report (<see cref="PlayerbotHomeReturn.Report"/>).
    /// </summary>
    private string? HomeLineOf(ActiveBot active) => !_homes.ContainsKey(active.Record.BotId) ? null
        : active.Controller is not null || active.PartyDriven || active.GroupDriven || active.BattlegroundDriven ? "suspended" : active.Brain.HomeReport;

    /// <summary>The home line of a bot that is not running.</summary>
    private string? StoppedHome(Guid botId) => _homes.ContainsKey(botId) ? "stopped" : null;

    /// <summary>The risk line of a running bot (its party AI's while that drives it); none for a scripted bot.</summary>
    private string? RiskOf(ActiveBot active) => active.Controller is not null ? null
        : active.BattlegroundDriven ? "decision=battleground"
        : active.GroupDriven ? coordinator?.FindAI(active.Record.BotId) is { Retreat.Active: true } ai
            ? $"retreat reason={ai.Retreat.Reason}" : "decision=group"
        : active.PartyDriven ? active.Party.RiskReport : active.Brain.RiskReport;

    /// <summary>A running bot's current stall (<see cref="PlayerbotStallWatch"/>), shown where a fault would be.</summary>
    private static string? StallOf(ActiveBot active) => active.PartyDriven || active.GroupDriven || active.BattlegroundDriven || active.Controller is not null ? null
        : active.Brain.StallReport is { } stall ? active.StallCode(stall) : null;

    private void ReplaceBrain(ActiveBot active)
    {
        lock (active.SetAsideGate)
        {
            PlayerbotSetAside[] current = active.Brain.Suspensions.Export(active.Record.BotId, active.Record.CharacterId,
                active.Session.World.NowMs, _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds());
            active.Brain.Stop();
            active.Brain = NewBrain(active.Session, active.Record.BotId);
            active.Brain.Suspensions.Restore(current, active.Session.World.NowMs, _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds());
            Volatile.Write(ref active.SavedSetAsideVersion, -1);
        }
    }

    private async Task SaveBotSetAsidesAsync(ActiveBot active, bool force = false)
    {
        if (!force && Volatile.Read(ref active.SavedSetAsideVersion) == active.Brain.Suspensions.Version) return;
        (PlayerbotSetAside[] rows, int version, PlayerbotBrain brain) Capture()
        {
            PlayerbotBrain brain = active.Brain;
            PlayerbotSuspensions state = brain.Suspensions;
            return (state.Export(active.Record.BotId, active.Record.CharacterId, active.Session.World.NowMs,
                _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds()), state.Version, brain);
        }
        // WorldTestHost and the production host may stop the world before stopping features. No tick can mutate the brain then.
        (PlayerbotSetAside[] rows, int version, PlayerbotBrain brain) snapshot;
        try { snapshot = _world!.IsStopped || _world.IsWorldThread ? Capture() : await _world.InvokeAsync(Capture).ConfigureAwait(false); }
        catch (TaskCanceledException) when (_world!.IsStopped) { snapshot = Capture(); }
        (PlayerbotSetAside[] rows, int version, PlayerbotBrain brain) = snapshot;
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<IManagedPlayerbotSetAsideStore>() is { } store)
            await store.ReplaceAsync(active.Record.BotId, rows).ConfigureAwait(false);
        if (force) _loadedSetAsides[active.Record.BotId] = rows;
        lock (active.SetAsideGate)
            if (ReferenceEquals(active.Brain, brain)) Volatile.Write(ref active.SavedSetAsideVersion, version);
    }

    private async Task SaveSharedSetAsidesAsync()
    {
        if (Volatile.Read(ref _savedSharedSetAsideVersion) == _sharedSetAsides.Version) return;
        (PlayerbotSetAside[] rows, int version) Capture()
        {
            return (_sharedSetAsides.Export(_world.NowMs, _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds()),
                _sharedSetAsides.Version);
        }
        (PlayerbotSetAside[] rows, int version) snapshot;
        try { snapshot = _world!.IsStopped || _world.IsWorldThread ? Capture() : await _world.InvokeAsync(Capture).ConfigureAwait(false); }
        catch (TaskCanceledException) when (_world!.IsStopped) { snapshot = Capture(); }
        (PlayerbotSetAside[] rows, int version) = snapshot;
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<IManagedPlayerbotSetAsideStore>() is { } store)
            await store.ReplaceAsync(Guid.Empty, rows).ConfigureAwait(false);
        Volatile.Write(ref _savedSharedSetAsideVersion, version);
    }

    /// <summary>
    /// An exception out of a bot's update (world thread): log all of it, remember the fault for the checkpoint, close the session.
    /// The next checkpoint quarantines the bot (<see cref="RecordFault"/>).
    /// </summary>
    private void Fault(ActiveBot active, string what, Exception ex)
    {
        // Counted in the fault ledger (.server faults); logged below on every occurrence (not throttled): the QA log tap reads these lines as action faults.
        FaultLedger.Shared.Record("playerbot", ex);
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
        PlayerbotStatus next = new(bot.BotId, name, bot.State, bot.DesiredEnabled, bot.Goal, bot.TargetEntry, bot.QuestId, mapId, 0, bot.ErrorCode,
            Home: StoppedHome(bot.BotId));
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
        public int SavedSetAsideVersion;
        public readonly object SetAsideGate = new();
        public PlayerbotPartyAI Party { get; } = party;
        public bool PartyDriven;
        public bool GroupDriven;
        public bool BattlegroundDriven;
        public string Name = session.Player!.Name;
        public uint MapId = session.Player!.Map!.MapId;
        public uint LastUpdateMs = session.World.NowMs;
        public volatile bool Paused;
        public IPlayerbotController? Controller;
        public string? FaultCode;
        public int StallsLogged;
        public PlayerbotStatus? Status; // world thread: the last status line (StatusOf)
        private string? _stallSource;
        private string? _stallCode;

        /// <summary>The status code of a stall report (<see cref="Code"/>), computed once per report instead of every tick.</summary>
        public string StallCode(string report)
        {
            if (!ReferenceEquals(report, _stallSource))
            {
                _stallSource = report;
                _stallCode = Code(report);
            }

            return _stallCode!;
        }
        public PlayerbotControllerContext ControllerContext { get; } = new(record.BotId, session);
    }
}
