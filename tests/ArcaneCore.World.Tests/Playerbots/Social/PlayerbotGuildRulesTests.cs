using ArcaneCore.Game.Guilds;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The rules of bot guilds that need no world: how a founding is chosen, how many bots it needs, how many invitations a guild may
/// still send, who is accepted, who is promoted, and the bounds and defaults of <c>World:Playerbots:Social</c> (docs/areas/playbots-social.md).
/// </summary>
public sealed class PlayerbotGuildRulesTests
{
    private static PlayerbotGuildMemberFacts Member(string name, byte rank, byte level, bool leader = false)
        => new((uint)(name.GetHashCode() & 0xffff), name, rank, level, leader);

    // --- caps ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 0, 25, 24)]
    [InlineData(20, 3, 25, 2)]
    [InlineData(20, 5, 25, 0)]
    [InlineData(25, 0, 25, 0)]
    [InlineData(30, 4, 25, 0)] // a guild over the target (target lowered later) invites nobody; nothing is ever negative
    [InlineData(2, 0, 2, 0)]
    public void InviteRoom_CountsPendingInvitationsAgainstTheTargetSize(int members, int pending, int target, int room)
        => Assert.Equal(room, PlayerbotGuildMembership.InviteRoom(members, pending, target));

    [Theory]
    [InlineData(9, 10)]
    [InlineData(2, 3)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)] // the server clamps MinPetitionSigns to 0..9 (vmangos World.cpp:666)
    [InlineData(15, 10)]
    public void ACharterFounding_NeedsTheFounderAndOneBotForEachSignatureTheServerWants(int minPetitionSigns, int bots)
        => Assert.Equal(bots, PlayerbotGuildMembership.CharterCandidatesNeeded(new GuildOptions { MinPetitionSigns = minPetitionSigns }));

    [Theory]
    [InlineData(PlayerbotGuildCreation.Auto, true, PlayerbotGuildCreation.ClientCreate)]
    [InlineData(PlayerbotGuildCreation.Auto, false, PlayerbotGuildCreation.Charter)]
    [InlineData(PlayerbotGuildCreation.Charter, true, PlayerbotGuildCreation.Charter)]
    [InlineData(PlayerbotGuildCreation.Charter, false, PlayerbotGuildCreation.Charter)]
    [InlineData(PlayerbotGuildCreation.ClientCreate, true, PlayerbotGuildCreation.ClientCreate)]
    [InlineData(PlayerbotGuildCreation.ClientCreate, false, PlayerbotGuildCreation.ClientCreate)] // the server then refuses it; the choice is not overridden
    public void Creation_AutoFollowsWhatTheServerHonours(PlayerbotGuildCreation configured, bool allowClientCreate, PlayerbotGuildCreation resolved)
        => Assert.Equal(resolved, PlayerbotGuildMembership.ResolveCreation(configured, new GuildOptions { AllowClientGuildCreate = allowClientCreate }));

    // --- who is accepted ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(PlayerbotInvitePolicy.Anyone, false, false, true)]
    [InlineData(PlayerbotInvitePolicy.GuildOrFriends, false, false, false)]
    [InlineData(PlayerbotInvitePolicy.GuildOrFriends, true, false, true)] // on the bot's own friend list
    [InlineData(PlayerbotInvitePolicy.None, false, false, false)]
    [InlineData(PlayerbotInvitePolicy.None, true, false, false)]
    [InlineData(PlayerbotInvitePolicy.None, false, true, true)] // the allowlist is always accepted
    [InlineData(PlayerbotInvitePolicy.GuildOrFriends, false, true, true)]
    public void ARealPlayersInvitation_IsAcceptedByTheInvitePolicy_AndTheAllowlistAlways(PlayerbotInvitePolicy policy, bool friend, bool allowlisted, bool accepted)
    {
        string[] allowlist = allowlisted ? ["Testplayer"] : ["Someoneelse"];
        Assert.Equal(accepted, PlayerbotGuildMembership.AcceptsRealPlayer(policy, allowlist, "testplayer", friend));
    }

    [Fact]
    public void AnEmptyInviterName_IsNeverAccepted()
        => Assert.False(PlayerbotGuildMembership.AcceptsRealPlayer(PlayerbotInvitePolicy.Anyone, ["Testplayer"], string.Empty, onBotsFriendList: true));

    // --- ranks --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(1, 1)]
    [InlineData(10, 1)]
    [InlineData(11, 2)]
    [InlineData(25, 3)]
    public void OfficersWanted_IsOneForEveryTenMembersRoundedUp(int members, int officers)
        => Assert.Equal(officers, PlayerbotGuildMembership.OfficersWanted(members));

    [Fact]
    public void ARenamedRankList_IsLeftAlone()
    {
        PlayerbotGuildMemberFacts[] members = [Member("Master", 0, 60, leader: true), Member("Alpha", 4, 30)];
        Assert.Null(PlayerbotGuildMembership.NextPromotion(members, rankCount: 6));
        Assert.Null(PlayerbotGuildMembership.NextPromotion(members, rankCount: 4));
        Assert.NotNull(PlayerbotGuildMembership.NextPromotion(members, Guild.MinRanks));
    }

    [Fact]
    public void OfficersComeFirst_TheHighestLevelMemberBelowOfficerWithAMembersLevelClimbs()
    {
        PlayerbotGuildMemberFacts[] members =
        [
            Member("Master", 0, 60, leader: true), Member("Low", 4, 9), Member("Mid", 3, 25), Member("High", 4, 31), Member("Vet", 2, 31),
        ];

        // Five members want one officer; level 9 is below a Member's level, the others are ordered by level, then rank, then name.
        Assert.Equal("Vet", PlayerbotGuildMembership.NextPromotion(members, Guild.MinRanks)!.Value.Name);

        PlayerbotGuildMemberFacts[] sameLevel = [Member("Master", 0, 60, leader: true), Member("Zed", 4, 31), Member("Amy", 4, 31)];
        Assert.Equal("Amy", PlayerbotGuildMembership.NextPromotion(sameLevel, Guild.MinRanks)!.Value.Name);
    }

    [Fact]
    public void NobodyIsPromotedPastOfficer_AndTheLeaderIsNeverMoved()
    {
        // An officer already stands (one wanted for five members), so the second rule applies: Initiates and Members only.
        PlayerbotGuildMemberFacts[] members =
        [
            Member("Master", 0, 60, leader: true), Member("Officer", 1, 60), Member("Vet", 2, 60), Member("Fresh", 4, 1), Member("Newbie", 4, 9),
        ];
        Assert.Null(PlayerbotGuildMembership.NextPromotion(members, Guild.MinRanks));

        // Only a leader and an officer: nothing below them, nothing to do, and the leader (rank 0) is never a candidate even at a high level.
        Assert.Null(PlayerbotGuildMembership.NextPromotion([Member("Master", 0, 60, leader: true), Member("Officer", 1, 60)], Guild.MinRanks));
        Assert.Null(PlayerbotGuildMembership.NextPromotion([Member("Master", 0, 60, leader: true)], Guild.MinRanks));
        Assert.Null(PlayerbotGuildMembership.NextPromotion([], Guild.MinRanks));
    }

    [Fact]
    public void InitiatesBecomeMembersAtLevelTen_AndMembersVeteransAtForty()
    {
        PlayerbotGuildMemberFacts[] base5 = [Member("Master", 0, 60, leader: true), Member("Officer", 1, 60)];

        Assert.Equal("Ten", PlayerbotGuildMembership.NextPromotion([.. base5, Member("Nine", 4, 9), Member("Ten", 4, 10)], Guild.MinRanks)!.Value.Name);
        Assert.Null(PlayerbotGuildMembership.NextPromotion([.. base5, Member("Nine", 4, 9)], Guild.MinRanks));
        Assert.Null(PlayerbotGuildMembership.NextPromotion([.. base5, Member("Thirtynine", 3, 39)], Guild.MinRanks));
        Assert.Equal("Forty", PlayerbotGuildMembership.NextPromotion([.. base5, Member("Thirtynine", 3, 39), Member("Forty", 3, 40)], Guild.MinRanks)!.Value.Name);

        // Several are due: the first by name goes first, one at a time.
        Assert.Equal("Abel", PlayerbotGuildMembership.NextPromotion([.. base5, Member("Cain", 4, 12), Member("Abel", 3, 50)], Guild.MinRanks)!.Value.Name);
    }

    // --- options ------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaults_AreTheDocumentedOnes()
    {
        var options = new PlayerbotSocialOptions();
        options.Validate();
        Assert.True(options.Guilds.Enabled);
        Assert.Equal(4, options.Guilds.MaxBotGuilds);
        Assert.Equal(25, options.Guilds.TargetSize);
        Assert.Equal(PlayerbotInvitePolicy.GuildOrFriends, options.Guilds.InvitePolicy);
        Assert.Equal(PlayerbotGuildCreation.Auto, options.Guilds.Creation);
        Assert.True(options.GuildChat.Enabled);
        Assert.Equal(4, options.GuildChat.MessagesPerHour);
        Assert.True(options.Emotes.Enabled); // idle bots greet real players nearby, within the documented gaps
    }

    [Fact]
    public void TheTimings_AreTheDocumentedOnes()
    {
        var timings = new PlayerbotSocialTimings();
        Assert.Equal(
            new long[] { 1000L, 60_000L, 1000L, 300_000L, 5000L, 60_000L, 600_000L, 5000L, 60_000L, 120_000L, 600_000L, 300_000L, 600_000L, 1_800_000L },
            new[]
            {
                (long)timings.PassIntervalMs, timings.RegistryRefreshMs, timings.RegistryMissGapMs, timings.FoundingSpacingMs, timings.RecruitSpacingMs,
                timings.PendingInviteTimeoutMs, timings.CharterStallMs, timings.OfferSpacingMs, timings.PromotionSpacingMs, timings.SpeakerGapMs,
                timings.BgWinSpacingMs, timings.EmoteBotGapMs, timings.EmotePlayerGapMs, timings.EmotePairGapMs,
            });
        Assert.Equal(12, timings.EmotesPerMinute);
        Assert.True(timings.IdleChatter);
    }

    [Theory]
    [InlineData(-1, 25, 4, true)]
    [InlineData(65, 25, 4, true)]
    [InlineData(0, 25, 4, false)]
    [InlineData(64, 25, 4, false)]
    [InlineData(4, 1, 4, true)]
    [InlineData(4, 501, 4, true)]
    [InlineData(4, 2, 4, false)]
    [InlineData(4, 500, 4, false)]
    [InlineData(4, 25, -1, true)]
    [InlineData(4, 25, 61, true)]
    [InlineData(4, 25, 0, false)]
    [InlineData(4, 25, 60, false)]
    public void TheBounds_AreEnforced(int maxBotGuilds, int targetSize, int messagesPerHour, bool refused)
    {
        var options = new PlayerbotSocialOptions
        {
            Guilds = { MaxBotGuilds = maxBotGuilds, TargetSize = targetSize },
            GuildChat = { MessagesPerHour = messagesPerHour },
        };
        if (refused) Assert.Throws<InvalidOperationException>(options.Validate);
        else options.Validate();
    }

    [Fact]
    public void AnUndefinedPolicyOrCreation_IsRefused()
    {
        var policy = new PlayerbotSocialOptions { Guilds = { InvitePolicy = (PlayerbotInvitePolicy)99 } };
        Assert.Throws<InvalidOperationException>(policy.Validate);
        var creation = new PlayerbotSocialOptions { Guilds = { Creation = (PlayerbotGuildCreation)99 } };
        Assert.Throws<InvalidOperationException>(creation.Validate);
    }
}
