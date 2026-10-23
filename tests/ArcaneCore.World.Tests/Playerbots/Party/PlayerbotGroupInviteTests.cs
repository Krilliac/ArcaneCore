using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Party;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// Who may invite a managed bot (<see cref="PlayerbotPartyOptions.InvitePolicy"/> and the allowlist; mangoszero AcceptInvitationAction),
/// and how <c>World:Playerbots:Party</c> binds and validates.
/// </summary>
public sealed class PlayerbotGroupInviteTests
{
    public static TheoryData<PlayerbotInvitePolicy, bool, bool, bool, bool> Matrix() => new()
    {
        // policy, allowlisted, same guild, inviter on the BOT's friend list (the inviter's own list never counts), accepted
        { PlayerbotInvitePolicy.None, false, false, false, false },
        { PlayerbotInvitePolicy.None, false, true, true, false },
        { PlayerbotInvitePolicy.None, true, false, false, true },
        { PlayerbotInvitePolicy.GuildOrFriends, false, false, false, false },
        { PlayerbotInvitePolicy.GuildOrFriends, false, true, false, true },
        { PlayerbotInvitePolicy.GuildOrFriends, false, false, true, true },
        { PlayerbotInvitePolicy.GuildOrFriends, true, false, false, true },
        { PlayerbotInvitePolicy.Anyone, false, false, false, true },
        { PlayerbotInvitePolicy.Anyone, true, true, true, true },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void InvitePolicyMatrix(PlayerbotInvitePolicy policy, bool allowlisted, bool sameGuild, bool onBotsFriendList, bool accepted)
    {
        string[] allowlist = allowlisted ? ["Someoneelse", "Inviter"] : ["Someoneelse"];
        Assert.Equal(accepted, PlayerbotGroupInvites.Allows(policy, allowlist, "Inviter", sameGuild, onBotsFriendList));
    }

    [Theory]
    [InlineData(false, false, false, false, false, false, false)] // a stranger
    [InlineData(false, false, false, true, false, false, true)]   // known well enough
    [InlineData(false, false, false, false, true, false, true)]   // same quest or dungeon goal
    [InlineData(true, false, false, false, false, false, true)]   // guild mate
    [InlineData(false, true, false, false, false, false, true)]   // friend
    [InlineData(true, true, true, true, true, false, false)]      // a rival is always declined, even a guild mate on the same goal
    [InlineData(false, false, true, false, false, false, false)]  // a rival with nothing else
    public void TheAffinityPolicy_AcceptsTheKnownAndTheOnSameGoal_AndNeverARival(bool sameGuild, bool friend, bool rival, bool wellKnown, bool sharesGoal, bool allowlisted, bool accepted)
    {
        string[] allowlist = allowlisted ? ["Inviter"] : [];
        var standing = new PlayerbotGroupInvites.Standing(rival, wellKnown, sharesGoal);
        Assert.Equal(accepted, PlayerbotGroupInvites.Allows(PlayerbotInvitePolicy.Affinity, allowlist, "Inviter", sameGuild, friend, standing));
    }

    [Fact]
    public void TheAffinityPolicy_WithoutStanding_ReadsAStranger_AndTheAllowlistStaysAbsolute()
    {
        Assert.False(PlayerbotGroupInvites.Allows(PlayerbotInvitePolicy.Affinity, [], "Inviter", false, false));
        Assert.True(PlayerbotGroupInvites.Allows(PlayerbotInvitePolicy.Affinity, [], "Inviter", true, false));
        Assert.True(PlayerbotGroupInvites.Allows(PlayerbotInvitePolicy.Affinity, ["inviter"], "Inviter", false, false, new PlayerbotGroupInvites.Standing(true, false, false)));
    }

    [Fact]
    public void TheAllowlistIgnoresCase()
        => Assert.True(PlayerbotGroupInvites.Allows(PlayerbotInvitePolicy.None, ["inviter"], "INVITER", false, false));

    [Fact]
    public void ANamelessInviterIsDeclined_UnderEveryPolicy()
    {
        foreach (PlayerbotInvitePolicy policy in Enum.GetValues<PlayerbotInvitePolicy>())
            Assert.False(PlayerbotGroupInvites.Allows(policy, [""], "", true, true));
    }

    [Fact]
    public void TheInviterIsReadFromSmsgGroupInvite()
    {
        var writer = new PacketWriter();
        writer.WriteCString("Masterone");
        Assert.Equal("Masterone", PlayerbotGroupInvites.ReadInviter(writer.ToArray()));
        Assert.Null(PlayerbotGroupInvites.ReadInviter([]));
    }

    [Fact]
    public void Defaults_FollowVmangos_AndValidate()
    {
        var party = new PlayerbotPartyOptions();
        Assert.Equal(PlayerbotInvitePolicy.Affinity, party.InvitePolicy);
        Assert.Equal(10, party.AcceptInviteAffinity);
        Assert.Empty(party.Allowlist);
        Assert.True(party.TeleportToLeader);
        Assert.True(party.AutoRevive);
        Assert.Equal(PlayerbotLootRoll.Need, party.LootRoll);
        new PlayerbotOptions().Validate();
    }

    [Fact]
    public void ThePartySectionBinds()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PlayerbotOptions.SectionName + ":Party:InvitePolicy"] = "Anyone",
            [PlayerbotOptions.SectionName + ":Party:Allowlist:0"] = "Masterone",
            [PlayerbotOptions.SectionName + ":Party:Allowlist:1"] = "Mastertwo",
            [PlayerbotOptions.SectionName + ":Party:TeleportToLeader"] = "false",
            [PlayerbotOptions.SectionName + ":Party:AutoRevive"] = "false",
            [PlayerbotOptions.SectionName + ":Party:LootRoll"] = "Greed",
            [PlayerbotOptions.SectionName + ":Party:MasterTimeoutSeconds"] = "30",
        }).Build();

        PlayerbotOptions options = PlayerbotOptions.Bind(configuration);
        options.Validate();

        Assert.Equal(PlayerbotInvitePolicy.Anyone, options.Party.InvitePolicy);
        Assert.Equal(["Masterone", "Mastertwo"], options.Party.Allowlist);
        Assert.False(options.Party.TeleportToLeader);
        Assert.False(options.Party.AutoRevive);
        Assert.Equal(PlayerbotLootRoll.Greed, options.Party.LootRoll);
        Assert.Equal(30, options.Party.MasterTimeoutSeconds);
    }

    [Theory]
    [InlineData("MasterTimeoutSeconds", "0")]
    [InlineData("MasterTimeoutSeconds", "3601")]
    [InlineData("Allowlist:0", "Not a name")]
    [InlineData("Allowlist:0", "Waytoolongforaname")]
    public void InvalidPartySettingsFailValidation(string key, string value)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PlayerbotOptions.SectionName + ":Party:" + key] = value,
        }).Build();
        Assert.Throws<InvalidOperationException>(PlayerbotOptions.Bind(configuration).Validate);
    }

    [Fact]
    public void DuplicateAllowlistNamesFailValidation()
    {
        var options = new PlayerbotOptions { Party = { Allowlist = ["Masterone", "MASTERONE"] } };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void UndefinedEnumValuesFailValidation()
    {
        Assert.Throws<InvalidOperationException>(new PlayerbotOptions { Party = { InvitePolicy = (PlayerbotInvitePolicy)7 } }.Validate);
        Assert.Throws<InvalidOperationException>(new PlayerbotOptions { Party = { LootRoll = (PlayerbotLootRoll)7 } }.Validate);
    }
}
