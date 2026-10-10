using System.Text;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Creatures;

/// <summary>
/// <see cref="SmartAreaTriggerFeature"/> in a running world: discovered as an <see cref="IAreaTriggerListener"/> (the seam TeleportHandlers walks on
/// CMSG_AREATRIGGER), it reads the catalog from the creature feature at use time and runs source type 2 rows for the invoking player
/// (AzerothCore SmartTrigger, SmartAI.cpp:1586-1601; docs/integration/smartai-slice2-20261010.md).
/// </summary>
public sealed class SmartAreaTriggerFeatureTests
{
    private const uint SpeakerEntry = 302;
    private const int TriggerId = 100;
    private const string PlayerName = "Smarta";

    // Type 2 row: event 46 AREATRIGGER_ONTRIGGER (param1 0 = any), action 1 TALK broadcast text 9001, target 19 CLOSEST_CREATURE of the speaker's entry.
    private static SmartScriptRow TalkRow(int trigger) => new()
    {
        EntryOrGuid = trigger, SourceType = 2, Id = 0, EventType = (byte)SmartEvent.AreaTriggerOnTrigger,
        ActionType = (byte)SmartAction.Talk, ActionParam1 = 9001,
        TargetType = (byte)SmartTarget.ClosestCreature, TargetParam1 = SpeakerEntry,
    };

    private static WorldTestHost Start(params SmartScriptRow[] rows)
    {
        var speaker = new CreatureTemplate { Entry = SpeakerEntry, Name = "Speaker", MinLevel = 2, MaxLevel = 2, DisplayIds = [903], Faction = 35, MinLevelHealth = 55, MaxLevelHealth = 55 };
        // Ten yards from the human start (-8949.95, -132.49).
        var spawn = new CreatureSpawn { Guid = 4243, Entry = SpeakerEntry, MapId = 0, X = -8940f, Y = -132f, Z = 83.5f };
        var content = new CreatureContent([speaker], [spawn], [], [], [],
            new CreatureAiContent([], [], new BroadcastTextCatalog([new BroadcastText(9001, "Grr, $N!", "", 0, 0, 0, [0, 0, 0], [0, 0, 0])]),
                [], EventAiDialect.CMangos, [])
            { SmartScripts = new SmartScriptCatalog(rows) });
        var context = new CreatureTestContext(content);
        CreatureTestStore.Current.Value = context;
        try
        {
            return WorldTestHost.Start();
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }
    }

    private static SmartAreaTriggerFeature FeatureOf(WorldTestHost host)
        => host.WorldServices.GetServices<IWorldFeature>().OfType<SmartAreaTriggerFeature>().Single();

    private static async Task WaitForContentAsync(WorldTestHost host)
        => await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<CreatureWorldFeature>().ContentInstalled, "the creature content is installed");

    [Fact]
    public void Feature_IsDiscoveredAsAnAreaTriggerListener()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var feature = new SmartAreaTriggerFeature(services);
        Assert.IsAssignableFrom<IAreaTriggerListener>(feature);
        Assert.IsAssignableFrom<IWorldFeature>(feature);
        Assert.Throws<ArgumentNullException>(() => feature.Attach(null!));
    }

    [Fact]
    public async Task OnAreaTrigger_RunsTheTriggersRows_ForTheInvokingPlayer()
    {
        await using WorldTestHost host = Start(TalkRow(TriggerId));
        await using WorldTestClient client = await host.EnterWorldAsync("SMARTA", PlayerName);
        await WaitForContentAsync(host);
        Player player = await host.PlayerAsync(PlayerName);

        await host.OnWorldAsync(() => ((IAreaTriggerListener)FeatureOf(host)).OnAreaTrigger(player, TriggerId));

        byte[] chat = await client.ReadUntilAsync(WorldOpcode.SmsgMessagechat);
        string text = Encoding.UTF8.GetString(chat);
        Assert.Contains("Speaker", text, StringComparison.Ordinal);                 // the closest creature of the entry speaks
        Assert.Contains($"Grr, {PlayerName}!", text, StringComparison.Ordinal);     // "$N" is the invoking player
    }

    [Fact]
    public async Task OnAreaTrigger_WithoutRowsForTheId_SaysNothing()
    {
        await using WorldTestHost host = Start(TalkRow(TriggerId));
        await using WorldTestClient client = await host.EnterWorldAsync("SMARTB", PlayerName);
        await WaitForContentAsync(host);
        Player player = await host.PlayerAsync(PlayerName);

        // A trigger with no rows first, then the one with rows, in one world call: exactly one chat arrives, so the first run said nothing.
        await host.OnWorldAsync(() =>
        {
            IAreaTriggerListener listener = FeatureOf(host);
            listener.OnAreaTrigger(player, TriggerId + 1);
            listener.OnAreaTrigger(player, TriggerId);
        });

        byte[] first = await client.ReadUntilAsync(WorldOpcode.SmsgMessagechat);
        Assert.Contains($"Grr, {PlayerName}!", Encoding.UTF8.GetString(first), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnAreaTrigger_WithAnEmptyCatalogOrNoCreatureFeature_RunsNothing()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient client = await host.EnterWorldAsync("SMARTC", PlayerName);
        await WaitForContentAsync(host);
        Player player = await host.PlayerAsync(PlayerName);

        // The empty catalog of the running creature feature.
        await host.OnWorldAsync(() => ((IAreaTriggerListener)FeatureOf(host)).OnAreaTrigger(player, TriggerId));

        // A container with no creature feature at all falls back to the empty catalog rather than throwing.
        using var bare = new ServiceCollection().BuildServiceProvider();
        await host.OnWorldAsync(() => new SmartAreaTriggerFeature(bare).OnAreaTrigger(player, TriggerId));

        Assert.Throws<ArgumentNullException>(() => new SmartAreaTriggerFeature(bare).OnAreaTrigger(null!, TriggerId));
    }
}
