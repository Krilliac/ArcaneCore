using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>The social memory: event deltas, clamps, cooldowns, decay, eviction, versions and the traits.</summary>
public sealed class PlayerbotSocialMemoryTests
{
    private const long Day = 86_400_000L;
    private static readonly Guid Bot = Guid.Parse("00000000-0000-0000-0000-0000000000c1");

    private sealed class Clock
    {
        public long Now = 1_760_000_000_000;
    }

    private static (PlayerbotSocialMemory Memory, PlayerbotSocialMemoryOptions Options, Clock Clock) Make()
    {
        var options = new PlayerbotSocialMemoryOptions();
        var clock = new Clock();
        var memory = new PlayerbotSocialMemory(options, () => clock.Now);
        memory.SetSelf(Bot, 1);
        return (memory, options, clock);
    }

    private static PlayerbotRelation Get(PlayerbotSocialMemory memory, ulong other)
    {
        Assert.True(memory.TryGet(Bot, other, out PlayerbotRelation relation));
        return relation;
    }

    [Theory]
    [InlineData("Grouped", 3)]
    [InlineData("GroupSucceeded", 5)]
    [InlineData("GroupWiped", 1)]
    [InlineData("Kicked", -10)]
    [InlineData("TradeCompleted", 3)]
    [InlineData("HelpedThem", 8)]
    [InlineData("HelpedBy", 8)]
    [InlineData("ChatStrike", -15)]
    [InlineData("KilledBy", -20)]
    [InlineData("KilledThem", -5)]
    [InlineData("ChatExchange", 1)]
    [InlineData("FriendlyEmote", 2)]
    [InlineData("RudeEmote", -5)]
    [InlineData("WatchedMeDie", -12)]
    public void EachEvent_AppliesItsDelta_AndOneFamiliarity(string eventName, int delta)
    {
        (PlayerbotSocialMemory memory, _, _) = Make();
        memory.Note(Bot, 7, 100, false, Enum.Parse<PlayerbotSocialEvent>(eventName));
        PlayerbotRelation r = Get(memory, 100);
        Assert.Equal(delta, r.Affinity);
        Assert.Equal(1, r.Familiarity);
        Assert.Equal(7, r.CharacterId);
        Assert.Equal(500, r.TrustPermille);
    }

    [Fact]
    public void GroupEvents_SetTheGroupedFlag_AndOtherFactionIsRecorded()
    {
        (PlayerbotSocialMemory memory, _, _) = Make();
        memory.Note(Bot, 7, 100, true, PlayerbotSocialEvent.Grouped);
        memory.Note(Bot, 7, 101, false, PlayerbotSocialEvent.KilledBy, otherFaction: true);
        Assert.True(Get(memory, 100).OtherIsBot);
        Assert.True(Get(memory, 100).Flags.HasFlag(PlayerbotRelationFlags.Grouped));
        Assert.True(Get(memory, 101).Flags.HasFlag(PlayerbotRelationFlags.OtherFaction));
        Assert.False(Get(memory, 101).Flags.HasFlag(PlayerbotRelationFlags.Grouped));
    }

    [Fact]
    public void Affinity_ClampsBothWays()
    {
        (PlayerbotSocialMemory memory, _, _) = Make();
        for (int i = 0; i < 30; i++) memory.Note(Bot, 7, 100, false, PlayerbotSocialEvent.HelpedBy);
        Assert.Equal(100, Get(memory, 100).Affinity);
        for (int i = 0; i < 30; i++) memory.Note(Bot, 7, 101, false, PlayerbotSocialEvent.KilledBy);
        Assert.Equal(-100, Get(memory, 101).Affinity);
    }

    [Fact]
    public void ChatExchange_RaisesFamiliarityAndAffinityOncePerCooldownWindow()
    {
        (PlayerbotSocialMemory memory, _, Clock clock) = Make();
        memory.Note(Bot, 7, 100, false, PlayerbotSocialEvent.ChatExchange);
        memory.Note(Bot, 7, 100, false, PlayerbotSocialEvent.ChatExchange);
        Assert.Equal(1, Get(memory, 100).Familiarity);
        Assert.Equal(1, Get(memory, 100).Affinity);
        clock.Now += 10 * 60_000;
        memory.Note(Bot, 7, 100, false, PlayerbotSocialEvent.ChatExchange);
        Assert.Equal(2, Get(memory, 100).Familiarity);
    }

    [Fact]
    public void Emotes_ShareTheChatCooldown_AndARudeOneAlwaysCounts()
    {
        (PlayerbotSocialMemory memory, _, Clock clock) = Make();
        memory.Note(Bot, 7, 100, false, PlayerbotSocialEvent.ChatExchange);
        memory.Note(Bot, 7, 100, false, PlayerbotSocialEvent.FriendlyEmote);
        PlayerbotRelation r = Get(memory, 100);
        Assert.Equal(1, r.Familiarity);
        Assert.Equal(1, r.Affinity);
        memory.Note(Bot, 7, 100, false, PlayerbotSocialEvent.RudeEmote);
        Assert.Equal(-4, Get(memory, 100).Affinity);
        Assert.Equal(1, Get(memory, 100).Familiarity);
        clock.Now += 10 * 60_000;
        memory.Note(Bot, 7, 100, false, PlayerbotSocialEvent.FriendlyEmote);
        Assert.Equal(2, Get(memory, 100).Familiarity);
        Assert.Equal(-2, Get(memory, 100).Affinity);
    }

    [Fact]
    public void Familiarity_HalvesPerHalfLife_ReadLazily_AndFloors()
    {
        (PlayerbotSocialMemory memory, PlayerbotSocialMemoryOptions options, Clock clock) = Make();
        for (int i = 0; i < 9; i++) memory.Note(Bot, 7, 100, false, PlayerbotSocialEvent.Grouped);
        Assert.Equal(9, Get(memory, 100).Familiarity);
        clock.Now += options.FamiliarityHalfLifeDays * Day;
        Assert.Equal(4, Get(memory, 100).Familiarity); // floor(4.5)
        Assert.Equal(9, Assert.Single(memory.Snapshot(Bot)).Familiarity); // stored as of the last seen time: persisted values never double-decay
        clock.Now += options.FamiliarityHalfLifeDays * Day;
        Assert.Equal(2, Get(memory, 100).Familiarity);
        memory.Note(Bot, 7, 100, false, PlayerbotSocialEvent.Grouped); // a write folds the decay in
        Assert.Equal(3, Get(memory, 100).Familiarity);
        Assert.Equal(3, Assert.Single(memory.Snapshot(Bot)).Familiarity);
    }

    [Fact]
    public void Rival_FollowsTheThreshold_BothWays()
    {
        (PlayerbotSocialMemory memory, _, _) = Make();
        memory.Note(Bot, 7, 100, false, PlayerbotSocialEvent.KilledBy);
        memory.Note(Bot, 7, 100, false, PlayerbotSocialEvent.KilledBy);
        Assert.Equal(-40, Get(memory, 100).Affinity);
        Assert.False(Get(memory, 100).Flags.HasFlag(PlayerbotRelationFlags.Rival));
        memory.Note(Bot, 7, 100, false, PlayerbotSocialEvent.Kicked);
        Assert.Equal(-50, Get(memory, 100).Affinity);
        Assert.True(Get(memory, 100).Flags.HasFlag(PlayerbotRelationFlags.Rival));
        memory.Note(Bot, 7, 100, false, PlayerbotSocialEvent.HelpedBy);
        Assert.Equal(-42, Get(memory, 100).Affinity);
        Assert.False(Get(memory, 100).Flags.HasFlag(PlayerbotRelationFlags.Rival));
    }

    [Fact]
    public void SelfPair_DisabledMemory_AndEmptyBot_AreNoOps()
    {
        (PlayerbotSocialMemory memory, PlayerbotSocialMemoryOptions options, _) = Make();
        memory.Note(Bot, 7, 1, false, PlayerbotSocialEvent.Grouped); // the bot itself
        Assert.False(memory.TryGet(Bot, 1, out _));
        memory.Note(Guid.Empty, 7, 100, false, PlayerbotSocialEvent.Grouped);
        options.Enabled = false;
        memory.Note(Bot, 7, 100, false, PlayerbotSocialEvent.Grouped);
        Assert.False(memory.TryGet(Bot, 100, out _));
        Assert.Equal(0, memory.Version(Bot));
    }

    [Fact]
    public void Version_BumpsOnlyOnChange()
    {
        (PlayerbotSocialMemory memory, _, _) = Make();
        Assert.Equal(0, memory.Version(Bot));
        memory.Note(Bot, 7, 100, false, PlayerbotSocialEvent.Grouped);
        long v1 = memory.Version(Bot);
        Assert.NotEqual(0, v1);
        _ = memory.Top(Bot, 5);
        _ = memory.Snapshot(Bot);
        Assert.True(memory.TryGet(Bot, 100, out _));
        Assert.Equal(v1, memory.Version(Bot));
        memory.SetTopic(Bot, 100, "greeting", "Hello.");
        long v2 = memory.Version(Bot);
        Assert.NotEqual(v1, v2);
        memory.SetTopic(Bot, 100, "greeting", "Hello.");
        memory.SetTopic(Bot, 999, "greeting", "Hello."); // no such relation
        memory.SetFlags(Bot, 100, PlayerbotRelationFlags.None);
        Assert.Equal(v2, memory.Version(Bot));
        memory.SetFlags(Bot, 100, PlayerbotRelationFlags.PrefersTank);
        Assert.NotEqual(v2, memory.Version(Bot));
    }

    [Fact]
    public void SetTopic_Truncates_AndSetFlags_KeepsTheManagedFlags()
    {
        (PlayerbotSocialMemory memory, _, _) = Make();
        memory.Note(Bot, 7, 100, false, PlayerbotSocialEvent.Grouped);
        memory.SetTopic(Bot, 100, new string('t', 80), new string('s', 400));
        PlayerbotRelation r = Get(memory, 100);
        Assert.Equal(32, r.LastTopic.Length);
        Assert.Equal(160, r.Summary.Length);
        memory.SetFlags(Bot, 100, PlayerbotRelationFlags.PrefersHealer | PlayerbotRelationFlags.Rival);
        r = Get(memory, 100);
        Assert.True(r.Flags.HasFlag(PlayerbotRelationFlags.PrefersHealer));
        Assert.True(r.Flags.HasFlag(PlayerbotRelationFlags.Grouped));
        Assert.False(r.Flags.HasFlag(PlayerbotRelationFlags.Rival));
    }

    [Fact]
    public void Eviction_DropsLeastFamiliarThenOldest_AndSparesStrongBonds()
    {
        (PlayerbotSocialMemory memory, PlayerbotSocialMemoryOptions options, Clock clock) = Make();
        options.MaxRelationsPerBot = 16;
        // 16 entries of familiarity 2, the first of them a grudge (affinity -100) and the second a bond.
        for (ulong other = 100; other < 116; other++)
        {
            memory.Note(Bot, 7, other, false, PlayerbotSocialEvent.Grouped);
            memory.Note(Bot, 7, other, false, PlayerbotSocialEvent.Grouped);
            clock.Now += 1000;
        }

        for (int i = 0; i < 6; i++) memory.Note(Bot, 7, 100, false, PlayerbotSocialEvent.KilledBy);
        for (int i = 0; i < 10; i++) memory.Note(Bot, 7, 101, false, PlayerbotSocialEvent.HelpedBy);
        clock.Now += 1000;
        Assert.Equal(-100, Get(memory, 100).Affinity);
        Assert.True(Get(memory, 101).Affinity >= 50);
        // 100 and 101 are now the most familiar anyway; make them the least by decaying, then add a 17th: they must survive.
        clock.Now += 200 * Day;
        memory.Note(Bot, 7, 500, false, PlayerbotSocialEvent.Grouped);
        Assert.Equal(16, memory.Snapshot(Bot).Count);
        Assert.True(memory.TryGet(Bot, 100, out _));
        Assert.True(memory.TryGet(Bot, 101, out _));
        Assert.True(memory.TryGet(Bot, 500, out _));
        // Among equals the oldest last-seen went: 102 was written first.
        Assert.False(memory.TryGet(Bot, 102, out _));
        Assert.True(memory.TryGet(Bot, 103, out _));
    }

    [Fact]
    public void Eviction_WhenAllAreStrong_RemovesTheLeastFamiliarAnyway()
    {
        (PlayerbotSocialMemory memory, PlayerbotSocialMemoryOptions options, Clock clock) = Make();
        options.MaxRelationsPerBot = 16;
        for (ulong other = 100; other < 116; other++)
        {
            for (int i = 0; i < 7; i++) memory.Note(Bot, 7, other, false, PlayerbotSocialEvent.HelpedBy);
            clock.Now += 1000;
        }

        memory.Note(Bot, 7, 500, false, PlayerbotSocialEvent.HelpedBy);
        Assert.Equal(16, memory.Snapshot(Bot).Count);
        Assert.True(memory.TryGet(Bot, 500, out _));
    }

    [Fact]
    public void LoadSnapshotTopForget_RoundTrip()
    {
        (PlayerbotSocialMemory memory, _, Clock clock) = Make();
        for (int i = 0; i < 3; i++) memory.Note(Bot, 7, 100, false, PlayerbotSocialEvent.Grouped);
        memory.Note(Bot, 7, 101, true, PlayerbotSocialEvent.Grouped);
        memory.SetTopic(Bot, 100, "greeting", "Hello there.");
        PlayerbotRelation[] saved = [.. memory.Snapshot(Bot)];

        var other = new PlayerbotSocialMemory(new PlayerbotSocialMemoryOptions(), () => clock.Now);
        other.Load(saved);
        Assert.Equal(0, other.Version(Bot)); // a loaded bot is not saved straight back
        Assert.Equal(saved, other.Snapshot(Bot));
        IReadOnlyList<PlayerbotRelation> top = other.Top(Bot, 10);
        Assert.Equal([100UL, 101UL], top.Select(r => r.OtherGuid));
        Assert.Single(other.Top(Bot, 1));
        Assert.Equal(saved.Length, other.SnapshotAll().Length);

        other.Forget(Bot);
        Assert.Empty(other.Snapshot(Bot));
    }

    [Fact]
    public void Traits_AreStable_InRange_AndDistinctPerTrait()
    {
        PlayerbotTraits a = PlayerbotTraits.For(0x1234);
        Assert.Equal(a, PlayerbotTraits.For(0x1234));
        float[] values = [a.Sociability, a.Chattiness, a.Generosity, a.Curiosity, a.Helpfulness];
        Assert.All(values, v => Assert.InRange(v, 0f, 1f));
        Assert.Equal(5, values.Distinct().Count());
        Assert.NotEqual(a, PlayerbotTraits.For(0x1235));
        Assert.Equal(0, PlayerbotTraits.Band(0.1f));
        Assert.Equal(1, PlayerbotTraits.Band(0.5f));
        Assert.Equal(2, PlayerbotTraits.Band(0.9f));
    }

    [Fact]
    public void Options_ValidateTheirRanges()
    {
        new PlayerbotSocialMemoryOptions().Validate();
        Assert.Throws<InvalidOperationException>(() => new PlayerbotSocialMemoryOptions { MaxRelationsPerBot = 15 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotSocialMemoryOptions { AffinityPerStrike = 1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotSocialMemoryOptions { BystanderRadiusYards = 4 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotSocialMemoryOptions { CallForHelpBaseChance = 1.5f }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PlayerbotSocialMemoryOptions { CallForHelpSociabilityWeight = float.NaN }.Validate());
    }

    [Theory]
    [InlineData(101u, "Friendly")] // wave
    [InlineData(97u, "Friendly")] // thank
    [InlineData(77u, "Rude")] // rude
    [InlineData(89u, "Rude")] // spit
    [InlineData(143u, "Rude")] // violin
    [InlineData(303u, "Help")]
    [InlineData(34u, "Friendly")] // dance
    [InlineData(85u, "Neutral")] // sigh
    public void EmoteMoods_Classify(uint emote, string mood) => Assert.Equal(Enum.Parse<PlayerbotEmoteMood>(mood), PlayerbotEmoteMoods.Classify(emote));
}
