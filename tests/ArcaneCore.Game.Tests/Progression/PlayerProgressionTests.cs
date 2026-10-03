using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Progression;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Progression;

public sealed class PlayerProgressionTests
{
    // Synthetic human warrior rows: race,class,level,basehp,basemana,str,agi,sta,int,spi.
    private const string Stats = """
        # comment lines and blanks are ignored

        1,1,1,20,0,23,20,22,20,20
        1,1,2,29,0,24,21,23,20,21
        1,1,3,38,0,25,21,24,20,21
        """;

    [Fact]
    public void LoadedPlayer_GetsNextLevelXpAndLevelBaseValues()
    {
        (Player player, _) = Create();
        PlayerProgression progression = Progression();
        progression.InitializeLoadedPlayer(player, storedXp: 5000);
        Assert.Equal(400u, player.GetUInt32(UpdateFields.PlayerNextLevelXp));
        Assert.Equal(399u, PlayerProgression.CurrentXp(player)); // clamped below the requirement
        Assert.Equal(20u, player.GetUInt32(UpdateFields.UnitFieldBaseHealth));
        Assert.Equal(60u, player.MaxHealth); // 20 base + 40 from 22 stamina (20 × 1 + 2 × 10)
        Assert.Equal(60u, player.Health);
        Assert.Equal(23u, player.GetUInt32(UpdateFields.UnitFieldStat0));
        Assert.Equal(22u, player.GetUInt32(UpdateFields.UnitFieldStat0 + 2));
    }

    [Fact]
    public void NonKillXp_LevelsUpWithLogAndLevelUpPackets_AndRecalculatesStats()
    {
        (Player player, FakeSession session) = Create();
        PlayerProgression progression = Progression();
        progression.InitializeLoadedPlayer(player);
        int levelChanges = 0;
        progression.LevelChanged += _ => levelChanges++;
        session.Clear();

        Assert.Equal(450u, progression.GiveXp(player, 450, ObjectGuid.Empty));
        Assert.Equal(2, player.Level);
        Assert.Equal(50u, PlayerProgression.CurrentXp(player));
        Assert.Equal(900u, player.GetUInt32(UpdateFields.PlayerNextLevelXp));
        Assert.Equal(1, levelChanges);

        (WorldOpcode opcode, byte[] log) = session.Next();
        Assert.Equal(WorldOpcode.SmsgLogXpgain, opcode);
        var reader = new PacketReader(log);
        Assert.Equal(0ul, reader.ReadUInt64());
        Assert.Equal(450u, reader.ReadUInt32());
        Assert.Equal(1, reader.ReadByte());
        Assert.Equal(0, reader.Remaining);

        (opcode, byte[] levelUp) = session.Next();
        Assert.Equal(WorldOpcode.SmsgLevelupInfo, opcode);
        Assert.Equal(48, levelUp.Length);
        reader = new PacketReader(levelUp);
        Assert.Equal(2u, reader.ReadUInt32());
        Assert.Equal(19u, reader.ReadUInt32()); // 9 base health + 10 from stamina 22 → 23
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(0u, reader.ReadUInt32()); // warriors gain no mana
        }

        uint[] gains = new uint[5];
        for (int i = 0; i < gains.Length; i++)
        {
            gains[i] = reader.ReadUInt32();
        }

        Assert.Equal([1u, 1u, 1u, 0u, 1u], gains);
        Assert.Equal(79u, player.MaxHealth);
        Assert.Equal(79u, player.Health);
        Assert.Equal(24u, player.GetUInt32(UpdateFields.UnitFieldStat0));
    }

    [Fact]
    public void LargeGain_CrossesSeveralLevels_AndStopsAtTheMaximum()
    {
        (Player player, FakeSession session) = Create();
        PlayerProgression progression = Progression(maxLevel: 3);
        progression.InitializeLoadedPlayer(player);
        Assert.Equal((2, 600u), progression.Preview(1, 0, 1000));
        Assert.Equal((3, 0u), progression.Preview(1, 0, 100000));
        session.Clear();

        progression.GiveXp(player, 2000);
        Assert.Equal(3, player.Level);
        Assert.Equal(0u, PlayerProgression.CurrentXp(player));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerNextLevelXp));
        Assert.Equal(2, session.Sent.Count(p => p.Opcode == WorldOpcode.SmsgLevelupInfo));

        session.Clear();
        Assert.Equal(0u, progression.GiveXp(player, 500, ObjectGuid.Empty));
        Assert.Empty(session.Sent);
        Assert.Equal(3, player.Level);
    }

    [Fact]
    public void PreviewMatchesTheLiveGrant()
    {
        (Player player, _) = Create();
        PlayerProgression progression = Progression();
        progression.InitializeLoadedPlayer(player, 300);
        (byte level, uint xp) = progression.Preview(player.Level, PlayerProgression.CurrentXp(player), 1234);
        progression.GiveXp(player, 1234);
        Assert.Equal(level, player.Level);
        Assert.Equal(xp, PlayerProgression.CurrentXp(player));
    }

    [Fact]
    public void DeadOrSettlementHeldPlayers_GainNothing()
    {
        (Player player, FakeSession session) = Create();
        PlayerProgression progression = Progression();
        progression.InitializeLoadedPlayer(player);
        session.Clear();
        Guid operation = Guid.NewGuid();
        Assert.True(player.BeginQuestSettlement(operation));
        Assert.Equal(0u, progression.GiveXp(player, 100, ObjectGuid.Empty));
        Assert.True(player.EndQuestSettlement(operation));
        player.Health = 0;
        Assert.Equal(0u, progression.GiveXp(player, 100, ObjectGuid.Empty));
        Assert.Equal(0u, progression.GiveXp(player, 0, ObjectGuid.Empty));
        Assert.Empty(session.Sent);
        Assert.Equal(0u, PlayerProgression.CurrentXp(player));
    }

    [Fact]
    public void KillXp_ConsumesRestedBonus_AndReportsTheVictim()
    {
        (Player player, FakeSession session) = Create();
        PlayerProgression progression = Progression();
        progression.InitializeLoadedPlayer(player);
        progression.SetRestBonus(player, 80);
        Assert.Equal(PlayerProgression.RestStateRested, player.GetByte(UpdateFields.PlayerBytes2, 3));
        session.Clear();

        var victim = new ObjectGuid(0xF130000000000042);
        Assert.Equal(100u, progression.GiveXp(player, 50, victim));
        Assert.Equal(30f, progression.RestBonus(player));
        Assert.Equal(100u, PlayerProgression.CurrentXp(player));
        var reader = new PacketReader(session.Next().Payload);
        Assert.Equal(victim.Value, reader.ReadUInt64());
        Assert.Equal(100u, reader.ReadUInt32());
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(50u, reader.ReadUInt32());
        Assert.Equal(1.0f, reader.ReadSingle());
        Assert.Equal(0, reader.Remaining);

        // Non-kill XP never uses the rested pool.
        Assert.Equal(10u, progression.GiveXp(player, 10, ObjectGuid.Empty));
        Assert.Equal(30f, progression.RestBonus(player));
    }

    [Fact]
    public void RestBonus_IsClampedToOneAndAHalfLevels()
    {
        (Player player, _) = Create();
        PlayerProgression progression = Progression();
        progression.InitializeLoadedPlayer(player);
        progression.SetRestBonus(player, 1_000_000);
        Assert.Equal(300f, progression.RestBonus(player)); // 400 × 1.5 / 2
        progression.SetRestBonus(player, float.NaN);
        Assert.Equal(0f, progression.RestBonus(player));
        Assert.Equal(PlayerProgression.RestStateNormal, player.GetByte(UpdateFields.PlayerBytes2, 3));
    }

    [Fact]
    public void WithoutLevelStats_LevelUpsStillChangeLevelAndXp()
    {
        (Player player, FakeSession session) = Create();
        var progression = new PlayerProgression(new ProgressionOptions());
        progression.InitializeLoadedPlayer(player);
        uint maxHealth = player.MaxHealth;
        progression.GiveXp(player, 400);
        Assert.Equal(2, player.Level);
        Assert.Equal(maxHealth, player.MaxHealth);
        var reader = new PacketReader(session.Sent.Single(p => p.Opcode == WorldOpcode.SmsgLevelupInfo).Payload);
        Assert.Equal(2u, reader.ReadUInt32());
        for (int i = 0; i < 11; i++)
        {
            Assert.Equal(0u, reader.ReadUInt32());
        }
    }

    [Fact]
    public void LevelStatsTable_FailsClosedOnMalformedOrDuplicateRows()
    {
        Assert.Equal(3, PlayerLevelStatsTable.Parse(new StringReader(Stats)).Count);
        Assert.Throws<FormatException>(() => PlayerLevelStatsTable.Parse(new StringReader("1,1,1,20,0,23,20,22,20")));
        Assert.Throws<FormatException>(() => PlayerLevelStatsTable.Parse(new StringReader("1,1,0,20,0,23,20,22,20,20")));
        Assert.Throws<FormatException>(() => PlayerLevelStatsTable.Parse(new StringReader("1,1,1,20,0,23,20,22,20,x")));
        Assert.Throws<FormatException>(() => PlayerLevelStatsTable.Parse(new StringReader("1,1,1,20,0,23,20,22,20,20\n1,1,1,20,0,23,20,22,20,20")));
        Assert.ThrowsAny<Exception>(() => PlayerLevelStatsTable.Load("/nonexistent/levelstats.csv"));
    }

    [Fact]
    public void Packets_HaveTheBuild5875Layouts()
    {
        Assert.Equal(13, ProgressionPackets.LogXpGain(ObjectGuid.Empty, 10, 0).AsSpan().Length);
        Assert.Equal(21, ProgressionPackets.LogXpGain(new ObjectGuid(1), 10, 5).AsSpan().Length);
        Assert.Equal(48, ProgressionPackets.LevelUpInfo(2, -1, 0, [1, 0, 0, 0, -2]).AsSpan().Length);
        Assert.Throws<ArgumentException>(() => ProgressionPackets.LevelUpInfo(2, 0, 0, [1]));
    }

    internal static (Player Player, FakeSession Session) Create(byte level = 1)
    {
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        player.Level = level;
        return (player, session);
    }

    internal static PlayerProgression Progression(uint maxLevel = 60)
        => new(new ProgressionOptions { MaxPlayerLevel = maxLevel }, PlayerLevelStatsTable.Parse(new StringReader(Stats)));
}
