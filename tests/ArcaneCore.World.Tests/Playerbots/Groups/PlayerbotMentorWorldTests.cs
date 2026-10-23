using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
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
}
