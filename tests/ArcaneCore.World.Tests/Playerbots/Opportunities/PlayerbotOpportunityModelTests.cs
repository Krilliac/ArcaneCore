using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Opportunities;

/// <summary>The opportunity arithmetic (pure): the decision at the margin, the hard refusals, the ledger's backoff, the options' ranges.</summary>
public sealed class PlayerbotOpportunityModelTests
{
    private static RiskEnemy Guard(byte level, float dps, uint health = 60, uint rank = 0)
        => new(1000u + level, level, rank, health, dps, RiskJoin.Assist);

    private static OpportunityFacts Facts(float loot, byte botLevel, params RiskEnemy[] guards) => new()
    {
        Engagement = new PlayerbotEngagementFacts { BotLevel = botLevel, BotHealth = 200, BotMaxHealth = 200, BotDps = 10f, Enemies = guards },
        LootUnits = loot,
        TravelSeconds = 5f,
    };

    private static OpportunityVerdict Decide(OpportunityFacts facts, PlayerbotOpportunityOptions? options = null)
        => PlayerbotOpportunityModel.Decide(facts, options ?? new PlayerbotOpportunityOptions { PersonalityVariance = 0f }, new PlayerbotRiskOptions());

    [Fact]
    public void AnUnguardedFind_WorthMoreThanMinValue_IsEngaged_AndOneWorthLess_IsNot()
    {
        Assert.Equal(OpportunityDecision.Engage, Decide(Facts(2f, 10)).Decision);
        Assert.Equal("low-value", Decide(Facts(0.3f, 10)).Reason);
    }

    [Fact]
    public void RiskAppetite_ChangesTheDecision_AtTheMargin()
    {
        // Raise the guard's damage until the cautious bot (appetite 1) refuses; the bold one (appetite 2) still takes that same pack.
        var cautious = new PlayerbotOpportunityOptions { PersonalityVariance = 0f, MaxDeathProbability = 0.5f };
        var bold = new PlayerbotOpportunityOptions { PersonalityVariance = 0f, MaxDeathProbability = 0.5f, RiskAppetite = 2f };
        float margin = -1f;
        for (float dps = 1f; dps < 40f; dps += 0.25f)
        {
            OpportunityFacts facts = Facts(3f, 10, Guard(10, dps));
            if (Decide(facts, cautious).Decision == OpportunityDecision.Engage) continue;
            margin = dps;
            break;
        }

        Assert.True(margin > 1f, "the cautious bot refused even the weakest guard");
        OpportunityFacts atMargin = Facts(3f, 10, Guard(10, margin));
        Assert.Equal(OpportunityDecision.Refuse, Decide(atMargin, cautious).Decision);
        Assert.Equal(OpportunityDecision.Engage, Decide(atMargin, bold).Decision);
        // The same pack just under the margin is taken by both: the knob moves the line, not the whole behaviour.
        Assert.Equal(OpportunityDecision.Engage, Decide(Facts(3f, 10, Guard(10, margin - 0.25f)), cautious).Decision);
    }

    [Fact]
    public void TheDeathProbabilityCap_HoldsWhateverTheAppetite()
    {
        var reckless = new PlayerbotOpportunityOptions { PersonalityVariance = 0f, RiskAppetite = 4f, MaxDeathProbability = 0.05f };
        OpportunityVerdict verdict = default;
        for (float dps = 1f; dps < 60f; dps += 0.5f)
        {
            verdict = Decide(Facts(6f, 10, Guard(10, dps)), reckless);
            if (verdict.Reason == "death-risk") break;
        }

        Assert.Equal(OpportunityDecision.Refuse, verdict.Decision);
        Assert.Equal("death-risk", verdict.Reason);
        Assert.True(verdict.DeathProbability > 0.05f);
    }

    [Fact]
    public void AnEliteThreeLevelsAbove_AndAGuardFiveLevelsAbove_AreRefused_WhateverTheReward()
    {
        var bold = new PlayerbotOpportunityOptions { PersonalityVariance = 0f, RiskAppetite = 4f, MaxDeathProbability = 0.5f };
        Assert.Equal("elite-above", Decide(Facts(50f, 10, Guard(13, 0.5f, 30, rank: 1)), bold).Reason);
        Assert.Equal("over-level", Decide(Facts(50f, 10, Guard(15, 0.5f, 30)), bold).Reason);
        // The model weighs the first guard as the pull target; an elite behind a weak first guard is refused just the same.
        Assert.Equal("elite-above", Decide(Facts(50f, 10, Guard(8, 0.5f, 30), Guard(13, 0.5f, 30, rank: 1)), bold).Reason);
        Assert.Equal("over-level", Decide(Facts(50f, 10, Guard(8, 0.5f, 30), Guard(15, 0.5f, 30)), bold).Reason);
        Assert.Equal(OpportunityDecision.Engage, Decide(Facts(50f, 10, Guard(11, 0.5f, 30)), bold).Decision);
    }

    [Fact]
    public void APackLargerThanMaxGuards_IsRefused()
    {
        RiskEnemy[] pack = [Guard(8, 0.2f, 10), Guard(8, 0.2f, 10), Guard(8, 0.2f, 10)];
        Assert.Equal("pack-of-3", Decide(Facts(20f, 10, pack), new PlayerbotOpportunityOptions { PersonalityVariance = 0f, MaxGuards = 2 }).Reason);
        Assert.Equal(OpportunityDecision.Engage, Decide(Facts(20f, 10, pack), new PlayerbotOpportunityOptions { PersonalityVariance = 0f, MaxGuards = 3 }).Decision);
    }

    [Fact]
    public void ALowManaBot_WaitsForTheOpportunity_UnlessRestManaBelowPctIsOff_BotQa1480()
    {
        // A hybrid at 1% mana facing one easy guard: the risk is fine, but it has nothing to cast, so the default holds it.
        OpportunityFacts easy = Facts(3f, 10, Guard(10, 1f, 40));
        OpportunityFacts facts = easy with { Engagement = easy.Engagement with { BotManaPct = 1f, ManaDependence = 0.4f } };
        var options = new PlayerbotOpportunityOptions { PersonalityVariance = 0f };
        OpportunityVerdict waits = PlayerbotOpportunityModel.Decide(facts, options, new PlayerbotRiskOptions());
        Assert.Equal(OpportunityDecision.Wait, waits.Decision);
        Assert.Equal("low-mana", waits.Reason);
        Assert.Equal(OpportunityDecision.Engage, PlayerbotOpportunityModel.Decide(facts, options, new PlayerbotRiskOptions { RestManaBelowPct = 0f }).Decision);
    }

    [Fact]
    public void ARecoveringBot_Waits_AndALethalGuardIsRefused()
    {
        Assert.Equal(OpportunityDecision.Wait, Decide(Facts(5f, 10) with { Recovering = true }).Decision);
        RiskEnemy lethal = Guard(10, 1f) with { Lethal = true };
        Assert.Equal("lethal", Decide(Facts(50f, 10, lethal)).Reason);
    }

    [Fact]
    public void InterruptingAGoal_AsksMoreThanTheDefault()
    {
        // 1.0 net: above MinValue (0.75), below 1.5 x MinValue.
        OpportunityFacts facts = Facts(1.0f + (5f * PlayerbotOpportunityModel.KillUnitsPerSecond), 10);
        Assert.Equal(OpportunityDecision.Engage, Decide(facts).Decision);
        Assert.Equal("low-value", Decide(facts with { Interrupting = true }).Reason);
        Assert.Equal(OpportunityDecision.Engage, Decide(facts with { Interrupting = true, Committed = true }).Decision);
    }

    [Fact]
    public void LootUnits_FollowQuality_Vendor_AndUpgrade()
    {
        var green = new ArcaneCore.Kernel.Items.ItemTemplate { Entry = 1, Quality = 2, SellPrice = 0 };
        var grey = new ArcaneCore.Kernel.Items.ItemTemplate { Entry = 2, Quality = 0, SellPrice = 1 };
        var items = new Dictionary<uint, float> { [1] = 1f, [2] = 1f };
        float noUpgrade = PlayerbotOpportunityModel.LootUnits(items, 0, 10, id => id == 1 ? green : grey, _ => null);
        float upgrade = PlayerbotOpportunityModel.LootUnits(items, 0, 10, id => id == 1 ? green : grey, t => t.Entry == 1 ? 1f : null);
        Assert.Equal(PlayerbotOpportunityModel.QualityUnits(2) + (1f / PlayerbotOpportunityModel.KillCopper(10)), noUpgrade, 3);
        Assert.Equal(noUpgrade + PlayerbotOpportunityModel.UpgradeBase + PlayerbotOpportunityModel.UpgradeScale, upgrade, 3);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    public void ANodeVerdict_OnRiskOnly_NeverRefusesOnValue_ButStillRefusesWhatIsDangerous(int level)
    {
        byte bot = (byte)level;
        OpportunityFacts weak = Facts(0.05f, bot, Guard(bot, 1f, 40)); // a node worth almost nothing, one ordinary same-level guard
        Assert.Equal("low-value", Decide(weak).Reason); // as a find to travel for it is not worth the trip...
        OpportunityVerdict asNode = Decide(weak with { RiskOnly = true }); // ...but as a node the bot gathers anyway, only the guard matters
        Assert.Equal(OpportunityDecision.Engage, asNode.Decision);

        Assert.Equal("elite-above", Decide(Facts(0.05f, bot, Guard((byte)(bot + 8), 1f, 40, rank: 1)) with { RiskOnly = true }).Reason);
        Assert.Equal("over-level", Decide(Facts(0.05f, bot, Guard((byte)(bot + 6), 1f, 40)) with { RiskOnly = true }).Reason);
        Assert.Equal(OpportunityDecision.Refuse, Decide(Facts(0.05f, bot, Guard(bot, 80f, 400)) with { RiskOnly = true }).Decision); // it would kill the bot
        Assert.Equal("pack-of-5", Decide(Facts(0.05f, bot, Guard(bot, 1f, 40), Guard(bot, 1f, 40), Guard(bot, 1f, 40), Guard(bot, 1f, 40), Guard(bot, 1f, 40)) with { RiskOnly = true }).Reason);
    }

    [Fact]
    public void ALootGroup_ExpectsWhatTheGeneratorRolls_WhateverTheChancesAddUpTo()
    {
        static LootStoreRow Row(uint item, float chance) => new(1, item, chance, 1, 1, 1);
        static Dictionary<uint, float> Expect(params LootStoreRow[] rows)
            => PlayerbotOpportunityModel.ExpectedItems(new LootContent(rows.Select(r => (LootTableKind.GameObject, r)), []),
                LootTableKind.GameObject, 1);

        // Explicit chances that add up to more than 100: the roll runs out, the later rows get what is left (LootGenerator.PickFromGroup).
        Dictionary<uint, float> over = Expect(Row(10, 80f), Row(11, 80f));
        Assert.Equal(0.8f, over[10], 3);
        Assert.Equal(0.2f, over[11], 3);
        Assert.Equal(1f, over.Values.Sum(), 3); // one item per roll, never more

        // A row at 100 takes the whole roll that reaches it; nothing after it is ever picked, not even the equal-chance rows.
        Dictionary<uint, float> full = Expect(Row(10, 30f), Row(11, 100f), Row(12, 50f), Row(13, 0f));
        Assert.Equal(0.3f, full[10], 3);
        Assert.Equal(0.7f, full[11], 3);
        Assert.False(full.ContainsKey(12));
        Assert.False(full.ContainsKey(13));

        // The zero-chance rows share what the explicit chances leave.
        Dictionary<uint, float> shared = Expect(Row(10, 60f), Row(11, 0f), Row(12, 0f));
        Assert.Equal(0.6f, shared[10], 3);
        Assert.Equal(0.2f, shared[11], 3);
        Assert.Equal(0.2f, shared[12], 3);

        // A quest row takes its share of the roll but is worth nothing here.
        Dictionary<uint, float> quest = Expect(Row(10, 50f), Row(11, -30f), Row(12, 0f));
        Assert.Equal(0.5f, quest[10], 3);
        Assert.False(quest.ContainsKey(11));
        Assert.Equal(0.2f, quest[12], 3);
    }

    [Fact]
    public void ThePersonality_IsStablePerBot_AndWithinTheVariance()
    {
        float a = PlayerbotOpportunityModel.Personality(42, 0.15f);
        Assert.Equal(a, PlayerbotOpportunityModel.Personality(42, 0.15f));
        Assert.Equal(1f, PlayerbotOpportunityModel.Personality(42, 0f));
        for (ulong guid = 1; guid < 200; guid++)
            Assert.InRange(PlayerbotOpportunityModel.Personality(guid, 0.15f), 0.85f, 1.15f);
        Assert.NotEqual(PlayerbotOpportunityModel.Personality(1, 0.15f), PlayerbotOpportunityModel.Personality(2, 0.15f));
    }

    [Fact]
    public void TheLedger_BacksOff60s_Doubling_To300s_AndComesBackAfterTheRespawn()
    {
        var ledger = new PlayerbotOpportunityLedger();
        uint now = 1000;
        uint[] expected = [60_000, 120_000, 240_000, 300_000, 300_000];
        foreach (uint backoff in expected)
        {
            Assert.Equal(backoff, ledger.Defer(7, now));
            Assert.True(ledger.IsDeferred(7, now + backoff - 1));
            Assert.False(ledger.IsDeferred(7, now + backoff));
            now += backoff;
        }

        ledger.Complete(7, now, 600_000);
        Assert.True(ledger.IsDeferred(7, now + 599_999));
        Assert.False(ledger.IsDeferred(7, now + 600_000));
        Assert.Equal(60_000u, ledger.Defer(7, now + 600_000)); // a taken find forgets its failures
        ledger.Forget(7);
        Assert.False(ledger.IsDeferred(7, now));
        ledger.SetAside(8, now, 30_000);
        Assert.Equal(60_000u, ledger.Defer(8, now + 30_000)); // contention is no strike
    }

    [Fact]
    public void TheOptions_ValidateEveryRange_AndTheDefaultsAreValid()
    {
        new PlayerbotOpportunityOptions().Validate();
        Assert.Throws<InvalidOperationException>(() => new PlayerbotOpportunityOptions { ScanRadiusYards = 5f }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotOpportunityOptions { ScanRadiusYards = 200f }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotOpportunityOptions { RiskAppetite = 0f }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotOpportunityOptions { RiskAppetite = 5f }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotOpportunityOptions { MinValue = -1f }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotOpportunityOptions { MaxGuards = 13 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotOpportunityOptions { MaxDeathProbability = 0.9f }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotOpportunityOptions { PersonalityVariance = float.NaN }.Validate());
        var options = new PlayerbotOptions();
        options.Opportunities.MaxGuards = -1;
        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
