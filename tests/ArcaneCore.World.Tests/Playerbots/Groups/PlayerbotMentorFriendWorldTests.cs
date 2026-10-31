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
/// A friend well above a bot mentoring it, and strangers or bots within ten levels never doing. Split from the former <c>PlayerbotMentorWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotMentorWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotMentorFriendWorldTests : PlayerbotMentorWorldTestBase
{
    [Fact]
    public async Task AFriendWellAbove_ABotThatCalledForHelp_FormsATwoBotGroupOnItsQuest_AndItEnds()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], Options);
        var (low, mentors) = await SetupAsync(world, 12);
        List<Guid> passing = await MakeFriendsAsync(world, low, mentors, befriend: true);
        Assert.NotEmpty(passing);
        Guid mentorId = passing[0];

        await world.OnWorldAsync(() => { world.Coordinator.NoteHelpCall(low.Id); return true; });
        Assert.True(await world.RunUntilAsync(90_000, () => world.Coordinator.MentorGroupCount == 1), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single(g => g.Goal.Source == PlayerbotGroupCoordinator.MentorSource);
        Assert.Equal(2, group.Members.Count);
        Assert.Equal(mentorId, group.LeaderBotId);
        Assert.NotNull(group.Find(low.Id));
        Assert.Equal(DuoQuest, group.Goal.QuestId);
        Assert.Contains(world.Coordinator.Events, e => e.Contains("mentors Mentee", StringComparison.Ordinal));

        // It ends by itself: the objective, or at the latest MentorMaxMinutes (20).
        Assert.True(await world.RunUntilAsync(21 * 60_000, () => world.Coordinator.MentorGroupCount == 0), await world.TraceAsync());
    }

    [Fact]
    public async Task AStranger_OrABotWithinTenLevels_NeverMentors()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], Options);
        var (low, mentors) = await SetupAsync(world, 12);
        await MakeFriendsAsync(world, low, mentors, befriend: false); // liked, but not a friend or guildmate
        await world.OnWorldAsync(() => { world.Coordinator.NoteHelpCall(low.Id); return true; });
        await world.RunAsync(100_000);
        Assert.Equal(0, world.Coordinator.MentorGroupCount);
    }
}
