using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Playerbots.Party;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.WorldBossTestKit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Forming a world-boss raid of 30 or more (p7b design 3.5 step 8, review item 9): the any-unanswered-invite abort of <c>AdvanceForming</c> would end a
/// raid of 34 on one silent bot, so for a world-boss group the silent or refusing invitees are dropped and forming goes on while at least
/// <c>MinRaidSize</c> remain. In its own class because 36 bots cost real time. The silent bots are damage dealers that answer no to a bot group's
/// invitation, so each is a refusing invitee in the group's own bookkeeping.
/// </summary>
[Collection(MeterCollection)]
public sealed class PlayerbotWorldBossFormingWorldTests
{
    private const int Bots = 36;

    /// <summary>A world with 36 level-60 bots (6 tanks, 12 healers, 18 damage), the boss up, a raid asked for 30 to 36.</summary>
    private static async Task<GroupTestWorld> StartWorldAsync()
    {
        GroupTestWorld world = await StartAsync(boss =>
        {
            boss.MinRaidSize = 30;
            boss.DefaultRaidSize = Bots;
            boss.HoldSeconds = 900;
            boss.MusterWindowSeconds = 300;
        }, options => options.MaxBots = Bots + 4, Passive());
        List<(Guid Id, string Name)> bots = await AddBotsAsync(world, "Wf", 6, 12, 18);
        await SpawnAsync(world, bots[0].Id, Passive(), BossAt);
        return world;
    }

    /// <summary>
    /// Wait for the group to exist; set the minimum to <paramref name="minLeftAfter"/> fewer than its size (live); take the last
    /// <paramref name="silent"/> damage dealers of its member list (invited last, never the leader) and make them answer no to a bot group's
    /// invitation (the party AI's <c>AcceptsBotGroup</c> hook), so each is dropped or refused in the group's own bookkeeping.
    /// </summary>
    private static async Task<(PlayerbotGroupCoordinator.BotGroup Group, int Size, List<Guid> Silent)> FormWithSilentAsync(GroupTestWorld world, int silent, int minLeftAfter)
    {
        PlayerbotGroupCoordinator.BotGroup? group = null;
        Assert.True(await world.RunUntilAsync(300_000, () => (group = Raid(world)) is not null), await world.TraceAsync());
        int size = group!.Members.Count;
        Assert.InRange(size, 30, Bots);

        List<Guid> mute = await world.OnWorldAsync(() =>
        {
            world.Options.Groups.WorldBosses.MinRaidSize = size - minLeftAfter;
            List<Guid> chosen = [.. group.Members.Where(m => !m.Real && m.Role == PlayerbotGroupRole.Damage && m.BotId != group.LeaderBotId).Select(m => m.BotId).TakeLast(silent)];
            foreach (Guid id in chosen)
            {
                PlayerbotPartyAI ai = world.Bots.FindPartyAI(id)!;
                ai.AcceptsBotGroup = (_, _) => false;
                // The default invite policy (Affinity) takes a well-known inviter (affinity the bots built up over the run's game time) or one of the same goal
                // on top of the hook, so a "silent" bot would join anyway on a run where the leader is well known to it. A rival is never taken.
                ai.InviteStanding = (_, _) => new PlayerbotGroupInvites.Standing(Rival: true, WellKnown: false, SharesGoal: false);
            }
            return chosen;
        });
        Assert.Equal(silent, mute.Count);
        return (group, size, mute);
    }

    [Fact]
    public async Task TwoSilentInvitees_AreDropped_AndTheRaidFormsWithTheRest_NotEndedAsInvitesUnanswered()
    {
        using var counts = new OutcomeCounts();
        await using GroupTestWorld world = await StartWorldAsync();
        (PlayerbotGroupCoordinator.BotGroup group, int size, List<Guid> mute) = await FormWithSilentAsync(world, silent: 2, minLeftAfter: 2);

        Assert.True(await world.RunUntilAsync(600_000, () => group.State != PlayerbotGroupState.Forming || !world.Coordinator.Groups.Contains(group)), await world.TraceAsync());

        Assert.Contains(group, world.Coordinator.Groups);
        Assert.NotEqual(PlayerbotGroupState.Forming, group.State);
        Assert.False(Noted(world, "invites-unanswered"), await world.TraceAsync());
        Assert.Equal(size - 2, group.Members.Count);
        Assert.All(mute, id => Assert.Null(group.Find(id)));
        Assert.Equal(0, counts.Of(PlayerbotWorldBossOutcomes.FormingFailed));
    }

    [Fact]
    public async Task OneSilentInviteeMoreThanTheMinimumAllows_EndsTheFormingAsInvitesUnanswered_WithoutUsingAnAttempt_ButWithTheCooldown()
    {
        using var counts = new OutcomeCounts();
        await using GroupTestWorld world = await StartWorldAsync();
        (PlayerbotGroupCoordinator.BotGroup group, _, _) = await FormWithSilentAsync(world, silent: 3, minLeftAfter: 2);

        Assert.True(await world.RunUntilAsync(600_000, () => !world.Coordinator.Groups.Contains(group)), await world.TraceAsync());

        Assert.True(Noted(world, "disbanded (invites-unanswered)"), await world.TraceAsync());
        Assert.Equal(1, counts.Of(PlayerbotWorldBossOutcomes.FormingFailed));
        Assert.Equal(0, counts.Of(PlayerbotWorldBossOutcomes.Killed));
        PlayerbotWorldBossCampaign campaign = world.Coordinator.WorldBossCampaign(BossEntry)!;
        Assert.Equal(PlayerbotWorldBossPhase.Watch, campaign.Phase);
        Assert.Equal(0, campaign.AttemptsToday(world.Coordinator.UnixNow()));          // a forming failure is not one of the day's attempts
        Assert.True(campaign.CooldownUntilUnix > world.Coordinator.UnixNow());          // but it still waits out RetrySeconds
    }

    [Fact]
    public async Task AQuestGroup_StillEndsOnOneBotThatDoesNotAnswer()
    {
        // The drop rule is for world-boss groups only: any other group keeps the any-unanswered abort of AdvanceForming.
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([GroupTestWorld.EliteQuest]);
        var tank = await world.AddBotAsync("Wqtank", Human, Warrior, 10, [GroupTestWorld.Taunt]);
        var healer = await world.AddBotAsync("Wqheal", Human, Priest, 10, [LesserHeal, GroupTestWorld.Resurrection]);
        var mage = await world.AddBotAsync("Wqmage", Human, Mage, 10, [Fireball]);
        // Nobody answers an invitation. The hook is set on every tick before the group forms: a three-bot group is whole in the tick it forms in, so setting it after is too late.
        void MuteAll()
        {
            foreach (var bot in new[] { tank, healer, mage }) if (world.Bots.FindPartyAI(bot.Id) is { } ai)
            {
                ai.AcceptsBotGroup = (_, _) => false;
                ai.InviteStanding = (_, _) => new PlayerbotGroupInvites.Standing(Rival: true, WellKnown: false, SharesGoal: false); // the Affinity policy takes an inviter of the same goal otherwise; a rival is refused
            }
        }

        Assert.True(await world.RunUntilAsync(60_000, () => { MuteAll(); return new[] { tank, healer, mage }.All(b => world.HasQuest(b.Id, GroupTestWorld.EliteQuest)); }), await world.TraceAsync());

        PlayerbotGroupCoordinator.BotGroup? group = null;
        Assert.True(await world.RunUntilAsync(60_000, () => { MuteAll(); return (group = world.Coordinator.Groups.FirstOrDefault(g => g.WorldBoss is null)) is not null; }), await world.TraceAsync());

        Assert.True(await world.RunUntilAsync(120_000, () => !world.Coordinator.Groups.Contains(group!)), await world.TraceAsync());
        Assert.True(Noted(world, "invites-unanswered"), await world.TraceAsync());
    }
}
