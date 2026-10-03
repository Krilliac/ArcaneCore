using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Kernel.Social;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>
/// Guild rule parity with vmangos. Tests marked RED-FIRST failed before the fix; tests marked PIN
/// characterise behavior that was already right and must stay so.
/// </summary>
public sealed class GuildParityTests
{
    private static Guild Found(SocialFixture f, Player leader, GuildOptions? options = null)
    {
        if (options is not null)
        {
            f.Context.Guilds.Options = options;
        }

        f.Context.Guilds.Load([]);
        Assert.Equal(GuildAdminResult.Ok, f.Context.Guilds.Create(leader.Guid.Low, "Arcane", out Guild? guild));
        return guild!;
    }

    // --- offline add (Guild.cpp:197-278) ------------------------------------------------------------

    [Fact] // RED-FIRST
    public void AddingAnOfflineCharacter_StoresItsLevelZoneAndALogoutTimeOfNow()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Found(f, a);
        f.AddOffline(3, level: 40, zoneId: 14);
        long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.Equal(GuildAdminResult.Ok, f.Context.Guilds.AdminInvite(3, "Arcane"));

        // Guild.cpp:230-256 reads level/zone from the player cache; :250 sets LogoutTime = now.
        GuildMemberData stored = f.Persistence.Guilds.Values.Single().Members.Single(m => m.CharacterId == 3);
        Assert.Equal((byte)40, stored.Level);
        Assert.Equal(14u, stored.ZoneId);
        Assert.InRange(stored.LogoutTime, before, before + 2);
    }

    [Fact] // PIN: an online joiner takes the live level and zone
    public void AddingAnOnlinePlayer_TakesItsLiveLevelAndZone()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Found(f, a);

        f.Context.Guilds.Invite(a, b.Name);
        f.Context.Guilds.Accept(b);

        GuildMemberData stored = f.Persistence.Guilds.Values.Single().Members.Single(m => m.CharacterId == 2);
        Assert.Equal(b.Level, stored.Level);
        Assert.Equal(b.ZoneId, stored.ZoneId);
    }

    // --- load (Guild.cpp:396-398, :458-460) ---------------------------------------------------------

    [Fact] // RED-FIRST
    public void Load_ForcesTheGuildMasterRankToHaveEveryRight()
    {
        using var f = new SocialFixture();
        f.AddOffline(1);
        var data = new GuildData(1, "Arcane", 1, "m", "i", 0, -1, -1, -1, -1, -1,
            [new GuildRankData(0, "Guild Master", 0x41), new GuildRankData(1, "Officer", 0x41), new GuildRankData(2, "Veteran", 0x41),
             new GuildRankData(3, "Member", 0x41), new GuildRankData(4, "Initiate", 0x41)],
            [new GuildMemberData(1, 0, string.Empty, string.Empty, 1, 0, 0)]);

        f.Context.Guilds.Load([data]);

        Guild guild = f.Context.Guilds.Get(1)!;
        Assert.Equal(GuildRights.All, guild.Ranks[0].Rights);
        Assert.Equal(0x41u, guild.Ranks[1].Rights); // only the first rank is forced
    }

    // --- rank deletion (Guild.cpp:696-723) ----------------------------------------------------------

    [Fact] // RED-FIRST
    public void AnOutOfRangeRank_IsUnknownAndHasNoRights()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Guild guild = Found(f, a);

        Assert.Equal("<unknown>", guild.RankName(9));
        Assert.Equal(GuildRights.Empty, guild.RankRights(9));
        Assert.False(guild.HasRight(9, GuildRights.GuildChatSpeak));
    }

    [Fact] // RED-FIRST
    public void DeletingARank_ByDefault_LeavesItsMembersOnTheDeadRankLikeVmangos()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Guild guild = Found(f, a);
        f.Context.Guilds.AddRank(a, "Recruit");
        f.Context.Guilds.Invite(a, b.Name);
        f.Context.Guilds.Accept(b);
        Assert.Equal(5, guild.Find(2)!.Rank);

        f.Context.Guilds.DeleteRank(a);

        Assert.Equal(5, guild.Ranks.Count);
        Assert.Equal(5, guild.Find(2)!.Rank);
        Assert.Equal("<unknown>", guild.RankName(guild.Find(2)!.Rank));
        Assert.False(guild.HasRight(guild.Find(2)!.Rank, GuildRights.GuildChatSpeak));
    }

    [Fact] // PIN: the stored dead id is clamped at the next load (Guild.cpp:458-460)
    public void ADeadRankId_IsClampedToTheLowestRankAtTheNextLoad()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Guild guild = Found(f, a);
        f.Context.Guilds.AddRank(a, "Recruit");
        f.Context.Guilds.Invite(a, b.Name);
        f.Context.Guilds.Accept(b);
        f.Context.Guilds.DeleteRank(a);
        GuildData saved = f.Persistence.Guilds[guild.Id];

        using var again = new SocialFixture();
        again.AddOffline(1);
        again.AddOffline(2);
        again.Context.Guilds.Load([saved]);

        Assert.Equal(4, again.Context.Guilds.Get(guild.Id)!.Find(2)!.Rank);
    }

    [Fact] // PIN of the documented deviation switch
    public void DeletingARank_WithDeleteRankMovesMembers_MovesThemToTheNewLowestRank()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        Guild guild = Found(f, a, new GuildOptions { DeleteRankMovesMembers = true });
        f.Context.Guilds.AddRank(a, "Recruit");
        f.Context.Guilds.Invite(a, b.Name);
        f.Context.Guilds.Accept(b);

        f.Context.Guilds.DeleteRank(a);

        Assert.Equal(4, guild.Find(2)!.Rank);
    }

    // --- oversized text (GuildHandler.cpp:58-62,470-474,518-522,554-558,580-584,600-604) -------------

    private static readonly string Astral128 = string.Concat(Enumerable.Repeat("\U0001D11E", 128)); // 128 code points, 256 UTF-16 units

    public static TheoryData<string> OversizedActions => new() { "motd", "info", "publicnote", "officernote", "rankname", "addrank" };

    private static void Send(SocialFixture f, Player leader, string action, string text)
    {
        switch (action)
        {
            case "motd": f.Context.Guilds.SetMotd(leader, text); break;
            case "info": f.Context.Guilds.SetInfo(leader, text); break;
            case "publicnote": f.Context.Guilds.SetPublicNote(leader, leader.Name, text); break;
            case "officernote": f.Context.Guilds.SetOfficerNote(leader, leader.Name, text); break;
            case "rankname": f.Context.Guilds.SetRank(leader, 3, GuildRights.GuildChatListen, text); break;
            case "addrank": f.Context.Guilds.AddRank(leader, text); break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    private static int Limit(string action) => action switch
    {
        "motd" => Guild.MaxMotdLength,
        "info" => Guild.MaxInfoLength,
        "publicnote" or "officernote" => Guild.MaxNoteLength,
        _ => Guild.MaxRankNameLength,
    };

    [Theory] // RED-FIRST
    [MemberData(nameof(OversizedActions))]
    public void OversizedText_KicksTheSessionByDefault_AndChangesNothing(string action)
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Guild guild = Found(f, a);
        string before = guild.Motd + "|" + guild.Info + "|" + guild.Ranks.Count + "|" + guild.Find(1)!.PublicNote;

        Send(f, a, action, new string('x', Limit(action) + 1));

        Assert.True(f.Session(a).Kicked);
        Assert.Equal(before, guild.Motd + "|" + guild.Info + "|" + guild.Ranks.Count + "|" + guild.Find(1)!.PublicNote);
        Assert.All(guild.Ranks, r => Assert.NotEqual(new string('x', Limit(action) + 1), r.Name));
    }

    [Theory] // RED-FIRST
    [MemberData(nameof(OversizedActions))]
    public void OversizedText_WithKickDisabled_IsDroppedWithoutDisconnecting(string action)
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Found(f, a, new GuildOptions { KickOnOversizedText = false });

        Send(f, a, action, new string('x', Limit(action) + 1));

        Assert.False(f.Session(a).Kicked);
    }

    [Theory] // PIN: text at the limit is accepted and nobody is kicked
    [MemberData(nameof(OversizedActions))]
    public void TextAtTheLimit_IsAccepted(string action)
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Found(f, a);

        Send(f, a, action, new string('x', Limit(action)));

        Assert.False(f.Session(a).Kicked);
    }

    [Fact] // RED-FIRST: lengths count code points (utf8length), not UTF-16 units
    public void TheMotdLimit_CountsCodePoints()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        Guild guild = Found(f, a);

        f.Context.Guilds.SetMotd(a, Astral128);

        Assert.Equal(Astral128, guild.Motd);
        Assert.False(f.Session(a).Kicked);
    }
}
