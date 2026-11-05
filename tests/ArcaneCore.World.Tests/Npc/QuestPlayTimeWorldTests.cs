using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Progression;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Npc;

/// <summary>
/// The play-time flags (<c>World:LimitPlayTime</c>) on a quest turn-in, over the wire: a NO_PLAY_TIME player is refused the reward with the unhealthy-time warning
/// (vmangos Player::CanRewardQuest, Player.cpp:12730-12736), and a PARTIAL_PLAY_TIME player is given half the experience in the settlement preview as well as in the
/// live grant, so the level the settlement commits is the level the player reaches (<see cref="PlayerProgression.EffectiveXp"/>).
/// Written without a build or test run (lane rule); unproven until the integration gate.
/// </summary>
public sealed class QuestPlayTimeWorldTests
{
    private static WorldTestHost Start(QuestInteractionFixture fixture)
    {
        QuestInteractionTestServices.Current.Value = fixture;
        try
        {
            return WorldTestHost.Start();
        }
        finally
        {
            QuestInteractionTestServices.Current.Value = null;
        }
    }

    private static Task VisibleAsync(WorldTestHost host, string name) => host.WaitForWorldAsync(
        () => host.World.FindOnlinePlayer(name)!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "questgiver becomes visible");

    private static byte[] QuestBody()
    {
        var writer = new PacketWriter(12);
        writer.WriteUInt64(QuestInteractionFixture.Guid.Value);
        writer.WriteUInt32(QuestInteractionFixture.QuestId);
        return writer.ToArray();
    }

    private static byte[] ChooseRewardBody()
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt64(QuestInteractionFixture.Guid.Value);
        writer.WriteUInt32(QuestInteractionFixture.QuestId);
        writer.WriteUInt32(0);
        return writer.ToArray();
    }

    private static async Task<QuestNpcFeature> AcceptAsync(WorldTestHost host, WorldTestClient client, string name)
    {
        await VisibleAsync(host, name);
        await client.SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, QuestBody());
        await client.ReadUntilAsync(WorldOpcode.SmsgGossipComplete);
        return await host.PlayerStateAsync(name, p => ((WorldSession)p.Session).Services.GetRequiredService<QuestNpcFeature>());
    }

    [Fact]
    public async Task ChooseReward_ANoPlayTimePlayer_IsRefusedWithTheUnhealthyWarning_AndNothingIsRewarded()
    {
        var fixture = new QuestInteractionFixture { NoObjectives = true, CommitRewardsToMemory = true };
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient client = await host.EnterWorldAsync("PTQUEST", "Ptquestone");
        QuestNpcFeature feature = await AcceptAsync(host, client, "Ptquestone");
        int id = await host.PlayerStateAsync("Ptquestone", p => checked((int)p.Guid.Low));
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Ptquestone")!.Flags |= PlayerFlags.NoPlayTime);
        await client.CollectAsync();

        await client.SendAsync(WorldOpcode.CmsgQuestgiverChooseReward, ChooseRewardBody());
        // SMSG_PLAY_TIME_WARNING: u32 flag (PTF_UNHEALTHY_TIME, WorldSession.h:149), i32 seconds left (0), Player.cpp:12730-12736.
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x80, 0, 0, 0, 0 }, await client.ReadUntilAsync(WorldOpcode.SmsgPlayTimeWarning));
        await client.AssertNoPacketsAsync(WorldOpcode.SmsgQuestgiverQuestComplete);
        Assert.False(await host.PlayerStateAsync("Ptquestone", p => p.IsQuestSettlementPending));
        Assert.Empty(fixture.RewardCommits);
        Assert.False(fixture.Characters.Stored(id, QuestInteractionFixture.QuestId).Rewarded);

        // Positive control: the same request settles once the flag is gone, so the refusal above was the flag.
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Ptquestone")!.Flags &= ~PlayerFlags.NoPlayTime);
        await client.SendAsync(WorldOpcode.CmsgQuestgiverChooseReward, ChooseRewardBody());
        await client.ReadUntilAsync(WorldOpcode.SmsgQuestgiverQuestComplete);
        await feature.WaitForSettlementAsync(id).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(fixture.RewardCommits);
        Assert.True(fixture.Characters.Stored(id, QuestInteractionFixture.QuestId).Rewarded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChooseReward_ALevelCrossingQuestWithPartialPlayTime_CommitsTheLevelTheLiveGrantReaches(bool partial)
    {
        // Quest level 1 with RewMoneyMaxLevel 300 derives 500 XP (300 / 0.6). A level 1 player needs less than that for level 2 but more than the halved 250.
        var fixture = new QuestInteractionFixture { NoObjectives = true, CommitRewardsToMemory = true, RewardMoneyMaxLevel = 300 };
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient client = await host.EnterWorldAsync("PTQXP", "Ptqxpone");
        QuestNpcFeature feature = await AcceptAsync(host, client, "Ptqxpone");
        int id = await host.PlayerStateAsync("Ptqxpone", p => checked((int)p.Guid.Low));
        (byte levelBefore, uint next) = await host.PlayerStateAsync("Ptqxpone", p => (p.Level, p.GetUInt32(UpdateFields.PlayerNextLevelXp)));
        Assert.Equal((byte)1, levelBefore);
        Assert.InRange(next, 251u, 500u); // the precondition that makes the halved grant stay on level 1 and the full one cross it
        if (partial)
        {
            await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Ptqxpone")!.Flags |= PlayerFlags.PartialPlayTime);
        }

        await client.SendAsync(WorldOpcode.CmsgQuestgiverChooseReward, ChooseRewardBody());
        await client.ReadUntilAsync(WorldOpcode.SmsgQuestgiverQuestComplete);
        await feature.WaitForSettlementAsync(id).WaitAsync(TimeSpan.FromSeconds(30));

        CharacterQuestRewardRequest commit = Assert.Single(fixture.RewardCommits);
        (byte liveLevel, uint liveXp) = await host.PlayerStateAsync("Ptqxpone", p => (p.Level, PlayerProgression.CurrentXp(p)));
        Assert.Equal(commit.After.Level, liveLevel); // the committed level is the live level
        if (partial)
        {
            Assert.Equal((byte)1, liveLevel);
            Assert.Equal(250u, liveXp);
        }
        else
        {
            Assert.Equal((byte)2, liveLevel);
        }
    }
}
