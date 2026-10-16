using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A bot start that the caller cancels (the World is asked to stop while it restores its bots) is not the bot's fault: its record is left as it was so the next
/// start restores it. Any other cancellation (an internal timeout) is still a fault.
/// </summary>
public sealed class ManagedPlayerbotStartCancelTests
{
    public enum Path { RestoreOnStartup, OperatorStart }

    private sealed record Fixture(WorldTestHost Host, ManagedPlayerbotFeature Feature,
        ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore Owners, ManagedPlayerbot Bot);

    private static async Task<Fixture> StartAsync(string name)
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        Account account = await accounts.CreateAsync(new Account { Username = "PB" + name.ToUpperInvariant(), Salt = new byte[32], Verifier = new byte[32], Security = AccountSecurity.Player });
        CharacterRecord character = await characters.CreateAsync(new CharacterRecord { AccountId = account.Id, Name = name, Race = 1, Class = 1, Level = 1 });
        var bot = new ManagedPlayerbot(Guid.NewGuid(), account.Id, character.Id, account.Username, true,
            ManagedPlayerbotState.Stopped, PlayerbotGoalKind.Explore, 0, 0, 0, 1, 1);
        await owners.CreateAsync(bot);
        // Faults never expire inside the test and a single fault does not disable the bot, so a recorded fault stays visible as a quarantine entry.
        WorldTestHost host = ManagedPlayerbotLifecycleTests.Start(accounts, characters, owners, new ManagedPlayerbotLifecycleTests.MemoryProvisionStore(accounts),
            restoreOnStartup: true, configure: o => { o.MaxFaults = 3; o.FaultBackoffSeconds = 3600; });
        return new(host, host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>(), owners, bot);
    }

    private static Task RunAsync(Fixture f, Path path, CancellationToken token)
        => path == Path.RestoreOnStartup
            ? f.Feature.StartupAsync(token)
            : f.Feature.StartAsync(f.Bot.BotId.ToString(), token);

    [Theory]
    [InlineData(Path.RestoreOnStartup)]
    [InlineData(Path.OperatorStart)]
    public async Task ACallerCancelledStart_IsNotAFault_AndLeavesTheRecordUntouched(Path path)
    {
        Fixture f = await StartAsync("Cancelme");
        await using WorldTestHost host = f.Host;
        using var cts = new CancellationTokenSource();
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Inside the try block, after the operation lock: the first write of the start waits for the caller's token.
        f.Owners.BeforeUpdate = async (bot, token) =>
        {
            if (bot.State != ManagedPlayerbotState.Starting) return;
            parked.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };

        Task run = RunAsync(f, path, cts.Token);
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(30)));
        ManagedPlayerbot stored = f.Owners.Peek(f.Bot.BotId)!;
        Assert.NotEqual(ManagedPlayerbotState.Faulted, stored.State);
        Assert.True(stored.DesiredEnabled);
        Assert.Equal(f.Bot.ErrorCode, stored.ErrorCode);
        Assert.Equal(f.Bot, stored);
        Assert.Equal(0, f.Feature.QuarantineCount);
        Assert.False(f.Feature.IsActive(f.Bot.BotId));
    }

    [Theory]
    [InlineData(Path.RestoreOnStartup)]
    [InlineData(Path.OperatorStart)]
    public async Task ACancellationThatIsNotTheCallers_IsStillAFault(Path path)
    {
        Fixture f = await StartAsync("Timedout");
        await using WorldTestHost host = f.Host;
        // An internal timeout surfaces as an OperationCanceledException while the caller's token is untouched.
        f.Owners.BeforeUpdate = (bot, _) => bot.State == ManagedPlayerbotState.Starting
            ? Task.FromException(new OperationCanceledException("internal-timeout"))
            : Task.CompletedTask;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(f, path, CancellationToken.None));

        ManagedPlayerbot stored = f.Owners.Peek(f.Bot.BotId)!;
        Assert.Equal(ManagedPlayerbotState.Faulted, stored.State);
        Assert.Contains("start-failed", stored.ErrorCode);
        // A restore is a quarantine-style start (one more fault, bot stays desired); an operator start is reported and not retried.
        Assert.Equal(path == Path.RestoreOnStartup, stored.DesiredEnabled);
        Assert.Equal(path == Path.RestoreOnStartup ? 1 : 0, f.Feature.QuarantineCount);
    }
}
