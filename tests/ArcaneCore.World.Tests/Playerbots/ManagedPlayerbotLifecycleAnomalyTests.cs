using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.ManagedPlayerbotLifecycleTests;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// H1 hardening: <c>bot-lifecycle/session-lost</c>, <c>think-fault</c>, <c>quarantined</c> and <c>disabled</c>, reported by
/// <see cref="ManagedPlayerbotFeature"/> when a bot's session ends without a stop, its update throws, and the fault policy puts it aside or
/// takes it out. Same harness as <see cref="ManagedPlayerbotLifecycleTests"/> (real feature, real world, in-memory stores). The ledger is
/// process-wide, so each capture filters on the one bot's name (names unique to this class).
/// </summary>
public sealed class ManagedPlayerbotLifecycleAnomalyTests
{
    private static readonly string SessionLost = PlayerbotAnomalies.Key("lifecycle", "session-lost");
    private static readonly string ThinkFault = PlayerbotAnomalies.Key("lifecycle", "think-fault");
    private static readonly string Quarantined = PlayerbotAnomalies.Key("lifecycle", "quarantined");
    private static readonly string Disabled = PlayerbotAnomalies.Key("lifecycle", "disabled");

    private static AnomalyCapture CaptureOf(string name)
        => AnomalyLedger.Shared.Capture(n => n.Subsystem == "bot-lifecycle" && n.Subject.Contains($"bot {name} ", StringComparison.Ordinal));

    private static async Task WaitAsync(Func<Task<bool>> condition, string what, int seconds = 20)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out waiting for: {what}");
            await Task.Delay(20);
        }
    }

    private sealed class Idle : IPlayerbotController
    {
        public void Tick(PlayerbotControllerContext context, uint elapsedMs) { }

        public void Detached(Guid botId) { }
    }

    [Fact]
    public async Task AnActionFault_FiresThinkFault_AndTheFirstOfThreeAllowedFaults_FiresQuarantined()
    {
        var owners = new MemoryManagedPlayerbotStore();
        await using WorldTestHost host = Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            configure: o => { o.FaultBackoffSeconds = 300; o.MaxFaults = 3; });
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        using AnomalyCapture capture = CaptureOf("Lifeq");
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync("Lifeq", 1, 1)).BotId);

        Assert.True((await feature.StartScriptedAsync(id.ToString(), new ThrowingController(1, "anomaly-think-fault"))).Success);
        await WaitAsync(async () => (await owners.FindAsync(id))?.State == ManagedPlayerbotState.Faulted, "the quarantine");

        Assert.Equal(1, capture.Count(ThinkFault));
        Assert.Equal(1, capture.Count(Quarantined));
        Assert.False(capture.Any(Disabled), capture.Describe());
        Assert.False(capture.Any(SessionLost), capture.Describe());
        AnomalyNotice fault = capture.Notices.Single(n => n.Key == ThinkFault);
        Assert.Equal(AnomalySeverity.Warning, fault.Severity);
        Assert.Contains($"bot {id:D}", fault.Detail, StringComparison.Ordinal);
        Assert.Contains("anomaly-think-fault", fault.Detail, StringComparison.Ordinal);
        AnomalyNotice quarantine = capture.Notices.Single(n => n.Key == Quarantined);
        Assert.Equal(AnomalySeverity.Warning, quarantine.Severity);
        Assert.Contains("fault 1 of 3", quarantine.Detail, StringComparison.Ordinal);
        Assert.Contains("quarantined for 300 s", quarantine.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLastAllowedFault_FiresDisabledAtErrorSeverity_InsteadOfQuarantined()
    {
        var owners = new MemoryManagedPlayerbotStore();
        await using WorldTestHost host = Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners,
            configure: o => { o.FaultBackoffSeconds = 300; o.MaxFaults = 1; });
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        using AnomalyCapture capture = CaptureOf("Lifed");
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync("Lifed", 1, 1)).BotId);

        Assert.True((await feature.StartScriptedAsync(id.ToString(), new ThrowingController(1))).Success);
        await WaitAsync(async () => (await owners.FindAsync(id))?.State == ManagedPlayerbotState.Faulted, "the fault");

        Assert.Equal(1, capture.Count(ThinkFault));
        Assert.Equal(1, capture.Count(Disabled));
        Assert.False(capture.Any(Quarantined), capture.Describe());
        AnomalyNotice notice = capture.Notices.Single(n => n.Key == Disabled);
        Assert.Equal(AnomalySeverity.Error, notice.Severity);
        Assert.Contains("disabled after 1 faults", notice.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASessionThatEndsUnderTheFeature_FiresSessionLost_Once()
    {
        var owners = new MemoryManagedPlayerbotStore();
        await using WorldTestHost host = Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        using AnomalyCapture capture = CaptureOf("Lifelost");
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync("Lifelost", 1, 1)).BotId);
        Assert.True((await feature.StartScriptedAsync(id.ToString(), new Idle())).Success);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Lifelost") is not null, "the bot in the world");

        // The session is closed from outside (not by a stop, not by a fault of the bot's own): the feature still has the bot registered.
        await host.OnWorldAsync(() => { ((WorldSession)host.World.FindOnlinePlayer("Lifelost")!.Session).Kick(); return true; });
        await WaitAsync(() => Task.FromResult(capture.Any(SessionLost)), "the lost session to be reported");
        await WaitAsync(async () => (await owners.FindAsync(id))?.State != ManagedPlayerbotState.Running, "the feature to notice and stop the bot");

        Assert.Equal(1, capture.Count(SessionLost));
        Assert.False(capture.Any(ThinkFault) || capture.Any(Quarantined) || capture.Any(Disabled), capture.Describe());
        AnomalyNotice notice = capture.Notices.Single();
        Assert.Equal(AnomalySeverity.Warning, notice.Severity);
        Assert.Contains("session lost while registered active", notice.Detail, StringComparison.Ordinal);
        Assert.Contains($"bot {id:D}", notice.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHealthyBot_AndAnOperatorStop_ReportNothing()
    {
        var owners = new MemoryManagedPlayerbotStore();
        await using WorldTestHost host = Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), owners);
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);
        using AnomalyCapture capture = CaptureOf("Lifefine");
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync("Lifefine", 1, 1)).BotId);
        Assert.True((await feature.StartScriptedAsync(id.ToString(), new Idle())).Success);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Lifefine") is not null, "the bot in the world");
        await Task.Delay(TimeSpan.FromSeconds(1)); // ten thinks and a checkpoint or more

        Assert.True((await feature.StopAsync(id.ToString())).Success);
        await WaitAsync(async () => (await owners.FindAsync(id))?.State == ManagedPlayerbotState.Stopped, "the stop");
        await Task.Delay(TimeSpan.FromSeconds(1)); // past the next checkpoint: a stopped bot is not a lost session

        Assert.True(capture.Notices.Count == 0, capture.Describe());
    }

    [Fact]
    public async Task ARestoreThatCannotLogTheBotIn_FiresQuarantined_ForTheStartFailure()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new MemoryManagedPlayerbotStore();
        Account owner = await accounts.CreateAsync(new Account { Username = "PBLIFEANOM", Salt = new byte[32], Verifier = new byte[32], Security = AccountSecurity.Player });
        CharacterRecord character = await characters.CreateAsync(new CharacterRecord { AccountId = owner.Id, Name = "Lifenomap", Race = 1, Class = 1, Level = 10 });
        var bot = new ManagedPlayerbot(Guid.NewGuid(), owner.Id, character.Id, owner.Username, true,
            ManagedPlayerbotState.Stopped, PlayerbotGoalKind.Explore, 0, 0, 0, 1, 1);
        await owners.CreateAsync(bot);
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Subsystem == "bot-lifecycle" && n.Subject.Contains("bot Lifenomap ", StringComparison.Ordinal));

        // Saved on a map outside AllowedMaps (as after a dungeon logout): the login is refused before any player exists.
        await using WorldTestHost host = Start(accounts, characters, owners, restoreOnStartup: true, configure: o => { o.AllowedMaps = [1]; o.FaultBackoffSeconds = 300; });
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        await feature.StartupAsync(default);

        Assert.Equal(ManagedPlayerbotState.Faulted, (await owners.FindAsync(bot.BotId))!.State);
        Assert.Equal(1, capture.Count(Quarantined));
        Assert.False(capture.Any(Disabled) || capture.Any(ThinkFault) || capture.Any(SessionLost), capture.Describe());
        AnomalyNotice notice = capture.Notices.Single();
        Assert.Contains("start-failed: login-refused", notice.Detail, StringComparison.Ordinal);
        Assert.Contains("fault 1 of 3", notice.Detail, StringComparison.Ordinal);
    }
}
