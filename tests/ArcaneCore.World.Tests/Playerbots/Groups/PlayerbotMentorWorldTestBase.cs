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
public abstract class PlayerbotMentorWorldTestBase
{
    private protected const byte Human = 1, Warrior = 1, Mage = 8;
    private protected const uint Fireball = 133;

    private protected static readonly Vector3 South = new(StartX, StartY - 150f, StartZ);

    private protected static void Options(PlayerbotOptions options)
    {
        options.MoveSpeed = 0.1f;
        options.Social.Guilds.Enabled = false;
        options.Groups.Cooperation.Recruit = false;
        options.Groups.Cooperation.MentorChance = 1f;
        options.Groups.Cooperation.RecruitRadiusYards = 1500; // bots wander off quickly; the radius has its own rule
    }

    private protected static async Task<((Guid Id, string Name) Low, List<(Guid Id, string Name)> Mentors)> SetupAsync(GroupTestWorld world, byte mentorLevel)
    {
        var low = await world.AddBotAsync("Mentee", Human, Warrior, 1, []);
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Any(w => w.BotId == low.Id)), await world.TraceAsync());
        var mentors = new List<(Guid Id, string Name)>();
        for (int i = 0; i < 6; i++)
            mentors.Add(await world.AddBotAsync("Mentor" + (char)('a' + i), Human, Mage, mentorLevel, [Fireball], South + new Vector3(i * 3f, 0, 0)));
        return (low, mentors);
    }

    /// <summary>A mentor that likes the bot, is its friend, and whose fixed roll passes (the help call path: no struggle time in the roll).</summary>
    private protected static Task<List<Guid>> MakeFriendsAsync(GroupTestWorld world, (Guid Id, string Name) low, List<(Guid Id, string Name)> mentors, bool befriend)
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

    /// <summary>A mentor of the tests that likes the bot and is its friend.</summary>
    private protected static Task BefriendAsync(GroupTestWorld world, (Guid Id, string Name) low, (Guid Id, string Name) mentorBot)
        => world.OnWorldAsync(() =>
        {
            Player lowPlayer = world.Player(low.Id), mentor = world.Player(mentorBot.Id);
            PlayerbotSocialMemory memory = world.Bots.Social.Memory;
            memory.Load([new PlayerbotRelation(mentorBot.Id, (int)mentor.Guid.Low, lowPlayer.Guid.Value, true, 40, 6, 500, PlayerbotRelationFlags.None, memory.NowMs, "", "")]);
            world.World.Services.GetRequiredService<SocialFeature>().Context.Friends.AddFriend(mentor, lowPlayer.Name);
            return true;
        });

    private protected static void SoloOptions(PlayerbotOptions options)
    {
        Options(options);
        options.Groups.Cooperation.MentorChance = 1f;
    }

    private protected sealed class MentorProbeFault : Exception
    {
        public MentorProbeFault() : base("mentor probe fault") { }
    }
}
