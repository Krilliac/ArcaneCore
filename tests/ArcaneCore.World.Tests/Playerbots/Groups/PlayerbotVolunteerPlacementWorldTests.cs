using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Volunteers placed in a group: ready or not, invited, and placed with the cap full. Split from the former <c>PlayerbotVolunteerWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotVolunteerWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotVolunteerPlacementWorldTests : PlayerbotVolunteerWorldTestBase
{
    /// <summary>(B) The plain case, no cap pressure: Ready, then the invitation of the waiting bot, which leads.</summary>
    [Fact]
    public async Task AVolunteerBesideALoneWaitingBot_IsReady_AndIsInvitedByThatBot()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options => Quiet(options));
        await world.World.PrepareCharacterAsync("VOLHAPPY", "Volhappy", AccountSecurity.Player);
        var bot = await WaitingBotAsync(world, "Volbotone", Warrior, 1, []);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLHAPPY", "Volhappy", AccountSecurity.Player);

        PlayerbotVolunteerResult result = await VolunteerAsync(world, bot.Id, "Volhappy");

        Assert.Equal(PlayerbotVolunteerOutcome.Ready, result.Outcome);
        await AssertInvitedByAsync(world, player, bot.Name);
        Assert.Equal(bot.Id, world.Coordinator.Groups.Single().LeaderBotId);
    }

    /// <summary>
    /// (A) Live bug: bot groups hold every slot of MaxGroups, so Match returned before it looked at the volunteer and "inv in a sec"
    /// was never followed by an invitation. A group with a real player is not a bot quest group: it must not wait for a free slot.
    /// </summary>
    [Fact]
    public async Task WhenBotGroupsFillTheCap_AVolunteerIsStillReady_AndInvited()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options => Quiet(options, maxGroups: 1));
        await world.World.PrepareCharacterAsync("VOLCAP", "Volcap", AccountSecurity.Player);
        await FillTheCapAsync(world, "Volcapfill", 2, Warrior, []);
        var lone = await WaitingBotAsync(world, "Volcaplone", Warrior, 1, []);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLCAP", "Volcap", AccountSecurity.Player);
        Assert.Single(world.Coordinator.Groups); // the cap is used up by the bots' own group

        PlayerbotVolunteerResult result = await VolunteerAsync(world, lone.Id, "Volcap");

        Assert.Equal(PlayerbotVolunteerOutcome.Ready, result.Outcome);
        await AssertInvitedByAsync(world, player, lone.Name);
        Assert.Contains(world.Coordinator.Groups, g => g.LeaderBotId == lone.Id);
    }

    /// <summary>(D) Surplus: waiting damage bots already make a plan of their own; the volunteer must not be locked out of it.</summary>
    [Fact]
    public async Task WhenAsManyDamageBotsWaitAsTheGroupHolds_TheVolunteerIsStillPlacedInTheGroup()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options => Quiet(options, maxGroups: 1));
        await world.World.PrepareCharacterAsync("VOLSURP", "Volsurp", AccountSecurity.Player);
        await FillTheCapAsync(world, "Volsurpfill", 3, Mage, [Fireball]); // the cap is used up, so the next three keep waiting
        var first = await WaitingBotAsync(world, "Volsurpone", Mage, 1, [Fireball]);
        await WaitingBotAsync(world, "Volsurptwo", Mage, 1, [Fireball]);
        await WaitingBotAsync(world, "Volsurpthree", Mage, 1, [Fireball]);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLSURP", "Volsurp", AccountSecurity.Player);

        PlayerbotVolunteerResult result = await VolunteerAsync(world, first.Id, "Volsurp");

        Assert.Equal(PlayerbotVolunteerOutcome.Ready, result.Outcome);
        Assert.True(await world.RunUntilPacedAsync(() => PlayerInvited(world), 30_000), "the volunteer was not placed in a group" + Environment.NewLine + await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single(g => g.Members.Any(m => m.Real));
        Assert.Equal(3, group.Goal.Size);
        Assert.Single(group.Members, m => m.Real);
        byte[] invite = await player.ReadUntilAsync(WorldOpcode.SmsgGroupInvite, TimeSpan.FromSeconds(30));
        Assert.Equal(group.Members.Single(m => m.BotId == group.LeaderBotId).Name, new PacketReader(invite).ReadCString());
    }

    /// <summary>(C) A player the coordinator cannot place gets no promise: not Ready, and no invitation ever comes.</summary>
    [Theory]
    [InlineData(Unplaceable.TooFar)]
    [InlineData(Unplaceable.OtherMap)]
    [InlineData(Unplaceable.OutOfLevelRange)]
    [InlineData(Unplaceable.Dead)]
    public async Task AVolunteerTheCoordinatorCannotPlace_IsNotReady_AndGetsNoInvitation(Unplaceable why)
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options => Quiet(options));
        await world.World.PrepareCharacterAsync("VOLNOPE", "Volnope", AccountSecurity.Player);
        var bot = await WaitingBotAsync(world, "Volnopebot", Warrior, why == Unplaceable.OutOfLevelRange ? (byte)10 : (byte)1, []);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLNOPE", "Volnope", AccountSecurity.Player);
        await PlaceAsync(world, "Volnope", why);

        PlayerbotVolunteerResult result = await VolunteerAsync(world, bot.Id, "Volnope");

        // Headcount is not short (one bot plus the player make the group of two): the honest answer is that the player cannot be placed.
        Assert.Equal(PlayerbotVolunteerOutcome.CannotPlace, result.Outcome);
        Assert.False(world.Coordinator.IsVolunteer(Online(world, "Volnope").Guid, (PlayerbotGroupGoalKind.Quest, OgreEntry)), "an unplaceable volunteer was kept");
        await world.RunAsync(30_000);
        Assert.False(PlayerInvited(world), "an unplaceable volunteer was invited");
        Assert.Null(await world.OnWorldAsync(() => world.GroupManager.GetInvite(Online(world, "Volnope").Guid)));
        Assert.DoesNotContain(world.Coordinator.Groups, g => g.Members.Any(m => m.Real));
    }
}
