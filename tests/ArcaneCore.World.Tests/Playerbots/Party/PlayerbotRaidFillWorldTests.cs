using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// Bots in a real player's raid: <c>.bot party fill</c> past five once the group is a raid, <c>.bot raid N</c> (form, convert, clamp,
/// refuse, summon), and a bot invited into a raid by hand. Each test waits for a specific reply or world state.
/// </summary>
public sealed class PlayerbotRaidFillWorldTests
{
    /// <summary>
    /// The live report: once the GM's party is converted to a raid, <c>.bot party fill</c> still answered "Your party is already full."
    /// at five members. In a raid it fills one more subgroup's worth (here the four bots left), up to forty; the bots placed in the
    /// second subgroup follow the GM exactly as those in the first.
    /// </summary>
    [Fact]
    public async Task PartyFill_AfterTheGroupBecameARaid_InvitesPastFive()
    {
        await using FleetWorld world = await FleetWorld.StartAsync(o => o.MaxBots = 8);
        await using WorldTestClient gm = await world.AddGmAsync("RFILLGM", "Rfillgm");
        string[] names = ["Rfilla", "Rfillb", "Rfillc", "Rfilld", "Rfille", "Rfillf", "Rfillg", "Rfillh"];
        for (int i = 0; i < names.Length; i++) await world.AddBotAsync(names[i], 6 + (i * 3));

        List<string> first = await FleetWorld.SayAsync(gm, ".bot party fill");
        Assert.StartsWith("Playerbot party fill: invited 4", first[0]);
        Assert.Equal(5, await MemberCountAsync(world, "Rfillgm"));

        await gm.SendAsync(WorldOpcode.CmsgGroupRaidConvert, []);
        await world.Host.WaitForWorldAsync(() => GroupOf(world, "Rfillgm") is { IsRaid: true }, "the party becomes a raid");

        List<string> second = await FleetWorld.SayAsync(gm, ".bot party fill");
        Assert.StartsWith("Playerbot party fill: invited 4", second[0]);
        Assert.Equal(9, await MemberCountAsync(world, "Rfillgm"));

        string[] secondSubgroup = await world.Host.OnWorldAsync(() =>
            GroupOf(world, "Rfillgm")!.Members.Where(m => m.SubGroup > 0).Select(m => m.Name).ToArray());
        Assert.NotEmpty(secondSubgroup);
        foreach (string name in secondSubgroup)
        {
            Guid id = world.Ids[name];
            await world.Host.WaitForWorldAsync(() => world.Bots.IsPartyDriven(id), name + " is driven by the party AI");
            Assert.Equal("Rfillgm", (await world.Bots.InspectAsync(name))!.Master);
        }
    }

    /// <summary>
    /// With no group, <c>.bot raid N</c> forms one, makes it a raid and summons the bots from 300 yards away (the party AI's own
    /// catch-up teleport is off, so only the summon can bring them).
    /// </summary>
    [Fact]
    public async Task Raid_WithNoGroup_FormsARaid_AndSummonsTheBots()
    {
        await using FleetWorld world = await FleetWorld.StartAsync(o =>
        {
            o.MaxBots = 4;
            o.Party.TeleportToLeader = false;
        });
        await using WorldTestClient gm = await world.AddGmAsync("RGATHGM", "Rgathgm");
        string[] names = ["Rgatha", "Rgathb", "Rgathc"];
        for (int i = 0; i < names.Length; i++) await world.AddBotAsync(names[i], 300 + (i * 3));

        List<string> reply = await FleetWorld.SayAsync(gm, ".bot raid 3");
        Assert.StartsWith("Playerbot raid: invited 3 of 3, raid formed; raid 4/40", reply[0]);
        Assert.Contains("Rgatha, Rgathb, Rgathc", reply[0]);
        Assert.True(await world.Host.OnWorldAsync(() => GroupOf(world, "Rgathgm") is { IsRaid: true, MemberCount: 4 }));

        await world.Host.WaitForWorldAsync(() =>
        {
            Player leader = world.Host.World.FindOnlinePlayer("Rgathgm")!;
            return names.All(n => world.Host.World.FindOnlinePlayer(n) is { IsInWorld: true } bot && FleetWorld.Xy(bot, leader) < 30f);
        }, "the bots are summoned to the GM");
    }

    /// <summary>
    /// A started bot's status line says it runs when the world thread reads it at once, before any tick has published it. A command
    /// handler picks bots from this read (<c>.bot raid</c> and <c>.bot party fill</c> take only running ones); it used to return the
    /// last tick's copy, still holding the stopped row of the bot's creation, so <see cref="Raid_WithNoGroup_FormsARaid_AndSummonsTheBots"/>
    /// and <see cref="Raid_PastFortyMembers_IsClampedToTheFreeSlots"/> answered "invited 2 of 3" or "no eligible online bots" in CI.
    /// </summary>
    [Fact]
    public async Task Snapshot_OnTheWorldThread_ShowsABotTheMomentItsStartReturns()
    {
        await using FleetWorld world = await FleetWorld.StartAsync(o => o.MaxBots = 12);
        await world.AddBotAsync("Rsnapa", 6);
        var missed = new List<string>();
        for (int i = 0; i < 11; i++)
        {
            string name = "Rsnapb" + (char)('a' + i);
            PlayerbotOperationResult created = await world.Bots.CreateAsync(name, 1, 1);
            Assert.True(created.Success, created.Code);
            Guid id = created.BotId!.Value;
            Assert.True((await world.Bots.StartAsync(id.ToString())).Success);
            bool running = await world.Host.OnWorldAsync(() =>
                world.Bots.Snapshot().Any(s => s.BotId == id && s.State == ManagedPlayerbotState.Running));
            if (!running) missed.Add(name);
        }

        Assert.Empty(missed);
    }

    /// <summary>A party the caller leads is converted to a raid and filled.</summary>
    [Fact]
    public async Task Raid_FromAPartyTheCallerLeads_ConvertsIt()
    {
        await using FleetWorld world = await FleetWorld.StartAsync(o => o.MaxBots = 4);
        await using WorldTestClient gm = await world.AddGmAsync("RCONVGM", "Rconvgm");
        string[] names = ["Rconva", "Rconvb", "Rconvc"];
        for (int i = 0; i < names.Length; i++) await world.AddBotAsync(names[i], 6 + (i * 3));

        Assert.StartsWith("Playerbot ok: invited", (await FleetWorld.SayAsync(gm, ".bot party add Rconva"))[0]);
        Assert.True(await world.Host.OnWorldAsync(() => GroupOf(world, "Rconvgm") is { IsRaid: false, MemberCount: 2 }));

        List<string> reply = await FleetWorld.SayAsync(gm, ".bot raid 2");
        Assert.StartsWith("Playerbot raid: invited 2 of 2, party converted to a raid; raid 4/40", reply[0]);
        Assert.True(await world.Host.OnWorldAsync(() => GroupOf(world, "Rconvgm") is { IsRaid: true, MemberCount: 4 }));
    }

    /// <summary>A party member who does not lead cannot have it converted: the reply says why and the group stays a party.</summary>
    [Fact]
    public async Task Raid_FromAPartyTheCallerDoesNotLead_IsRefused()
    {
        await using FleetWorld world = await FleetWorld.StartAsync(o => o.MaxBots = 2);
        await using WorldTestClient gm = await world.AddGmAsync("RLEADGM", "Rleadgm");
        await using WorldTestClient lead = await world.Host.EnterWorldAsync("RLEADER", "Rleader");
        await world.AddBotAsync("Rleada", 6);
        await world.Host.OnWorldAsync(() =>
        {
            Groups(world).Invite(world.Host.World.FindOnlinePlayer("Rleader")!, "Rleadgm");
            Groups(world).Accept(world.Host.World.FindOnlinePlayer("Rleadgm")!);
            return GroupOf(world, "Rleadgm") is { IsRaid: false, MemberCount: 2 };
        });

        List<string> reply = await FleetWorld.SayAsync(gm, ".bot raid 1");
        Assert.Equal("Playerbot raid: only the party leader (Rleader) can convert the party to a raid.", reply[0]);
        Assert.True(await world.Host.OnWorldAsync(() => GroupOf(world, "Rleadgm") is { IsRaid: false, MemberCount: 2 }));
    }

    /// <summary>A count past the free raid slots is clamped (and said so); the bots there are invited.</summary>
    [Fact]
    public async Task Raid_PastFortyMembers_IsClampedToTheFreeSlots()
    {
        await using FleetWorld world = await FleetWorld.StartAsync(o => o.MaxBots = 2);
        await using WorldTestClient gm = await world.AddGmAsync("RCLAMPGM", "Rclampgm");
        await world.AddBotAsync("Rclampa", 6);
        await world.AddBotAsync("Rclampb", 9);

        List<string> reply = await FleetWorld.SayAsync(gm, ".bot raid 50", 2);
        Assert.Equal("Playerbot raid: 50 requested, clamped to the 39 free raid slots.", reply[0]);
        Assert.StartsWith("Playerbot raid: invited 2 of 39, raid formed; raid 3/40", reply[1]);
    }

    /// <summary>A bot invited into a raid by hand (the client's own invite, as a right-click does) accepts and joins the raid.</summary>
    [Fact]
    public async Task AManualInviteIntoARaid_IsAccepted()
    {
        await using FleetWorld world = await FleetWorld.StartAsync(o =>
        {
            o.MaxBots = 2;
            o.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone;
        });
        await using WorldTestClient gm = await world.AddGmAsync("RMANGM", "Rmangm");
        await world.AddBotAsync("Rmana", 6);
        await world.AddBotAsync("Rmanb", 9);

        Assert.StartsWith("Playerbot raid: invited 1 of 1, raid formed", (await FleetWorld.SayAsync(gm, ".bot raid 1"))[0]);
        Assert.True(await world.Host.OnWorldAsync(() => GroupOf(world, "Rmangm") is { IsRaid: true, MemberCount: 2 }));

        await gm.SendAsync(WorldOpcode.CmsgGroupInvite, PartyTestHost.CString("Rmanb"));
        await world.Host.WaitForWorldAsync(() => GroupOf(world, "Rmangm") is { IsRaid: true, MemberCount: 3 }, "the bot joins the raid");
        await world.Host.WaitForWorldAsync(() => world.Bots.IsPartyDriven(world.Ids["Rmanb"]), "the party AI drives the bot");
    }

    internal static Group? GroupOf(FleetWorld world, string leader)
        => world.Host.World.FindOnlinePlayer(leader) is { } p ? Groups(world).GetGroup(p.Guid) : null;

    internal static Task<int> MemberCountAsync(FleetWorld world, string leader)
        => world.Host.OnWorldAsync(() => GroupOf(world, leader)?.MemberCount ?? 0);

    internal static GroupManager Groups(FleetWorld world) => world.Host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups;
}
