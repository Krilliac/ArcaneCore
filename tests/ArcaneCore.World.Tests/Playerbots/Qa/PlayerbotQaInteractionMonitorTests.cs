using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Diagnostics;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>The QA interaction monitor on its own: repeat windows, throttles, objective attempts, the packet tap, real players and the hot path.</summary>
public sealed class PlayerbotQaInteractionMonitorTests
{
    [Fact]
    public void AnExpectedRefusal_OutsideTheWindow_StartsCountingAgain()
    {
        var sink = new QaInteractionRecordingSink();
        using var kit = new QaInteractionKit(sink);
        for (int i = 0; i < PlayerbotQaInteractionMonitor.RepeatThreshold - 1; i++)
        {
            kit.Monitor.OnInteractionFailed(kit.Bot, InteractionFailure.BuyNoMoney, 2, QaInteractionKit.VendorEntry, QaInteractionKit.Sword, 0);
        }

        kit.NowMs += PlayerbotQaInteractionMonitor.RepeatWindowMs + 1; // the bot backed off long enough: it learned
        kit.Monitor.OnInteractionFailed(kit.Bot, InteractionFailure.BuyNoMoney, 2, QaInteractionKit.VendorEntry, QaInteractionKit.Sword, 0);
        Assert.Empty(sink.Signals);

        for (int i = 0; i < PlayerbotQaInteractionMonitor.RepeatThreshold - 1; i++)
        {
            kit.Monitor.OnInteractionFailed(kit.Bot, InteractionFailure.BuyNoMoney, 2, QaInteractionKit.VendorEntry, QaInteractionKit.Sword, 0);
        }

        QaSignal signal = Assert.Single(sink.Signals);
        Assert.Equal(("buy-no-money", 2, QaInteractionKit.BotKey), (signal.Reason, signal.Code, signal.BotKey!.Value));
        Assert.StartsWith("5 times in", signal.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ASuspiciousRefusal_IsThrottledPerBot_AndReportedAgainAfterTheThrottle()
    {
        var sink = new QaInteractionRecordingSink();
        using var kit = new QaInteractionKit(sink);
        for (int i = 0; i < 4; i++) kit.Monitor.OnInteractionFailed(kit.Bot, InteractionFailure.BuyNotSold, 0, QaInteractionKit.VendorEntry, QaInteractionKit.Unlisted, 0);
        Assert.Single(sink.Signals);
        kit.NowMs += PlayerbotQaInteractionMonitor.ThrottleMs;
        kit.Monitor.OnInteractionFailed(kit.Bot, InteractionFailure.BuyNotSold, 0, QaInteractionKit.VendorEntry, QaInteractionKit.Unlisted, 0);
        Assert.Equal(2, sink.Signals.Count);
    }

    [Fact]
    public void ABuyThatCannotBeStored_IsReportedOnce_NotAgainByItsEquipError()
    {
        var sink = new QaInteractionRecordingSink();
        using var kit = new QaInteractionKit(sink);
        // An equip error a purchase should never get (IntBagError): suspicious, so it is reported at once - by the vendor site, with the vendor.
        kit.Monitor.OnInteractionFailed(kit.Bot, InteractionFailure.BuyCannotStore, (int)InventoryResult.IntBagError, QaInteractionKit.VendorEntry, QaInteractionKit.Bread, 0);
        kit.Monitor.OnPlayerPacket(kit.Bot, WorldOpcode.SmsgInventoryChangeFailure, ItemPackets.InventoryChangeFailure(InventoryResult.IntBagError, default, default));
        QaSignal signal = Assert.Single(sink.Signals);
        Assert.Equal((QaInteractionKit.VendorEntry, "buy-cannot-store"), (signal.Npc, signal.Reason));

        kit.NowMs += PlayerbotQaInteractionMonitor.ThrottleMs; // a later equip error of its own is the tap's
        kit.Monitor.OnPlayerPacket(kit.Bot, WorldOpcode.SmsgInventoryChangeFailure, ItemPackets.InventoryChangeFailure(InventoryResult.IntBagError, default, default));
        Assert.Equal("inventory", sink.Signals[^1].Reason);
    }

    [Fact]
    public void TheInventoryTap_NamesTheItemOfTheFailure()
    {
        var sink = new QaInteractionRecordingSink();
        using var kit = new QaInteractionKit(sink);
        Assert.True(kit.Items.StoreNewItem(kit.Bot, QaInteractionKit.Sword, 1));
        Item sword = kit.Bot.Inventory.AllItems.Single(i => i.Entry == QaInteractionKit.Sword);
        kit.Bot.Inventory.SendEquipError(InventoryResult.YouCanNeverUseThatItem, sword, null); // through the bot's session, as the server sends it
        QaSignal signal = Assert.Single(sink.Signals);
        Assert.Equal(("inventory", (int)InventoryResult.YouCanNeverUseThatItem, QaInteractionKit.Sword), (signal.Reason, signal.Code, signal.Item));

        kit.NowMs += PlayerbotQaInteractionMonitor.ThrottleMs;
        kit.Bot.Inventory.SendEquipError(InventoryResult.CantEquipLevelI, sword, null, entry: QaInteractionKit.Sword); // the level is before the guid
        kit.Bot.Inventory.SendEquipError(InventoryResult.BagFull, null, null); // expected: only on repeats
        Assert.Single(sink.Signals);
    }

    [Fact]
    public void ALootRefusal_UnsupportedAtOnce_OtherResultsOnlyOnRepeats()
    {
        var sink = new QaInteractionRecordingSink();
        using var kit = new QaInteractionKit(sink);
        ObjectGuid chest = ObjectGuid.WithEntry(HighGuid.GameObject, 4000, 1);
        ObjectGuid corpse = ObjectGuid.WithEntry(HighGuid.Unit, 3000, 1);
        kit.Monitor.OnLootRefused(kit.Bot, chest, LootResult.Unsupported);
        kit.Monitor.OnLootRefused(kit.Bot, corpse, LootResult.TooFar);
        kit.Monitor.OnLootRefused(kit.Bot, corpse, LootResult.Ok);
        QaSignal signal = Assert.Single(sink.Signals);
        Assert.Equal(("loot", 4000u, 0u), (signal.Reason, signal.GameObject, signal.Npc));
    }

    [Fact]
    public void ObjectiveAttempts_AreCountedPerObjective_AndResetByItsCredit()
    {
        var sink = new QaInteractionRecordingSink();
        using var kit = new QaInteractionKit(sink);
        kit.GiveQuest(kit.Bot, QaInteractionKit.KillQuest);
        Creature boar = kit.Victim(QaInteractionKit.ObjectiveCreature);
        Creature other = kit.Victim(993091);

        for (int i = 0; i < PlayerbotQaInteractionMonitor.ObjectiveAttempts - 1; i++) kit.Monitor.OnUnitKilled(kit.Bot, boar);
        kit.Monitor.OnUnitKilled(kit.Bot, other); // not an objective: no attempt
        kit.Monitor.OnPlayerPacket(kit.Bot, WorldOpcode.SmsgQuestupdateAddKill, AddKill(QaInteractionKit.KillQuest, QaInteractionKit.ObjectiveCreature));
        kit.Monitor.OnUnitKilled(kit.Bot, boar); // counting starts again after the credit
        Assert.Empty(sink.Signals);

        for (int i = 0; i < PlayerbotQaInteractionMonitor.ObjectiveAttempts - 1; i++) kit.Monitor.OnUnitKilled(kit.Bot, boar);
        QaSignal signal = Assert.Single(sink.Signals);
        Assert.Equal((QaSignalKind.QuestBroken, "no-progress-kill", QaInteractionKit.KillQuest, 0), (signal.Kind, signal.Reason, signal.Quest, signal.EffectIndex));
    }

    [Fact]
    public void ObjectiveAttempts_OfACompleteObjective_OrAQuestNotInTheLog_AreNotCounted()
    {
        var sink = new QaInteractionRecordingSink();
        using var kit = new QaInteractionKit(sink);
        Creature boar = kit.Victim(QaInteractionKit.ObjectiveCreature);
        for (int i = 0; i < 10; i++) kit.Monitor.OnUnitKilled(kit.Bot, boar); // no quest
        kit.GiveQuest(kit.Bot, QaInteractionKit.KillQuest);
        kit.BotState.Quests.Get(QaInteractionKit.KillQuest)!.CreatureOrGOCount[0] = 8; // done
        for (int i = 0; i < 10; i++) kit.Monitor.OnUnitKilled(kit.Bot, boar);
        Assert.Empty(sink.Signals);
        Assert.Equal(0, kit.Monitor.TrackedAttempts);
    }

    [Fact]
    public void AnObjectiveObject_UsedAgainAndAgainWithNoCredit_IsReported()
    {
        var sink = new QaInteractionRecordingSink();
        using var kit = new QaInteractionKit(sink);
        // An object objective (a negative ReqCreatureOrGOId) is credited through SMSG_QUESTUPDATE_ADD_KILL with the object flag.
        var quest = new Kernel.Quests.QuestTemplate { Entry = 993010, Method = 2, MinLevel = 1, QuestLevel = 5, Title = "Use it", ReqCreatureOrGOId1 = -(int)QaInteractionKit.FlagDropObject, ReqCreatureOrGOCount1 = 3 };
        kit.Services.ReplaceQuests(new Game.Quests.QuestStore(new Kernel.Quests.QuestContent([quest], [], [])));
        kit.GiveQuest(kit.Bot, 993010);
        Game.GameObjects.GameObject banner = kit.Object(QaInteractionKit.FlagDropObject);
        for (int i = 0; i < PlayerbotQaInteractionMonitor.ObjectiveAttempts - 1; i++) kit.Monitor.OnObjectUsed(kit.Bot, banner);
        kit.Monitor.OnPlayerPacket(kit.Bot, WorldOpcode.SmsgQuestupdateAddKill, AddKill(993010, QaInteractionKit.FlagDropObject | 0x80000000u));
        kit.Monitor.OnObjectUsed(kit.Bot, banner);
        Assert.Empty(sink.Signals);
        for (int i = 0; i < PlayerbotQaInteractionMonitor.ObjectiveAttempts - 1; i++) kit.Monitor.OnObjectUsed(kit.Bot, banner);
        QaSignal signal = Assert.Single(sink.Signals);
        Assert.Equal(("no-progress-object", QaInteractionKit.FlagDropObject), (signal.Reason, signal.GameObject));
    }

    [Fact]
    public void ACreatureAiGap_IsReportedWithoutABot_AndWithTheBotTheCreatureFights()
    {
        var sink = new QaInteractionRecordingSink();
        using var kit = new QaInteractionKit(sink);
        kit.Monitor.OnCreatureAiGap(null, 0, "summon", 5555, "instance script summon");
        QaSignal content = Assert.Single(sink.Signals);
        Assert.Equal((QaSignalKind.ScriptUnsupported, (Guid?)null, 5555u, false), (content.Kind, content.BotKey, content.Target, content.HasPosition));
        kit.Monitor.OnCreatureAiGap(null, 0, "summon", 5555, "instance script summon"); // throttled
        Assert.Single(sink.Signals);
    }

    [Fact]
    public void WithQaDisabled_NothingIsReportedOrCounted()
    {
        var sink = new QaInteractionRecordingSink { Enabled = false };
        using var kit = new QaInteractionKit(sink);
        Assert.False(kit.Monitor.Active);
        kit.GiveQuest(kit.Bot, QaInteractionKit.KillQuest);
        Creature boar = kit.Victim(QaInteractionKit.ObjectiveCreature);
        for (int i = 0; i < 10; i++)
        {
            kit.Monitor.OnUnitKilled(kit.Bot, boar);
            kit.Monitor.OnInteractionFailed(kit.Bot, InteractionFailure.BuyNotSold, 0, 1, 2, 0);
            kit.Monitor.OnCreatureAiGap(null, 7, "ai-name", 0, "X");
        }

        Assert.Empty(sink.Signals);
        Assert.Equal(0, kit.Monitor.TrackedAttempts);
    }

    [Fact]
    public void TheHotPaths_AllocateNothing_ForWhatIsNotAFailure()
    {
        var sink = new QaInteractionRecordingSink();
        using var kit = new QaInteractionKit(sink);
        kit.GiveQuest(kit.Bot, QaInteractionKit.KillQuest);
        Creature boar = kit.Victim(QaInteractionKit.ObjectiveCreature);
        Creature stranger = kit.Victim(993091);
        byte[] move = new byte[32];
        byte[] addKill = AddKill(QaInteractionKit.KillQuest, QaInteractionKit.ObjectiveCreature);
        byte[] push = ItemPush(kit.Bot.Guid, QaInteractionKit.Bread);

        void Run()
        {
            for (int i = 0; i < 2_000; i++)
            {
                kit.Monitor.OnPlayerPacket(kit.Bot, WorldOpcode.SmsgMonsterMove, move); // most bot packets
                kit.Monitor.OnPlayerPacket(kit.Bot, WorldOpcode.SmsgQuestupdateAddKill, addKill); // a credit, nothing tracked
                kit.Monitor.OnPlayerPacket(kit.Bot, WorldOpcode.SmsgItemPushResult, push); // a pushed item no quest wants
                kit.Monitor.OnUnitKilled(kit.Human, boar); // a real player's kill
                kit.Monitor.OnUnitKilled(kit.Bot, stranger); // a bot's kill of a creature no objective names
                kit.Monitor.OnUnitKilled(null, boar);
            }
        }

        Run(); // warm up (JIT, first dictionary probes)
        ArcaneCore.TestSupport.AllocationProbe.AssertEveryCleanWindowAtMost(Run, 0, "the monitor hot paths for what is not a failure");
        Assert.Empty(sink.Signals);
    }

    internal static byte[] AddKill(uint quest, uint entry)
    {
        byte[] payload = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, quest);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), entry);
        return payload;
    }

    private static byte[] ItemPush(ObjectGuid player, uint entry)
    {
        byte[] payload = new byte[45];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, player.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(25), entry);
        return payload;
    }
}
