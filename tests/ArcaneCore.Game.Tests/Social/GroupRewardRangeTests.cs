using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>
/// WorldObject::IsWithinLootXPDist / Player::IsAtGroupRewardDistance (vmangos Object.cpp:1478-1499,
/// 1738-1752; Player.cpp:20034-20050), hand-simulated.
/// </summary>
public sealed class GroupRewardRangeTests
{
    private const uint RaidMap = 409;
    private static readonly GroupRewardOptions Retail = new();

    private sealed class Rig
    {
        public required WorldRuntime World { get; init; }
        public Creature Victim { get; set; } = null!;

        public Player Join(uint guid, float x, float y, float z = 83.5f, uint mapId = 0)
        {
            var session = new FakeSession((int)guid);
            Player player = TestWorld.CreatePlayer(guid, x, y, session, mapId);
            player.Z = z;
            World.AddPlayer(player);
            World.RunTick(50);
            return player;
        }
    }

    private static Rig CreateRig(uint rank = 0)
    {
        CreatureContent creatures = Content([Template(WolfEntry, b => b.Rank = rank)], [Spawn(1, WolfEntry, 0, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(creatures);
        WorldMaps.Of(world).Load(new MapContent(
            [
                new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
                new MapTemplate(RaidMap, 0, MapType.Raid, 0, 40, 7, 0, 0, 0, "Molten Core", ""),
            ], [], [], [], []));
        var rig = new Rig { World = world };
        rig.Join(900, 0, 0); // keeps the grid with the wolf loaded
        rig.Victim = system.Creatures.Single();
        return rig;
    }

    private static float Limit(Player p, Creature v, float yards = 74f) => yards + p.BoundingRadius + v.BoundingRadius;

    [Fact]
    public void Distance_IsHorizontalOnly()
    {
        Rig rig = CreateRig();
        Player near = rig.Join(1, 73, 0, z: 183.5f); // 100 yd above the victim: today's 3D rule rejects him
        Assert.True(GroupRewardRange.IsWithinLootXpDist(near, rig.Victim, Retail));
    }

    [Fact]
    public void Limit_IsStrict_AndGrowsByBothBoundingRadii()
    {
        Rig rig = CreateRig();
        Player p = rig.Join(1, 0, 0);
        float limit = Limit(p, rig.Victim);
        p.Relocate(limit - 0.05f, 0, 83.5f, 0, 0);
        Assert.True(GroupRewardRange.IsWithinLootXpDist(p, rig.Victim, Retail));
        p.Relocate(limit, 0, 83.5f, 0, 0);
        Assert.False(GroupRewardRange.IsWithinLootXpDist(p, rig.Victim, Retail)); // strictly less
        Assert.True(limit > 74.0f); // the radii are added to 74
    }

    [Fact]
    public void WorldBoss_Adds150Yards_OtherRanksDoNot()
    {
        Rig boss = CreateRig(rank: 3);
        Player p = boss.Join(1, 200, 0);
        Assert.True(GroupRewardRange.IsWithinLootXpDist(p, boss.Victim, Retail));
        p.Relocate(Limit(p, boss.Victim, 224f) + 1, 0, 83.5f, 0, 0);
        Assert.False(GroupRewardRange.IsWithinLootXpDist(p, boss.Victim, Retail));
        Assert.False(GroupRewardRange.IsWithinLootXpDist(boss.Join(2, 200, 0), boss.Victim, Retail with { BossDistanceBonus = 0 }));

        Rig elite = CreateRig(rank: 1);
        Assert.False(GroupRewardRange.IsWithinLootXpDist(elite.Join(1, 200, 0), elite.Victim, Retail));
    }

    [Fact]
    public void ARaidMap_HasNoLimit_UnlessSwitchedOff()
    {
        Rig rig = CreateRig();
        Player a = rig.Join(1, 0, 0, mapId: RaidMap);
        Player b = rig.Join(2, 500, 0, mapId: RaidMap);
        Assert.True(GroupRewardRange.IsWithinLootXpDist(b, a, Retail));
        Assert.False(GroupRewardRange.IsWithinLootXpDist(b, a, Retail with { RaidMapsUnlimited = false }));
        Assert.False(GroupRewardRange.IsWithinLootXpDist(rig.Join(3, 500, 0), rig.Join(4, 0, 0), Retail)); // a continent has the limit
    }

    [Fact]
    public void ADifferentMap_IsOutOfRange()
    {
        Rig rig = CreateRig();
        Player elsewhere = rig.Join(1, 0, 0, mapId: RaidMap);
        Assert.False(GroupRewardRange.IsWithinLootXpDist(elsewhere, rig.Victim, Retail));
    }

    [Fact]
    public void ADeadPlayer_CountsThroughHisCorpse_ALivingOneDoesNot()
    {
        Rig rig = CreateRig();
        Player p = rig.Join(1, 20, 0);
        p.Combat.Corpse = Corpse.CreateFor(p, pvpDeath: false); // died 20 yd from the victim
        p.Relocate(300, 0, 83.5f, 0, 0); // the ghost is far away
        Assert.False(GroupRewardRange.IsAtGroupRewardDistance(p, rig.Victim, Retail)); // still alive: the corpse does not count

        p.Combat.DeathState = DeathState.Dead;
        Assert.True(GroupRewardRange.IsAtGroupRewardDistance(p, rig.Victim, Retail));
        Assert.False(GroupRewardRange.IsWithinLootXpDist(p, rig.Victim, Retail)); // the ghost itself is not in reach

        p.Combat.Corpse = null;
        Assert.False(GroupRewardRange.IsAtGroupRewardDistance(p, rig.Victim, Retail));
    }

    [Fact]
    public void CorpseRaidHook_IsIgnoredOnAContinent()
    {
        Rig rig = CreateRig();
        Player far = rig.Join(1, 500, 0);
        GroupRewardOptions hook = Retail with { CorpseRaid = _ => true };
        Assert.False(GroupRewardRange.IsWithinLootXpDist(far, rig.Victim, hook)); // continent map: not instanceable
    }
}
