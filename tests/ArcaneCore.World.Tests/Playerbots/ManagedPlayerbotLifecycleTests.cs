using System.Collections.Concurrent;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class ManagedPlayerbotLifecycleTests
{
    [Fact]
    public async Task FailedRegistration_ReconcilesTheOrdinaryCharacterAndPendingProvision()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new MemoryManagedPlayerbotStore { FailNextCreate = true };
        var provisions = new MemoryProvisionStore(accounts);
        await using WorldTestHost host = Start(accounts, characters, owners, provisions);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        PlayerbotOperationResult result = await feature.CreateAsync("Provisionbad", 1, 1);
        Assert.False(result.Success);
        Assert.False(owners.FailNextCreate); // Creation reached the injected registry failure.
        Assert.Empty(await owners.LoadAllAsync());
        Assert.Empty(await characters.GetByAccountAsync(1));
        Assert.Empty(await provisions.LoadPendingAsync());
        Assert.Equal(1, provisions.RollbackCount);
        Assert.Null(host.Registry.Find(1));
    }

    [Fact]
    public async Task FailedStop_RetainsHandleAndCanBeReconciledByExplicitRetry()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new MemoryManagedPlayerbotStore();
        await using WorldTestHost host = Start(accounts, characters, owners);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync("StopFail", 1, 1)).BotId);
        Assert.True((await feature.StartAsync(id.ToString())).Success);
        owners.FailNextUpdate = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => feature.StopAsync(id.ToString()));
        Assert.NotNull(host.Registry.Find(1));
        Assert.Equal("stop-incomplete", (await feature.StartAsync(id.ToString())).Code);
        Assert.True((await feature.StopAsync(id.ToString())).Success);
        Assert.Null(host.Registry.Find(1));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Stopfail") is null, "retry cleanup");
        Assert.True(characters.SaveCount > 0);
    }

    [Fact]
    public async Task CreateStartStopAndRestart_UsesTheOrdinaryWorldSessionAndPreservesCharacter()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new MemoryManagedPlayerbotStore();
        await using WorldTestHost first = Start(accounts, characters, owners);
        ManagedPlayerbotFeature feature = first.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);

        PlayerbotOperationResult created = await feature.CreateAsync("ManagedOne", 1, 1);
        Assert.True(created.Success, created.Code);
        Guid botId = Assert.IsType<Guid>(created.BotId);
        CharacterRecord character = Assert.Single(await characters.GetByAccountAsync(1));

        PlayerbotOperationResult started = await feature.StartAsync(botId.ToString());
        Assert.True(started.Success, started.Code);
        await first.WaitForWorldAsync(() => first.World.FindOnlinePlayer("ManagedOne") is not null, "managed bot login");
        Assert.Equal(character.Id, (await characters.GetByIdAsync(character.Id))!.Id);

        PlayerbotOperationResult stopped = await feature.StopAsync(botId.ToString());
        Assert.True(stopped.Success, stopped.Code);
        await first.WaitForWorldAsync(() => first.World.FindOnlinePlayer("ManagedOne") is null, "managed bot logout");
        ManagedPlayerbot? persisted = await owners.FindAsync(botId);
        Assert.NotNull(persisted);
        Assert.Equal(ManagedPlayerbotState.Stopped, persisted!.State);
        Assert.True(characters.SaveCount > 0);

        await first.WorldServices.GetRequiredService<ManagedPlayerbotFeature>().ShutdownBeforeWorldStopAsync();
        await using WorldTestHost second = Start(accounts, characters, owners);
        ManagedPlayerbotFeature restarted = second.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await restarted.StartupAsync(default);
        PlayerbotOperationResult startedAgain = await restarted.StartAsync(botId.ToString());
        Assert.True(startedAgain.Success, startedAgain.Code);
        await second.WaitForWorldAsync(() => second.World.FindOnlinePlayer("ManagedOne") is not null, "managed bot restart");
    }

    [Fact]
    public async Task RegistryCleanup_RejectsDuplicateStartThenAllowsTakeoverAfterStop()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new MemoryManagedPlayerbotStore();
        await using WorldTestHost host = Start(accounts, characters, owners);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync("Takeover", 1, 1)).BotId);
        Assert.True((await feature.StartAsync(id.ToString())).Success);
        PlayerbotOperationResult duplicate = await feature.StartAsync(id.ToString());
        Assert.True(duplicate.Success);
        Assert.Equal("already-running", duplicate.Code);

        Assert.True((await feature.StopAsync(id.ToString())).Success);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Takeover") is null, "managed bot cleanup");
        Assert.True((await feature.StartAsync(id.ToString())).Success);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Takeover") is not null, "managed bot takeover");
    }

    [Fact]
    public async Task DisabledAndUnknownHumanName_DoNothing()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new MemoryManagedPlayerbotStore();
        await using WorldTestHost disabled = Start(accounts, characters, owners, enabled: false);
        ManagedPlayerbotFeature feature = disabled.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Assert.Equal("playerbots-disabled", (await feature.CreateAsync("Disabled", 1, 1)).Code);
        Assert.Empty(feature.Snapshot());

        await using WorldTestHost enabled = Start(accounts, characters, owners);
        ManagedPlayerbotFeature active = enabled.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await active.StartupAsync(default);
        Assert.Equal("bot-not-found", (await active.StartAsync("HumanCharacter")).Code);
        Assert.Empty(active.Snapshot());
    }

    [Fact]
    public async Task RegisteredNonPlayerOwner_IsRefusedWithoutRenamingOrdinaryCharacter()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new MemoryManagedPlayerbotStore();
        Account human = await accounts.CreateAsync(new Account { Username = "HumanOwner", Salt = new byte[32], Verifier = new byte[32], Security = AccountSecurity.Administrator });
        CharacterRecord character = await characters.CreateAsync(new CharacterRecord
        {
            AccountId = human.Id, Name = "HumanCharacter", Race = 1, Class = 1, Level = 1,
        });
        var bot = new ManagedPlayerbot(Guid.NewGuid(), human.Id, character.Id, human.Username, false,
            ManagedPlayerbotState.Stopped, PlayerbotGoalKind.Explore, 0, 0, 0, 1, 1);
        await owners.CreateAsync(bot);

        await using WorldTestHost host = Start(accounts, characters, owners);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);

        PlayerbotOperationResult result = await feature.StartAsync(bot.BotId.ToString());
        Assert.Equal("owner-refused", result.Code);
        Assert.Equal("HumanCharacter", (await characters.GetByIdAsync(character.Id))!.Name);
    }

    [Fact]
    public async Task DesiredBot_RestoresOnStartupThroughOrdinaryLogin()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new MemoryManagedPlayerbotStore();
        var provisions = new MemoryProvisionStore(accounts);
        Account owner = await accounts.CreateAsync(new Account { Username = "PBRESTORE", Salt = new byte[32], Verifier = new byte[32], Security = AccountSecurity.Player });
        CharacterRecord character = await characters.CreateAsync(new CharacterRecord
        {
            AccountId = owner.Id, Name = "RestoreBot", Race = 1, Class = 1, Level = 1,
        });
        var bot = new ManagedPlayerbot(Guid.NewGuid(), owner.Id, character.Id, owner.Username, true,
            ManagedPlayerbotState.Stopped, PlayerbotGoalKind.Explore, 0, 0, 0, 1, 1);
        await owners.CreateAsync(bot);

        await using WorldTestHost host = Start(accounts, characters, owners, provisions, restoreOnStartup: true);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);

        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("RestoreBot") is not null, "desired bot restore");
        Assert.Equal(ManagedPlayerbotState.Running, (await owners.FindAsync(bot.BotId))!.State);
    }

    /// <summary>
    /// The live fault of 2026-10-07: one action fault logged only the exception type and left the bot with DesiredEnabled 0, so it never
    /// came back. One fault now quarantines the bot: the whole exception is logged, the record keeps DesiredEnabled with a Faulted state
    /// and the fault as its error code.
    /// </summary>
    [Fact]
    public async Task OneActionFault_LogsTheException_AndKeepsTheBotDesired()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new MemoryManagedPlayerbotStore();
        var log = new CapturingLogger();
        await using WorldTestHost host = Start(accounts, characters, owners, logger: log);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync("Faultonce", 1, 1)).BotId);
        Assert.True((await feature.StartScriptedAsync(id.ToString(), new ThrowingController(1))).Success);

        await WaitAsync(async () => (await owners.FindAsync(id))?.State == ManagedPlayerbotState.Faulted, "the fault is recorded", 20);

        ManagedPlayerbot record = (await owners.FindAsync(id))!;
        Assert.True(record.DesiredEnabled);
        Assert.Contains("fixture-action-fault", record.ErrorCode);
        (Exception? error, string message) = Assert.Single(log.Entries, e => e.Message.Contains("action failed"));
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("fixture-action-fault", message);
    }

    [Fact]
    public async Task AQuarantinedBot_LogsInAgainAfterTheBackoff_Autonomous()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new MemoryManagedPlayerbotStore();
        await using WorldTestHost host = Start(accounts, characters, owners, configure: o => { o.FaultBackoffSeconds = 1; o.MaxFaults = 3; });
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync("Faultback", 1, 1)).BotId);
        Assert.True((await feature.StartScriptedAsync(id.ToString(), new ThrowingController(1))).Success);
        await WaitAsync(async () => (await owners.FindAsync(id))?.State == ManagedPlayerbotState.Faulted, "the quarantine", 20);
        Assert.StartsWith("quarantined (fault 1/3): action: fixture-action-fault", (await owners.FindAsync(id))!.ErrorCode);

        await WaitAsync(async () => (await owners.FindAsync(id))?.State == ManagedPlayerbotState.Running, "the retry", 30);

        ManagedPlayerbot record = (await owners.FindAsync(id))!;
        Assert.True(record.DesiredEnabled);
        Assert.Null(record.ErrorCode);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Faultback") is not null, "the bot is back in the world");
        Assert.False(feature.IsScripted(id)); // the scenario controller that faulted is gone; the brain runs it
    }

    [Fact]
    public async Task TheLastAllowedFault_DisablesTheBot_AndItStaysOut()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new MemoryManagedPlayerbotStore();
        await using WorldTestHost host = Start(accounts, characters, owners, configure: o => { o.FaultBackoffSeconds = 1; o.MaxFaults = 1; });
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync("Faultout", 1, 1)).BotId);
        Assert.True((await feature.StartScriptedAsync(id.ToString(), new ThrowingController(1))).Success);

        await WaitAsync(async () => (await owners.FindAsync(id))?.State == ManagedPlayerbotState.Faulted, "the fault", 20);
        ManagedPlayerbot record = (await owners.FindAsync(id))!;
        Assert.False(record.DesiredEnabled);
        Assert.StartsWith("disabled after 1 faults: action: fixture-action-fault", record.ErrorCode);

        await Task.Delay(TimeSpan.FromSeconds(7)); // past the next checkpoint and the backoff
        Assert.Equal(ManagedPlayerbotState.Faulted, (await owners.FindAsync(id))!.State);
        Assert.Null(await host.World.InvokeAsync(() => host.World.FindOnlinePlayer("Faultout")));
    }

    [Fact]
    public void FaultOptions_AreBounded()
    {
        var options = new PlayerbotOptions();
        Assert.Equal((30, 3, 3600), (options.FaultBackoffSeconds, options.MaxFaults, options.FaultWindowSeconds));
        options.Validate();
        options.MaxFaults = 0;
        Assert.Throws<InvalidOperationException>(options.Validate);
        options.MaxFaults = 3;
        options.FaultBackoffSeconds = 0;
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, string what, int seconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out waiting for: {what}");
            await Task.Delay(20);
        }
    }

    /// <summary>A scripted controller whose first <paramref name="faults"/> ticks throw (an action fault in the bot's update).</summary>
    private sealed class ThrowingController(int faults) : IPlayerbotController
    {
        private int _remaining = faults;

        public void Tick(PlayerbotControllerContext context, uint elapsedMs)
        {
            if (Interlocked.Decrement(ref _remaining) >= 0) throw new InvalidOperationException("fixture-action-fault");
        }

        public void Detached(Guid botId) { }
    }

    private sealed class CapturingLogger : ILogger<ManagedPlayerbotFeature>
    {
        private readonly ConcurrentQueue<(Exception? Error, string Message)> _entries = new();

        public IReadOnlyList<(Exception? Error, string Message)> Entries => [.. _entries];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _entries.Enqueue((exception, formatter(state, exception)));
    }

    private static WorldTestHost Start(InMemoryAccountStore accounts, InMemoryCharacterStore characters,
        MemoryManagedPlayerbotStore owners, MemoryProvisionStore? provisions = null,
        bool enabled = true, bool restoreOnStartup = false, CapturingLogger? logger = null, Action<PlayerbotOptions>? configure = null)
        => WorldTestHost.Start(configureServices: services =>
        {
            if (logger is not null) services.AddSingleton<ILogger<ManagedPlayerbotFeature>>(logger);
            var options = new PlayerbotOptions
            {
                Enabled = enabled, RestoreOnStartup = restoreOnStartup, MaxBots = 4, ThinkIntervalMs = 100,
                MaxActionsPerTick = 2, MaxPathPoints = 32, MaxRouteYards = 100, AllowedMaps = [0, 1],
            };
            configure?.Invoke(options);
            services.AddSingleton<IAccountStore>(accounts);
            services.AddSingleton<IAccountAdmin>(accounts);
            services.AddSingleton<ICharacterStore>(characters);
            services.AddSingleton<ICharacterLifeStore>(characters);
            services.AddSingleton<IManagedPlayerbotStore>(owners);
            services.AddSingleton<IOptions<PlayerbotOptions>>(Options.Create(options));
            services.AddSingleton<IManagedPlayerbotProvisionStore>(provisions ?? new MemoryProvisionStore(accounts));
        });

    private sealed class MemoryProvisionStore(InMemoryAccountStore accounts) : IManagedPlayerbotProvisionStore
    {
        private readonly ConcurrentDictionary<Guid, ManagedPlayerbotProvision> _pending = new();
        public int RollbackCount { get; private set; }

        public async Task<Account> CreateAsync(Guid botId, Account account, CancellationToken cancellationToken = default)
        {
            Account created = await accounts.CreateAsync(account, cancellationToken);
            _pending[botId] = new ManagedPlayerbotProvision(botId, created.Id, created.Username, 1);
            return created;
        }

        public Task<IReadOnlyList<ManagedPlayerbotProvision>> LoadPendingAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ManagedPlayerbotProvision>>(_pending.Values.ToArray());

        public Task<bool> CompleteAsync(Guid botId, int accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(_pending.TryRemove(botId, out ManagedPlayerbotProvision? proof) && proof.AccountId == accountId);

        public Task<bool> RollbackEmptyOwnerAsync(Guid botId, int accountId, CancellationToken cancellationToken = default)
        {
            bool removed = _pending.TryRemove(botId, out ManagedPlayerbotProvision? proof) && proof.AccountId == accountId;
            if (removed) RollbackCount++;
            return Task.FromResult(removed);
        }
    }

    private sealed class MemoryManagedPlayerbotStore : IManagedPlayerbotStore
    {
        private readonly ConcurrentDictionary<Guid, ManagedPlayerbot> _items = new();
        public bool FailNextCreate { get; set; }
        public bool FailNextUpdate { get; set; }

        public Task<IReadOnlyList<ManagedPlayerbot>> LoadAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ManagedPlayerbot>>(_items.Values.OrderBy(item => item.BotId).ToArray());

        public Task<ManagedPlayerbot?> FindAsync(Guid botId, CancellationToken cancellationToken = default)
            => Task.FromResult(_items.GetValueOrDefault(botId));

        public Task CreateAsync(ManagedPlayerbot bot, CancellationToken cancellationToken = default)
        {
            if (FailNextCreate) { FailNextCreate = false; throw new InvalidOperationException("fixture-create-failure"); }
            if (!_items.TryAdd(bot.BotId, bot)) throw new InvalidOperationException("duplicate-owner");
            return Task.CompletedTask;
        }

        public Task<bool> UpdateAsync(ManagedPlayerbot bot, long expectedRevision, CancellationToken cancellationToken = default)
        {
            if (FailNextUpdate) { FailNextUpdate = false; throw new InvalidOperationException("fixture-update-failure"); }
            while (_items.TryGetValue(bot.BotId, out ManagedPlayerbot? current))
            {
                if (current.Revision != expectedRevision || current.AccountId != bot.AccountId
                    || current.CharacterId != bot.CharacterId || current.AccountName != bot.AccountName)
                    return Task.FromResult(false);
                if (_items.TryUpdate(bot.BotId, bot, current)) return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
    }
}
