using ArcaneCore.Game;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots.BotSocial;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>The recorder every hook goes through: managed-bot lookup, both-way pairs, the safe wrapper.</summary>
public sealed class PlayerbotSocialRecorderTests
{
    private static readonly Guid BotA = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid BotB = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly ObjectGuid GuidA = ObjectGuid.Player(11);
    private static readonly ObjectGuid GuidB = ObjectGuid.Player(12);
    private static readonly ObjectGuid Human = ObjectGuid.Player(900);

    private static PlayerbotSocialRecorder Make()
    {
        var recorder = new PlayerbotSocialRecorder(new PlayerbotSocialMemory(new PlayerbotSocialMemoryOptions(), () => 1_760_000_000_000), NullLogger.Instance);
        recorder.Track(BotA, 11);
        recorder.Track(BotB, 12);
        return recorder;
    }

    [Fact]
    public void Group_PairsRecordBothWays_AndAPlayerHasNoMemory()
    {
        PlayerbotSocialRecorder recorder = Make();
        recorder.NoteBoth(GuidA, GuidB, PlayerbotSocialEvent.Grouped);
        recorder.NoteBoth(GuidA, Human, PlayerbotSocialEvent.Grouped);

        Assert.True(recorder.Memory.TryGet(BotA, GuidB.Value, out PlayerbotRelation ab));
        Assert.True(ab.OtherIsBot);
        Assert.True(recorder.Memory.TryGet(BotB, GuidA.Value, out _));
        Assert.True(recorder.Memory.TryGet(BotA, Human.Value, out PlayerbotRelation ah));
        Assert.False(ah.OtherIsBot);
        Assert.Equal(2, recorder.Memory.Snapshot(BotA).Count);
        Assert.Empty(recorder.Memory.Snapshot(Guid.NewGuid()));
    }

    [Fact]
    public void Strike_LowersAffinity_TradeRaisesIt_RudeAndFriendlyEmotesMoveIt()
    {
        PlayerbotSocialRecorder recorder = Make();
        recorder.NoteById(BotA, GuidA, Human, PlayerbotSocialEvent.ChatStrike);
        recorder.Memory.TryGet(BotA, Human.Value, out PlayerbotRelation r);
        Assert.Equal(-15, r.Affinity);
        recorder.NoteById(BotA, GuidA, Human, PlayerbotSocialEvent.TradeCompleted);
        recorder.Memory.TryGet(BotA, Human.Value, out r);
        Assert.Equal(-12, r.Affinity);
        recorder.NoteById(BotA, GuidA, Human, PlayerbotSocialEvent.RudeEmote, otherFaction: true);
        recorder.Memory.TryGet(BotA, Human.Value, out r);
        Assert.Equal(-17, r.Affinity);
        Assert.True(r.Flags.HasFlag(PlayerbotRelationFlags.OtherFaction));
        recorder.NoteById(BotA, GuidA, Human, PlayerbotSocialEvent.FriendlyEmote); // inside the shared emote window: no change
        recorder.Memory.TryGet(BotA, Human.Value, out r);
        Assert.Equal(-17, r.Affinity);
    }

    [Fact]
    public void KickedBot_RemembersTheKicker_ButAPlayerKickedRemembersNothing()
    {
        PlayerbotSocialRecorder recorder = Make();
        recorder.Note(GuidA, Human, PlayerbotSocialEvent.Kicked);
        recorder.Note(Human, GuidA, PlayerbotSocialEvent.Kicked);
        recorder.Memory.TryGet(BotA, Human.Value, out PlayerbotRelation r);
        Assert.Equal(-10, r.Affinity);
        Assert.False(recorder.Memory.TryGet(BotA, GuidA.Value, out _));
    }

    [Fact]
    public void NonPlayerOthers_AndUntrackedBots_AreIgnored()
    {
        PlayerbotSocialRecorder recorder = Make();
        recorder.Note(GuidA, ObjectGuid.WithEntry(HighGuid.Unit, 100, 1), PlayerbotSocialEvent.Grouped);
        Assert.Empty(recorder.Memory.Snapshot(BotA));
        recorder.Untrack(11);
        recorder.Note(GuidA, Human, PlayerbotSocialEvent.Grouped);
        Assert.Empty(recorder.Memory.Snapshot(BotA));
        Assert.False(recorder.IsManaged(GuidA));
        Assert.True(recorder.IsManaged(GuidB));
    }

    [Fact]
    public void Guarded_SwallowsAThrowingStep()
    {
        PlayerbotSocialRecorder recorder = Make();
        recorder.Guarded(() => throw new InvalidOperationException("boom"));
    }
}
