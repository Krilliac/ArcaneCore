using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// A friend within ten levels never mentoring, and a bot soloing a quest. Split from the former <c>PlayerbotMentorWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotMentorWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotMentorLevelGapWorldTests : PlayerbotMentorWorldTestBase
{
    [Fact]
    public async Task AFriendWithinTenLevels_NeverMentors()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], Options);
        var (low, mentors) = await SetupAsync(world, 10); // nine levels above level 1
        await MakeFriendsAsync(world, low, mentors, befriend: true);
        await world.OnWorldAsync(() => { world.Coordinator.NoteHelpCall(low.Id); return true; });
        await world.RunAsync(100_000);
        Assert.Equal(0, world.Coordinator.MentorGroupCount);
    }

    [Fact]
    public async Task ABotSoloingAQuest_ThatIsNotWaiting_AndCallsForHelp_IsMentoredOnThatQuest()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([SoloQuest], Options);
        var low = await world.AddBotAsync("Mentee", Human, Warrior, 1, []);
        var mentors = new List<(Guid Id, string Name)>();
        for (int i = 0; i < 6; i++)
            mentors.Add(await world.AddBotAsync("Mentor" + (char)('a' + i), Human, Mage, 12, [Fireball], South + new Vector3(i * 3f, 0, 0)));
        List<Guid> passing = await MakeFriendsAsync(world, low, mentors, befriend: true);
        Assert.NotEmpty(passing);

        // The bot takes the solo quest and, as it sets out on it (its brain's current quest), calls for help the way its fight social does
        // (the feature's SayCallForHelp, which tells the coordinator and names the quest).
        bool called = false;
        Assert.True(await world.RunUntilAsync(60_000, () =>
        {
            if (world.Bots.FindBrain(low.Id) is not { QuestId: SoloQuest } || !world.HasQuest(low.Id, SoloQuest)) return false;
            world.Bots.FightOf(low.Id)!.CallForHelp!(world.Player(low.Id), world.Player(passing[0]));
            return called = true;
        }), await world.TraceAsync());
        Assert.True(called);
        Assert.DoesNotContain(world.Coordinator.WaitingBots, w => w.BotId == low.Id);
        Assert.True(await world.RunUntilAsync(90_000, () => world.Coordinator.MentorGroupCount == 1), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single(g => g.Goal.Source == PlayerbotGroupCoordinator.MentorSource);
        Assert.Equal(passing[0], group.LeaderBotId);
        Assert.NotNull(group.Find(low.Id));
        Assert.Equal(SoloQuest, group.Goal.QuestId);
        Assert.Equal(RatEntry, group.Goal.ObjectiveEntry);
        Assert.Equal(2, group.Members.Count);
    }
}
