using System.Text.Json.Nodes;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Npc;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Control;

/// <summary>
/// <c>.botctl</c> from the server console (docs/ops/botctl.md): take a bot over, query it as JSON, walk it, script it, release it;
/// failures are machine-readable (<see cref="CommandContext.Failure"/>, the reason RA reports as <c>#done error &lt;reason&gt;</c>).
/// </summary>
public sealed class BotControlConsoleTests
{
    /// <summary>Run a console line and wait until the command settled (botctl verbs answer later, off the world thread).</summary>
    private static async Task<(string[] Lines, string? Failure)> ExecAsync(FleetWorld world, string line)
    {
        int skip = world.Console.Lines.Length;
        CommandContext? context = await world.Host.World.InvokeAsync(() => world.Console.Executor.Run(line));
        Assert.NotNull(context);
        Task settled = context!.WhenSettled;
        Assert.Same(settled, await Task.WhenAny(settled, Task.Delay(TimeSpan.FromSeconds(40))));
        return ([.. world.Console.Lines.Skip(skip)], context.Failure);
    }

    private static JsonObject JsonLine(string[] lines) => Assert.IsType<JsonObject>(JsonNode.Parse(Assert.Single(lines, l => l.StartsWith('{'))));

    [Fact]
    public async Task Take_State_Release_RoundTrip()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddBotAsync("Botcaa", 10);

        (string[] take, string? takeFailure) = await ExecAsync(world, ".botctl take Botcaa");
        Assert.Null(takeFailure);
        Assert.Contains(take, l => l.StartsWith("OK take (1/1 bots ok)", StringComparison.Ordinal));

        (string[] state, string? stateFailure) = await ExecAsync(world, ".botctl state Botcaa --json");
        Assert.Null(stateFailure);
        JsonObject json = JsonLine(state);
        Assert.True(json["ok"]!.GetValue<bool>());
        JsonObject bot = json["bots"]!.AsArray()[0]!.AsObject();
        Assert.Equal("Botcaa", bot["bot"]!.GetValue<string>());
        JsonObject data = bot["data"]!.AsObject();
        Assert.True(data["controlled"]!.GetValue<bool>());
        Assert.True(data["alive"]!.GetValue<bool>());
        Assert.Equal(FleetWorld.BaseX + 10, data["x"]!.GetValue<float>(), 1);

        (_, string? releaseFailure) = await ExecAsync(world, ".botctl release Botcaa");
        Assert.Null(releaseFailure);
        (string[] after, _) = await ExecAsync(world, ".botctl state Botcaa --json");
        Assert.False(JsonLine(after)["bots"]!.AsArray()[0]!["data"]!["controlled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Failures_AreMachineReadable()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddBotAsync("Botcba", 10);

        (string[] nobody, string? nobodyFailure) = await ExecAsync(world, ".botctl take Nobodyhere");
        Assert.Equal("no-match", nobodyFailure);
        Assert.Contains(nobody, l => l.StartsWith("ERR no-match take", StringComparison.Ordinal));

        (_, string? notControlled) = await ExecAsync(world, ".botctl cast Botcba 133");
        Assert.Equal("not-controlled", notControlled);

        (_, string? badFilter) = await ExecAsync(world, ".botctl list where:colour=red");
        Assert.Equal("bad-selector", badFilter);

        (_, string? syntax) = await ExecAsync(world, ".botctl move");
        Assert.Equal("syntax", syntax);

        (_, string? unknown) = await ExecAsync(world, ".botctl frobnicate Botcba");
        Assert.Equal("unknown-subcommand", unknown);

        (_, string? timeout) = await ExecAsync(world, ".botctl wait Botcba dead timeout 0.3");
        Assert.Equal("timeout", timeout);
    }

    [Fact]
    public async Task Run_Sequence_StopsAtTheFirstFailure()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddBotAsync("Botcca", 10);

        (string[] ok, string? okFailure) = await ExecAsync(world, ".botctl run Botcca take defensive ; mode passive ; wait alive and nocombat timeout 5 ; state ; release");
        Assert.Null(okFailure);
        Assert.Contains(ok, l => l.StartsWith("OK run", StringComparison.Ordinal) && l.Contains("5/5 steps ok", StringComparison.Ordinal));

        (string[] bad, string? badFailure) = await ExecAsync(world, ".botctl run Botcca take ; cast 0 ; release");
        Assert.Equal("step-failed", badFailure);
        Assert.Contains(bad, l => l.Contains("step 2", StringComparison.Ordinal));
        (string[] state, _) = await ExecAsync(world, ".botctl state Botcca --json");
        Assert.True(JsonLine(state)["bots"]!.AsArray()[0]!["data"]!["controlled"]!.GetValue<bool>());
        await ExecAsync(world, ".botctl release Botcca");
    }

    [Fact]
    public async Task Move_WalksToThePoint_AndWaitSeesArrival()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await PartyTestHost.InstallFlatGroundAsync(world.Host, FleetWorld.BaseZ);
        await world.AddBotAsync("Botcda", 10);

        float x = FleetWorld.BaseX + 18;
        (_, string? takeFailure) = await ExecAsync(world, ".botctl take Botcda");
        Assert.Null(takeFailure);
        (string[] move, string? moveFailure) = await ExecAsync(world, FormattableString.Invariant($".botctl move Botcda {x} {FleetWorld.BaseY} {FleetWorld.BaseZ}"));
        Assert.True(moveFailure is null, string.Join(" | ", move));
        (string[] wait, string? waitFailure) = await ExecAsync(world, ".botctl wait Botcda arrived timeout 30");
        Assert.True(waitFailure is null, string.Join(" | ", wait));
        (float X, float Y, float Z) at = await world.PositionAsync("Botcda");
        Assert.InRange(at.X, x - 2.5f, x + 2.5f);
        await ExecAsync(world, ".botctl release Botcda");
    }

    [Fact]
    public async Task Events_RecordTheServersRefusal()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddBotAsync("Botcea", 10);
        await ExecAsync(world, ".botctl take Botcea");

        // A spell the bot does not know: the server refuses it (SMSG_CAST_RESULT), and the refusal is the command's result.
        (string[] cast, string? castFailure) = await ExecAsync(world, ".botctl cast Botcea 10 self");
        Assert.True(castFailure is "server-error" or "refused" or "ignored", castFailure + ": " + string.Join(" | ", cast));
        (string[] errors, _) = await ExecAsync(world, ".botctl errors Botcea --json");
        Assert.NotEmpty(JsonLine(errors)["bots"]!.AsArray()[0]!["data"]!["events"]!.AsArray());
        (string[] events, _) = await ExecAsync(world, ".botctl events Botcea kind action --json");
        Assert.Contains(JsonLine(events)["bots"]!.AsArray()[0]!["data"]!["events"]!.AsArray(), e => e!["text"]!.GetValue<string>().Contains("CMSG_CAST_SPELL", StringComparison.Ordinal));
        await ExecAsync(world, ".botctl release Botcea");
    }

    [Fact]
    public async Task List_TextOutput_IsSingleLinePerBot_AndJsonPreservesStructuredData()
    {
        await using FleetWorld world = await FleetWorld.StartAsync(options => options.MaxBulkCommand = 1);
        Guid idA = await world.AddBotAsync("Botlaa", 10);
        Guid idB = await world.AddBotAsync("Botlab", 20);
        Guid idStopped = await world.CreateStoppedBotAsync("Botlac");
        await ExecAsync(world, ".botctl take Botlaa");

        // Text mode: strictly one line per bot + one summary line, with each bot's name and botId on the same line (never multi-line indented data).
        (string[] textLines, string? listFailure) = await ExecAsync(world, ".botctl list");
        Assert.Null(listFailure);
        Assert.Equal(4, textLines.Length);
        Assert.DoesNotContain(textLines, l => l.StartsWith("    botId:", StringComparison.Ordinal));
        Assert.Contains(textLines, l => l.StartsWith("  Botlaa: ok controlled by ", StringComparison.Ordinal) && l.Contains($"botId={idA:D}", StringComparison.Ordinal));
        Assert.Contains(textLines, l => l.StartsWith("  Botlab: ok autonomous ", StringComparison.Ordinal) && l.Contains($"botId={idB:D}", StringComparison.Ordinal));
        Assert.Contains(textLines, l => l.StartsWith("  Botlac: ok stopped ", StringComparison.Ordinal) && l.Contains($"botId={idStopped:D}", StringComparison.Ordinal));
        Assert.StartsWith("OK list (3/3 bots ok): 3 bots, 1 controlled", textLines[^1], StringComparison.Ordinal);

        // Bulk selectors on list (running, all, where:...) work without requiring --force even when exceeding MaxBulkCommand.
        (string[] runningLines, string? runningFailure) = await ExecAsync(world, ".botctl list running");
        Assert.Null(runningFailure);
        Assert.Equal(3, runningLines.Length);

        // JSON mode preserves full structured data per bot.
        (string[] jsonLines, string? jsonFailure) = await ExecAsync(world, ".botctl list --json");
        Assert.Null(jsonFailure);
        JsonArray bots = JsonLine(jsonLines)["bots"]!.AsArray();
        Assert.Equal(3, bots.Count);
        JsonObject botA = bots[0]!["data"]!.AsObject();
        Assert.Equal(idA.ToString("D"), botA["botId"]!.GetValue<string>());
        Assert.True(botA["running"]!.GetValue<bool>());
        Assert.True(botA["controlled"]!.GetValue<bool>());
        Assert.Equal(0u, botA["map"]!.GetValue<uint>());

        await ExecAsync(world, ".botctl release Botlaa");
    }

    [Trait("Category", "Timing")]
    [Fact]
    public async Task Interact_Gossip_Menu_Quest_Buy_Sell_Train_RoundTrip()
    {
        var fixture = new ControlNpcFixture();
        await using FleetWorld world = await FleetWorld.StartAsync(configureServices: fixture.Register);
        await world.AddBotAsync("Botnpc", 0);
        await world.Host.WaitForWorldAsync(() =>
        {
            Player? p = world.Host.World.FindOnlinePlayer("Botnpc");
            return p is not null && p.VisibleObjects.Contains(ControlNpcFixture.GossipNpcGuid) && p.VisibleObjects.Contains(ControlNpcFixture.DirectNpcGuid);
        }, "NPCs visible to Botnpc");

        await ExecAsync(world, ".botctl take Botnpc");

        // Until it was taken over the bot ran its own AI beside a quest giver: on a loaded machine it had thinks enough to take the quest
        // itself (the accept below then got QUEST_INVALID "already on", and the menu no longer offered it). Start from an empty log.
        (string[] questsAtTake, _) = await ExecAsync(world, ".botctl quests Botnpc --json");
        if (JsonLine(questsAtTake)["bots"]!.AsArray()[0]!["data"]!["quests"]!.AsArray().Count > 0)
        {
            (_, string? dropErr) = await ExecAsync(world, $".botctl quest Botnpc abandon {ControlNpcFixture.QuestId}");
            Assert.Null(dropErr);
        }

        // 1. interact + menu + gossip on a Gossip+Vendor+QuestGiver NPC.
        (string[] interactGossip, string? interactGossipErr) = await ExecAsync(world, $".botctl interact Botnpc npc:{ControlNpcFixture.GossipNpcEntry}");
        Assert.True(interactGossipErr is null, string.Join(" | ", interactGossip));
        (string[] menuLines, string? menuErr) = await ExecAsync(world, ".botctl menu Botnpc --json");
        Assert.Null(menuErr);
        JsonObject menuData = JsonLine(menuLines)["bots"]!.AsArray()[0]!["data"]!.AsObject();
        Assert.Equal(2, menuData["gossip"]!.AsArray().Count);
        Assert.Contains(menuData["quests"]!.AsArray(), q => q!["id"]!.GetValue<uint>() == ControlNpcFixture.QuestId);

        (string[] pickVendor, string? pickVendorErr) = await ExecAsync(world, $".botctl gossip Botnpc npc:{ControlNpcFixture.GossipNpcEntry} 1");
        Assert.True(pickVendorErr is null, string.Join(" | ", pickVendor));
        Assert.Contains(pickVendor, l => l.Contains("vendor-list", StringComparison.Ordinal));

        // 2. interact on a non-gossip QuestGiver+Vendor+Trainer NPC opens quest, vendor, and trainer lists (including spell@cost).
        (string[] interactDirect, string? interactDirectErr) = await ExecAsync(world, $".botctl interact Botnpc npc:{ControlNpcFixture.DirectNpcEntry}");
        Assert.True(interactDirectErr is null, string.Join(" | ", interactDirect));
        Assert.Contains(interactDirect, l => l.Contains("trainer-list", StringComparison.Ordinal) && l.Contains($"{ControlNpcFixture.TeachingSpell}@10", StringComparison.Ordinal));

        // 3. quest hello / query / accept / abandon / accept / complete / reward.
        (_, string? helloErr) = await ExecAsync(world, $".botctl quest Botnpc hello npc:{ControlNpcFixture.DirectNpcEntry}");
        Assert.Null(helloErr);
        (_, string? queryErr) = await ExecAsync(world, $".botctl quest Botnpc query npc:{ControlNpcFixture.DirectNpcEntry} {ControlNpcFixture.QuestId}");
        Assert.Null(queryErr);
        (_, string? badAcceptErr) = await ExecAsync(world, $".botctl quest Botnpc accept npc:{ControlNpcFixture.DirectNpcEntry} 999999");
        Assert.Equal("not-accepted", badAcceptErr);

        (string[] acceptLines, string? acceptErr) = await ExecAsync(world, $".botctl quest Botnpc accept npc:{ControlNpcFixture.DirectNpcEntry} {ControlNpcFixture.QuestId}");
        Assert.True(acceptErr is null, string.Join(" | ", acceptLines));
        (string[] questsAfterAccept, _) = await ExecAsync(world, ".botctl quests Botnpc --json");
        Assert.Single(JsonLine(questsAfterAccept)["bots"]!.AsArray()[0]!["data"]!["quests"]!.AsArray());

        (_, string? abandonErr) = await ExecAsync(world, $".botctl quest Botnpc abandon {ControlNpcFixture.QuestId}");
        Assert.Null(abandonErr);
        (string[] questsAfterAbandon, _) = await ExecAsync(world, ".botctl quests Botnpc --json");
        Assert.Empty(JsonLine(questsAfterAbandon)["bots"]!.AsArray()[0]!["data"]!["quests"]!.AsArray());
        (_, string? abandonAgainErr) = await ExecAsync(world, $".botctl quest Botnpc abandon {ControlNpcFixture.QuestId}");
        Assert.Equal("no-quest", abandonAgainErr);

        (_, acceptErr) = await ExecAsync(world, $".botctl quest Botnpc accept npc:{ControlNpcFixture.DirectNpcEntry} {ControlNpcFixture.QuestId}");
        Assert.Null(acceptErr);
        (_, string? completeErr) = await ExecAsync(world, $".botctl quest Botnpc complete npc:{ControlNpcFixture.DirectNpcEntry} {ControlNpcFixture.QuestId}");
        Assert.Null(completeErr);
        (string[] rewardLines, string? rewardErr) = await ExecAsync(world, $".botctl quest Botnpc reward npc:{ControlNpcFixture.DirectNpcEntry} {ControlNpcFixture.QuestId} 0");
        Assert.True(rewardErr is null, string.Join(" | ", rewardLines));
        Assert.Contains(rewardLines, l => l.Contains("quest-rewarded", StringComparison.Ordinal));
        (_, string? waitRewardedErr) = await ExecAsync(world, $".botctl wait Botnpc quest {ControlNpcFixture.QuestId} rewarded timeout 2");
        Assert.Null(waitRewardedErr);

        // 4. buy + sell (using the 50 copper earned from the quest reward).
        (string[] buyLines, string? buyErr) = await ExecAsync(world, $".botctl buy Botnpc npc:{ControlNpcFixture.DirectNpcEntry} {ControlNpcFixture.BreadItem} 2");
        Assert.True(buyErr is null, string.Join(" | ", buyLines));
        (string[] bagAfterBuy, _) = await ExecAsync(world, ".botctl inventory Botnpc --json");
        Assert.Contains(JsonLine(bagAfterBuy)["bots"]!.AsArray()[0]!["data"]!["inventory"]!.AsArray(),
            i => i!["entry"]!.GetValue<uint>() == ControlNpcFixture.BreadItem && i["count"]!.GetValue<uint>() == 2);

        (string[] sellLines, string? sellErr) = await ExecAsync(world, $".botctl sell Botnpc npc:{ControlNpcFixture.DirectNpcEntry} {ControlNpcFixture.BreadItem} 1");
        Assert.True(sellErr is null, string.Join(" | ", sellLines));
        (string[] bagAfterSell, _) = await ExecAsync(world, ".botctl inventory Botnpc --json");
        Assert.Contains(JsonLine(bagAfterSell)["bots"]!.AsArray()[0]!["data"]!["inventory"]!.AsArray(),
            i => i!["entry"]!.GetValue<uint>() == ControlNpcFixture.BreadItem && i["count"]!.GetValue<uint>() == 1);

        (_, string? sellMissingErr) = await ExecAsync(world, $".botctl sell Botnpc npc:{ControlNpcFixture.DirectNpcEntry} 999999 1");
        Assert.Equal("no-item", sellMissingErr);

        // 5. train (passing the learned spell ID; Train resolves the trainer's teaching spell ID and verifies learning).
        (string[] trainLines, string? trainErr) = await ExecAsync(world, $".botctl train Botnpc npc:{ControlNpcFixture.DirectNpcEntry} {ControlNpcFixture.LearnedSpell}");
        Assert.True(trainErr is null, string.Join(" | ", trainLines));
        Assert.Contains(trainLines, l => l.Contains($"trained spell {ControlNpcFixture.TeachingSpell}", StringComparison.Ordinal));

        // Training the same spell again is refused by the trainer (SMSG_TRAINER_BUY_FAILED) because the bot now knows it.
        (_, string? retrainErr) = await ExecAsync(world, $".botctl train Botnpc npc:{ControlNpcFixture.DirectNpcEntry} {ControlNpcFixture.TeachingSpell}");
        Assert.NotNull(retrainErr);

        await ExecAsync(world, ".botctl release Botnpc");
    }

    [Fact]
    public async Task Loot_ExtractsGoldFromCorpse()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddBotAsync("Botlooter", 10);
        await ExecAsync(world, ".botctl take Botlooter");

        ObjectGuid corpseGuid = default;
        await world.Host.OnWorldAsync(() =>
        {
            Player player = world.Host.World.FindOnlinePlayer("Botlooter")!;
            CreatureMapSystem creatures = PartyTestHost.GoldCreatures(player);
            corpseGuid = PartyTestHost.KillGoldCreature(creatures, player, 2f, player).Guid;
        });

        // .botctl loot looks through bot.VisibleObjects, which the visibility update fills a tick after the kill.
        await world.Host.WaitForWorldAsync(() => world.Host.World.FindOnlinePlayer("Botlooter")?.VisibleObjects.Contains(corpseGuid) == true, "the corpse visible to Botlooter");

        (string[] lootLines, string? lootErr) = await ExecAsync(world, ".botctl loot Botlooter corpse --json");
        Assert.True(lootErr is null, string.Join(" | ", lootLines));
        JsonObject lootData = JsonLine(lootLines)["bots"]!.AsArray()[0]!["data"]!.AsObject();
        Assert.Equal(PartyTestHost.CorpseGold, lootData["gold"]!.GetValue<uint>());

        (string[] stateAfter, _) = await ExecAsync(world, ".botctl state Botlooter --json");
        Assert.Equal(PartyTestHost.CorpseGold, JsonLine(stateAfter)["bots"]!.AsArray()[0]!["data"]!["money"]!.GetValue<uint>());

        await ExecAsync(world, ".botctl release Botlooter");
    }

    [Fact]
    public async Task Battleground_Join_Status_Port_Leave_RoundTrip()
    {
        var bgFixture = new ControlBattlegroundFixture();
        await using FleetWorld world = await FleetWorld.StartAsync(configureServices: bgFixture.Register);
        await world.AddBotAsync("Botbgally", 0);
        await world.Host.WaitForWorldAsync(() =>
        {
            Player? p = world.Host.World.FindOnlinePlayer("Botbgally");
            return p is not null && p.VisibleObjects.Contains(ControlBattlegroundFixture.MasterGuid);
        }, "Battlemaster visible to Botbgally");

        await ExecAsync(world, ".botctl take Botbgally");

        (string[] joinLines, string? joinErr) = await ExecAsync(world, $".botctl bg Botbgally join npc:{ControlBattlegroundFixture.MasterEntry} 489");
        Assert.True(joinErr is null, string.Join(" | ", joinLines));

        (string[] statusLines, string? statusErr) = await ExecAsync(world, ".botctl bg Botbgally status");
        Assert.True(statusErr is null, string.Join(" | ", statusLines));
        Assert.Contains(statusLines, l => l.Contains("battlefield-status", StringComparison.Ordinal) && l.Contains("map 489", StringComparison.Ordinal));

        (string[] portLeaveLines, string? portLeaveErr) = await ExecAsync(world, ".botctl bg Botbgally port 489 leave");
        Assert.True(portLeaveErr is null, string.Join(" | ", portLeaveLines));

        (string[] leaveLines, string? leaveErr) = await ExecAsync(world, ".botctl bg Botbgally leave 489");
        Assert.True(leaveErr is null, string.Join(" | ", leaveLines));

        await ExecAsync(world, ".botctl release Botbgally");
    }

    [Fact]
    public async Task Wait_Conditions_And_Stream_Events()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddBotAsync("Botstream", 10);
        await world.AddBotAsync("Botstreamtwo", 20);
        await ExecAsync(world, ".botctl take Botstream,Botstreamtwo");

        // Turn event streaming on (--json), trigger an action, and verify a #event line was pushed to the console.
        (_, string? streamOnErr) = await ExecAsync(world, ".botctl stream Botstream on --json");
        Assert.Null(streamOnErr);

        int beforeCast = world.Console.Lines.Length;
        await ExecAsync(world, ".botctl cast Botstream 10 self");
        string[] afterCast = [.. world.Console.Lines.Skip(beforeCast)];
        Assert.Contains(afterCast, l => l.StartsWith("#event {", StringComparison.Ordinal) && l.Contains("\"bot\":\"Botstream\"", StringComparison.Ordinal));

        // Wait predicates: hp (attached operator), power (space-separated operator), near, nocombat, notcasting, map, and --any across two bots.
        (float x, float y, float z) = await world.PositionAsync("Botstream");
        (string[] waitNearLines, string? waitNearErr) = await ExecAsync(world, FormattableString.Invariant($".botctl wait Botstream hp>=1 and power >= 0 and nocombat and notcasting and map 0 and near {x} {y} {z} 5 timeout 2"));
        Assert.True(waitNearErr is null, string.Join(" | ", waitNearLines));

        (string[] waitAnyLines, string? waitAnyErr) = await ExecAsync(world, FormattableString.Invariant($".botctl wait Botstream,Botstreamtwo near {x} {y} {z} 2 --any timeout 2"));
        Assert.True(waitAnyErr is null, string.Join(" | ", waitAnyLines));

        (_, string? streamOffErr) = await ExecAsync(world, ".botctl stream Botstream off");
        Assert.Null(streamOffErr);

        int beforeSecondCast = world.Console.Lines.Length;
        await ExecAsync(world, ".botctl cast Botstream 10 self");
        string[] afterSecondCast = [.. world.Console.Lines.Skip(beforeSecondCast)];
        Assert.DoesNotContain(afterSecondCast, l => l.StartsWith("#event ", StringComparison.Ordinal));

        await ExecAsync(world, ".botctl release Botstream,Botstreamtwo");
    }

    private sealed class ControlNpcFixture : ICreatureDataStore, INpcContentStore, IQuestContentStore, ISpellContentStore, ICharacterQuestRewardStore
    {
        public const uint GossipNpcEntry = 991000;
        public const uint DirectNpcEntry = 991002;
        public const uint BreadItem = 991001;
        public const uint TeachingSpell = 991010;
        public const uint LearnedSpell = 991011;
        public const uint QuestId = 991020;
        public static readonly ObjectGuid GossipNpcGuid = ObjectGuid.WithEntry(HighGuid.Unit, GossipNpcEntry, 991000);
        public static readonly ObjectGuid DirectNpcGuid = ObjectGuid.WithEntry(HighGuid.Unit, DirectNpcEntry, 991002);
        private readonly MemoryQuestStore _questStore = new();

        public void Register(IServiceCollection services)
        {
            var items = new InMemoryItemTemplateSource();
            items.Templates.Add(new ItemTemplate
            {
                Entry = BreadItem, Name = "Control Bread", Class = 0, SubClass = 5, Quality = 1,
                BuyPrice = 10, SellPrice = 5, Stackable = 20, InventoryType = 0,
            });
            var itemStore = new InMemoryItemStore();
            services.AddSingleton<IItemTemplateSource>(items);
            services.AddSingleton<IItemStore>(itemStore);
            services.AddSingleton<IItemStateStore>(itemStore);
            services.AddSingleton<ICreatureDataStore>(this);
            services.AddSingleton<INpcContentStore>(this);
            services.AddSingleton<IQuestContentStore>(this);
            services.AddSingleton<ISpellContentStore>(this);
            services.AddSingleton<ICharacterQuestStore>(_questStore);
            services.AddScoped<ICharacterQuestRewardStore>(_ => this);
            services.AddSingleton(new FactionTemplateCatalog(
            [
                new FactionTemplateRecord(1, 1, 0, 1, 0, 0),
                new FactionTemplateRecord(900011, 0, 0, 8, 0, 0),
            ]));
        }

        Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
            [
                new CreatureTemplate
                {
                    Entry = GossipNpcEntry, Name = "Control Quartermaster", Faction = 900011, DisplayIds = [49],
                    NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.QuestGiver | NpcFlags.Vendor),
                    MinLevel = 10, MaxLevel = 10, MinLevelHealth = 100, MaxLevelHealth = 100,
                },
                new CreatureTemplate
                {
                    Entry = DirectNpcEntry, Name = "Control Instructor", Faction = 900011, DisplayIds = [49],
                    NpcFlags = (uint)(NpcFlags.QuestGiver | NpcFlags.Vendor | NpcFlags.Trainer),
                    TrainerType = (uint)TrainerType.Class, TrainerClass = 1,
                    MinLevel = 10, MaxLevel = 10, MinLevelHealth = 100, MaxLevelHealth = 100,
                },
            ],
            [
                new CreatureSpawn { Guid = 991000, Entry = GossipNpcEntry, MapId = 0, X = FleetWorld.BaseX + 2f, Y = FleetWorld.BaseY, Z = FleetWorld.BaseZ },
                new CreatureSpawn { Guid = 991002, Entry = DirectNpcEntry, MapId = 0, X = FleetWorld.BaseX + 2f, Y = FleetWorld.BaseY + 2f, Z = FleetWorld.BaseZ },
            ], [], [], []));

        Task<NpcContent> INpcContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(NpcContent.Empty with
        {
            GossipMenuOptions =
            [
                new GossipMenuOption
                {
                    MenuId = 0, Id = 0, OptionId = (byte)GossipOption.Gossip, NpcOptionNpcFlag = (uint)NpcFlags.Gossip,
                    OptionText = "Tell me about the outpost.",
                },
                new GossipMenuOption
                {
                    MenuId = 0, Id = 1, OptionId = (byte)GossipOption.Vendor, NpcOptionNpcFlag = (uint)NpcFlags.Vendor,
                    OptionText = "Show me your wares.",
                },
            ],
            VendorItems =
            [
                new VendorItem { Entry = GossipNpcEntry, Item = BreadItem, Slot = 1 },
                new VendorItem { Entry = DirectNpcEntry, Item = BreadItem, Slot = 1 },
            ],
            TrainerSpells =
            [
                new TrainerSpell { Entry = DirectNpcEntry, Spell = TeachingSpell, SpellCost = 10, ReqLevel = 1 },
            ],
        });

        Task<QuestContent> IQuestContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new QuestContent(
            [
                new QuestTemplate
                {
                    Entry = QuestId, Method = 2, MinLevel = 1, QuestLevel = 1,
                    Title = "Control Supply", Details = "Report for duty.", Objectives = "Speak with the instructor.",
                    OfferRewardText = "Well done.", RewXP = 50, RewOrReqMoney = 50,
                },
            ],
            [
                new CreatureQuestRelation { Id = GossipNpcEntry, Quest = QuestId },
                new CreatureQuestRelation { Id = DirectNpcEntry, Quest = QuestId },
            ],
            [
                new CreatureQuestRelation { Id = GossipNpcEntry, Quest = QuestId },
                new CreatureQuestRelation { Id = DirectNpcEntry, Quest = QuestId },
            ]));

        Task<SpellContent> ISpellContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new SpellContent(
            [
                new SpellTemplateRow
                {
                    Id = TeachingSpell, SpellName = "Teach Control Strike", SpellLevel = 1,
                    Effect1 = (uint)SpellEffectName.LearnSpell, EffectTriggerSpell1 = LearnedSpell,
                    EffectImplicitTargetA1 = (uint)SpellImplicitTarget.UnitCaster,
                },
                new SpellTemplateRow
                {
                    Id = LearnedSpell, SpellName = "Control Strike", SpellLevel = 1,
                },
            ], [], [], [], [], [], []));

        Task ISpellContentStore.ReplaceDbcTablesAsync(SpellDbcContent content, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        Task<QuestRewardCommitResult> ICharacterQuestRewardStore.CommitAsync(CharacterQuestRewardRequest request, CancellationToken cancellationToken)
        {
            _questStore.Seed(request.RewardedQuest);
            return Task.FromResult(QuestRewardCommitResult.Committed);
        }
    }

    private sealed class ControlBattlegroundFixture : ICreatureDataStore, IBattlegroundContentStore, IMapDataStore, IGraveyardDataStore
    {
        public const uint MasterEntry = 991030;
        public static readonly ObjectGuid MasterGuid = ObjectGuid.WithEntry(HighGuid.Unit, MasterEntry, 991030);

        public void Register(IServiceCollection services)
        {
            services.AddSingleton<ICreatureDataStore>(this);
            services.AddSingleton<IBattlegroundContentStore>(this);
            services.AddSingleton<IMapDataStore>(this);
            services.AddSingleton<IGraveyardDataStore>(this);
            services.AddSingleton(new FactionTemplateCatalog(
            [
                new FactionTemplateRecord(1, 1, 0, 1, 0, 0),
                new FactionTemplateRecord(900011, 0, 0, 8, 0, 0),
            ]));
        }

        Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
            [
                new CreatureTemplate
                {
                    Entry = MasterEntry, Name = "Control Battlemaster", Faction = 900011, DisplayIds = [49],
                    NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.BattleMaster),
                    MinLevel = 60, MaxLevel = 60, MinLevelHealth = 100, MaxLevelHealth = 100,
                },
            ],
            [
                new CreatureSpawn { Guid = 991030, Entry = MasterEntry, MapId = 0, X = FleetWorld.BaseX + 2f, Y = FleetWorld.BaseY, Z = FleetWorld.BaseZ },
            ], [], [], []));

        Task<BattlegroundContent> IBattlegroundContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new BattlegroundContent(
            [new BattlegroundTemplateRecord(2, 1, 10, 1, 60, 769, 770, 75, 0)],
            [], [],
            [new BattlemasterRecord(MasterEntry, 2)]));

        Task<MapContent> IMapDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new MapContent(
            [
                new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
                new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
                new MapTemplate(489, 0, MapType.Battleground, 3277, 20, 0, -1, 0, 0, "Warsong Gulch", ""),
            ], [], [], [], []));

        Task<GraveyardContent> IGraveyardDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new GraveyardContent(
            [
                new WorldSafeLoc(769, 489, 1415.33f, 1554.79f, 343.156f, 3.14159f, "WSG Alliance flag room"),
                new WorldSafeLoc(770, 489, 1029.14f, 1387.49f, 340.836f, 3.14159f, "WSG Horde flag room"),
            ], []));
    }
}