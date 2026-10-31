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
/// The bot's ready whisper, a warrior volunteer above the bot, and a noted volunteer. Split from the former <c>PlayerbotVolunteerWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotVolunteerWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotVolunteerReadyWhisperWorldTests : PlayerbotVolunteerWorldTestBase
{
    /// <summary>(E) Control and the live bug: "sure, inv in a sec" goes only to a player the bot can really group with.</summary>
    [Fact]
    public async Task TheBotSaysReadyToAPlayerItCanGroup_AndTheInvitationFollows()
    {
        await using GroupTestWorld world = await StartVoiceWorldAsync();
        await world.World.PrepareCharacterAsync("VOLVOICE", "Volvoice", AccountSecurity.Player);
        var bot = await WaitingBotAsync(world, "Volvoicebot", Warrior, 1, []);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLVOICE", "Volvoice", AccountSecurity.Player);

        (string? said, string? inviter) = await AskForInviteAsync(world, player, bot, expectInvite: true);

        Assert.NotNull(said);
        Assert.Contains(said, ReadyLines);
        Assert.Equal(bot.Name, inviter); // the whisper and the invitation arrive in either order
    }

    [Fact]
    public async Task TheBotDoesNotSayReady_ToAPlayerItCannotPlace()
    {
        await using GroupTestWorld world = await StartVoiceWorldAsync();
        await world.World.PrepareCharacterAsync("VOLMUTE", "Volmute", AccountSecurity.Player);
        var bot = await WaitingBotAsync(world, "Volmutebot", Warrior, 1, []);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLMUTE", "Volmute", AccountSecurity.Player);
        await PlaceAsync(world, "Volmute", Unplaceable.TooFar);

        (string? said, _) = await AskForInviteAsync(world, player, bot, expectInvite: false);

        Assert.NotNull(said); // the voice path works: the bot answers, and honestly
        Assert.Contains(said, CantLines);
        Assert.DoesNotContain(said, ReadyLines);
        Assert.False(PlayerInvited(world));
    }

    // --- the roles, the cap on the Match path, expiry ---------------------------------------------------------------------------------

    /// <summary>With the default tank quota a higher-level warrior volunteer would win the tank slot and lead: a bot tanks and leads instead.</summary>
    [Fact]
    public async Task AWarriorVolunteerAboveTheBotWarrior_IsReady_AndTheBotTanksAndLeads()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options => Quiet(options, minTank: 1));
        await world.World.PrepareCharacterAsync("VOLTANK", "Voltank", AccountSecurity.Player);
        var warrior = await WaitingBotAsync(world, "Voltankbot", Warrior, 1, []);
        await WaitingBotAsync(world, "Voltankmage", Mage, 1, [Fireball]);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLTANK", "Voltank", AccountSecurity.Player);
        await world.OnWorldAsync(() =>
        {
            Online(world, "Voltank").Level = 3;
            return true;
        });

        PlayerbotVolunteerResult result = await VolunteerAsync(world, warrior.Id, "Voltank");

        Assert.Equal(PlayerbotVolunteerOutcome.Ready, result.Outcome);
        await AssertInvitedByAsync(world, player, warrior.Name);
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single(g => g.Members.Any(m => m.Real));
        Assert.Equal(warrior.Id, group.LeaderBotId);
        Assert.Equal(PlayerbotGroupRole.Tank, group.Members.Single(m => m.BotId == warrior.Id).Role);
    }

    /// <summary>The volunteer waits (Noted, a true count), the bots then become enough while the cap is full: the Match path forms the group.</summary>
    [Fact]
    public async Task ANotedVolunteer_IsPlacedByMatch_WhileBotGroupsFillTheCap()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options => Quiet(options, maxGroups: 1));
        await world.World.PrepareCharacterAsync("VOLMP", "Volmp", AccountSecurity.Player);
        await FillTheCapAsync(world, "Volmpfill", 3, Mage, [Fireball]);
        var first = await WaitingBotAsync(world, "Volmpone", Mage, 1, [Fireball]);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLMP", "Volmp", AccountSecurity.Player);

        PlayerbotVolunteerResult result = await VolunteerAsync(world, first.Id, "Volmp");

        Assert.Equal(PlayerbotVolunteerOutcome.Noted, result.Outcome);
        Assert.Equal(1, result.Missing); // three are asked for: one bot and the player are two
        Assert.Single(world.Coordinator.Groups);
        var second = await world.AddBotAsync("Volmptwo", Human, Mage, 1, [Fireball]); // joins the volunteer at once: it is never seen waiting
        Assert.True(await world.RunUntilPacedAsync(() => PlayerInvited(world), 30_000), "the volunteer was not placed by Match" + Environment.NewLine + await world.TraceAsync());
        Assert.Equal(2, world.Coordinator.Groups.Count); // beyond MaxGroups = 1: a group with a real player is not held back by the cap
        Assert.Contains(world.Coordinator.Groups, g => g.Members.Any(m => m.Real) && g.Members.Any(m => m.BotId == first.Id) && g.Members.Any(m => m.BotId == second.Id));
    }
}
