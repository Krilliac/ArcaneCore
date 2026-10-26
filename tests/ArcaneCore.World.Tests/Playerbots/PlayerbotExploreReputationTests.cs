using System.Reflection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Bots;
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

    private static void Set(PlayerbotBrain brain, string field, object? value)
        => typeof(PlayerbotBrain).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(brain, value);

    /// <summary>
    /// The brain's own filters, with the state a search sets (the fields are private; the search itself needs a full atlas): a vendor spawn is
    /// not an eligible explore candidate (<c>IsExploreServiceNpc</c> in <c>ExploreEligible</c>) while an ordinary spawn of the same faction
    /// is, and the faction filter follows the reputation source the search holds (<c>_exploreSearchReactions</c>). The assignment of that
    /// field from the quest feature at the start of a search is not covered here.
    /// </summary>
    [Fact]
    public async Task TheBrainsExploreFilters_SkipAVendorSpawn_AndAskTheReputationSource()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                var vendor = new CreatureTemplate { Entry = 11557, Name = "vendor", Faction = 900012, NpcFlags = (uint)NpcFlags.Vendor, MinLevel = 8, MaxLevel = 8 };
                var beast = new CreatureTemplate { Entry = 11558, Name = "beast", Faction = 900012, MinLevel = 8, MaxLevel = 8 };
                CreatureSpawn At(uint guid, uint entry, float dx) => new() { Guid = guid, Entry = entry, MapId = player.MapId, X = player.X + dx, Y = player.Y, Z = player.Z };
                var content = new CreatureContent([vendor, beast], [At(1, 11557, 30f), At(2, 11558, 40f)], [], [], []);
                var atlas = new PlayerbotExploreAtlas(content);
                var catalog = new FactionTemplateCatalog([Self, Timbermaw]);
                var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
                Set(brain, "_exploreSearchPlayer", player);
                Set(brain, "_exploreSearchContent", content);
                Set(brain, "_exploreSearchAtlas", atlas);
                Set(brain, "_exploreThreatFilter", (Func<uint, bool>)(_ => false));
                Set(brain, "_exploreSearchFactions", catalog);
                Set(brain, "_exploreSearchSelf", Self);
                MethodInfo eligible = typeof(PlayerbotBrain).GetMethod("ExploreEligible", BindingFlags.Instance | BindingFlags.NonPublic)!;
                MethodInfo attackable = typeof(PlayerbotBrain).GetMethod("ExploreAttackableFaction", BindingFlags.Instance | BindingFlags.NonPublic)!;
                bool Eligible(CreatureSpawn spawn) => (bool)eligible.Invoke(brain, [new PlayerbotExploreAtlas.Candidate(spawn, 0, 0)])!;
                bool Attackable() => (bool)attackable.Invoke(brain, [900012u])!;

                Assert.False(Eligible(At(1, 11557, 30f)), "a vendor spawn is not an explore candidate");
                Assert.True(Eligible(At(2, 11558, 40f)), "an ordinary spawn of the same faction is");

                Set(brain, "_exploreSearchReactions", new TrainerReactions { Rank = ReputationRank.Honored });
                Assert.False(Attackable(), "Honored with the faction: not a target");
                Set(brain, "_exploreSearchReactions", new TrainerReactions { Rank = ReputationRank.Neutral });
                Assert.True(Attackable());
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }
}
