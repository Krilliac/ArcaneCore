using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// Each interaction kind end to end: a bot's request through the real game service (vendor, trainer, quest giver, quest log, game object, creature AI),
/// the game's QA seam, the interaction monitor, the sink and the signal queue, into a finding of the feature, and out as the issue the publisher creates
/// (with the interaction section, the names and the reproduction). The same requests by a real player report nothing.
/// </summary>
public sealed class PlayerbotQaInteractionFeatureTests : IDisposable
{
    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    private QaInteractionHarness Harness() => new(_directory, git => git.MinDistinctBots = 1); // the kit has one bot

    [Fact]
    public async Task InteractionFailed_AVendorThatDoesNotSellTheItem_IsReportedAtOnce_AndBecomesAnIssue()
    {
        using QaInteractionHarness harness = Harness();
        using var kit = new QaInteractionKit(harness.Sink);
        for (int i = 0; i < 3; i++)
        {
            kit.Services.BuyItem(kit.Bot, kit.Vendor.Guid, QaInteractionKit.Unlisted, 1); // suspicious: reported every time (one throttle apart)
            kit.NowMs += PlayerbotQaInteractionMonitor.ThrottleMs;
        }

        Assert.Equal(3, harness.Feature.Drain());
        await harness.PassAsync();

        QaFindingView finding = Assert.Single(harness.Findings);
        Assert.Equal((QaSignalKind.InteractionFailed, QaCause.BehaviourBug, 3L, "buy-not-sold"), (finding.Kind, finding.Cause, finding.Count, finding.Reason));
        Assert.Equal((QaInteractionKit.VendorEntry, QaInteractionKit.Unlisted), (finding.Npc, finding.Item));
        (string title, string body, _) = Assert.Single(harness.Transport.Created);
        Assert.Equal($"[bot-qa] interaction failed: buy-not-sold creature {QaInteractionKit.VendorEntry} item {QaInteractionKit.Unlisted}: vendor does not sell it (CantFindItem (0))", title);
        Assert.Contains("## Interaction\n", body, StringComparison.Ordinal);
        Assert.Contains("| Reason | buy-not-sold |", body, StringComparison.Ordinal);
        Assert.Contains($"| Creature | {QaInteractionKit.VendorEntry} |", body, StringComparison.Ordinal);
        Assert.Contains($"`.go creature id {QaInteractionKit.VendorEntry}`", body, StringComparison.Ordinal);
        Assert.Contains("the vendor does not list the item", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InteractionFailed_AnExpectedRefusal_IsReportedOnlyWhenOneBotKeepsRepeatingIt()
    {
        using QaInteractionHarness harness = Harness();
        using var kit = new QaInteractionKit(harness.Sink);
        kit.Bot.Money = 0;
        for (int i = 0; i < PlayerbotQaInteractionMonitor.RepeatThreshold - 1; i++) kit.Services.BuyItem(kit.Bot, kit.Vendor.Guid, QaInteractionKit.Sword, 1);
        Assert.Equal(0, harness.Feature.Drain()); // no money four times: a refusal of play, nothing reported

        kit.Services.BuyItem(kit.Bot, kit.Vendor.Guid, QaInteractionKit.Sword, 1); // the fifth inside the window: a bot that does not learn
        Assert.Equal(1, harness.Feature.Drain());

        QaFindingView finding = Assert.Single(harness.Findings);
        Assert.Equal(("buy-no-money", QaCause.BehaviourBug), (finding.Reason, finding.Cause));
        Assert.Contains("one bot kept asking", finding.CauseDetail, StringComparison.Ordinal);
        Assert.Contains("5 times in", finding.CauseDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InteractionFailed_ATrainerThatCannotTeachAValidSpell_IsReportedAtOnce()
    {
        using QaInteractionHarness harness = Harness();
        using var kit = new QaInteractionKit(harness.Sink, teachingFails: true);
        for (int i = 0; i < 3; i++)
        {
            kit.Services.BuyTrainerSpell(kit.Bot, kit.Trainer.Guid, QaInteractionKit.TeachBroken);
            kit.NowMs += PlayerbotQaInteractionMonitor.ThrottleMs;
        }

        kit.Services.BuyTrainerSpell(kit.Bot, kit.Trainer.Guid, QaInteractionKit.TeachGood); // a spell it can teach: nothing to report
        Assert.Equal(3, harness.Feature.Drain());
        await harness.PassAsync();

        QaFindingView finding = Assert.Single(harness.Findings);
        Assert.Equal(("train-teaching-failed", QaCause.NotImplemented, QaInteractionKit.TeachBroken), (finding.Reason, finding.Cause, finding.Spell));
        (string title, string body, _) = Assert.Single(harness.Transport.Created);
        Assert.Contains("trainer cannot teach a valid spell", title, StringComparison.Ordinal);
        Assert.Contains($"| Spell | {QaInteractionKit.TeachBroken} |", body, StringComparison.Ordinal);
        Assert.Contains("| Classes | Warrior |", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuestBroken_AnObjectiveCreatureKilledAgainAndAgainWithNoCredit_BecomesAnIssue_AndACreditedOneDoesNot()
    {
        using QaInteractionHarness harness = Harness();
        using var kit = new QaInteractionKit(harness.Sink);
        kit.GiveQuest(kit.Bot, QaInteractionKit.KillQuest);
        Creature boar = kit.Victim(QaInteractionKit.ObjectiveCreature);

        // Credited kills (the quest service sends SMSG_QUESTUPDATE_ADD_KILL, which the bot's session hands to the tap): never a finding.
        for (int i = 0; i < 6; i++)
        {
            kit.Monitor.OnUnitKilled(kit.Bot, boar);
            kit.Services.KilledMonsterCredit(kit.Bot, QaInteractionKit.ObjectiveCreature, boar.Guid);
        }

        Assert.Equal(0, harness.Feature.Drain());

        // Kills the server never credits (here: no kill-credit route at all): every fifth in a row is a report of a broken objective; three make an issue.
        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < PlayerbotQaInteractionMonitor.ObjectiveAttempts; i++) kit.Monitor.OnUnitKilled(kit.Bot, boar);
            kit.NowMs += PlayerbotQaInteractionMonitor.ThrottleMs;
        }

        Assert.Equal(3, harness.Feature.Drain());
        await harness.PassAsync();

        QaFindingView finding = Assert.Single(harness.Findings);
        Assert.Equal((QaSignalKind.QuestBroken, "no-progress-kill", QaInteractionKit.KillQuest, 0, QaInteractionKit.ObjectiveCreature),
            (finding.Kind, finding.Reason, finding.Quest, finding.EffectIndex, finding.Npc));
        Assert.Equal(QaCause.NotImplemented, finding.Cause);
        (string title, string body, _) = Assert.Single(harness.Transport.Created);
        Assert.Equal($"[bot-qa] quest broken: quest {QaInteractionKit.KillQuest} objective 0: objective kill never credited (creature {QaInteractionKit.ObjectiveCreature})", title);
        Assert.Contains($"`.quest add {QaInteractionKit.KillQuest}`", body, StringComparison.Ordinal);
        Assert.Contains($"`.go creature id {QaInteractionKit.ObjectiveCreature}`", body, StringComparison.Ordinal);
        Assert.Contains("| Objective | 0 |", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuestBroken_AnObjectiveItemReceivedAgainAndAgainWithNoCount_IsReported_AndACountedOneIsNot()
    {
        using QaInteractionHarness harness = Harness();
        using var kit = new QaInteractionKit(harness.Sink);
        kit.GiveQuest(kit.Bot, QaInteractionKit.ItemQuest);

        // Through the real store: the item push and the quest's item check (SMSG_QUESTUPDATE_ADD_ITEM) both reach the tap.
        for (int i = 0; i < 6; i++)
        {
            Assert.True(kit.Items.StoreNewItem(kit.Bot, QaInteractionKit.QuestItem, 1));
            kit.Services.ItemAdded(kit.Bot, QaInteractionKit.QuestItem, 1);
        }

        Assert.Equal(0, harness.Feature.Drain());

        // Items that reach the bags with no quest check: the objective never counts them.
        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < PlayerbotQaInteractionMonitor.ObjectiveAttempts; i++) Assert.True(kit.Items.StoreNewItem(kit.Bot, QaInteractionKit.QuestItem, 1));
            kit.NowMs += PlayerbotQaInteractionMonitor.ThrottleMs;
        }

        Assert.Equal(3, harness.Feature.Drain());

        QaFindingView finding = Assert.Single(harness.Findings);
        Assert.Equal(("no-progress-item", QaInteractionKit.ItemQuest, QaInteractionKit.QuestItem), (finding.Reason, finding.Quest, finding.Item));
        await harness.PassAsync();
        Assert.Contains($"`.additem {QaInteractionKit.QuestItem}`", Assert.Single(harness.Transport.Created).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuestBroken_AQuestTheServerCannotSupport_IsReportedWhenABotTriesToTakeIt()
    {
        using QaInteractionHarness harness = Harness();
        using var kit = new QaInteractionKit(harness.Sink);
        Assert.False(kit.Services.AcceptQuest(kit.Bot, kit.Giver.Guid, QaInteractionKit.UnsupportedQuest));
        Assert.Equal(1, harness.Feature.Drain());

        QaFindingView finding = Assert.Single(harness.Findings);
        Assert.Equal(("unsupported", QaCause.NotImplemented, QaInteractionKit.UnsupportedQuest), (finding.Reason, finding.Cause, finding.Quest));
        Assert.Contains("SrcSpell", finding.CauseDetail, StringComparison.Ordinal);
        Assert.Equal($"quest {QaInteractionKit.UnsupportedQuest}: quest not supported", finding.Subject);
        await harness.PassAsync();
    }

    [Fact]
    public async Task ObjectUnsupported_ABotUsingAnUnhandledType_IsOneIssue_ByEntryAndType()
    {
        using QaInteractionHarness harness = Harness();
        using var kit = new QaInteractionKit(harness.Sink);
        Assert.Equal(Game.GameObjects.GameObjectUseResult.Unsupported, kit.Objects.Use(kit.Bot, kit.Object(QaInteractionKit.FlagDropObject).Guid));
        Assert.Equal(Game.GameObjects.GameObjectUseResult.Unsupported, kit.Objects.Use(kit.Bot, kit.Object(QaInteractionKit.QuestGiverObject).Guid));
        Assert.Equal(2, harness.Feature.Drain());
        await harness.PassAsync();

        Assert.Equal(2, harness.Findings.Count);
        QaFindingView flag = Assert.Single(harness.Findings, f => f.GameObject == QaInteractionKit.FlagDropObject);
        Assert.Equal((QaSignalKind.ObjectUnsupported, QaCause.NotImplemented, "type"), (flag.Kind, flag.Cause, flag.Reason));
        QaFindingView giver = Assert.Single(harness.Findings, f => f.GameObject == QaInteractionKit.QuestGiverObject);
        Assert.Equal("quest-menu", giver.Reason);

        // One occurrence each is enough (bot-independent), but the kind's own cap allows one issue a pass: the second waits for the next pass.
        (string title, string body, _) = Assert.Single(harness.Transport.Created);
        Assert.StartsWith("[bot-qa] object unsupported: game object ", title, StringComparison.Ordinal);
        Assert.Contains("`.go object id ", body, StringComparison.Ordinal);
        await harness.PassAsync();
        Assert.Equal(2, harness.Transport.Created.Count);
    }

    [Fact]
    public async Task ScriptUnsupported_ACreatureWithAnUnknownAIName_IsReportedForTheContent()
    {
        using QaInteractionHarness harness = Harness();
        using var kit = new QaInteractionKit(harness.Sink);
        var template = new CreatureTemplate { Entry = 997001, Name = "Odd Ghoul", AIName = "NoSuchAI", MinLevel = 5, MaxLevel = 5, DisplayIds = [16000], Faction = 14,
            MinLevelHealth = 100, MaxLevelHealth = 100 };
        var spawn = new CreatureSpawn { Guid = 997101, Entry = 997001, MapId = 0, X = kit.Bot.X + 3, Y = kit.Bot.Y, Z = kit.Bot.Z };
        // The map's grid is loaded already (the players stand in it): the system spawns the creature while it is built, so the observer is a constructor argument.
        var system = new CreatureMapSystem(kit.World.GetMap(0), new CreatureContent([template], [spawn], [], [], []), qa: kit.Monitor);
        kit.World.GetMap(0).AddUpdater(system);
        for (int i = 0; i < 4; i++) kit.World.RunTick(200);
        Assert.Contains(system.Creatures, c => c.Entry == 997001);

        Assert.Equal(1, harness.Feature.Drain());
        await harness.PassAsync();
        QaFindingView finding = Assert.Single(harness.Findings);
        Assert.Equal((QaSignalKind.ScriptUnsupported, "ai-name", 997001u, 0), (finding.Kind, finding.Reason, finding.Npc, finding.DistinctBots));
        (string title, _, _) = Assert.Single(harness.Transport.Created);
        Assert.Equal("[bot-qa] script unsupported: creature 997001: unknown AIName 'NoSuchAI'", title);
    }

    [Fact]
    public async Task ARealPlayer_DoingAllOfIt_ReportsNothing()
    {
        using QaInteractionHarness harness = Harness();
        using var kit = new QaInteractionKit(harness.Sink);
        kit.Human.Money = 0;
        kit.GiveQuest(kit.Human, QaInteractionKit.KillQuest);
        Creature boar = kit.Victim(QaInteractionKit.ObjectiveCreature);
        for (int i = 0; i < 10; i++)
        {
            kit.Services.BuyItem(kit.Human, kit.Vendor.Guid, QaInteractionKit.Unlisted, 1);
            kit.Services.BuyItem(kit.Human, kit.Vendor.Guid, QaInteractionKit.Sword, 1);
            kit.Services.BuyTrainerSpell(kit.Human, kit.Trainer.Guid, QaInteractionKit.TeachBroken);
            kit.Services.AcceptQuest(kit.Human, kit.Giver.Guid, QaInteractionKit.UnsupportedQuest);
            kit.Monitor.OnUnitKilled(kit.Human, boar);
            kit.Objects.Use(kit.Human, kit.Object(QaInteractionKit.FlagDropObject).Guid);
            kit.NowMs += PlayerbotQaInteractionMonitor.ThrottleMs;
        }

        Assert.Equal(0, harness.Feature.Drain());
        Assert.Empty(harness.Findings);
        Assert.Equal(0, kit.Monitor.TrackedAttempts);
        await harness.PassAsync();
        Assert.Empty(harness.Transport.Created);
    }
}
