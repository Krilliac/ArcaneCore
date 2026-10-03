using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Progression;
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

    private sealed class Kit : IDisposable
    {
        private readonly Dictionary<uint, FakeSession> _sessions = [];

        public Kit(byte creatureLevel = 10, uint rank = 0)
        {
            var template = new CreatureTemplate { Entry = 90, Name = "Victim", Faction = 2, MinLevel = creatureLevel, MaxLevel = creatureLevel, Rank = rank };
            Victim = new Creature(900100, template, new CreatureSpawn { Guid = 900100, Entry = 90, MapId = 0, X = 0, Y = 0, Z = 83.5f },
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
            if (Victim.Map is null && mapId == 0)
            {
                player.Map!.AddObject(Victim);
            }

            return player;
        }

        public void Dispose() => World.Dispose();
    }
}
