using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Gm.Quest.QuestCommandFixture;

namespace ArcaneCore.World.Tests.Gm.Quest;

/// <summary>
/// <c>.quest add|complete|remove|status</c> (mangos zero ChatCommands/QuestCommands.cpp:47-283; levels Chat.cpp:527-533) and the
/// item-started quest path of CMSG_QUESTGIVER_QUERY_QUEST / CMSG_QUESTGIVER_ACCEPT_QUEST (QuestHandler.cpp:171-181, 264-273;
/// Player::AddQuest's start-item removal, PlayerQuest.cpp:780-803). Reply texts: mangos zero Language.h:421-423.
/// </summary>
public sealed class QuestCommandTests
{
    private static string Link(string name) => $"|cffffffff|Hplayer:{name}|h[{name}]|h|r";

    [Fact]
    public void Levels_FollowTheMangosTable_AndStatusIsAGameMasterCommand()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "quest add", "quest complete", "quest remove" })   // SEC_ADMINISTRATOR (6)
        {
            Assert.Null(table.Resolve(path, AccountSecurity.GameMaster));
            Assert.NotNull(table.Resolve(path, AccountSecurity.Administrator));
        }

        Assert.Null(table.Resolve("quest status", AccountSecurity.Moderator));
        Assert.NotNull(table.Resolve("quest status", AccountSecurity.GameMaster));
        Assert.NotNull(table.Resolve("quest", AccountSecurity.GameMaster));
    }

    [Fact]
    public async Task Add_PutsTheQuestInTheLog_AndRefusesDuplicatesUnknownItemStartedAndWithheldQuests()
    {
        var fixture = new QuestCommandFixture();
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient gm = await host.EnterWorldAsync("QADDGM", "Qaddgm", AccountSecurity.Administrator);
        await gm.CollectAsync();
        int id = await host.PlayerStateAsync("Qaddgm", p => checked((int)p.Guid.Low));
        QuestNpcFeature feature = await FeatureAsync(host, "Qaddgm");

        Assert.StartsWith("Syntax:", (await Run(gm, ".quest add"))!);
        await gm.CollectAsync();   // the help text has a second line

        Assert.Null(await Run(gm, $".quest add {KillQuest}", expectReply: false));
        Assert.Equal(KillQuest, await host.PlayerStateAsync("Qaddgm", p => p.GetUInt32(UpdateFields.PlayerQuestLog11)));
        await feature.Persistence.FlushCharacterAsync(id);
        Assert.Equal((byte)QuestStatus.Incomplete, fixture.Characters.Stored(id, KillQuest).Status);

        Assert.Equal($"Quest {KillQuest} is already in the quest log of {Link("Qaddgm")}.", await Run(gm, $".quest add {KillQuest}"));
        Assert.Equal("Quest 999999 not found.", await Run(gm, ".quest add 999999"));
        Assert.Equal("Could not find 'Nosuchquest'", await Run(gm, ".quest add Nosuchquest"));
        Assert.Equal($"Quest {ItemStartedQuest} started from item. For correct work, please, add item to inventory and start quest in normal way: .additem {StartNote}",
            await Run(gm, $".quest add {ItemStartedQuest}"));
        Assert.Equal($"Quest {WithheldQuest} is withheld on this server (missing adapters: Mail).", await Run(gm, $".quest add {WithheldQuest}"));

        // A shift-click link and an exact title (case-insensitive) name the quest too.
        Assert.Null(await Run(gm, $".quest add |cffffffff|Hquest:{MoneyQuest}:1|h[Pay the test]|h|r", expectReply: false));
        Assert.Null(await Run(gm, ".quest add [gather the test]", expectReply: false));
        Assert.Equal(MoneyQuest, await host.PlayerStateAsync("Qaddgm", p => p.GetUInt32(UpdateFields.PlayerQuestLog11 + QuestConstants.FieldsPerSlot)));
        Assert.Equal(ItemQuest, await host.PlayerStateAsync("Qaddgm", p => p.GetUInt32(UpdateFields.PlayerQuestLog11 + (2 * QuestConstants.FieldsPerSlot))));
        // The source item of the gathering quest was handed over (vmangos GiveQuestSourceItemIfNeed).
        Assert.Equal(1u, await host.PlayerStateAsync("Qaddgm", p => p.Inventory.GetItemCount(SourceToken)));
    }

    [Fact]
    public async Task Add_WithAFullLog_RefusesAndTellsBoth()
    {
        var fixture = new QuestCommandFixture();
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient gm = await host.EnterWorldAsync("QFULLGM", "Qfullgm", AccountSecurity.Administrator);
        await gm.CollectAsync();

        for (int i = 0; i < QuestConstants.MaxQuestLogSize; i++)
        {
            Assert.Null(await Run(gm, $".quest add {FillerQuest + (uint)i}", expectReply: false));
        }

        await gm.SendChatAsync(ChatType.Say, Language.Common, $".quest add {KillQuest}");
        await gm.ReadUntilAsync(WorldOpcode.SmsgQuestlogFull);
        Assert.Equal($"The quest log of {Link("Qfullgm")} is full.", (await gm.ReadChatAsync()).Text);
        Assert.Equal(QuestStatus.None, await host.PlayerStateAsync("Qfullgm", p => Services(p).StateOf(p)!.Quests.GetStatus(KillQuest)));
    }

    [Fact]
    public async Task Complete_CreditsKillsGivesItemsAndMoney_AndRefusesAQuestNotTaken()
    {
        var fixture = new QuestCommandFixture();
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient gm = await host.EnterWorldAsync("QCMPGM", "Qcmpgm", AccountSecurity.Administrator);
        await gm.CollectAsync();
        int id = await host.PlayerStateAsync("Qcmpgm", p => checked((int)p.Guid.Low));
        QuestNpcFeature feature = await FeatureAsync(host, "Qcmpgm");

        Assert.Equal($"Quest {KillQuest} not found.", await Run(gm, $".quest complete {KillQuest}"));   // QuestCommands.cpp:178-183

        // Kill objective: one SMSG_QUESTUPDATE_ADD_KILL per credited kill, then the quest is complete.
        Assert.Null(await Run(gm, $".quest add {KillQuest}", expectReply: false));
        await gm.SendChatAsync(ChatType.Say, Language.Common, $".quest complete {KillQuest}");
        byte[] first = await gm.ReadUntilAsync(WorldOpcode.SmsgQuestupdateAddKill);
        byte[] second = await gm.ReadUntilAsync(WorldOpcode.SmsgQuestupdateAddKill);
        Assert.Equal(KillQuest, BinaryPrimitives.ReadUInt32LittleEndian(first));
        Assert.Equal(90u, BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(8)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(second.AsSpan(8)));
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(second.AsSpan(16)));   // no killer guid
        await feature.Persistence.FlushCharacterAsync(id);
        CharacterQuestStatus killed = fixture.Characters.Stored(id, KillQuest);
        Assert.Equal((byte)QuestStatus.Complete, killed.Status);
        Assert.Equal(2u, killed.MobCount1);
        Assert.Equal(QuestConstants.SlotStateComplete, await host.PlayerStateAsync("Qcmpgm", p => p.GetByte(UpdateFields.PlayerQuestLog11 + 1, 3)));

        // Delivery objective: the missing items are stored and announced; the counter follows the bags.
        Assert.Null(await Run(gm, $".quest add {ItemQuest}", expectReply: false));
        await gm.SendChatAsync(ChatType.Say, Language.Common, $".quest complete {ItemQuest}");
        byte[] push = await gm.ReadUntilAsync(WorldOpcode.SmsgItemPushResult);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(push.AsSpan(8)));    // received
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(push.AsSpan(12)));   // not created
        Assert.Equal(QuestPelt, BinaryPrimitives.ReadUInt32LittleEndian(push.AsSpan(25)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(push.AsSpan(37)));
        Assert.Equal(3u, await host.PlayerStateAsync("Qcmpgm", p => p.Inventory.GetItemCount(QuestPelt)));
        await feature.Persistence.FlushCharacterAsync(id);
        CharacterQuestStatus gathered = fixture.Characters.Stored(id, ItemQuest);
        Assert.Equal((byte)QuestStatus.Complete, gathered.Status);
        Assert.Equal(3u, gathered.ItemCount1);

        // Required money is given (QuestCommands.cpp:261-266).
        uint before = await host.PlayerStateAsync("Qcmpgm", p => p.Money);
        Assert.Null(await Run(gm, $".quest add {MoneyQuest}", expectReply: false));
        Assert.Null(await Run(gm, $".quest complete {MoneyQuest}", expectReply: false));
        Assert.Equal(before + 100, await host.PlayerStateAsync("Qcmpgm", p => p.Money));
        Assert.Equal(QuestStatus.Complete, await host.PlayerStateAsync("Qcmpgm", p => Services(p).StateOf(p)!.Quests.GetStatus(MoneyQuest)));

        // A kill objective naming a creature without a template is skipped (ObjectMgr::GetCreatureTemplate, QuestCommands.cpp:227-234).
        Assert.Null(await Run(gm, $".quest add {FillerQuest}", expectReply: false));
        Assert.Null(await Run(gm, $".quest complete {FillerQuest}", expectReply: false));
        await feature.Persistence.FlushCharacterAsync(id);
        CharacterQuestStatus filler = fixture.Characters.Stored(id, FillerQuest);
        Assert.Equal((byte)QuestStatus.Complete, filler.Status);
        Assert.Equal(0u, filler.MobCount1);
    }

    [Fact]
    public async Task Remove_ClearsTheLogSlot_TakesTheSourceItem_AndResetsTheRewardedFlag()
    {
        var fixture = new QuestCommandFixture();
        await using WorldTestHost host = Start(fixture);
        byte[] key = await host.AddAccountAsync("QREMGM", AccountSecurity.Administrator);
        await using WorldTestClient gm = await host.ConnectAsync();
        await gm.AuthenticateAsync("QREMGM", key);
        await gm.CreateCharacterAsync("Qremgm");
        Account account = (await host.Accounts.FindByUsernameAsync("QREMGM"))!;
        int id = (await host.Characters.GetByAccountAsync(account.Id)).Single().Id;
        fixture.Characters.Seed(new CharacterQuestStatus(id, KillQuest, (byte)QuestStatus.Complete, true, false, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0));
        await gm.LoginAsync((ulong)id);
        await gm.CollectAsync();
        QuestNpcFeature feature = await FeatureAsync(host, "Qremgm");

        Assert.Equal("Quest 999999 not found.", await Run(gm, ".quest remove 999999"));

        // A quest the player never had answers "removed" and writes nothing (mangos would store a NONE row).
        Assert.Equal("Quest removed.", await Run(gm, $".quest remove {MoneyQuest}"));
        await feature.Persistence.FlushCharacterAsync(id);
        Assert.DoesNotContain((await fixture.Characters.LoadAsync(id)).Quests, r => r.Quest == MoneyQuest);

        // The source item handed over on accept is taken back (TakeQuestSourceItem) and the slot is cleared.
        Assert.Null(await Run(gm, $".quest add {ItemQuest}", expectReply: false));
        Assert.Equal(1u, await host.PlayerStateAsync("Qremgm", p => p.Inventory.GetItemCount(SourceToken)));
        Assert.Equal("Quest removed.", await Run(gm, $".quest remove {ItemQuest}"));
        Assert.Equal(0u, await host.PlayerStateAsync("Qremgm", p => p.GetUInt32(UpdateFields.PlayerQuestLog11)));
        Assert.Equal(0u, await host.PlayerStateAsync("Qremgm", p => p.Inventory.GetItemCount(SourceToken)));
        await feature.Persistence.FlushCharacterAsync(id);
        Assert.Equal((byte)QuestStatus.None, fixture.Characters.Stored(id, ItemQuest).Status);

        // A rewarded quest forgets its reward so it can be done again (QuestCommands.cpp:150-152).
        Assert.True(fixture.Characters.Stored(id, KillQuest).Rewarded);
        Assert.Equal("Quest removed.", await Run(gm, $".quest remove {KillQuest}"));
        await feature.Persistence.FlushCharacterAsync(id);
        CharacterQuestStatus reset = fixture.Characters.Stored(id, KillQuest);
        Assert.Equal((byte)QuestStatus.None, reset.Status);
        Assert.False(reset.Rewarded);
        Assert.Null(await Run(gm, $".quest add {KillQuest}", expectReply: false));
        Assert.Equal(KillQuest, await host.PlayerStateAsync("Qremgm", p => p.GetUInt32(UpdateFields.PlayerQuestLog11)));
    }

    [Fact]
    public async Task Status_ListsTheLog_AndDescribesOneQuest()
    {
        var fixture = new QuestCommandFixture();
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient gm = await host.EnterWorldAsync("QSTAGM", "Qstagm", AccountSecurity.Administrator);
        await gm.CollectAsync();

        Assert.Equal($"Quest log of {Link("Qstagm")}: 0 quest(s).", await Run(gm, ".quest status"));
        Assert.Null(await Run(gm, $".quest add {KillQuest}", expectReply: false));
        Assert.Equal($"Quest log of {Link("Qstagm")}: 1 quest(s).", await Run(gm, ".quest status"));
        Assert.Equal($"  slot 0: {KillQuest} - |cffffffff|Hquest:{KillQuest}:1|h[Cull the test]|h|r [Incomplete]", (await gm.ReadChatAsync()).Text);
        Assert.Equal($"{KillQuest} - |cffffffff|Hquest:{KillQuest}:1|h[Cull the test]|h|r: Incomplete, slot 0, creature 90 0/2", await Run(gm, $".quest status {KillQuest}"));
        Assert.Equal($"{MoneyQuest} - |cffffffff|Hquest:{MoneyQuest}:1|h[Pay the test]|h|r: None", await Run(gm, $".quest status {MoneyQuest}"));
        Assert.Equal("Quest 999999 not found.", await Run(gm, ".quest status 999999"));
    }

    [Fact]
    public async Task Commands_ActOnTheSelectedPlayer_AndRefuseACreatureSelection()
    {
        var fixture = new QuestCommandFixture();
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient gm = await host.EnterWorldAsync("QSELGM", "Qselgm", AccountSecurity.Administrator);
        await using WorldTestClient victim = await host.EnterWorldAsync("QSELVIC", "Qselvic");
        await gm.CollectAsync();
        await victim.CollectAsync();

        Player target = await host.PlayerAsync("Qselvic");
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Qselgm")!.Selection = target.Guid);
        Assert.Null(await Run(gm, $".quest add {KillQuest}", expectReply: false));
        Assert.Equal(KillQuest, await host.PlayerStateAsync("Qselvic", p => p.GetUInt32(UpdateFields.PlayerQuestLog11)));
        Assert.Equal(0u, await host.PlayerStateAsync("Qselgm", p => p.GetUInt32(UpdateFields.PlayerQuestLog11)));
        Assert.Equal($"Quest log of {Link("Qselvic")}: 1 quest(s).", await Run(gm, ".quest status"));
        await gm.CollectAsync();

        // A selection that is not an online player: "No character selected." (getSelectedPlayer).
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Qselgm")!.Selection = ObjectGuid.WithEntry(HighGuid.Unit, 90, 7));
        Assert.Equal("No character selected.", await Run(gm, $".quest add {KillQuest}"));
        Assert.Equal("No character selected.", await Run(gm, $".quest remove {KillQuest}"));
        Assert.Equal("No character selected.", await Run(gm, $".quest complete {KillQuest}"));
        Assert.Equal("No character selected.", await Run(gm, ".quest status"));
    }

    [Fact]
    public async Task ItemStartedQuest_IsOfferedAndAcceptedWithTheItemGuid_AndTheItemIsConsumedUnlessTheQuestNeedsIt()
    {
        var fixture = new QuestCommandFixture();
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient gm = await host.EnterWorldAsync("QITEMGM", "Qitemgm", AccountSecurity.Administrator);
        await gm.CollectAsync();
        int id = await host.PlayerStateAsync("Qitemgm", p => checked((int)p.Guid.Low));
        QuestNpcFeature feature = await FeatureAsync(host, "Qitemgm");

        Assert.Null(await Run(gm, $".additem {StartNote}", expectReply: false));
        Assert.Null(await Run(gm, $".additem {Letter}", expectReply: false));
        ObjectGuid note = await host.PlayerStateAsync("Qitemgm", p => p.Inventory.AllItems.Single(i => i.Entry == StartNote).Guid);
        ObjectGuid letter = await host.PlayerStateAsync("Qitemgm", p => p.Inventory.AllItems.Single(i => i.Entry == Letter).Guid);
        Assert.Equal(HighGuid.Item, note.High);

        // The wrong quest for the item, a quest of a creature, and a creature GUID the player cannot see: no details.
        await gm.SendAsync(WorldOpcode.CmsgQuestgiverQueryQuest, QuestBody(note, KillQuest));
        await gm.SendAsync(WorldOpcode.CmsgQuestgiverQueryQuest, QuestBody(ObjectGuid.WithEntry(HighGuid.Unit, 90, 7), ItemStartedQuest));
        await gm.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, QuestBody(note, KillQuest));
        await gm.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);   // the accept always closes the window
        Assert.Equal(0u, await host.PlayerStateAsync("Qitemgm", p => p.GetUInt32(UpdateFields.PlayerQuestLog11)));

        // The item's own quest: the details name the item GUID (HandleQuestgiverQueryQuestOpcode, TYPEMASK_..._OR_ITEM).
        await gm.SendAsync(WorldOpcode.CmsgQuestgiverQueryQuest, QuestBody(note, ItemStartedQuest));
        byte[] details = await gm.ReadUntilAsync(WorldOpcode.SmsgQuestgiverQuestDetails);
        Assert.Equal(note.Value, BinaryPrimitives.ReadUInt64LittleEndian(details));
        Assert.Equal(ItemStartedQuest, BinaryPrimitives.ReadUInt32LittleEndian(details.AsSpan(8)));

        // Accepting takes the quest and destroys the note (PlayerQuest.cpp:780-803: neither a required nor the source item).
        await gm.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, QuestBody(note, ItemStartedQuest));
        await gm.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);
        Assert.Equal(ItemStartedQuest, await host.PlayerStateAsync("Qitemgm", p => p.GetUInt32(UpdateFields.PlayerQuestLog11)));
        Assert.Equal(0u, await host.PlayerStateAsync("Qitemgm", p => p.Inventory.GetItemCount(StartNote)));
        await feature.Persistence.FlushCharacterAsync(id);
        Assert.Equal((byte)QuestStatus.Incomplete, fixture.Characters.Stored(id, ItemStartedQuest).Status);

        // The note is gone, so its GUID offers nothing any more.
        await gm.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, QuestBody(note, ItemStartedQuest));
        await gm.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);
        Assert.Equal(0u, await host.PlayerStateAsync("Qitemgm", p => p.GetUInt32(UpdateFields.PlayerQuestLog11 + QuestConstants.FieldsPerSlot)));

        // A letter the quest wants delivered stays in the bags, and the delivery counter sees it at once.
        await gm.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, QuestBody(letter, SelfDeliveredQuest));
        await gm.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);
        Assert.Equal(SelfDeliveredQuest, await host.PlayerStateAsync("Qitemgm", p => p.GetUInt32(UpdateFields.PlayerQuestLog11 + QuestConstants.FieldsPerSlot)));
        Assert.Equal(1u, await host.PlayerStateAsync("Qitemgm", p => p.Inventory.GetItemCount(Letter)));
        await feature.Persistence.FlushCharacterAsync(id);
        CharacterQuestStatus delivered = fixture.Characters.Stored(id, SelfDeliveredQuest);
        Assert.Equal((byte)QuestStatus.Complete, delivered.Status);
        Assert.Equal(1u, delivered.ItemCount1);
    }

    [Fact]
    public async Task ItemStartedQuest_IsRefusedToADeadPlayer()
    {
        var fixture = new QuestCommandFixture();
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient gm = await host.EnterWorldAsync("QDEADGM", "Qdeadgm", AccountSecurity.Administrator);
        await gm.CollectAsync();
        Assert.Null(await Run(gm, $".additem {StartNote}", expectReply: false));
        ObjectGuid note = await host.PlayerStateAsync("Qdeadgm", p => p.Inventory.AllItems.Single(i => i.Entry == StartNote).Guid);
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Qdeadgm")!.Health = 0);

        await gm.SendAsync(WorldOpcode.CmsgQuestgiverQueryQuest, QuestBody(note, ItemStartedQuest));
        await gm.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, QuestBody(note, ItemStartedQuest));
        while (true)
        {
            (WorldOpcode opcode, _) = await gm.ReadAsync();
            Assert.NotEqual(WorldOpcode.SmsgQuestgiverQuestDetails, opcode);
            if (opcode == WorldOpcode.SmsgGossipComplete)
            {
                break;
            }
        }

        Assert.Equal(0u, await host.PlayerStateAsync("Qdeadgm", p => p.GetUInt32(UpdateFields.PlayerQuestLog11)));
        Assert.Equal(1u, await host.PlayerStateAsync("Qdeadgm", p => p.Inventory.GetItemCount(StartNote)));
    }

    private static byte[] QuestBody(ObjectGuid giver, uint questId)
    {
        var writer = new PacketWriter(12);
        writer.WriteUInt64(giver.Value);
        writer.WriteUInt32(questId);
        return writer.ToArray();
    }

    private static Game.Npc.QuestNpcServices Services(Player player)
        => ((WorldSession)player.Session).Services.GetRequiredService<QuestNpcFeature>().Services;

    private static Task<QuestNpcFeature> FeatureAsync(WorldTestHost host, string name)
        => host.PlayerStateAsync(name, p => ((WorldSession)p.Session).Services.GetRequiredService<QuestNpcFeature>());

    /// <summary>Send a command; the next chat line (the reply) or null when it produced none.</summary>
    private static async Task<string?> Run(WorldTestClient client, string command, bool expectReply = true)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        if (expectReply)
        {
            return (await client.ReadChatAsync()).Text;
        }

        // Flush: a following sentinel command's reply proves nothing else was said before it.
        await client.SendChatAsync(ChatType.Say, Language.Common, ".nosuchcommand");
        string next = (await client.ReadChatAsync()).Text;
        Assert.Equal("There is no such command", next);
        return null;
    }

    private static WorldTestHost Start(QuestCommandFixture fixture)
    {
        var content = new ItemTestContent();
        content.Templates.Templates.AddRange(
        [
            new ItemTemplate { Entry = 25, Class = 2, SubClass = 7, Name = "Worn Shortsword", DisplayId = 1542, Quality = 1, InventoryType = 21, Delay = 1900, MaxDurability = 20, Damages = [new ItemDamage(1, 3, 0)] },
            new ItemTemplate { Entry = 117, Class = 0, SubClass = 0, Name = "Tough Jerky", DisplayId = 2473, Quality = 1, Stackable = 20 },
            new ItemTemplate { Entry = QuestPelt, Class = 12, SubClass = 0, Name = "Test Pelt", DisplayId = 108, Quality = 1, Stackable = 10, Bonding = 4 },
            new ItemTemplate { Entry = SourceToken, Class = 12, SubClass = 0, Name = "Test Token", DisplayId = 110, Quality = 1, Bonding = 4 },
            new ItemTemplate { Entry = StartNote, Class = 12, SubClass = 0, Name = "Test Note", DisplayId = 109, Quality = 1, Bonding = 4, StartQuest = ItemStartedQuest },
            new ItemTemplate { Entry = Letter, Class = 12, SubClass = 0, Name = "Test Letter", DisplayId = 111, Quality = 1, Bonding = 4, StartQuest = SelfDeliveredQuest },
        ]);
        content.Templates.StartingItems.AddRange([new StartingItem(1, 1, 25, 1), new StartingItem(1, 1, 117, 4)]);
        QuestCommandTestServices.Current.Value = fixture;
        try
        {
            using (content.Use())
            {
                return WorldTestHost.Start();
            }
        }
        finally
        {
            QuestCommandTestServices.Current.Value = null;
        }
    }
}

internal sealed class QuestCommandTestServices : IWorldTestServices
{
    public static readonly AsyncLocal<QuestCommandFixture?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture)
        {
            return;
        }

        services.AddSingleton<IQuestContentStore>(fixture);
        services.AddSingleton<ICreatureDataStore>(fixture);
        services.AddSingleton<ICharacterQuestStore>(fixture.Characters);
    }
}

/// <summary>
/// Synthetic quests for the GM tooling: a kill quest, a delivery quest with a source item, an item-started quest, a quest the
/// support gate withholds (reward mail), a quest that costs money, an item-started quest that wants its item back, and twenty
/// fillers naming a creature without a template. Creatures 90 and 91 have templates; 92 does not.
/// </summary>
internal sealed class QuestCommandFixture : IQuestContentStore, ICreatureDataStore
{
    public const uint KillQuest = 910001;
    public const uint ItemQuest = 910002;
    public const uint ItemStartedQuest = 910003;
    public const uint WithheldQuest = 910004;
    public const uint MoneyQuest = 910005;
    public const uint SelfDeliveredQuest = 910006;
    public const uint FillerQuest = 920001;
    public const uint QuestPelt = 910100;
    public const uint SourceToken = 910101;
    public const uint StartNote = 910102;
    public const uint Letter = 910103;

    public MemoryQuestStore Characters { get; } = new();

    public Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<QuestTemplate> templates =
        [
            new() { Entry = KillQuest, Method = 2, MinLevel = 1, QuestLevel = 1, Title = "Cull the test", ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 2 },
            new() { Entry = ItemQuest, Method = 2, MinLevel = 1, QuestLevel = 1, Title = "Gather the test", ReqItemId1 = QuestPelt, ReqItemCount1 = 3, SrcItemId = SourceToken, SrcItemCount = 1 },
            new() { Entry = ItemStartedQuest, Method = 2, MinLevel = 1, QuestLevel = 1, Title = "Read the test", ReqCreatureOrGOId1 = 91, ReqCreatureOrGOCount1 = 1 },
            new() { Entry = WithheldQuest, Method = 2, MinLevel = 1, QuestLevel = 1, Title = "Mail the test", RewMailTemplateId = 1 },
            new() { Entry = MoneyQuest, Method = 2, MinLevel = 1, QuestLevel = 1, Title = "Pay the test", RewOrReqMoney = -100 },
            new() { Entry = SelfDeliveredQuest, Method = 2, MinLevel = 1, QuestLevel = 1, Title = "Deliver the test", ReqItemId1 = Letter, ReqItemCount1 = 1 },
        ];
        for (uint i = 0; i < QuestConstants.MaxQuestLogSize; i++)
        {
            templates.Add(new QuestTemplate { Entry = FillerQuest + i, Method = 2, MinLevel = 1, QuestLevel = 1, Title = $"Filler {i}", ReqCreatureOrGOId1 = 92, ReqCreatureOrGOCount1 = 1 });
        }

        return Task.FromResult(new QuestContent(templates, [], []));
    }

    Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
        [
            new CreatureTemplate { Entry = 90, Name = "Test quarry", Faction = 14, DisplayIds = [49] },
            new CreatureTemplate { Entry = 91, Name = "Test reader", Faction = 14, DisplayIds = [49] },
        ], [], [], [], []));
}
