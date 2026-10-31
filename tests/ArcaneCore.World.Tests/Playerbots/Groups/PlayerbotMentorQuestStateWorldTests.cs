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
/// A bot with no quest, a set-aside quest, and a fault in the mentor scan. Split from the former <c>PlayerbotMentorWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotMentorWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotMentorQuestStateWorldTests : PlayerbotMentorWorldTestBase
{
    [Fact]
    public async Task ABotWithNoQuest_ThatCallsForHelp_IsNeverMentored()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([], Options);
        var low = await world.AddBotAsync("Mentee", Human, Warrior, 1, []);
        var mentors = new List<(Guid Id, string Name)>();
        for (int i = 0; i < 6; i++)
            mentors.Add(await world.AddBotAsync("Mentor" + (char)('a' + i), Human, Mage, 12, [Fireball], South + new Vector3(i * 3f, 0, 0)));
        List<Guid> passing = await MakeFriendsAsync(world, low, mentors, befriend: true);
        Assert.NotEmpty(passing);
        await world.OnWorldAsync(() => { world.Coordinator.NoteHelpCall(low.Id); return true; });
        await world.RunAsync(100_000);
        Assert.Equal(0, world.Coordinator.MentorGroupCount);
    }

    [Fact]
    public async Task AMentorGroupOnASetAsideQuest_LiftsTheBrainsSuspensionOfThatQuest()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], SoloOptions);
        var (low, mentors) = await SetupAsync(world, 12);
        foreach ((Guid Id, string Name) mentor in mentors) await BefriendAsync(world, low, mentor); // the roll per mentor is a hash: some pass

        // As a failed group leaves it: the goal set aside, the quest and its creature suspended in the brain.
        bool suspended = await world.OnWorldAsync(() =>
        {
            PlayerbotGroupGoal goal = world.Coordinator.WaitingBots.Single(w => w.BotId == low.Id).Goal;
            world.Coordinator.SetAside(low.Id, goal, suspendInBrain: true, failed: true);
            PlayerbotSuspensions brain = world.Bots.FindBrain(low.Id)!.Suspensions;
            uint now = world.World.Host.World.NowMs;
            return brain.IsQuestSuspended(DuoQuest, now) && brain.IsEntrySuspended(OgreEntry, now);
        });
        Assert.True(suspended, "the set-aside suspends the quest in the brain");

        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.MentorGroupCount == 1), await world.TraceAsync());
        await world.OnWorldAsync(() =>
        {
            PlayerbotSuspensions brain = world.Bots.FindBrain(low.Id)!.Suspensions;
            uint now = world.World.Host.World.NowMs;
            Assert.False(brain.IsQuestSuspended(DuoQuest, now), "the mentor group works the quest: it is not suspended any more");
            Assert.False(brain.IsEntrySuspended(OgreEntry, now));
            return true;
        });
    }

    [Fact]
    public async Task AFaultInTheMentorScan_IsRecordedUnderPlayerbotGroups_AndTheScanGoesOn()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([DuoQuest], Options);
        int probes = 0;
        world.Coordinator.MentorProbe = () =>
        {
            probes++;
            throw new MentorProbeFault();
        };
        var bot = await world.AddBotAsync("Probee", Human, Warrior, 1, []);
        Assert.True(await world.RunUntilAsync(120_000, () => probes >= 2), await world.TraceAsync());
        Assert.Contains(FaultLedger.Shared.Snapshot(), f => f.Site == "playerbot.groups" && f.ExceptionType.Contains(nameof(MentorProbeFault), StringComparison.Ordinal));
        Assert.True(await world.OnWorldAsync(() => world.Coordinator.WaitingBots.Any(w => w.BotId == bot.Id)), "the rest of the scan ran: the bot waits for a group");
    }
}
