using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class ManagedPlayerbotReplaceBrainSetAsideTests
{
    [Fact]
    public async Task AReplacedBrain_KeepsTheStrikesTheTurnInCleared_NotTheOnesReadAtStartup()
    {
        var store = new MemorySetAsideStore();
        await using WorldTestHost host = PartyTestHost.Start(o =>
        {
            o.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone;
            o.Party.MasterTimeoutSeconds = 1;
        }, services => services.AddSingleton<IManagedPlayerbotSetAsideStore>(store));
        Guid id = await PartyTestHost.StartBotAsync(host, "Rbstrikes");
        ManagedPlayerbotFeature feature = PartyTestHost.Feature(host);
        var key = new DeferKey(DeferKind.UnreachableQuest, 1361);

        // Two diagnoses, each over at once: strikes the bot keeps. A stop saves them and the next start restores them.
        await host.World.InvokeAsync(() =>
        {
            long now = PlayerbotWorldClock.UptimeMs(host.World);
            PlayerbotDeferLedger ledger = feature.FindBrain(id)!.Suspensions.Ledger;
            ledger.Defer(key, now, untilMs: now);
            ledger.Defer(key, now, untilMs: now);
            return true;
        });
        Assert.True((await feature.StopAsync(id.ToString())).Success);
        Assert.Equal(2, Assert.Single(await store.LoadAllAsync(), row => row.Kind == PlayerbotSetAsideKind.UnreachableQuest).RepeatCount);
        Assert.True((await feature.StartAsync(id.ToString())).Success);
        await host.WaitForWorldAsync(() => feature.FindBrain(id) is not null, "the bot runs again");
        Assert.Equal(2, await host.World.InvokeAsync(() => feature.FindBrain(id)!.Suspensions.Ledger.Strikes(key)));

        // The quest is turned in: the strikes go. The rows read at startup are older than that and must not come back.
        await host.World.InvokeAsync(() => { feature.FindBrain(id)!.Suspensions.QuestSucceeded(1361); return true; });
        await feature.StartupAsync(default); // reads the store again: the stale rows are the startup rows of the feature

        WorldTestClient master = await host.EnterWorldAsync("RBMASTER", "Rbmaster");
        await master.SendAsync(WorldOpcode.CmsgGroupInvite, PartyTestHost.CString("Rbstrikes"));
        await host.WaitForWorldAsync(() => feature.IsPartyDriven(id), "the party AI drives the grouped bot");
        await master.DisposeAsync();
        await host.WaitForWorldAsync(() => !feature.IsPartyDriven(id) && feature.FindBrain(id) is not null, "a fresh brain drives the bot again");

        Assert.Equal(0, await host.World.InvokeAsync(() => feature.FindBrain(id)!.Suspensions.Ledger.Strikes(key)));
        await feature.ShutdownBeforeWorldStopAsync();
    }

    private sealed class MemorySetAsideStore : IManagedPlayerbotSetAsideStore
    {
        private readonly Dictionary<Guid, PlayerbotSetAside[]> _rows = [];

        public Task<IReadOnlyList<PlayerbotSetAside>> LoadAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PlayerbotSetAside>>([.. _rows.Values.SelectMany(rows => rows)]);

        public Task ReplaceAsync(Guid botId, IReadOnlyList<PlayerbotSetAside> rows, CancellationToken cancellationToken = default)
        {
            _rows[botId] = [.. rows];
            return Task.CompletedTask;
        }
    }
}
