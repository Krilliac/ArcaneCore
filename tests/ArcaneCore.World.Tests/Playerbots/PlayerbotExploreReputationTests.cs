using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Exploring bots judge a creature by the bot's reputation with its faction and leave service NPCs alone. Live 2026-10-25 (issues #1478
/// and #1479): bots walked into the Timbermaw Hold tunnel after its vendors and quest givers, whose faction template alone says "attack".
/// </summary>
public sealed class PlayerbotExploreReputationTests
{
    // Faction 576 is Timbermaw Hold; the template ids are the test's own.
    private static readonly FactionTemplateRecord Self = new(1, 1, 0, 1, 0, 0);
    private static readonly FactionTemplateRecord Timbermaw = new(900012, 576, 0, 8, 0, 1);

    [Theory]
    [InlineData(ReputationRank.Hated, true)]
    [InlineData(ReputationRank.Hostile, true)]
    [InlineData(ReputationRank.Unfriendly, true)]
    [InlineData(ReputationRank.Neutral, true)]
    [InlineData(ReputationRank.Friendly, false)]
    [InlineData(ReputationRank.Honored, false)]
    [InlineData(ReputationRank.Exalted, false)]
    public async Task ExploreAttackable_FollowsTheReputationReaction_NotTheTemplate(ReputationRank rank, bool attackable)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            bool result = await host.OnWorldAsync(() =>
                PlayerbotBrain.ExploreAttackable(session.Player!, Timbermaw, Self, new TrainerReactions { Rank = rank }));
            Assert.Equal(attackable, result);
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task ExploreAttackable_WithoutAReactionSource_FallsBackToTheTemplates()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            // Hostile both ways (each side's hostile mask holds the other's own mask), and a creature whose friendly mask holds the bot's.
            var hostileSelf = new FactionTemplateRecord(1, 1, 0, 1, 0, 8);
            var hostileNpc = new FactionTemplateRecord(900013, 576, 0, 8, 0, 1);
            var friendlyNpc = new FactionTemplateRecord(900014, 576, 0, 8, 1, 0);
            (bool hostile, bool friendly) = await host.OnWorldAsync(() => (
                PlayerbotBrain.ExploreAttackable(session.Player!, hostileNpc, hostileSelf, null),
                PlayerbotBrain.ExploreAttackable(session.Player!, friendlyNpc, Self, null)));
            Assert.True(hostile);
            Assert.False(friendly);
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Theory]
    [InlineData(NpcFlags.Vendor, true)]
    [InlineData(NpcFlags.Vendor | NpcFlags.Repair, true)]
    [InlineData(NpcFlags.QuestGiver, true)]
    [InlineData(NpcFlags.Gossip, true)]
    [InlineData(NpcFlags.Trainer, false)]
    [InlineData(NpcFlags.None, false)]
    public void IsExploreServiceNpc_IsTrueForVendorQuestGiverAndGossipFlags(NpcFlags flags, bool service)
        => Assert.Equal(service, PlayerbotBrain.IsExploreServiceNpc((uint)flags));
}
