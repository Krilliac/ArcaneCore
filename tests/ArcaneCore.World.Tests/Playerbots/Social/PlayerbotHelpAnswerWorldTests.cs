using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// Answering a call for help (P2 section 1) through the real paths: the asker's targeted /helpme emote (CMSG_TEXT_EMOTE, the server's broadcast,
/// the bot's party intake, <see cref="PlayerbotSocialRecorder.NoteHelpCall"/>), the help lane's decision on the social pass (a hand-built pass at a
/// world time the test picks) and the bot's own think joining the fight through the assist lane, which damages the asker's attacker.
/// </summary>
public sealed class PlayerbotHelpAnswerWorldTests
{
    private static byte[] HelpEmote(ObjectGuid bot) => PlayerbotSocialPackets.TextEmote(PlayerbotEmoteMoods.HelpMe, bot);

    private static async Task<(Guid Bot, ObjectGuid BotGuid, ObjectGuid AskerGuid, Creature Mob)> SetUpAsync(SocialWorld world)
    {
        Guid bot = await world.AddBotAsync("Helpanswer");
        await HelpTestKit.StandAsync(world, "Helpanswer", ("Helpasker", 2f));
        ObjectGuid botGuid = await world.Host.PlayerStateAsync("Helpanswer", p => p.Guid);
        ObjectGuid askerGuid = await world.Host.PlayerStateAsync("Helpasker", p => p.Guid);
        Creature mob = null!;
        await world.Host.OnWorldAsync(() =>
        {
            Player victim = HelpTestKit.Online(world, "Helpasker");
            mob = HelpTestKit.SpawnMob(victim, 4f);
            HelpTestKit.Engage(mob, victim);
            Assert.True(victim.Combat.IsInCombat);
        });
        return (bot, botGuid, askerGuid, mob);
    }

    /// <summary>The asker calls /helpme at the bot; waits until the call has reached the recorder.</summary>
    private static async Task CallAsync(SocialWorld world, WorldTestClient asker, ObjectGuid botGuid)
    {
        await asker.SendAsync(WorldOpcode.CmsgTextEmote, HelpEmote(botGuid));
        await HelpTestKit.UntilAsync(() => HelpTestKit.RecorderOf(world).PendingHelpCalls > 0, "the bot to take the call for help");
    }

    private static Task RunLaneAsync(SocialWorld world, long nowMs)
        => world.Host.OnWorldAsync(() => SocialTestKit.Feature(world.Host).Help.Run(SocialTestKit.BotPass(world.Host, nowMs)));

    [Fact]
    public async Task AskedBot_WithAYesRoll_JoinsTheFight_AndDamagesTheAttacker()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        await using WorldTestClient asker = await world.AddPlayerAsync("Helpasker");
        (Guid bot, ObjectGuid botGuid, ObjectGuid askerGuid, Creature mob) = await SetUpAsync(world);
        float helpfulness = PlayerbotTraits.For(botGuid.Value).Helpfulness;
        long now = HelpTestKit.BucketWhere(b => PlayerbotCallForHelp.Answers(botGuid.Value, askerGuid.Value, b, helpfulness, 0));
        uint healthBefore = await world.Host.OnWorldAsync(() => mob.Health);

        await CallAsync(world, asker, botGuid);
        await RunLaneAsync(world, now);

        PlayerbotHelpLane lane = SocialTestKit.Feature(world.Host).Help;
        Assert.Equal(1, lane.Answered);
        Assert.Equal(0, lane.Declined);
        PlayerbotBrain brain = HelpTestKit.BrainOf(world, bot);
        await HelpTestKit.UntilAsync(() => brain.HelpsDone == 1, "the bot's think to carry out the request");
        Assert.Equal(PlayerbotHelpKind.Assist, brain.LastHelpKind);
        Assert.Equal(askerGuid, brain.LastHelpTarget);
        await HelpTestKit.UntilAsync(() => world.Host.OnWorldAsync(() => mob.Health).Result < healthBefore, "the bot to damage the asker's attacker");

        // The assist is recorded once as help, in the bot's memory of the asker.
        PlayerbotSocialMemory memory = HelpTestKit.RecorderOf(world).Memory;
        Assert.True(memory.TryGet(bot, askerGuid.Value, out PlayerbotRelation relation));
        Assert.Equal((short)new PlayerbotSocialMemoryOptions().AffinityPerHelp, relation.Affinity);
    }

    [Fact]
    public async Task ADislikedAsker_IsIgnored_EvenWhenTheRollWouldSayYes()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        await using WorldTestClient asker = await world.AddPlayerAsync("Helpasker");
        (Guid bot, ObjectGuid botGuid, ObjectGuid askerGuid, Creature mob) = await SetUpAsync(world);
        // The asker killed the bot in PvP: affinity -20, a dislike.
        await world.Host.OnWorldAsync(() => HelpTestKit.RecorderOf(world).Note(botGuid, askerGuid, PlayerbotSocialEvent.KilledBy));
        Assert.True(HelpTestKit.RecorderOf(world).Memory.TryGet(bot, askerGuid.Value, out PlayerbotRelation relation));
        Assert.True(relation.Affinity < 0);
        float helpfulness = PlayerbotTraits.For(botGuid.Value).Helpfulness;
        long now = HelpTestKit.BucketWhere(b => PlayerbotCallForHelp.Answers(botGuid.Value, askerGuid.Value, b, helpfulness, relation.Affinity));

        await CallAsync(world, asker, botGuid);
        await RunLaneAsync(world, now);

        PlayerbotHelpLane lane = SocialTestKit.Feature(world.Host).Help;
        Assert.Equal(0, lane.Answered);
        Assert.Equal(1, lane.Declined);
        PlayerbotBrain brain = HelpTestKit.BrainOf(world, bot);
        await Task.Delay(1500);
        Assert.Equal(PlayerbotHelpKind.None, brain.PendingHelp);
        Assert.Equal(0, brain.HelpsDone);
        Assert.False(brain.Contention.IsAssisting(mob.Guid));
    }

    [Fact]
    public async Task ABotInItsOwnFight_IgnoresTheCall()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        await using WorldTestClient asker = await world.AddPlayerAsync("Helpasker");
        (Guid bot, ObjectGuid botGuid, ObjectGuid askerGuid, Creature mob) = await SetUpAsync(world);
        float helpfulness = PlayerbotTraits.For(botGuid.Value).Helpfulness;
        long now = HelpTestKit.BucketWhere(b => PlayerbotCallForHelp.Answers(botGuid.Value, askerGuid.Value, b, helpfulness, 0));
        await world.Host.OnWorldAsync(() =>
        {
            Player self = HelpTestKit.Online(world, "Helpanswer");
            HelpTestKit.Engage(HelpTestKit.SpawnMob(self, -4f), self); // a mob of its own
            Assert.True(self.Combat.IsInCombat);
        });

        await CallAsync(world, asker, botGuid);
        await RunLaneAsync(world, now);

        PlayerbotHelpLane lane = SocialTestKit.Feature(world.Host).Help;
        Assert.Equal(0, lane.Answered);
        PlayerbotBrain brain = HelpTestKit.BrainOf(world, bot);
        Assert.Equal(PlayerbotHelpKind.None, brain.PendingHelp);
        Assert.False(brain.Contention.IsAssisting(mob.Guid));
    }

    [Fact]
    public async Task TheSwitch_AnswerHelp_SilencesTheLane()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(options => options.Social.Helping.AnswerHelp = false, frozen: true);
        await using WorldTestClient asker = await world.AddPlayerAsync("Helpasker");
        (Guid bot, ObjectGuid botGuid, ObjectGuid askerGuid, _) = await SetUpAsync(world);
        float helpfulness = PlayerbotTraits.For(botGuid.Value).Helpfulness;
        long now = HelpTestKit.BucketWhere(b => PlayerbotCallForHelp.Answers(botGuid.Value, askerGuid.Value, b, helpfulness, 0));

        await CallAsync(world, asker, botGuid);
        await RunLaneAsync(world, now);

        Assert.Equal(0, SocialTestKit.Feature(world.Host).Help.Answered);
        Assert.Equal(0, HelpTestKit.RecorderOf(world).PendingHelpCalls); // the call was taken and dropped, not kept for later
        Assert.Equal(PlayerbotHelpKind.None, HelpTestKit.BrainOf(world, bot).PendingHelp);
    }
}
