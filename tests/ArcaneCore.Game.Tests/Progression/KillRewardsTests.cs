using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Game.Tests.Reputation;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Combat;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Progression;

public sealed class KillRewardsTests
{
    [Fact]
    public void SoloActor_IsTheOnlyRecipient_WhateverTheDistance()
    {
        using var kit = new Kit();
        Player solo = kit.AddPlayer(1, 500, 0);
        Assert.Equal([solo], KillRewards.Recipients(solo, kit.Victim, null, 74));
        Assert.Equal([solo], KillRewards.Recipients(solo, kit.Victim, new RewardGroup([new ObjectGuid(99)], false), 74));
    }

    [Fact]
    public void GroupRecipients_AreOnlineMembersOnTheMapWithinTheRewardDistance()
    {
        using var kit = new Kit();
        Player killer = kit.AddPlayer(1, 10, 0);
        Player near = kit.AddPlayer(2, 0, 70);
        Player far = kit.AddPlayer(3, 80, 0);
        Player otherMap = kit.AddPlayer(4, 0, 0, mapId: 1);
        var group = new RewardGroup([killer.Guid, near.Guid, far.Guid, otherMap.Guid, new ObjectGuid(5)], false);
        IReadOnlyList<Player> recipients = KillRewards.Recipients(killer, kit.Victim, group, 74);
        Assert.Equal([killer, near], recipients);
    }

    [Fact]
    public void AMemberOnAHill_70YardsAwayIn2D_IsInRange_Though3DIsOver74()
    {
        // vmangos IsWithinLootXPDist is 2D (Object.cpp:1738-1752): a 40 yd height difference does not push a member out.
        using var kit = new Kit();
        Player killer = kit.AddPlayer(1, 5, 0);
        Player hill = kit.AddPlayer(2, 70, 0);
        hill.Z = kit.Victim.Z + 40f; // 3D distance is about 80.6
        var group = new RewardGroup([killer.Guid, hill.Guid], false);
        Assert.Equal([killer, hill], KillRewards.Recipients(killer, kit.Victim, group, 74));
        Assert.Equal([killer, hill], KillRewards.TapRecipients(killer, kit.Victim, group, 74));
    }

    [Fact]
    public void AMemberExactlyAtTheLimit_IsOut_JustInsideIsIn()
    {
        // The limit is 74 plus both bounding radii and strict (Object.cpp:1738-1752).
        using var kit = new Kit();
        Player killer = kit.AddPlayer(1, 5, 0);
        Player edge = kit.AddPlayer(2, 0, 0);
        float limit = 74f + edge.BoundingRadius + kit.Victim.BoundingRadius;
        var group = new RewardGroup([killer.Guid, edge.Guid], false);
        edge.Relocate(limit - 0.05f, 0, kit.Victim.Z, 0, 0);
        Assert.Equal([killer, edge], KillRewards.Recipients(killer, kit.Victim, group, 74));
        edge.Relocate(limit, 0, kit.Victim.Z, 0, 0);
        Assert.Equal([killer], KillRewards.Recipients(killer, kit.Victim, group, 74));
    }

    [Fact]
    public void ARaidMember_500YardsAway_GetsCredit()
    {
        // Object.cpp:1482-1483: a raid map has no reward distance limit.
        using var kit = new Kit(victimMapId: 409);
        Player killer = kit.AddPlayer(1, 5, 0, mapId: 409);
        Player far = kit.AddPlayer(2, 500, 0, mapId: 409);
        var group = new RewardGroup([killer.Guid, far.Guid], true);
        Assert.Equal([killer, far], KillRewards.Recipients(killer, kit.Victim, group, 74));
    }

    [Fact]
    public void ADeadMember_WhoseCorpseIsInRange_GetsCredit_ALivingFarOneDoesNot()
    {
        // Player::IsAtGroupRewardDistance (Player.cpp:20034-20050): a dead player counts through his corpse.
        using var kit = new Kit();
        Player killer = kit.AddPlayer(1, 5, 0);
        Player ghost = kit.AddPlayer(2, 20, 0);
        Player livingFar = kit.AddPlayer(3, 20, 0);
        ghost.Combat.Corpse = Corpse.CreateFor(ghost, pvpDeath: false);
        livingFar.Combat.Corpse = Corpse.CreateFor(livingFar, pvpDeath: false);
        ghost.Relocate(300, 0, kit.Victim.Z, 0, 0);
        livingFar.Relocate(300, 0, kit.Victim.Z, 0, 0);
        ghost.Combat.DeathState = DeathState.Dead;
        var group = new RewardGroup([killer.Guid, ghost.Guid, livingFar.Guid], false);
        Assert.Equal([killer, ghost], KillRewards.Recipients(killer, kit.Victim, group, 74));
    }

    [Fact]
    public void XpQuestAndReputation_PickTheSameRecipientsAsTheCorpseMoneySplit()
    {
        using var kit = new Kit();
        Player killer = kit.AddPlayer(1, 5, 0);
        Player hill = kit.AddPlayer(2, 70, 0);
        hill.Z = kit.Victim.Z + 40f;
        Player edge = kit.AddPlayer(3, 0, 0);
        edge.Relocate(74f + edge.BoundingRadius + kit.Victim.BoundingRadius, 0, kit.Victim.Z, 0, 0);
        Player far = kit.AddPlayer(4, 80, 0);
        Player ghost = kit.AddPlayer(5, 20, 0);
        ghost.Combat.Corpse = Corpse.CreateFor(ghost, pvpDeath: false);
        ghost.Relocate(300, 0, kit.Victim.Z, 0, 0);
        ghost.Combat.DeathState = DeathState.Dead;
        Player[] members = [killer, hill, edge, far, ghost];

        var groups = new FakeGroups();
        groups.Create(LootMethod.FreeForAll, members);
        var loot = new LootService(new LootContent([], [])) { Groups = groups };
        List<Player> sharers = loot.RecipientsFor(killer, kit.Victim, out _);
        Assert.Equal([killer, hill, ghost], sharers);

        var group = new RewardGroup([.. members.Select(m => m.Guid)], false);
        Assert.Equal(sharers, KillRewards.Recipients(killer, kit.Victim, group, 74));
        Assert.Equal(sharers, KillRewards.TapRecipients(killer, kit.Victim, group, 74));
        var service = new ReputationService(ReputationFixtures.Factions, [], roll: () => 0.0);
        Assert.Equal(sharers, ReputationKillCredit.Award(service, killer, kit.Victim, group, 74));
    }

    [Fact]
    public void GroupKill_SplitsExperience_AndDeadMembersReceiveNone()
    {
        using var kit = new Kit(creatureLevel: 10);
        Player killer = kit.AddPlayer(1, 5, 0, level: 10);
        Player friend = kit.AddPlayer(2, 0, 5, level: 10);
        Player dead = kit.AddPlayer(3, 0, 6, level: 10);
        dead.Health = 0;
        var progression = new PlayerProgression(new ProgressionOptions());
        foreach (Player p in new[] { killer, friend, dead })
        {
            progression.InitializeLoadedPlayer(p);
        }

        IReadOnlyList<Player> recipients = KillRewards.Recipients(killer, kit.Victim,
            new RewardGroup([killer.Guid, friend.Guid, dead.Guid], false), 74);
        Assert.Equal(3, recipients.Count);
        IReadOnlyList<uint> granted = KillRewards.AwardExperience(progression, recipients, kit.Victim, nonRaidDungeon: false);
        Assert.Equal([47u, 47u, 0u], granted);
        Assert.Equal(47u, PlayerProgression.CurrentXp(killer));
        Assert.Equal(0u, PlayerProgression.CurrentXp(dead));
        Assert.True(KillRewards.CanReceiveQuestCredit(dead)); // not released yet
        dead.Flags |= PlayerFlags.Ghost;
        Assert.False(KillRewards.CanReceiveQuestCredit(dead));
        var reader = new PacketReader(kit.Session(1).Sent.Single(p => p.Opcode == WorldOpcode.SmsgLogXpgain).Payload);
        Assert.Equal(kit.Victim.Guid.Value, reader.ReadUInt64());
    }

    [Fact]
    public void NoXpStaticFlag_VictimGrantsNoExperience()
    {
        // vmangos MaNGOS::XP::Gain returns 0 for CREATURE_STATIC_FLAG_NO_XP (0x2; Formulas.h:112).
        using var kit = new Kit(creatureLevel: 10, staticFlags1: 0x2);
        Player killer = kit.AddPlayer(1, 5, 0, level: 10);
        var progression = new PlayerProgression(new ProgressionOptions());
        progression.InitializeLoadedPlayer(killer);

        Assert.Equal([0u], KillRewards.AwardExperience(progression, [killer], kit.Victim, nonRaidDungeon: false));
        Assert.Equal(0u, PlayerProgression.CurrentXp(killer));
    }

    [Fact]
    public void GiveXp_LevelUp_RaisesTheOnLevelChangedScriptHook()
    {
        // vmangos Player::GiveXP levels through GiveLevel for each level gained, so the level hook fires for XP level-ups.
        using var kit = new Kit(creatureLevel: 10);
        Player player = kit.AddPlayer(1, 5, 0, level: 1);
        var hook = new LevelProbe();
        kit.World.Scripts.Register(hook);
        var progression = new PlayerProgression(new ProgressionOptions());
        progression.InitializeLoadedPlayer(player);

        progression.GiveXp(player, player.GetUInt32(UpdateFields.PlayerNextLevelXp), ObjectGuid.Empty);

        Assert.Equal(2, player.Level);
        Assert.Equal([(byte)1], hook.OldLevels);
    }

    private sealed class LevelProbe : ArcaneCore.Game.Scripting.IPlayerHooks
    {
        public List<byte> OldLevels { get; } = [];

        public void OnLevelChanged(Player player, byte oldLevel) => OldLevels.Add(oldLevel);
    }

    [Fact]
    public void EliteVictims_DoubleTheGain_AndTripleAndAHalfInDungeons()
    {
        using var kit = new Kit(creatureLevel: 10, rank: 1);
        Player killer = kit.AddPlayer(1, 5, 0, level: 10);
        var progression = new PlayerProgression(new ProgressionOptions());
        progression.InitializeLoadedPlayer(killer);
        Assert.True(KillRewards.IsElite(kit.Victim));
        Assert.Equal([190u], KillRewards.AwardExperience(progression, [killer], kit.Victim, nonRaidDungeon: false));
        Assert.Equal([238u], KillRewards.AwardExperience(progression, [killer], kit.Victim, nonRaidDungeon: true));
    }

    [Fact]
    public void KillExperience_IsDoubledFromTheRestedPool_UntilThePoolRunsOut()
    {
        using var kit = new Kit(creatureLevel: 10);
        Player killer = kit.AddPlayer(1, 5, 0, level: 10);
        var progression = new PlayerProgression(new ProgressionOptions());
        progression.InitializeLoadedPlayer(killer);
        progression.SetRestBonus(killer, 150);
        Assert.Equal(PlayerProgression.RestStateRested, killer.GetByte(UpdateFields.PlayerBytes2, 3));

        // A kill is worth 95 at equal level: the pool adds as much, up to the whole pool.
        Assert.Equal([190u], KillRewards.AwardExperience(progression, [killer], kit.Victim, nonRaidDungeon: false));
        Assert.Equal(55f, progression.RestBonus(killer));
        Assert.Equal(190u, PlayerProgression.CurrentXp(killer));

        Assert.Equal([150u], KillRewards.AwardExperience(progression, [killer], kit.Victim, nonRaidDungeon: false)); // 95 + the last 55
        Assert.Equal(0f, progression.RestBonus(killer));
        Assert.Equal(PlayerProgression.RestStateNormal, killer.GetByte(UpdateFields.PlayerBytes2, 3));

        Assert.Equal([95u], KillRewards.AwardExperience(progression, [killer], kit.Victim, nonRaidDungeon: false)); // an empty pool adds nothing
        Assert.Equal(190u + 150u + 95u, PlayerProgression.CurrentXp(killer));
    }

    private sealed class Kit : IDisposable
    {
        private readonly Dictionary<uint, FakeSession> _sessions = [];

        private readonly uint _victimMapId;

        public Kit(byte creatureLevel = 10, uint rank = 0, uint staticFlags1 = 0, uint victimMapId = 0)
        {
            _victimMapId = victimMapId;
            WorldMaps.Of(World).Load(new MapContent(
                [
                    new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
                    new MapTemplate(409, 0, MapType.Raid, 0, 40, 7, 0, 0, 0, "Molten Core", ""),
                ], [], [], [], []));
            var template = new CreatureTemplate { Entry = 90, Name = "Victim", Faction = 2, MinLevel = creatureLevel, MaxLevel = creatureLevel, Rank = rank, StaticFlags1 = staticFlags1 };
            Victim = new Creature(900100, template, new CreatureSpawn { Guid = 900100, Entry = 90, MapId = victimMapId, X = 0, Y = 0, Z = 83.5f },
                CreatureContent.Empty, new Random(1));
            Victim.Level = creatureLevel;
        }

        public WorldRuntime World { get; } = TestWorld.CreateRuntime();

        public Creature Victim { get; }

        public FakeSession Session(uint guid) => _sessions[guid];

        public Player AddPlayer(uint guid, float x, float y, uint mapId = 0, byte level = 1)
        {
            var session = new FakeSession((int)guid);
            _sessions[guid] = session;
            Player player = TestWorld.CreatePlayer(guid, x, y, session, mapId);
            player.Level = level;
            World.AddPlayer(player);
            if (Victim.Map is null && mapId == _victimMapId)
            {
                player.Map!.AddObject(Victim);
            }

            return player;
        }

        public void Dispose() => World.Dispose();
    }
}
