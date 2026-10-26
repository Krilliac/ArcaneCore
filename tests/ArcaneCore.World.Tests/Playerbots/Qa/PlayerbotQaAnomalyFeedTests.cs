using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// The bot-QA anomaly feed admits a short allowlist of world keys (<see cref="PlayerbotQaAnomalyFeed.WorldKeys"/>) next to <c>bot-*</c> keys,
/// under the same severity and count thresholds. Reasoning for the RED proof: before the allowlist, <c>Qualifies</c> required a
/// <c>bot-</c> subsystem prefix, so every assertion below that expects a signal for a world key failed.
/// </summary>
public sealed class PlayerbotQaAnomalyFeedTests
{
    [Fact]
    public void ErrorGameObjectLootEmpty_QualifiesOnTheFirstOccurrence()
    {
        var ledger = new AnomalyLedger();
        var feed = new PlayerbotQaAnomalyFeed(ledger);

        ledger.Report("loot", "gameobject-loot-empty", "gameobject entry 180523 loot 17378", "reason no-rows", AnomalySeverity.Error);

        QaSignal signal = Assert.Single(feed.Pull());
        Assert.Equal(QaSignalKind.Anomaly, signal.Kind);
        Assert.Equal("loot/gameobject-loot-empty", signal.FaultCode);
        Assert.Equal("loot", signal.Goal);
        Assert.Equal("gameobject-loot-empty", signal.Reason);
    }

    [Fact]
    public void UseFailedWarning_NeedsFiveOccurrencesLikeABotKey()
    {
        var ledger = new AnomalyLedger();
        var feed = new PlayerbotQaAnomalyFeed(ledger);

        for (int i = 0; i < 4; i++)
        {
            ledger.Report("gameobject", "use-failed", "gameobject entry 5 type 3", "use refused (Unsupported)", AnomalySeverity.Warning);
        }

        Assert.Empty(feed.Pull());

        ledger.Report("gameobject", "use-failed", "gameobject entry 5 type 3", "use refused (Unsupported)", AnomalySeverity.Warning);

        QaSignal signal = Assert.Single(feed.Pull());
        Assert.Equal("gameobject/use-failed", signal.FaultCode);
        Assert.Equal(5, signal.Occurrences);
    }

    [Fact]
    public void OtherWorldKeys_StayOutOfTheFeed_EvenAtErrorSeverity()
    {
        var ledger = new AnomalyLedger();
        var feed = new PlayerbotQaAnomalyFeed(ledger);

        ledger.Report("loot", "window-left-open", "loot source 1 (GameObject)", "released", AnomalySeverity.Error);

        Assert.Empty(feed.Pull());
    }

    [Fact]
    public void TheFourWorldKeys_AreExactlyTheOnesTheServerReports()
    {
        // Built from the constants the reporting code passes to the ledger, so a renamed code breaks this test instead of silently leaving the feed.
        string[] reported =
        [
            AnomalyLedger.KeyOf(ArcaneCore.Game.Loot.LootService.AnomalySubsystem, ArcaneCore.Game.Loot.LootService.GameObjectLootEmptyCode),
            AnomalyLedger.KeyOf("gameobject", ArcaneCore.Game.GameObjects.GameObjectMapSystem.UseFailedCode),
            AnomalyLedger.KeyOf("gameobject", ArcaneCore.World.Skills.GatheringSpells.OpenNoEffectCode),
            AnomalyLedger.KeyOf("spell", ArcaneCore.Game.Spells.SpellSystem.GameObjectEffectSkippedCode),
        ];
        Assert.Equal(reported.Order(StringComparer.Ordinal), PlayerbotQaAnomalyFeed.WorldKeys.Order(StringComparer.Ordinal));
    }
}
