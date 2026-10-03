using System.Text;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.Protocol;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Reload;

/// <summary>
/// The NPC service table reloads (vmangos ServerCommands.cpp:1071-1091 and :1264-1311 → ObjectMgr loaders, ObjectMgr.cpp:6827,
/// 9081, 10600, 10780, 10865, 10931, 11013): one table of the live <see cref="NpcStore"/> is replaced from the content tables, the
/// others keep what is live. Each test changes a row between the first load and the reload and reads it through the live services.
/// </summary>
public sealed class NpcReloadTests
{
    private const uint Spawn = 7001;
    private const uint Entry = 7002;
    private const uint Menu = 7003;
    private const uint TextId = 7004;
    private const uint PoiId = 7005;

    private static readonly string[] AllTables = ["npc_gossip", "npc_text", "npc_trainer", "npc_vendor", "points_of_interest", "gossip_menu", "gossip_menu_option"];

    public static TheoryData<string> Tables => [.. AllTables];

    private static NpcText Text(string text) => new() { Id = TextId, Options = [new NpcTextOption(1, text, "", 0, 0, 0, 0, 0, 0, 0)] };

    /// <summary>The state of every table, "old" until <see cref="Change"/>.</summary>
    private static ReloadContentFixture Content()
    {
        var content = new ReloadContentFixture();
        Fill(content, "old", 1);
        return content;
    }

    private static void Fill(ReloadContentFixture c, string label, uint number)
    {
        c.NpcGossips.Clear();
        c.NpcGossips.Add(new NpcGossip { NpcGuid = Spawn, TextId = 100 + number });
        c.NpcTexts.Clear();
        c.NpcTexts.Add(Text(label));
        c.TrainerSpells.Clear();
        c.TrainerSpells.Add(new TrainerSpell { Entry = Entry, Spell = 200 + number });
        c.VendorItems.Clear();
        c.VendorItems.Add(new VendorItem { Entry = Entry, Item = 300 + number });
        c.PointsOfInterest.Clear();
        c.PointsOfInterest.Add(new PointOfInterest { Entry = PoiId, IconName = label });
        c.GossipMenus.Clear();
        c.GossipMenus.Add(new GossipMenu { Entry = Menu, TextId = 400 + number });
        c.GossipMenuOptions.Clear();
        c.GossipMenuOptions.Add(new GossipMenuOption { MenuId = Menu, Id = 0, OptionText = label });
    }

    private static string Observe(NpcStore store, string table) => table switch
    {
        "npc_gossip" => store.NpcGossipText(Spawn).ToString(),
        "npc_text" => store.Text(TextId)?.Options[0].Text0 ?? string.Empty,
        "npc_trainer" => string.Join(",", store.TrainerSpells(Entry).Select(t => t.Spell)),
        "npc_vendor" => string.Join(",", store.VendorItems(Entry).Select(v => v.Item)),
        "points_of_interest" => store.Poi(PoiId)?.IconName ?? string.Empty,
        "gossip_menu" => string.Join(",", store.MenuTexts(Menu).Select(m => m.TextId)),
        "gossip_menu_option" => string.Join(",", store.MenuOptions(Menu).Select(o => o.OptionText)),
        _ => throw new ArgumentOutOfRangeException(nameof(table)),
    };

    private static string Old(string table) => table switch
    {
        "npc_gossip" => "101",
        "npc_trainer" => "201",
        "npc_vendor" => "301",
        "gossip_menu" => "401",
        _ => "old",
    };

    private static string New(string table) => table switch
    {
        "npc_gossip" => "102",
        "npc_trainer" => "202",
        "npc_vendor" => "302",
        "gossip_menu" => "402",
        _ => "new",
    };

    private static void Empty(ReloadContentFixture c, string table)
    {
        switch (table)
        {
            case "npc_gossip": c.NpcGossips.Clear(); break;
            case "npc_text": c.NpcTexts.Clear(); break;
            case "npc_trainer": c.TrainerSpells.Clear(); break;
            case "npc_vendor": c.VendorItems.Clear(); break;
            case "points_of_interest": c.PointsOfInterest.Clear(); break;
            case "gossip_menu": c.GossipMenus.Clear(); break;
            case "gossip_menu_option": c.GossipMenuOptions.Clear(); break;
            default: throw new ArgumentOutOfRangeException(nameof(table));
        }
    }

    private static ReloadCoordinator Coordinator(WorldTestHost host) => host.WorldServices.GetRequiredService<ReloadFeature>().Coordinator;

    private static QuestNpcServices Services(WorldTestHost host) => host.WorldServices.GetRequiredService<QuestNpcFeature>().Services;

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task AChangedRow_IsSeenThroughTheLiveServices(string table)
    {
        ReloadContentFixture content = Content();
        await using WorldTestHost host = ReloadContentTestServices.Start(content);
        QuestNpcServices services = Services(host);
        Assert.Equal(Old(table), Observe(services.Npcs, table));
        Fill(content, "new", 2);

        ReloadResult result = await Coordinator(host).ReloadAsync(table);

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(New(table), Observe(services.Npcs, table));
        Assert.Contains("1 ", result.Message);
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task OnlyTheNamedTableChanges(string table)
    {
        ReloadContentFixture content = Content();
        await using WorldTestHost host = ReloadContentTestServices.Start(content);
        QuestNpcServices services = Services(host);
        Fill(content, "new", 2);

        await Coordinator(host).ReloadAsync(table);

        foreach (string other in AllTables)
        {
            Assert.Equal(other == table ? New(other) : Old(other), Observe(services.Npcs, other));
        }
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task AnEmptyTable_EmptiesTheRows_AsVmangosDoes(string table)
    {
        // The loaders clear their map first (ObjectMgr.cpp:10867, 6829, 10604-10607, 10784-10787, 9083, 10933, 11015).
        ReloadContentFixture content = Content();
        await using WorldTestHost host = ReloadContentTestServices.Start(content);
        QuestNpcServices services = Services(host);
        Empty(content, table);

        ReloadResult result = await Coordinator(host).ReloadAsync(table);

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.True(string.IsNullOrEmpty(Observe(services.Npcs, table)) || Observe(services.Npcs, table) == "0", Observe(services.Npcs, table));
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public async Task AnEmptyTable_KeepsTheRows_WhenTheOptionSaysKeepLoaded(string table)
    {
        ReloadContentFixture content = Content();
        await using WorldTestHost host = ReloadContentTestServices.Start(content);
        host.WorldServices.GetRequiredService<ReloadFeature>().Options.EmptyTables = EmptyTablePolicy.KeepLoaded;
        QuestNpcServices services = Services(host);
        NpcStore before = services.Npcs;
        Empty(content, table);

        ReloadResult result = await Coordinator(host).ReloadAsync(table);

        Assert.Equal(ReloadStatus.KeptCurrent, result.Status);
        Assert.Same(before, services.Npcs);
        Assert.Equal(Old(table), Observe(services.Npcs, table));
    }

    [Fact]
    public async Task ANpcTextQuery_FromAnOnlineClient_ReturnsTheReloadedText()
    {
        ReloadContentFixture content = Content();
        await using WorldTestHost host = ReloadContentTestServices.Start(content);
        await using WorldTestClient client = await host.EnterWorldAsync("RNCLIENT", "Rnclient");
        await client.CollectAsync();
        Assert.Contains("old", await QueryTextAsync(client));
        content.NpcTexts[0] = Text("Greetings, reloaded traveller.");

        ReloadResult result = await Coordinator(host).ReloadAsync("npc_text");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Contains("Greetings, reloaded traveller.", await QueryTextAsync(client));
    }

    private static async Task<string> QueryTextAsync(WorldTestClient client)
    {
        var writer = new PacketWriter(12);
        writer.WriteUInt32(TextId);
        writer.WriteUInt64(0);
        await client.SendAsync(WorldOpcode.CmsgNpcTextQuery, writer.ToArray());
        return Encoding.UTF8.GetString(await client.ReadUntilAsync(WorldOpcode.SmsgNpcTextUpdate));
    }

    [Fact]
    public async Task ATableThatWasNotReloaded_KeepsTheFlightNetworkAndEverythingElse()
    {
        ReloadContentFixture content = Content();
        await using WorldTestHost host = ReloadContentTestServices.Start(content);
        QuestNpcServices services = Services(host);
        NpcContent before = services.Npcs.Content;
        Fill(content, "new", 2);

        await Coordinator(host).ReloadAsync("npc_vendor");

        NpcContent after = services.Npcs.Content;
        Assert.Same(before.TaxiNodes, after.TaxiNodes);
        Assert.Same(before.TaxiPaths, after.TaxiPaths);
        Assert.Same(before.RaceTaxiStarts, after.RaceTaxiStarts);
        Assert.Same(before.TrainerSpells, after.TrainerSpells);
        Assert.NotSame(before.VendorItems, after.VendorItems);
    }

    [Fact]
    public async Task AFailingStore_ChangesNothing()
    {
        ReloadContentFixture content = Content();
        await using WorldTestHost host = ReloadContentTestServices.Start(content);
        QuestNpcServices services = Services(host);
        NpcStore before = services.Npcs;
        content.Failure = new InvalidOperationException("world database unavailable");

        ReloadResult result = await Coordinator(host).ReloadAsync("npc_vendor");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Contains("world database unavailable", result.Message);
        Assert.Same(before, services.Npcs);
    }

    [Fact]
    public async Task WithoutAnNpcContentStore_TheReloadFails()
    {
        await using var host = WorldTestHost.Start();

        ReloadResult result = await Coordinator(host).ReloadAsync("npc_vendor");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Contains("NPC content store", result.Message);
    }

    [Fact]
    public async Task AFailureLaterInTheSwap_PutsTheOldStoreBack()
    {
        ReloadContentFixture content = Content();
        await using WorldTestHost host = ReloadContentTestServices.Start(content);
        QuestNpcServices services = Services(host);
        NpcStore before = services.Npcs;
        Fill(content, "new", 2);
        ArcaneCore.Game.Reload.ContentCandidate candidate = await new NpcVendorContentReloadable(host.WorldServices).BuildAsync(CancellationToken.None);

        await host.OnWorldAsync(() =>
        {
            var transaction = new ArcaneCore.Game.Reload.ReloadTransaction();
            candidate.Commit(host.World, transaction);
            Assert.NotSame(before, services.Npcs);
            Assert.Throws<InvalidOperationException>(() => transaction.Step("later step", () => throw new InvalidOperationException("boom"), () => { }));
            Assert.Empty(transaction.Rollback());
        });

        Assert.Same(before, services.Npcs);
        Assert.Equal("301", Observe(services.Npcs, "npc_vendor"));
    }
}
