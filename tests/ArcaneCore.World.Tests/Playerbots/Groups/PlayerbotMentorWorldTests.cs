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
/// Mentoring (docs/areas/playbots-groups.md, Cooperation): a friend well above a bot that called for help teams up with it as a group of
/// two on its quest, and the group ends; strangers and bots within ten levels never do.
/// </summary>
public sealed class PlayerbotMentorWorldTests
{
    private const byte Human = 1, Warrior = 1, Mage = 8;
    private const uint Fireball = 133;

    private static readonly Vector3 South = new(StartX, StartY - 150f, StartZ);

    private static void Options(PlayerbotOptions options)
    {
        options.MoveSpeed = 0.1f;
        options.Social.Guilds.Enabled = false;
        options.Groups.Cooperation.Recruit = false;
        options.Groups.Cooperation.MentorChance = 1f;
        options.Groups.Cooperation.RecruitRadiusYards = 1500; // bots wander off quickly; the radius has its own rule
    }

    private static async Task<((Guid Id, string Name) Low, List<(Guid Id, string Name)> Mentors)> SetupAsync(GroupTestWorld world, byte mentorLevel)
    {
        var low = await world.AddBotAsync("Mentee", Human, Warrior, 1, []);
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Any(w => w.BotId == low.Id)), await world.TraceAsync());
        var mentors = new List<(Guid Id, string Name)>();
        for (int i = 0; i < 6; i++)
            mentors.Add(await world.AddBotAsync("Mentor" + (char)('a' + i), Human, Mage, mentorLevel, [Fireball], South + new Vector3(i * 3f, 0, 0)));
        return (low, mentors);
    }

    /// <summary>A mentor that likes the bot, is its friend, and whose fixed roll passes (the help call path: no struggle time in the roll).</summary>
    private static Task<List<Guid>> MakeFriendsAsync(GroupTestWorld world, (Guid Id, string Name) low, List<(Guid Id, string Name)> mentors, bool befriend)
        => world.OnWorldAsync(() =>
        {
            Player lowPlayer = world.Player(low.Id);
            PlayerbotSocialMemory memory = world.Bots.Social.Memory;
            var chosen = new List<Guid>();
            foreach ((Guid id, string name) in mentors)
            {
                Player mentor = world.Player(id);
                double roll = PlayerbotRecruitment.Roll(mentor.Guid.Value, (int)lowPlayer.Guid.Low);
                if (roll >= PlayerbotTraits.For(mentor.Guid.Value).Helpfulness) continue;
                chosen.Add(id);
                if (chosen.Count > 1) continue; // only the first passing mentor is made a friend
                memory.Load([new PlayerbotRelation(id, (int)mentor.Guid.Low, lowPlayer.Guid.Value, true, 40, 6, 500, PlayerbotRelationFlags.None, memory.NowMs, "", "")]);
                if (befriend) world.World.Services.GetRequiredService<SocialFeature>().Context.Friends.AddFriend(mentor, lowPlayer.Name);
            }

            return chosen;
        });

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

    /// <summary>A mentor of the tests that likes the bot and is its friend.</summary>
    private static Task BefriendAsync(GroupTestWorld world, (Guid Id, string Name) low, (Guid Id, string Name) mentorBot)
        => world.OnWorldAsync(() =>
        {
            Player lowPlayer = world.Player(low.Id), mentor = world.Player(mentorBot.Id);
            PlayerbotSocialMemory memory = world.Bots.Social.Memory;
            memory.Load([new PlayerbotRelation(mentorBot.Id, (int)mentor.Guid.Low, lowPlayer.Guid.Value, true, 40, 6, 500, PlayerbotRelationFlags.None, memory.NowMs, "", "")]);
            world.World.Services.GetRequiredService<SocialFeature>().Context.Friends.AddFriend(mentor, lowPlayer.Name);
            return true;
        });

    private static void SoloOptions(PlayerbotOptions options)
    {
        Options(options);
        options.Groups.Cooperation.MentorChance = 1f;
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

    private sealed class MentorProbeFault : Exception
    {
        public MentorProbeFault() : base("mentor probe fault") { }
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
