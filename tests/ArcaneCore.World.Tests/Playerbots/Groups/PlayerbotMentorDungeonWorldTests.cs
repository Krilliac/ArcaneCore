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
/// Mentoring a bot that waits for a dungeon (Deadmines, map 36): a bot that anchors the instance goal (in band) may be mentored on its quest, but a
/// bot waiting only on derived reasons (a forming group seeded by someone else) is not, because its goal would not anchor a group of its own.
/// </summary>
public sealed class PlayerbotMentorDungeonWorldTests
{
    private const byte Human = 1, Warrior = 1, Mage = 8;
    private const uint Fireball = 133;

    // Mentors stand 150 yards from the quest giver (inside RecruitRadiusYards of the waiters, but away from the giver so they do not take the
    // dungeon quest and wait themselves, which would rule them out as mentors).
    private static readonly Vector3 MentorSpot = EntranceArea + new Vector3(0, -150f, 0);

    private static void Options(PlayerbotOptions options)
    {
        options.MoveSpeed = 0.1f;
        options.Social.Guilds.Enabled = false;
        options.Social.Lists.Enabled = false; // the only friendship is the one the test makes, so the waiter's reason stays "forminggroup"
        options.Groups.LevelRange = 50;
        options.Groups.Cooperation.Recruit = false;
        options.Groups.Cooperation.MentorChance = 1f;
        options.Groups.Cooperation.RecruitRadiusYards = 1500;
    }

    private static bool Noted(GroupTestWorld world, string text) => world.Coordinator.Events.Any(e => e.Contains(text, StringComparison.Ordinal));

    /// <summary>
    /// The same world for both tests: an in-band seed (level 20, band 15-28) and a level-35 drifter whose quest is grey (gray level 27) so it waits only
    /// on the forming group the seed gives it, plus six level-50 mentors. Only the bot that calls for help (and is befriended by a mentor) differs.
    /// Returns the mentee and the mentor that is made its friend.
    /// </summary>
    private static async Task<(GroupTestWorld World, (Guid Id, string Name) Mentee, Guid MentorId)> SetupAsync(bool menteeIsTheSeed)
    {
        GroupTestWorld world = await GroupTestWorld.StartAsync([DungeonQuest], Options, dungeon: true, dungeonQuestLevel: 20);
        try
        {
            var seed = await world.AddBotAsync("Anchora", Human, Warrior, 20, [Taunt], EntranceArea);
            Assert.True(await world.RunUntilAsync(60_000, () => world.HasQuest(seed.Id, DungeonQuest) && Noted(world, "Anchora needs a group")), await world.TraceAsync());
            var drifter = await world.AddBotAsync("Drifter", Human, Warrior, 35, [Taunt], EntranceArea);
            Assert.True(await world.RunUntilAsync(60_000, () => world.HasQuest(drifter.Id, DungeonQuest) && world.Coordinator.WaitingBots.Any(w => w.BotId == drifter.Id)), await world.TraceAsync());
            Assert.Contains(world.Coordinator.Events, e => e.Contains("Drifter needs a group", StringComparison.Ordinal) && e.EndsWith(": forminggroup", StringComparison.Ordinal));

            var mentors = new List<(Guid Id, string Name)>();
            for (int i = 0; i < 6; i++)
                mentors.Add(await world.AddBotAsync("Mentor" + (char)('a' + i), Human, Mage, 50, [Fireball], MentorSpot + new Vector3(i * 3f, 0, 0)));
            var mentee = menteeIsTheSeed ? seed : drifter;
            Guid mentorId = await BefriendAPassingMentorAsync(world, mentee, mentors);
            return (world, mentee, mentorId);
        }
        catch
        {
            await world.DisposeAsync();
            throw;
        }
    }

    /// <summary>The first mentor whose fixed roll passes (MentorChance times Helpfulness) is made a liked friend of the mentee; returns its id.</summary>
    private static Task<Guid> BefriendAPassingMentorAsync(GroupTestWorld world, (Guid Id, string Name) low, List<(Guid Id, string Name)> mentors)
        => world.OnWorldAsync(() =>
        {
            Player lowPlayer = world.Player(low.Id);
            PlayerbotSocialMemory memory = world.Bots.Social.Memory;
            float chance = world.Options.Groups.Cooperation.MentorChance;
            foreach ((Guid id, _) in mentors)
            {
                Player mentor = world.Player(id);
                if (PlayerbotRecruitment.Roll(mentor.Guid.Value, (int)lowPlayer.Guid.Low) >= chance * PlayerbotTraits.For(mentor.Guid.Value).Helpfulness) continue;
                memory.Load([new PlayerbotRelation(id, (int)mentor.Guid.Low, lowPlayer.Guid.Value, true, 40, 6, 500, PlayerbotRelationFlags.None, memory.NowMs, "", "")]);
                world.World.Services.GetRequiredService<SocialFeature>().Context.Friends.AddFriend(mentor, lowPlayer.Name);
                return id;
            }

            throw new InvalidOperationException("no mentor's roll passes for " + low.Name);
        });

    /// <summary>The mentor would pass every check of the mentor scan but the goal's: alive, same team and map, far enough above, in radius, not waiting or grouped.</summary>
    private static async Task AssertMentorIsOtherwiseEligibleAsync(GroupTestWorld world, (Guid Id, string Name) mentee, Guid mentorId)
    {
        await world.OnWorldAsync(() =>
        {
            Player low = world.Player(mentee.Id), mentor = world.Player(mentorId);
            var coop = world.Options.Groups.Cooperation;
            Assert.True(low.IsAlive && mentor.IsAlive);
            Assert.Equal(low.Team, mentor.Team);
            Assert.Equal(low.MapId, mentor.MapId);
            Assert.True(mentor.Level >= low.Level + coop.MentorLevelGap, $"mentor {mentor.Level} vs mentee {low.Level}");
            Assert.True(Vector3.Distance(new Vector3(mentor.X, mentor.Y, mentor.Z), new Vector3(low.X, low.Y, low.Z)) <= coop.RecruitRadiusYards);
            return true;
        });
        Assert.DoesNotContain(world.Coordinator.WaitingBots, w => w.BotId == mentorId);
        Assert.DoesNotContain(world.Coordinator.Groups, g => g.Find(mentorId) is not null);
    }

    [Fact]
    public async Task AnAnchoringInBandWaiter_ThatCallsForHelp_IsMentoredOnTheDungeonQuest()
    {
        (GroupTestWorld world, var mentee, Guid mentorId) = await SetupAsync(menteeIsTheSeed: true);
        await using (world)
        {
            await AssertMentorIsOtherwiseEligibleAsync(world, mentee, mentorId);
            await world.OnWorldAsync(() => { world.Coordinator.NoteHelpCall(mentee.Id); return true; });
            Assert.True(await world.RunUntilAsync(90_000, () => world.Coordinator.MentorGroupCount == 1), await world.TraceAsync());
            PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single(g => g.Goal.Source == PlayerbotGroupCoordinator.MentorSource);
            Assert.Equal(mentorId, group.LeaderBotId);
            Assert.NotNull(group.Find(mentee.Id));
            Assert.Equal(DungeonQuest, group.Goal.QuestId);
        }
    }

    [Fact]
    public async Task AnOutOfBandWaiter_OnDerivedReasonsOnly_ThatCallsForHelp_IsNeverMentoredOnTheDungeonGoal()
    {
        (GroupTestWorld world, var mentee, Guid mentorId) = await SetupAsync(menteeIsTheSeed: false);
        await using (world)
        {
            await AssertMentorIsOtherwiseEligibleAsync(world, mentee, mentorId);
            await world.OnWorldAsync(() => { world.Coordinator.NoteHelpCall(mentee.Id); return true; });

            // The mentor passes every other check (the twin test mentors the seed in the same world); the drifter's instance goal would not anchor.
            await world.RunAsync(100_000);
            Assert.Equal(0, world.Coordinator.MentorGroupCount);
            Assert.DoesNotContain(world.Coordinator.Events, e => e.Contains("mentors Drifter", StringComparison.Ordinal));
        }
    }
}
