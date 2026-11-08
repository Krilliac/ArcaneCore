using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Diagnostics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// The objective counters keyed per quest, the raid-group rule of the kill hook, the gap in the object-unsupported key, the observer's call-site
/// gate and the detachable kill feature (bot QA interaction monitor review items).
/// </summary>
public sealed class PlayerbotQaInteractionRepairTests
{
    private static QaBot? BotOf(QaInteractionKit kit, Unit? unit) => ReferenceEquals(unit, kit.Bot) ? new QaBot(QaInteractionKit.BotKey, 1) : null;

    [Fact]
    public void TwoQuestsNamingOneCreature_EachCountTheirOwnKills_AndAreReportedTogether()
    {
        var sink = new QaInteractionRecordingSink();
        using var kit = new QaInteractionKit(sink);
        kit.GiveQuest(kit.Bot, QaInteractionKit.KillQuest);
        kit.GiveQuest(kit.Bot, QaInteractionKit.HighQuest); // the same objective creature
        Creature boar = kit.Victim(QaInteractionKit.ObjectiveCreature);

        for (int i = 0; i < PlayerbotQaInteractionMonitor.ObjectiveAttempts; i++) kit.Monitor.OnUnitKilled(kit.Bot, boar);

        Assert.Equal(new[] { QaInteractionKit.KillQuest, QaInteractionKit.HighQuest }, sink.Signals.Select(s => s.Quest).Order());
        Assert.All(sink.Signals, s => Assert.Equal("no-progress-kill", s.Reason));
    }

    [Fact]
    public void ACreditForOneQuest_DoesNotClearTheKillsCountedForTheOther()
    {
        var sink = new QaInteractionRecordingSink();
        using var kit = new QaInteractionKit(sink);
        kit.GiveQuest(kit.Bot, QaInteractionKit.KillQuest);
        kit.GiveQuest(kit.Bot, QaInteractionKit.HighQuest);
        Creature boar = kit.Victim(QaInteractionKit.ObjectiveCreature);

        for (int i = 0; i < PlayerbotQaInteractionMonitor.ObjectiveAttempts - 1; i++) kit.Monitor.OnUnitKilled(kit.Bot, boar);
        kit.Monitor.OnPlayerPacket(kit.Bot, WorldOpcode.SmsgQuestupdateAddKill, PlayerbotQaInteractionMonitorTests.AddKill(QaInteractionKit.KillQuest, QaInteractionKit.ObjectiveCreature));
        kit.Monitor.OnUnitKilled(kit.Bot, boar);

        QaSignal signal = Assert.Single(sink.Signals); // only the quest that was not credited reached five
        Assert.Equal(QaInteractionKit.HighQuest, signal.Quest);
    }

    [Fact]
    public void ABotInARaidGroup_MakesNoKillAttemptForAQuestThatIsNotAllowedInRaids()
    {
        var raidSink = new QaInteractionRecordingSink();
        var soloSink = new QaInteractionRecordingSink();
        using var kit = new QaInteractionKit(new QaInteractionRecordingSink());
        kit.GiveQuest(kit.Bot, QaInteractionKit.KillQuest);
        Creature boar = kit.Victim(QaInteractionKit.ObjectiveCreature);
        var inRaid = new PlayerbotQaInteractionMonitor(raidSink, unit => BotOf(kit, unit), () => kit.NowMs, () => kit.Services, inRaidGroup: _ => true);
        var alone = new PlayerbotQaInteractionMonitor(soloSink, unit => BotOf(kit, unit), () => kit.NowMs, () => kit.Services, inRaidGroup: _ => false);

        for (int i = 0; i < PlayerbotQaInteractionMonitor.ObjectiveAttempts * 2; i++)
        {
            inRaid.OnUnitKilled(kit.Bot, boar); // the kill credit skips this quest for a raid group, so these kills are no sign of a broken objective
            alone.OnUnitKilled(kit.Bot, boar);
        }

        Assert.Empty(raidSink.Signals);
        Assert.Equal(0, inRaid.TrackedAttempts);
        Assert.Equal("no-progress-kill", soloSink.Signals.First().Reason); // the control: the same kills outside a raid are reported
    }

    [Fact]
    public void AnObjectUnsupportedFinding_IsKeyedByTheGapToo()
    {
        static QaSignal Gap(string reason) => new()
        {
            Kind = QaSignalKind.ObjectUnsupported, AtUtc = DateTime.UtcNow, BotKey = QaInteractionKit.BotKey, GameObject = 994001, Code = 26, Reason = reason, Message = "a bot used it",
        };

        Assert.NotEqual(PlayerbotQaFingerprint.Of(Gap("type")), PlayerbotQaFingerprint.Of(Gap("quest-menu")));
        Assert.Equal(PlayerbotQaFingerprint.Of(Gap("type")), PlayerbotQaFingerprint.Of(Gap("type") with { BotKey = Guid.NewGuid() }));
    }

    [Fact]
    public void TheObserver_WatchesABotOnly_AndOnlyWhileQaReports()
    {
        var sink = new QaInteractionRecordingSink();
        using var kit = new QaInteractionKit(sink);
        IGameQaObserver observer = kit.Monitor;
        Assert.True(observer.Reporting);
        Assert.True(observer.Observes(kit.Bot));
        Assert.False(observer.Observes(kit.Human));
        sink.Enabled = false;
        Assert.False(observer.Reporting);
        Assert.False(observer.Observes(kit.Bot));
    }

    [Fact]
    public async Task TheKillFeature_IsStateless_AndItsStopAndDetachUnsubscribeEveryMap()
    {
        var sink = new QaInteractionRecordingSink();
        using var kit = new QaInteractionKit(sink);
        using ServiceProvider services = new ServiceCollection().AddSingleton(kit.Monitor).BuildServiceProvider();
        var feature = new PlayerbotQaInteractionFeature(services);
        Assert.Equal(DetachKind.Stateless, feature.DetachKind);

        var bag = new FeatureSubscriptions("test");
        feature.AttachFrom(kit.World, null, bag);
        int maps = feature.SubscribedMaps;
        Assert.True(maps > 0, "no map was subscribed: the check proves nothing");
        Assert.True(bag.Count > 0);

        feature.Detach(new FeatureHandover());
        bag.Dispose();
        Assert.Equal(0, feature.SubscribedMaps);
        Assert.Equal(0, bag.Count);

        var again = new FeatureSubscriptions("test");
        feature.AttachFrom(kit.World, null, again); // a cold attach after a detach subscribes the maps once each
        Assert.Equal(maps, feature.SubscribedMaps);
        await feature.StopAsync();
        Assert.Equal(0, feature.SubscribedMaps);
        Assert.True(again.IsDisposed);
    }
}
