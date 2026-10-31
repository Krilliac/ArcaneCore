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
/// Expired volunteers, roles when the player tanks, and instance goals without a waiting anchor. Split from the former <c>PlayerbotVolunteerWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotVolunteerWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotVolunteerExpiryAndAnchorWorldTests : PlayerbotVolunteerWorldTestBase
{
    /// <summary>A volunteer that ran out unplaced hears one honest whisper from the bot.</summary>
    [Fact]
    public async Task AnExpiredVolunteer_HearsTheBotSayItCouldNotGetAGroupTogether()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Quiet(options);
            options.Chat.Enabled = true;
            options.Chat.PerPlayerCooldownSeconds = 0;
            options.Chat.Voice.LookingForGroup = false;
        });
        await world.World.PrepareCharacterAsync("VOLEXP", "Volexp", AccountSecurity.Player);
        var bot = await WaitingBotAsync(world, "Volexpbot", Warrior, 1, []);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLEXP", "Volexp", AccountSecurity.Player);
        Assert.True(await world.RunUntilAsync(10_000, () => world.Bots.Chat!.Responder is not null), await world.TraceAsync());
        Assert.Equal(PlayerbotVolunteerOutcome.Noted, (await VolunteerAsync(world, bot.Id, "Volexp")).Outcome);
        ulong botGuid = (await world.OnWorldAsync(() => world.Player(bot.Id).Guid)).Value;

        await world.RunAsync(PlayerbotGroupCoordinator.VolunteerLifeMs + 15_000);

        string? said = null;
        for (int i = 0; i < 6 && said is null; i++) // other whispers of the bot, if any, are not the one asked about
        {
            Task<ChatMessage> reply = PartyTestHost.ReadWhisperFromAsync(player, botGuid);
            await world.RunUntilPacedAsync(() => reply.IsCompleted, 20_000);
            if (!reply.IsCompleted) break;
            string text = (await reply).Text;
            if (ExpiredLines.Contains(text)) said = text;
        }

        Assert.NotNull(said);
    }

    /// <summary>Only a volunteer who is still free is told: one with an invitation pending or in a group of their own hears nothing.</summary>
    [Theory]
    [InlineData(Engaged.Ungrouped)]
    [InlineData(Engaged.InvitePending)]
    [InlineData(Engaged.Grouped)]
    public async Task AnExpiringVolunteer_IsQueuedForTheWhisper_OnlyWhileFree(Engaged engaged)
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Quiet(options);
            options.Chat.Voice.Enabled = false; // the voice would take the queue
        });
        await world.World.PrepareCharacterAsync("VOLQ", "Volq", AccountSecurity.Player);
        var bot = await WaitingBotAsync(world, "Volqbot", Warrior, 1, []);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLQ", "Volq", AccountSecurity.Player);
        Assert.Equal(PlayerbotVolunteerOutcome.Noted, (await VolunteerAsync(world, bot.Id, "Volq")).Outcome);
        await world.RunAsync(PlayerbotGroupCoordinator.VolunteerLifeMs - 20_000); // an invitation lapses on its own: engage the player just before the end
        if (engaged != Engaged.Ungrouped)
        {
            await world.OnWorldAsync(() =>
            {
                world.GroupManager.Invite(world.Player(bot.Id), "Volq");
                if (engaged == Engaged.Grouped) world.GroupManager.Accept(Online(world, "Volq"));
                return true;
            });
        }

        await world.RunAsync(40_000);

        var queued = await world.OnWorldAsync(() => world.Coordinator.TakeExpiredVolunteers());
        if (engaged == Engaged.Ungrouped) Assert.Equal(bot.Id, Assert.Single(queued).BotId);
        else Assert.Empty(queued);
    }

    // --- review cycle 2 ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A volunteer placed by Form is not invited until the forming ticks, so the group manager does not know them yet: whispering a second
    /// waiting bot in that window must not book them into a second group.
    /// </summary>
    [Fact]
    public async Task AVolunteerAlreadyPlacedInAFormingGroup_IsNotBookedIntoASecondOne()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], options => Quiet(options, maxGroups: 1));
        await world.World.PrepareCharacterAsync("VOLDB", "Voldb", AccountSecurity.Player);
        await FillTheCapAsync(world, "Voldbfill", 2, Warrior, []);
        var a = await WaitingBotAsync(world, "Voldba", Warrior, 1, []);
        var b = await WaitingBotAsync(world, "Voldbb", Warrior, 1, []);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLDB", "Voldb", AccountSecurity.Player);

        (PlayerbotVolunteerResult first, PlayerbotVolunteerResult second) = await world.OnWorldAsync(() =>
        {
            Player real = Online(world, "Voldb");
            return (world.Coordinator.Volunteer(a.Id, real), world.Coordinator.Volunteer(b.Id, real)); // back to back: no tick between
        });

        Assert.Equal(PlayerbotVolunteerOutcome.Ready, first.Outcome);
        Assert.Equal(PlayerbotVolunteerOutcome.Grouped, second.Outcome);
        Assert.Equal(2, world.Coordinator.Groups.Count); // the cap group and the one with the player
        Assert.Single(world.Coordinator.Groups, g => g.Members.Any(m => m.Real));
        Assert.Contains(world.Coordinator.WaitingBots, w => w.BotId == b.Id);
    }

    /// <summary>The player is a tank class and no bot tanks: a bot leads, and a healer bot is preferred to a damage one.</summary>
    [Fact]
    public async Task WhenThePlayerTanks_AHealerBotLeads_NotADamageBot()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Quiet(options, minTank: 1);
            options.Groups.MinHealer = 1;
        });
        await world.World.PrepareCharacterAsync("VOLHEAL", "Volheal", AccountSecurity.Player);
        var mage = await WaitingBotAsync(world, "Volhealmage", Mage, 1, [Fireball]); // waits longest
        var priest = await WaitingBotAsync(world, "Volhealprie", Priest, 1, [LesserHeal]);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLHEAL", "Volheal", AccountSecurity.Player);

        PlayerbotVolunteerResult result = await VolunteerAsync(world, mage.Id, "Volheal");

        Assert.Equal(PlayerbotVolunteerOutcome.Ready, result.Outcome);
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single(g => g.Members.Any(m => m.Real));
        Assert.Equal(PlayerbotGroupRole.Tank, group.Members.Single(m => m.Real).Role);
        Assert.Equal(PlayerbotGroupRole.Healer, group.Members.Single(m => m.BotId == priest.Id).Role);
        Assert.Equal(priest.Id, group.LeaderBotId);
    }

    /// <summary>A full cap lets only a goal with a live volunteer through: another team's waiting goal is passed over without building candidates.</summary>
    [Fact]
    public async Task WhenTheCapIsFull_AGoalWithoutALiveVolunteer_StaysUnformedAndUnbuilt()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options => Quiet(options, maxGroups: 1));
        await world.World.PrepareCharacterAsync("VOLCS", "Volcs", AccountSecurity.Player);
        await FillTheCapAsync(world, "Volcsfill", 3, Mage, [Fireball]);
        var first = await WaitingBotAsync(world, "Volcsone", Mage, 1, [Fireball]);
        var horde = await world.AddBotAsync("Volcshorde", Orc, Warrior, 1, []);
        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.WaitingBots.Any(w => w.BotId == horde.Id)), await world.TraceAsync());
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLCS", "Volcs", AccountSecurity.Player);
        Assert.Equal(PlayerbotVolunteerOutcome.Noted, (await VolunteerAsync(world, first.Id, "Volcs")).Outcome);
        long before = await world.OnWorldAsync(() => world.Coordinator.CapSkippedGoals);

        await world.RunAsync(10_000);

        Assert.True(await world.OnWorldAsync(() => world.Coordinator.CapSkippedGoals) > before, "the other team's goal was not passed over");
        Assert.Single(world.Coordinator.Groups);
        Assert.Contains(world.Coordinator.WaitingBots, w => w.BotId == horde.Id);
    }

    /// <summary>
    /// The volunteer's bot is the youngest waiter and holds no anchor of its own. The size-1 list (that bot, then the longest waiting) is two
    /// derived-only bots, and with the tanking player it makes a plan Match accepts but that holds no waiting anchor; the full list is the
    /// one whose oldest two bots include the in-band anchor. The unanchored plan is passed over and the full list forms the group.
    /// </summary>
    [Fact]
    public async Task ForAnInstanceGoal_APlanWithNoWaitingAnchor_IsPassedOver_AndTheNextListFormsTheGroupWithTheAnchor()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], DungeonQuiet, dungeon: true);
        await world.World.PrepareCharacterAsync("VOLANC", "Volanc", AccountSecurity.Player);
        var seed = await WaitingAtTheEntranceAsync(world, "Volseed", 20);
        var oldest = await WaitingAtTheEntranceAsync(world, "Voldoldest", 60);
        var anchor = await WaitingAtTheEntranceAsync(world, "Volanchor", 20);
        var youngest = await WaitingAtTheEntranceAsync(world, "Voldyoungest", 60);
        await StopSeedAsync(world, seed); // the first anchor leaves: the anchor is now the 2nd oldest of the three that wait
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLANC", "Volanc", AccountSecurity.Player);
        await PlaceAtTheEntranceAsync(world, "Volanc", level: 20); // in band, so a plan with the player passes Match's qualifying rule

        // Preconditions: three bots wait, in the order oldest derived, anchor, youngest derived; nothing formed (no bot can tank).
        foreach (var bot in new[] { oldest, anchor, youngest })
            Assert.Contains(world.Coordinator.WaitingBots, w => w.BotId == bot.Id);
        uint oldestSince = await SinceAsync(world, oldest.Id), anchorSince = await SinceAsync(world, anchor.Id), youngestSince = await SinceAsync(world, youngest.Id);
        Assert.True(unchecked((int)(anchorSince - oldestSince)) > 0 && unchecked((int)(youngestSince - anchorSince)) > 0, "the waiters are not in the expected order");
        Assert.Empty(world.Coordinator.Groups);

        PlayerbotVolunteerResult result = await VolunteerAsync(world, youngest.Id, "Volanc");

        Assert.Equal(PlayerbotVolunteerOutcome.Ready, result.Outcome);
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single(g => g.Members.Any(m => m.Real));
        Assert.Equal(3, group.Members.Count);
        Assert.Contains(group.Members, m => m.BotId == anchor.Id); // the plan holds a waiting anchor
        Assert.Contains(group.Members, m => m.BotId == oldest.Id);
        Assert.DoesNotContain(group.Members, m => m.BotId == youngest.Id);
    }

    /// <summary>
    /// No list makes a plan with a waiting anchor (the anchor is the youngest waiter, left out of both lists): the volunteer is not promised
    /// anything, no group forms, and the volunteer stays kept across the Match ticks (a rejected plan loses nobody).
    /// </summary>
    [Fact]
    public async Task ForAnInstanceGoal_WhenNoPlanHoldsAWaitingAnchor_TheVolunteerIsKept_AndNoGroupForms()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], DungeonQuiet, dungeon: true);
        await world.World.PrepareCharacterAsync("VOLKEEP", "Volkeep", AccountSecurity.Player);
        var seed = await WaitingAtTheEntranceAsync(world, "Volkseed", 20);
        await using WorldTestClient player = await world.World.EnterWorldAsync("VOLKEEP", "Volkeep", AccountSecurity.Player);
        await PlaceAtTheEntranceAsync(world, "Volkeep", level: 20);
        // One bot waits: the group lacks one (a true count), so the volunteer is kept.
        PlayerbotVolunteerResult noted = await VolunteerAsync(world, seed.Id, "Volkeep");
        Assert.Equal(PlayerbotVolunteerOutcome.Noted, noted.Outcome);
        Assert.Equal(1, noted.Missing);
        var key = await world.OnWorldAsync(() => world.Coordinator.WaitingBots.Single(w => w.BotId == seed.Id).Goal.Key);
        ObjectGuid volunteer = Online(world, "Volkeep").Guid;
        Assert.True(world.Coordinator.IsVolunteer(volunteer, key));

        // The player steps out of reach while the others arrive, so the group does not form on the way: two derived-only bots, then the
        // anchor (the youngest), then the first anchor leaves. No plan of the three that wait can then hold the anchor.
        await world.OnWorldAsync(() =>
        {
            Player real = Online(world, "Volkeep");
            real.Relocate(real.X + 3000f, real.Y, real.Z, 0, world.World.Host.World.NowMs);
            return true;
        });
        var first = await WaitingAtTheEntranceAsync(world, "Volkfirst", 60);
        var second = await WaitingAtTheEntranceAsync(world, "Volksecond", 60);
        var anchor = await WaitingAtTheEntranceAsync(world, "Volkanchor", 20);
        await StopSeedAsync(world, seed);
        foreach (var bot in new[] { first, second, anchor })
            Assert.Contains(world.Coordinator.WaitingBots, w => w.BotId == bot.Id);
        Assert.True(world.Coordinator.IsVolunteer(volunteer, key), "the volunteer expired before the check");
        Assert.Empty(world.Coordinator.Groups);

        await PlaceAtTheEntranceAsync(world, "Volkeep"); // back in reach: every Match tick now plans with the volunteer
        // The window below must end before the volunteer would expire on its own, or a kept volunteer could not be told from an expired one.
        Assert.True(world.Coordinator.VolunteerRemainingMs(volunteer) > 65_000, $"setup used too much of the volunteer's life ({world.Coordinator.VolunteerRemainingMs(volunteer)} ms left)");

        Assert.False(await world.RunUntilAsync(60_000, () => world.Coordinator.Groups.Count > 0 || PlayerInvited(world)), "a group formed without a waiting anchor" + Environment.NewLine + await world.TraceAsync());
        Assert.True(world.Coordinator.IsVolunteer(volunteer, key), "the volunteer was dropped when its plan was rejected");
        Assert.Equal(0, world.Coordinator.Totals.Formed);
        Assert.Null(await world.OnWorldAsync(() => world.GroupManager.GetInvite(volunteer)));
        foreach (var bot in new[] { first, second, anchor })
            Assert.Contains(world.Coordinator.WaitingBots, w => w.BotId == bot.Id);
    }
}
