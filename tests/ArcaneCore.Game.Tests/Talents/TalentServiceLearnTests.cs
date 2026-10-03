using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Talents;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Talents;

/// <summary>
/// Learning talents and the free-point field. Reference: vmangos Player.cpp: LearnTalent :20684-20800, AddSpell talent
/// branches :3606-3622 / :3697-3716, UpdateFreeTalentPoints :3217-3247, RemoveSpell :3857.
/// </summary>
public sealed class TalentServiceLearnTests
{
    internal const uint Warrior = 1u << 0;

    // talent 1: three-rank passive (row 0); talent 2: row 1; talent 3: active LEARN_SPELL talent; talent 4: five ranks (tier points)
    internal const uint T1R1 = 3001, T1R2 = 3002, T1R3 = 3003;
    internal const uint T2R1 = 3011, T2R2 = 3012;
    internal const uint T3R1 = 3021, T3Child = 3022;
    internal const uint T4R1 = 3031, T4R2 = 3032, T4R3 = 3033, T4R4 = 3034, T4R5 = 3035;
    internal const uint MageR1 = 3901;

    internal static SpellInfo PassiveSpell(uint id) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
    {
        Attributes = SpellAttributes.Passive,
        Duration = new SpellDuration(-1, 0, -1),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    internal static SpellInfo[] Spells() =>
    [
        PassiveSpell(T1R1), PassiveSpell(T1R2), PassiveSpell(T1R3),
        PassiveSpell(T2R1), PassiveSpell(T2R2),
        Spell(T3R1, Effect(SpellEffectName.LearnSpell, 0, trigger: T3Child)),
        Spell(T3Child, Effect(SpellEffectName.Dummy, 0)),
        PassiveSpell(T4R1), PassiveSpell(T4R2), PassiveSpell(T4R3), PassiveSpell(T4R4), PassiveSpell(T4R5),
        PassiveSpell(MageR1),
    ];

    internal static TalentCatalog Catalog() => new(
        [new TalentTabRecord(1, Warrior, 0), new TalentTabRecord(2, 1u << 7, 1)],
        [
            new TalentRecord(1, 1, 0, 0, [T1R1, T1R2, T1R3, 0, 0], 0, 0, 0),
            new TalentRecord(2, 1, 1, 0, [T2R1, T2R2, 0, 0, 0], 0, 0, 0),
            new TalentRecord(3, 1, 0, 1, [T3R1, 0, 0, 0, 0], 0, 0, 0),
            new TalentRecord(4, 1, 0, 2, [T4R1, T4R2, T4R3, T4R4, T4R5], 0, 0, 0),
            new TalentRecord(9, 2, 0, 0, [MageR1, 0, 0, 0, 0], 0, 0, 0),
        ]);

    private sealed class Rig : IDisposable
    {
        public Rig(AccountSecurity security = AccountSecurity.Player, byte level = 10)
        {
            Kit = new SpellTestKit(Spells());
            Session = new FakeSession(1, security);
            Player = TestWorld.CreatePlayer(1, 0, 0, Session);
            Player.Level = level;
            Kit.World.AddPlayer(Player);
            Kit.World.RunTick(0);
            Session.Clear();
            Service = new TalentService(Catalog(), Kit.System, new TalentOptions(), () => 1_800_000_000);
        }

        public SpellTestKit Kit { get; }

        public FakeSession Session { get; }

        public Player Player { get; }

        public TalentService Service { get; }

        public uint Free => Player.GetUInt32(UpdateFields.PlayerCharacterPoints1);

        public bool Has(uint spell) => Kit.Spellbook.HasSpell(Player, spell);

        public void Dispose()
        {
            Service.Dispose();
            Kit.Dispose();
        }
    }

    [Theory]
    [InlineData(1, 0u)]
    [InlineData(9, 0u)]
    [InlineData(10, 1u)]
    [InlineData(11, 2u)]
    [InlineData(60, 51u)]
    public void InitTalentForLevel_WritesTheFreePointField(byte level, uint expected)
    {
        using var rig = new Rig(level: level);
        rig.Service.InitTalentForLevel(rig.Player);
        Assert.Equal(expected, rig.Free);
    }

    [Fact]
    public void Learning_AFirstRank_AddsTheSpell_AnnouncesIt_AndSpendsThePoint()
    {
        using var rig = new Rig();
        rig.Service.InitTalentForLevel(rig.Player);
        Assert.Equal(1u, rig.Free);

        Assert.True(rig.Service.LearnTalent(rig.Player, 1, 0));

        Assert.True(rig.Has(T1R1));
        Assert.Equal(BitConverter.GetBytes(T1R1), Packets(rig.Session, WorldOpcode.SmsgLearnedSpell).Single());
        Assert.Equal(0u, rig.Free);
        Assert.Equal(1u, rig.Service.UsedPoints(rig.Player));
        Assert.True(rig.Kit.System.HasAura(rig.Player, T1R1));   // a passive talent applies its aura
    }

    [Fact]
    public void Learning_ANextRank_RemovesTheOldRankBeforeAnnouncingTheNewOne()
    {
        using var rig = new Rig(level: 12);
        rig.Service.InitTalentForLevel(rig.Player);
        Assert.True(rig.Service.LearnTalent(rig.Player, 1, 0));
        Assert.Equal(2u, rig.Free);
        rig.Session.Clear();

        Assert.True(rig.Service.LearnTalent(rig.Player, 1, 1));

        Assert.False(rig.Has(T1R1));
        Assert.True(rig.Has(T1R2));
        WorldOpcode[] order = [.. Opcodes(rig.Session).Where(o => o is WorldOpcode.SmsgRemovedSpell or WorldOpcode.SmsgLearnedSpell)];
        Assert.Equal([WorldOpcode.SmsgRemovedSpell, WorldOpcode.SmsgLearnedSpell], order);
        Assert.Equal(BitConverter.GetBytes((ushort)T1R1), Packets(rig.Session, WorldOpcode.SmsgRemovedSpell).Single());
        Assert.Equal(2u, rig.Service.UsedPoints(rig.Player));
        Assert.Equal(1u, rig.Free);                       // exactly one more point was spent
        Assert.False(rig.Kit.System.HasAura(rig.Player, T1R1));
        Assert.True(rig.Kit.System.HasAura(rig.Player, T1R2));
    }

    [Fact]
    public void Learning_ARankJump_ChargesTheDelta()
    {
        using var rig = new Rig(level: 12);
        rig.Service.InitTalentForLevel(rig.Player);

        Assert.True(rig.Service.LearnTalent(rig.Player, 1, 2));   // rank 3 outright: three points

        Assert.Equal(0u, rig.Free);
        Assert.True(rig.Has(T1R3));
        Assert.Equal(3u, rig.Service.UsedPoints(rig.Player));
    }

    [Fact]
    public void Refusals_SendNothingAndChangeNothing()
    {
        using var rig = new Rig(level: 10);
        rig.Service.InitTalentForLevel(rig.Player);

        Assert.False(rig.Service.LearnTalent(rig.Player, 2, 0));    // row 1: needs five points in the tree
        Assert.False(rig.Service.LearnTalent(rig.Player, 9, 0));    // another class's tree
        Assert.False(rig.Service.LearnTalent(rig.Player, 999, 0));  // unknown talent
        Assert.False(rig.Service.LearnTalent(rig.Player, 1, 5));    // rank out of range

        Assert.Empty(rig.Session.Sent);
        Assert.Equal(1u, rig.Free);
        Assert.Equal(0u, rig.Service.UsedPoints(rig.Player));
        Assert.True(rig.Service.LearnTalent(rig.Player, 1, 0));     // positive control: a legal request still works
    }

    [Fact]
    public void TierGate_OpensWithFivePointsInTheTree()
    {
        using var rig = new Rig(level: 16);   // 7 points
        rig.Service.InitTalentForLevel(rig.Player);
        Assert.True(rig.Service.LearnTalent(rig.Player, 4, 3));     // 4 points
        Assert.False(rig.Service.LearnTalent(rig.Player, 2, 0));    // 4 spent: still locked
        Assert.True(rig.Service.LearnTalent(rig.Player, 4, 4));     // rank 5 replaces rank 4: 5 spent
        Assert.True(rig.Service.LearnTalent(rig.Player, 2, 0));
        Assert.Equal(1u, rig.Free);
    }

    [Fact]
    public void AGmLearnOfARankSpell_GetsTheSameReplacementAndAccounting()
    {
        using var rig = new Rig(level: 12);
        rig.Service.InitTalentForLevel(rig.Player);
        Assert.True(rig.Service.LearnTalent(rig.Player, 1, 0));
        rig.Session.Clear();

        Assert.True(rig.Kit.System.LearnSpell(rig.Player, T1R2));   // .learn path: no talent request involved

        Assert.False(rig.Has(T1R1));
        Assert.True(rig.Has(T1R2));
        Assert.Single(Packets(rig.Session, WorldOpcode.SmsgRemovedSpell));
        Assert.Equal(1u, rig.Free);                                  // 3 allowed (level 12) - 2 used
        Assert.Equal(2u, rig.Service.UsedPoints(rig.Player));
    }

    [Fact]
    public void AnActiveTalentWithALearnSpellEffect_IsCastTriggeredOnLearn()
    {
        using var rig = new Rig();
        rig.Service.InitTalentForLevel(rig.Player);

        Assert.True(rig.Service.LearnTalent(rig.Player, 3, 0));

        Assert.True(rig.Has(T3R1));
        Assert.True(rig.Has(T3Child));   // vmangos Player.cpp:3709-3716: CastSpell(this, spellId, true) teaches the ability
    }

    [Fact]
    public void TalentLearned_IsRaisedOncePerSuccessfulLearn()
    {
        using var rig = new Rig();
        rig.Service.InitTalentForLevel(rig.Player);
        int raised = 0;
        rig.Service.TalentLearned += _ => raised++;

        Assert.False(rig.Service.LearnTalent(rig.Player, 2, 0));
        Assert.Equal(0, raised);
        Assert.True(rig.Service.LearnTalent(rig.Player, 1, 0));
        Assert.Equal(1, raised);
    }

    [Fact]
    public void WhileAQuestSettlementIsPending_NothingIsLearned()
    {
        using var rig = new Rig();
        rig.Service.InitTalentForLevel(rig.Player);
        Guid operation = Guid.NewGuid();
        Assert.True(rig.Player.BeginQuestSettlement(operation));

        Assert.False(rig.Service.LearnTalent(rig.Player, 1, 0));
        Assert.False(rig.Has(T1R1));
        Assert.Equal(1u, rig.Free);

        Assert.True(rig.Player.EndQuestSettlement(operation));
        Assert.True(rig.Service.LearnTalent(rig.Player, 1, 0));   // positive control
    }

    [Fact]
    public void LoadNormalisation_KeepsOnlyTheHighestRankAndReportsTheRest()
    {
        using var rig = new Rig(level: 12);
        rig.Kit.Spellbook.Teach(rig.Player, T1R1, T1R2, T2R1, 100);

        IReadOnlyList<uint> dropped = rig.Service.RemoveSupersededRanks(rig.Player);

        Assert.Equal<uint>([T1R1], dropped);
        Assert.False(rig.Has(T1R1));
        Assert.True(rig.Has(T1R2));
        Assert.True(rig.Has(T2R1));
        Assert.True(rig.Has(100));
        Assert.Empty(rig.Session.Sent);                              // silent: the player is not in the client's world yet
        Assert.Empty(rig.Service.RemoveSupersededRanks(rig.Player)); // idempotent
    }

    [Fact]
    public void Overspend_ResetsAPlayersTalents_ButOnlyZeroesAnAdministrators()
    {
        // level 12 allows 3 points; the book holds 4 (talent 1 rank 3 + talent 3 rank 1)
        using var player = new Rig(AccountSecurity.Player, 12);
        player.Kit.Spellbook.Teach(player.Player, T1R3, T3R1, 100);
        player.Service.InitTalentForLevel(player.Player);
        Assert.False(player.Has(T1R3));
        Assert.False(player.Has(T3R1));
        Assert.True(player.Has(100));                // only talent rank spells are taken
        Assert.Equal(3u, player.Free);
        Assert.Equal(0u, player.Service.UsedPoints(player.Player));

        using var admin = new Rig(AccountSecurity.Administrator, 12);
        admin.Kit.Spellbook.Teach(admin.Player, T1R3, T3R1);
        admin.Service.InitTalentForLevel(admin.Player);
        Assert.True(admin.Has(T1R3));                // an administrator keeps the spells
        Assert.True(admin.Has(T3R1));
        Assert.Equal(0u, admin.Free);
    }

    [Fact]
    public void BelowLevelTen_SpentTalentsAreRefunded()
    {
        using var rig = new Rig(level: 9);
        rig.Kit.Spellbook.Teach(rig.Player, T1R1);

        rig.Service.InitTalentForLevel(rig.Player);

        Assert.False(rig.Has(T1R1));
        Assert.Equal(0u, rig.Free);
    }

    [Fact]
    public void AnotherClassesTalentSpells_AreNotTakenByAReset()
    {
        // vmangos Player.cpp:4101-4104: a spell some class knows normally but another class learns as a talent stays put
        using var rig = new Rig(level: 9);
        rig.Kit.Spellbook.Teach(rig.Player, T1R1, MageR1);

        rig.Service.InitTalentForLevel(rig.Player);

        Assert.False(rig.Has(T1R1));
        Assert.True(rig.Has(MageR1));
    }

    [Fact]
    public void WipeConfirmPacket_IsGuidThenCost_TwelveBytes()
    {
        byte[] body = TalentPackets.WipeConfirm(ObjectGuid.WithEntry(HighGuid.Unit, 500, 77), 50000).ToArray();
        Assert.Equal(12, body.Length);
        Assert.Equal(ObjectGuid.WithEntry(HighGuid.Unit, 500, 77).Value, BitConverter.ToUInt64(body, 0));
        Assert.Equal(50000u, BitConverter.ToUInt32(body, 8));
        Assert.Equal(new byte[12], TalentPackets.WipeConfirm(ObjectGuid.Empty, 0).ToArray());
    }
}
