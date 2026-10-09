using System.Collections.Concurrent;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class ManagedPlayerbotLifecycleTests
{
    [Fact]
    public async Task StoppedBots_DoNotTakeASlot_OnlyRunningBotsCountAgainstMaxBots()
    {
        // The live world of 2026-10-08 (MaxBots 10) refused '.playerbot create' with 5 bots running and 5 stopped.
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new MemoryManagedPlayerbotStore();
        await using WorldTestHost host = Start(accounts, characters, owners, configure: o => o.MaxBots = 2);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);

        Guid[] ids = new Guid[4];
        string[] names = ["Capone", "Captwo", "Capthree", "Capfour"];
        for (int i = 0; i < ids.Length; i++)
        {
            PlayerbotOperationResult created = await feature.CreateAsync(names[i], 1, 1);
            Assert.True(created.Success, $"{names[i]}: {created.Code}"); // was "playerbot-capacity" from the third on
            ids[i] = created.BotId!.Value;
        }

        Assert.True((await feature.StartAsync(ids[0].ToString())).Success);
        Assert.True((await feature.StartAsync(ids[1].ToString())).Success);
        Assert.Equal("playerbot-capacity", (await feature.StartAsync(ids[2].ToString())).Code);

        Assert.True((await feature.StopAsync(ids[0].ToString())).Success);
        PlayerbotOperationResult third = await feature.StartAsync(ids[2].ToString());
        Assert.True(third.Success, third.Code); // the stopped bot gave its slot back
        Assert.Equal("playerbot-capacity", (await feature.StartAsync(ids[3].ToString())).Code);
        await feature.ShutdownBeforeWorldStopAsync();
    }

    [Fact]
    public async Task TheStatusSnapshot_IsNotRebuiltWhileNothingInItChanges()
    {
        // Allocation per tick: the snapshot (one PlayerbotStatus per running bot) was rebuilt every tick, ~100 bytes per bot per
        // tick (docs/integration/perf-limits-20261008.md). A scripted bot that does nothing changes nothing in its status line.
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new MemoryManagedPlayerbotStore();
        await using WorldTestHost host = Start(accounts, characters, owners);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid id = (await feature.CreateAsync("Stillone", 1, 1)).BotId!.Value;
        Assert.True((await feature.StartScriptedAsync(id.ToString(), new IdleController())).Success);
        await host.WaitForWorldAsync(() => feature.Snapshot().Any(b => b.BotId == id && b.State == ManagedPlayerbotState.Running), "running in the snapshot");

        System.Reflection.FieldInfo field = typeof(ManagedPlayerbotFeature).GetField("_snapshot",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        object? before = await host.World.InvokeAsync(() => field.GetValue(feature));
        long ticks = host.World.Stats.Snapshot().TotalTicks;
        await host.WaitForWorldAsync(() => host.World.Stats.Snapshot().TotalTicks >= ticks + 10, "ten more ticks");
        object? after = await host.World.InvokeAsync(() => field.GetValue(feature));
        Assert.Same(before, after);

        Assert.True((await feature.StopAsync(id.ToString())).Success); // a change is still published at once
        Assert.Equal(ManagedPlayerbotState.Stopped, Assert.Single(feature.Snapshot(), b => b.BotId == id).State);
        await feature.ShutdownBeforeWorldStopAsync();
    }

    [Fact]
    public async Task ARefusedCreation_SaysWhy_InTheResultAndTheLog()
    {
        // The wave-9 rehearsal: '.playerbot create Bot7' answered "create-failed" and logged "(InvalidOperationException)" only.
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new MemoryManagedPlayerbotStore();
        var log = new CapturingLogger();
        await using WorldTestHost host = Start(accounts, characters, owners, logger: log);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();

        PlayerbotOperationResult refused = await feature.CreateAsync("Bot7", 1, 1);
        Assert.False(refused.Success);
        Assert.Equal("character-create-refused: the name may contain only letters of the realm's alphabet (no digits, symbols or mixed alphabets) (CharNameMixedLanguages)", refused.Code);
        Assert.Equal("Bot7", refused.Name);
        Assert.Contains(log.Entries, e => e.Message == "Managed playerbot creation of 'Bot7' failed: the name may contain only letters of the realm's alphabet (no digits, symbols or mixed alphabets) (CharNameMixedLanguages)");
        Assert.Empty(await owners.LoadAllAsync());

        // the refusal left nothing behind: a valid name is created next
        Assert.True((await feature.CreateAsync("Botseven", 1, 1)).Success);
    }

    [Fact]
    public void DescribeCreateRefusal_NamesTheRuleAndTheCode()
    {
        Assert.Equal("the account's character limit is reached (CharCreateAccountLimit)",
            ManagedPlayerbotFeature.DescribeCreateRefusal((int)ArcaneCore.Protocol.CharResult.CharCreateAccountLimit));
        Assert.Equal("the name is reserved (CharNameReserved)", ManagedPlayerbotFeature.DescribeCreateRefusal((int)ArcaneCore.Protocol.CharResult.CharNameReserved));
        Assert.Equal("no SMSG_CHAR_CREATE answer", ManagedPlayerbotFeature.DescribeCreateRefusal(-1));
        Assert.Equal("character creation failed (result 0x99)", ManagedPlayerbotFeature.DescribeCreateRefusal(0x99));
    }

    [Fact]
    public async Task MaxRegisteredBots_BoundsCreation()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new MemoryManagedPlayerbotStore();
        await using WorldTestHost host = Start(accounts, characters, owners, configure: o => o.MaxRegisteredBots = 2);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Assert.True((await feature.CreateAsync("Regone", 1, 1)).Success);
        Assert.True((await feature.CreateAsync("Regtwo", 1, 1)).Success);
        Assert.Equal("playerbot-registry-full", (await feature.CreateAsync("Regthree", 1, 1)).Code);
        Assert.Equal(2, (await owners.LoadAllAsync()).Count);
    }

    [Fact]
    public async Task MaxBots_ReadAtEachStart_ARaiseAdmitsMore_ALoweringStopsNobodyAndRefusesNewStarts()
    {
        // .reload config writes the running PlayerbotOptions (WorldConfigKeys: World:Playerbots:MaxBots is live).
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new MemoryManagedPlayerbotStore();
        PlayerbotOptions? options = null;
        await using WorldTestHost host = Start(accounts, characters, owners, configure: o => { o.MaxBots = 1; options = o; });
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid[] ids = new Guid[3];
        string[] names = ["Liveone", "Livetwo", "Livethree"];
        for (int i = 0; i < ids.Length; i++) ids[i] = (await feature.CreateAsync(names[i], 1, 1)).BotId!.Value;

        Assert.True((await feature.StartAsync(ids[0].ToString())).Success);
        Assert.Equal("playerbot-capacity", (await feature.StartAsync(ids[1].ToString())).Code);

        options!.MaxBots = 3;
        Assert.True((await feature.StartAsync(ids[1].ToString())).Success);
        Assert.True((await feature.StartAsync(ids[2].ToString())).Success);

        options.MaxBots = 1; // below the running count: everybody keeps running, new starts are refused
        await host.WaitForWorldAsync(() => feature.Snapshot().Count(b => b.State == ManagedPlayerbotState.Running) == 3, "three bots running");
        Assert.True((await feature.StopAsync(ids[2].ToString())).Success);
        Assert.Equal("playerbot-capacity", (await feature.StartAsync(ids[2].ToString())).Code);
        Assert.Equal(2, feature.Snapshot().Count(b => b.State == ManagedPlayerbotState.Running));
        await feature.ShutdownBeforeWorldStopAsync();
    }

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

    /// <summary>
    /// An operator '.playerbot stop' that takes the operation lock between the quarantine retry's snapshot and its start wins: the retry
    /// must not log the bot back in nor turn DesiredEnabled on again. The window is forced, step by step, on the manual world clock:
    /// the checkpoint that has the retry due is held inside the operation lock (a second, running bot's update), the operator stop
    /// queues on the lock and is held inside it (its bot lookup) once the checkpoint lets go, the checkpoint then schedules the retry
    /// (its "retry starting" line), and only then does the stop go on and remove the quarantine.
    /// <para>
    /// Before (2026-10-08, once under a loaded full run): the stop was released together with the checkpoint, and when it removed
    /// the quarantine before the checkpoint thread took its snapshot, no retry ever started and the wait for its refusal timed out.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AnOperatorStop_BetweenTheRetrySnapshotAndItsStart_StaysStopped()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new MemoryManagedPlayerbotStore();
        var log = new CapturingLogger();
        await using WorldTestHost host = Start(accounts, characters, owners, logger: log, manualClock: true,
            configure: o => { o.FaultBackoffSeconds = 1; o.MaxFaults = 3; });
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        Guid holder = Assert.IsType<Guid>((await feature.CreateAsync("Holdlock", 1, 1)).BotId);
        Assert.True((await feature.StartAsync(holder.ToString())).Success);
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync("Stoprace", 1, 1)).BotId);
        Assert.True((await feature.StartScriptedAsync(id.ToString(), new ThrowingController(1))).Success);

        // The bot faults on its first tick; the next checkpoint quarantines it with its retry due 1 s of game time later. Game time
        // stands still while a checkpoint runs, so that checkpoint never finds the retry due.
        await StepUntilAsync(host, feature, () => owners.Peek(id)?.State == ManagedPlayerbotState.Faulted, null, "the quarantine");
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains($"{id} quarantine retry starting"));

        // The next checkpoint (5 s of game time on, the retry due): hold it inside the operation lock at the running bot's update.
        TaskCompletionSource held = owners.HoldUpdatesOf(holder);
        await StepUntilAsync(host, feature, () => held.Task.IsCompleted, held.Task, "the checkpoint with the retry due");

        // The operator stop queues on the lock (the checkpoint holds it), and will wait inside it at its bot lookup.
        TaskCompletionSource looking = owners.HoldNextLoadAll();
        Task<PlayerbotOperationResult> stop = feature.StopAsync("Stoprace");
        Assert.False(stop.IsCompleted);
        owners.ReleaseHeldUpdates();
        await looking.Task.WaitAsync(TimeSpan.FromSeconds(30)); // the stop has the lock, the quarantine is still there

        // The checkpoint, out of the lock, schedules the retry; only then does the stop go on.
        await WaitAsync(() => Task.FromResult(log.Entries.Any(e => e.Message.Contains($"{id} quarantine retry starting"))),
            "the retry scheduled", 30);
        owners.ReleaseHeldLoadAll();
        Assert.True((await stop.WaitAsync(TimeSpan.FromSeconds(30))).Success);

        // The retry ran into the window and was refused under the lock (proof the race was exercised, not missed).
        await WaitAsync(() => Task.FromResult(log.Entries.Any(e => e.Message.Contains($"{id} quarantine retry ended (quarantine-cleared)"))),
            "the retry refused under the lock", 30);
        ManagedPlayerbot record = (await owners.FindAsync(id))!;
        Assert.False(record.DesiredEnabled, $"state {record.State}, error {record.ErrorCode}");
        Assert.NotEqual(ManagedPlayerbotState.Running, record.State);
        Assert.Null(await host.World.InvokeAsync(() => host.World.FindOnlinePlayer("Stoprace")));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains($"{id} left quarantine"));
    }

    /// <summary>
    /// The stored error code is echoed to chat by '.playerbot' status: an exception message's control characters (newlines, tabs) are
    /// replaced by spaces, and the 128-character cut never splits a surrogate pair.
    /// </summary>
    [Fact]
    public async Task AFaultMessage_IsStoredWithoutControlCharacters_AndCutOnACharacterBoundary()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new MemoryManagedPlayerbotStore();
        await using WorldTestHost host = Start(accounts, characters, owners);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync("Faultmsg", 1, 1)).BotId);
        // "quarantined (fault 1/3): action: " is 33 characters; 94 more put the emoji's high surrogate at index 127 (each control
        // character becomes one space, so the length is kept and the cut falls inside the pair).
        string message = "first\r\nsecond\t" + new string('x', 80) + "\U0001F600tail";
        Assert.True((await feature.StartScriptedAsync(id.ToString(), new ThrowingController(1, message))).Success);

        await WaitAsync(async () => (await owners.FindAsync(id))?.State == ManagedPlayerbotState.Faulted, "the fault is recorded", 20);

        string code = (await owners.FindAsync(id))!.ErrorCode!;
        Assert.StartsWith("quarantined (fault 1/3): action: first  second x", code);
        Assert.Equal(127, code.Length); // cut before the pair, not through it
        Assert.True(code.Length <= 128, code);
        Assert.DoesNotContain(code, char.IsControl);
        for (int i = 0; i < code.Length; i++)
        {
            if (char.IsHighSurrogate(code[i])) Assert.True(i + 1 < code.Length && char.IsLowSurrogate(code[i + 1]), $"lone high surrogate at {i}");
            else if (char.IsLowSurrogate(code[i])) Assert.True(i > 0 && char.IsHighSurrogate(code[i - 1]), $"lone low surrogate at {i}");
        }
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

    /// <summary>
    /// A restore at startup that cannot log the bot in (here: it is saved on a map outside AllowedMaps, as a bot that logged out in a
    /// dungeon would be) is a fault like any other: the bot
    /// stays desired and is retried, instead of losing DesiredEnabled because of one failed login.
    /// </summary>
    [Fact]
    public async Task AFailedRestoreOnStartup_KeepsTheBotDesired()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new MemoryManagedPlayerbotStore();
        Account owner = await accounts.CreateAsync(new Account { Username = "PBDUNGEON", Salt = new byte[32], Verifier = new byte[32], Security = AccountSecurity.Player });
        CharacterRecord character = await characters.CreateAsync(new CharacterRecord
        {
            AccountId = owner.Id, Name = "DungeonBot", Race = 1, Class = 1, Level = 10,
        });
        var bot = new ManagedPlayerbot(Guid.NewGuid(), owner.Id, character.Id, owner.Username, true,
            ManagedPlayerbotState.Stopped, PlayerbotGoalKind.Explore, 0, 0, 0, 1, 1);
        await owners.CreateAsync(bot);

        await using WorldTestHost host = Start(accounts, characters, owners, restoreOnStartup: true, configure: o => o.AllowedMaps = [1]);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);

        ManagedPlayerbot record = (await owners.FindAsync(bot.BotId))!;
        Assert.Equal(ManagedPlayerbotState.Faulted, record.State);
        Assert.True(record.DesiredEnabled);
        Assert.StartsWith("quarantined (fault 1/3): start-failed: login-refused", record.ErrorCode);
    }

    /// <summary>
    /// A bot in a real player's group is driven by its party AI (vmangos PartyBotAI), not the brain; when the group is gone the brain
    /// drives it again, a fresh one (the old one's routes and targets belong to another place), and the bot reports its own goals.
    /// </summary>
    [Fact]
    public async Task AGroupedBot_SwitchesToThePartyAI_AndBackToAFreshBrainWhenUngrouped()
    {
        await using WorldTestHost host = Party.PartyTestHost.Start(o => o.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone);
        Guid id = await Party.PartyTestHost.StartBotAsync(host, "Lifeparty");
        ManagedPlayerbotFeature feature = Party.PartyTestHost.Feature(host);
        Assert.False(feature.IsPartyDriven(id));
        await using WorldTestClient master = await host.EnterWorldAsync("LIFEMASTER", "Lifemaster");

        await master.SendAsync(Protocol.WorldOpcode.CmsgGroupInvite, Party.PartyTestHost.CString("Lifeparty"));
        await host.WaitForWorldAsync(() => feature.IsPartyDriven(id), "the party AI drives the grouped bot");
        await host.WaitForWorldAsync(() => feature.Snapshot().Single(s => s.BotId == id).Goal is PlayerbotGoalKind.Follow or PlayerbotGoalKind.Assist,
            "the bot reports its party goal");
        PlayerbotBrain grouped = (await host.OnWorldAsync(() => feature.FindBrain(id)))!;

        await master.SendAsync(Protocol.WorldOpcode.CmsgGroupDisband, []);
        await host.WaitForWorldAsync(() => !feature.IsPartyDriven(id), "the brain drives the ungrouped bot");
        Assert.NotSame(grouped, await host.OnWorldAsync(() => feature.FindBrain(id)));
        await host.WaitForWorldAsync(() => feature.Snapshot().Single(s => s.BotId == id).Goal is not (PlayerbotGoalKind.Follow or PlayerbotGoalKind.Assist),
            "the bot reports its own goal again");
        Assert.Null(await host.OnWorldAsync(() => feature.FindParty(id)));
    }

    /// <summary>
    /// A controller that takes over a grouped bot (scripted mode) drives it alone: the party AI lets go, so nothing reports the party
    /// goal, master or mode while it does. When the controller detaches, the bot (still grouped) is engaged afresh.
    /// </summary>
    [Fact]
    public async Task AControllerTakingOverAGroupedBot_ClearsThePartyState_UntilItDetaches()
    {
        await using WorldTestHost host = Party.PartyTestHost.Start(o => o.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone);
        Guid id = await Party.PartyTestHost.StartBotAsync(host, "Lifescript");
        ManagedPlayerbotFeature feature = Party.PartyTestHost.Feature(host);
        await using WorldTestClient master = await host.EnterWorldAsync("LIFESCRIPTM", "Lifescriptm");
        await master.SendAsync(Protocol.WorldOpcode.CmsgGroupInvite, Party.PartyTestHost.CString("Lifescript"));
        await host.WaitForWorldAsync(() => feature.IsPartyDriven(id), "the party AI drives the grouped bot");

        Assert.True(await feature.SetControllerAsync(id, new IdleController()));

        Assert.False(feature.IsPartyDriven(id));
        Assert.Null(await host.OnWorldAsync(() => feature.FindParty(id)));
        PlayerbotInspection scripted = (await feature.InspectAsync("Lifescript"))!;
        Assert.Null(scripted.Master);
        Assert.Null(scripted.PartyMode);
        Assert.False(scripted.Goal is PlayerbotGoalKind.Follow or PlayerbotGoalKind.Assist, scripted.Goal.ToString());
        await host.WaitForWorldAsync(() => feature.Snapshot().Single(s => s.BotId == id).Goal is not (PlayerbotGoalKind.Follow or PlayerbotGoalKind.Assist),
            "the snapshot reports no party goal");

        Assert.True(await feature.SetControllerAsync(id, null));
        await host.WaitForWorldAsync(() => feature.IsPartyDriven(id), "the party AI drives the still grouped bot again");
        Assert.Equal("Lifescriptm", (await feature.InspectAsync("Lifescript"))!.Master);
    }

    private sealed class IdleController : IPlayerbotController
    {
        public void Tick(PlayerbotControllerContext context, uint elapsedMs) { }

        public void Detached(Guid botId) { }
    }

    /// <summary>
    /// A master who logs out is waited for (<see cref="PlayerbotPartyOptions.MasterTimeoutSeconds"/>); then the bot leaves the group
    /// (vmangos requestRemoval) and goes back to the brain.
    /// </summary>
    [Fact]
    public async Task AMasterWhoLogsOut_IsWaitedFor_ThenTheBotLeavesTheGroupForTheBrain()
    {
        await using WorldTestHost host = Party.PartyTestHost.Start(o =>
        {
            o.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone;
            o.Party.MasterTimeoutSeconds = 1;
        });
        Guid id = await Party.PartyTestHost.StartBotAsync(host, "Lifewaiter");
        ManagedPlayerbotFeature feature = Party.PartyTestHost.Feature(host);
        WorldTestClient master = await host.EnterWorldAsync("LIFEGONE", "Lifegone");
        await master.SendAsync(Protocol.WorldOpcode.CmsgGroupInvite, Party.PartyTestHost.CString("Lifewaiter"));
        await host.WaitForWorldAsync(() => feature.IsPartyDriven(id), "the party AI drives the grouped bot");
        Game.ObjectGuid bot = await host.PlayerStateAsync("Lifewaiter", p => p.Guid);
        Game.Groups.GroupManager groups = host.WorldServices.GetRequiredService<ArcaneCore.World.Social.SocialFeature>().Context.Groups;

        await master.DisposeAsync();
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Lifegone") is null, "the master logs out");
        await host.WaitForWorldAsync(() => groups.GetGroup(bot) is null, "the bot leaves the group after the timeout");
        await host.WaitForWorldAsync(() => !feature.IsPartyDriven(id), "the brain drives the bot again");
    }

    /// <summary>
    /// Advance the manual world clock one tick at a time until <paramref name="done"/> holds, letting each checkpoint the tick started
    /// finish before game time moves on (or until <paramref name="stopOn"/> completes while one runs).
    /// </summary>
    private static async Task StepUntilAsync(WorldTestHost host, ManagedPlayerbotFeature feature, Func<bool> done, Task? stopOn, string what)
    {
        for (int tick = 0; tick < 2_000; tick++)
        {
            if (done()) return;
            await host.World.AdvanceClockAsync(50);
            Task checkpoint = await host.World.InvokeAsync(() => feature.LastCheckpoint);
            Task finished = stopOn is null ? checkpoint : await Task.WhenAny(checkpoint, stopOn);
            await finished.WaitAsync(TimeSpan.FromSeconds(30));
        }

        if (!done()) throw new TimeoutException($"timed out waiting for: {what} (100 s of game time)");
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
    private sealed class ThrowingController(int faults, string message = "fixture-action-fault") : IPlayerbotController
    {
        private int _remaining = faults;

        public void Tick(PlayerbotControllerContext context, uint elapsedMs)
        {
            if (Interlocked.Decrement(ref _remaining) >= 0) throw new InvalidOperationException(message);
        }

        public void Detached(Guid botId) { }
    }

    internal sealed class CapturingLogger : ILogger<ManagedPlayerbotFeature>
    {
        private readonly ConcurrentQueue<(Exception? Error, string Message)> _entries = new();

        public IReadOnlyList<(Exception? Error, string Message)> Entries => [.. _entries];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _entries.Enqueue((exception, formatter(state, exception)));
    }

    internal static WorldTestHost Start(InMemoryAccountStore accounts, InMemoryCharacterStore characters,
        MemoryManagedPlayerbotStore owners, MemoryProvisionStore? provisions = null,
        bool enabled = true, bool restoreOnStartup = false, CapturingLogger? logger = null, Action<PlayerbotOptions>? configure = null,
        bool manualClock = false)
        => WorldTestHost.Start(configureServices: services =>
        {
            if (manualClock) services.AddSingleton<IWorldFeature, ManualClock>();
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

    private sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    internal sealed class MemoryProvisionStore(InMemoryAccountStore accounts) : IManagedPlayerbotProvisionStore
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

    internal sealed class MemoryManagedPlayerbotStore : IManagedPlayerbotStore
    {
        private readonly ConcurrentDictionary<Guid, ManagedPlayerbot> _items = new();
        public bool FailNextCreate { get; set; }
        public bool FailNextUpdate { get; set; }

        private TaskCompletionSource? _loadHeld;
        private TaskCompletionSource _loadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The next <see cref="LoadAllAsync"/> waits until <see cref="ReleaseHeldLoadAll"/>; the returned task completes when it is waiting.</summary>
        public TaskCompletionSource HoldNextLoadAll()
        {
            _loadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _loadHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseHeldLoadAll() => _loadRelease.TrySetResult();

        public async Task<IReadOnlyList<ManagedPlayerbot>> LoadAllAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _loadHeld, null) is { } held)
            {
                held.TrySetResult();
                await _loadRelease.Task.WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
            }

            return _items.Values.OrderBy(item => item.BotId).ToArray();
        }

        /// <summary>The stored record, read without any hold (any thread).</summary>
        public ManagedPlayerbot? Peek(Guid botId) => _items.GetValueOrDefault(botId);

        public Task<ManagedPlayerbot?> FindAsync(Guid botId, CancellationToken cancellationToken = default)
            => Task.FromResult(_items.GetValueOrDefault(botId));

        public Task CreateAsync(ManagedPlayerbot bot, CancellationToken cancellationToken = default)
        {
            if (FailNextCreate) { FailNextCreate = false; throw new InvalidOperationException("fixture-create-failure"); }
            if (!_items.TryAdd(bot.BotId, bot)) throw new InvalidOperationException("duplicate-owner");
            return Task.CompletedTask;
        }

        private Guid? _holdBot;
        private TaskCompletionSource? _held;
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The next update of <paramref name="botId"/> waits until <see cref="ReleaseHeldUpdates"/>; the returned task completes when it is waiting.</summary>
        public TaskCompletionSource HoldUpdatesOf(Guid botId)
        {
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _holdBot = botId;
            return _held;
        }

        public void ReleaseHeldUpdates()
        {
            _holdBot = null;
            _release.TrySetResult();
        }

        public async Task<bool> UpdateAsync(ManagedPlayerbot bot, long expectedRevision, CancellationToken cancellationToken = default)
        {
            if (_holdBot == bot.BotId && _held is { } held)
            {
                held.TrySetResult();
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            }

            return Update(bot, expectedRevision);
        }

        private bool Update(ManagedPlayerbot bot, long expectedRevision)
        {
            if (FailNextUpdate) { FailNextUpdate = false; throw new InvalidOperationException("fixture-update-failure"); }
            while (_items.TryGetValue(bot.BotId, out ManagedPlayerbot? current))
            {
                if (current.Revision != expectedRevision || current.AccountId != bot.AccountId
                    || current.CharacterId != bot.CharacterId || current.AccountName != bot.AccountName)
                    return false;
                if (_items.TryUpdate(bot.BotId, bot, current)) return true;
            }

            return false;
        }
    }
}
