using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Npc.NpcServiceKit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>Trainers over the real spell system (vmangos SendTrainerList, HandleTrainerBuySpellOpcode).</summary>
public sealed class NpcTrainerServiceTests
{
    private const uint TeachStrike = 2001;   // teaches 100 (level 1)
    private const uint TeachSlam = 2002;     // teaches 101 (level 10)
    private const uint TeachStrike2 = 2003;  // teaches 102, rank 2 of 100
    private const uint TeachFireball = 2004; // teaches 103, mages only

    private static readonly SpellInfo[] Spells =
    [
        Teacher(TeachStrike, 100), Learned(100, 1),
        Teacher(TeachSlam, 101), Learned(101, 10),
        Teacher(TeachStrike2, 102), Learned(102, 1),
        Teacher(TeachFireball, 103), Learned(103, 1),
    ];

    private static readonly SkillLineAbilityCatalog Abilities = new(
    [
        new SkillLineAbilityRecord(1, 26, 100, 0, 0, 0, 102, 0, 0, 0),
        new SkillLineAbilityRecord(2, 26, 102, 0, 0, 0, 0, 0, 0, 0),
        new SkillLineAbilityRecord(3, 8, 103, 0, 1u << 7, 0, 0, 0, 0, 0),
    ]);

    private static SpellInfo Teacher(uint id, uint learned)
        => SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.LearnSpell, 0, trigger: learned));

    private static SpellInfo Learned(uint id, uint level) => SpellTestKit.Spell(id) with { SpellLevel = level };

    private sealed class Rig : IDisposable
    {
        public Rig(TrainerType type = TrainerType.Class, byte trainerClass = (byte)Class.Warrior, float distance = 1, float discount = 1)
        {
            System = new SpellSystem(new SpellStore(Spells, [], []), () => 0u, spellbook: Book);
            var learner = new SpellSystemLearner(() => System, Abilities);
            NpcContent content = NpcContent.Empty with
            {
                TrainerSpells =
                [
                    new TrainerSpell { Entry = NpcServiceKit.Entry, Spell = TeachStrike, SpellCost = 10 },
                    new TrainerSpell { Entry = NpcServiceKit.Entry, Spell = TeachSlam, SpellCost = 100 },
                    new TrainerSpell { Entry = NpcServiceKit.Entry, Spell = TeachStrike2, SpellCost = 50 },
                    new TrainerSpell { Entry = NpcServiceKit.Entry, Spell = TeachFireball, SpellCost = 10 },
                ],
            };
            Kit = new NpcServiceKit(NpcFlags.Trainer | NpcFlags.Gossip, content,
                new QuestNpcDependencies(Spells: learner, Reputation: new NpcVendorServiceTests.FixedReputation(discount)),
                npcDistance: distance);
            Kit.Npc = Kit.Npc with { TrainerType = type, TrainerClass = trainerClass };
        }

        public MemorySpellbook Book { get; } = new();

        public SpellSystem System { get; }

        public NpcServiceKit Kit { get; }

        public void Dispose() => Kit.Dispose();
    }

    private static List<(uint Spell, byte State, uint Cost, byte Level, uint Node1)> ReadList(byte[] payload, out uint type)
    {
        var r = new PacketReader(payload);
        r.ReadUInt64();
        type = r.ReadUInt32();
        uint count = r.ReadUInt32();
        var rows = new List<(uint, byte, uint, byte, uint)>();
        for (int i = 0; i < count; i++)
        {
            uint spell = r.ReadUInt32();
            byte state = r.ReadByte();
            uint cost = r.ReadUInt32();
            r.ReadUInt32();
            r.ReadUInt32();
            byte level = r.ReadByte();
            r.ReadUInt32();
            r.ReadUInt32();
            uint node1 = r.ReadUInt32();
            r.ReadUInt32();
            r.ReadUInt32();
            rows.Add((spell, state, cost, level, node1));
        }

        Assert.Equal(NpcPackets.TrainerHello, r.ReadCString());
        Assert.Equal(0, r.Remaining);
        return rows;
    }

    [Fact]
    public void TrainerList_ShowsStatesCostsLevelsAndRankChains_AndHidesOtherClasses()
    {
        using var rig = new Rig(discount: 0.9f);
        rig.Kit.Services.TrainerList(rig.Kit.Player, rig.Kit.Npc.Guid);
        var rows = ReadList(rig.Kit.Single(WorldOpcode.SmsgTrainerList), out uint type);
        Assert.Equal((uint)TrainerType.Class, type);
        Assert.Equal(
        [
            (TeachStrike, (byte)TrainerSpellState.Green, 9u, (byte)1, 0u),
            (TeachSlam, (byte)TrainerSpellState.Red, 90u, (byte)10, 0u),
            (TeachStrike2, (byte)TrainerSpellState.Red, 45u, (byte)1, 100u),
        ], rows);
    }

    [Fact]
    public void TrainerPrice_RoundsHalfCopperUpInListAndPurchase()
    {
        using var rig = new Rig(discount: 0.85f);
        NpcServiceKit kit = rig.Kit;
        kit.Services.TrainerList(kit.Player, kit.Npc.Guid);
        var rows = ReadList(kit.Single(WorldOpcode.SmsgTrainerList), out _);
        Assert.Equal(9u, rows.Single(r => r.Spell == TeachStrike).Cost);

        kit.Player.Money = 9;
        kit.Services.BuyTrainerSpell(kit.Player, kit.Npc.Guid, TeachStrike);
        Assert.True(rig.Book.HasSpell(kit.Player, 100));
        Assert.Equal(0u, kit.Player.Money);
    }

    [Fact]
    public void BuyTrainerSpell_LearnsTheTaughtSpellAndCharges()
    {
        using var rig = new Rig();
        NpcServiceKit kit = rig.Kit;
        kit.Player.Money = 60;
        kit.Services.BuyTrainerSpell(kit.Player, kit.Npc.Guid, TeachStrike);
        Assert.True(rig.Book.HasSpell(kit.Player, 100));
        Assert.Equal(50u, kit.Player.Money);
        var sent = kit.Drain();
        Assert.Contains(sent, p => p.Opcode == WorldOpcode.SmsgLearnedSpell);
        byte[] ok = Assert.Single(sent, p => p.Opcode == WorldOpcode.SmsgTrainerBuySucceeded).Payload;
        Assert.Equal(kit.Npc.Guid.Value, BitConverter.ToUInt64(ok, 0));
        Assert.Equal(TeachStrike, BitConverter.ToUInt32(ok, 8));

        // Rank 2 now has its prerequisite.
        kit.Services.BuyTrainerSpell(kit.Player, kit.Npc.Guid, TeachStrike2);
        Assert.True(rig.Book.HasSpell(kit.Player, 102));
        Assert.Equal(0u, kit.Player.Money);

        // Known spells turn gray.
        kit.Drain();
        kit.Services.TrainerList(kit.Player, kit.Npc.Guid);
        var rows = ReadList(kit.Single(WorldOpcode.SmsgTrainerList), out _);
        Assert.Equal((byte)TrainerSpellState.Gray, rows.Single(r => r.Spell == TeachStrike).State);
        Assert.Equal((byte)TrainerSpellState.Gray, rows.Single(r => r.Spell == TeachStrike2).State);
    }

    private static void AssertFailed(NpcServiceKit kit, uint spell, uint reason)
    {
        byte[] failed = kit.Single(WorldOpcode.SmsgTrainerBuyFailed);
        Assert.Equal(12 + 4, failed.Length);
        Assert.Equal(spell, BitConverter.ToUInt32(failed, 8));
        Assert.Equal(reason, BitConverter.ToUInt32(failed, 12));
    }

    [Fact]
    public void BuyTrainerSpell_Refusals_UseTheTrainingFailureReasons()
    {
        using var rig = new Rig();
        NpcServiceKit kit = rig.Kit;
        kit.Player.Money = 9;
        kit.Services.BuyTrainerSpell(kit.Player, kit.Npc.Guid, TeachStrike);
        AssertFailed(kit, TeachStrike, 1); // not enough money

        kit.Player.Money = 1000;
        kit.Services.BuyTrainerSpell(kit.Player, kit.Npc.Guid, TeachSlam);
        AssertFailed(kit, TeachSlam, 2); // level 10 required
        kit.Services.BuyTrainerSpell(kit.Player, kit.Npc.Guid, TeachStrike2);
        AssertFailed(kit, TeachStrike2, 2); // rank 1 missing
        kit.Services.BuyTrainerSpell(kit.Player, kit.Npc.Guid, TeachFireball);
        AssertFailed(kit, TeachFireball, 2); // wrong class
        kit.Services.BuyTrainerSpell(kit.Player, kit.Npc.Guid, 9999);
        AssertFailed(kit, 9999, 0); // not on the list

        kit.Player.Level = 10;
        kit.Services.BuyTrainerSpell(kit.Player, kit.Npc.Guid, TeachSlam);
        Assert.True(rig.Book.HasSpell(kit.Player, 101));
        Assert.Equal(900u, kit.Player.Money);

        kit.Drain();
        kit.Services.BuyTrainerSpell(kit.Player, kit.Npc.Guid, TeachSlam);
        AssertFailed(kit, TeachSlam, 2); // already known (gray)
        Assert.Equal(900u, kit.Player.Money);
    }

    [Fact]
    public void ClassTrainer_RefusesOtherClasses_WithItsGossipText()
    {
        using var rig = new Rig(trainerClass: (byte)Class.Mage);
        NpcServiceKit kit = rig.Kit;
        kit.Player.Money = 1000;
        kit.Services.TrainerList(kit.Player, kit.Npc.Guid);
        var sent = kit.Drain();
        Assert.DoesNotContain(sent, p => p.Opcode == WorldOpcode.SmsgTrainerList);
        Assert.Contains(sent, p => p.Opcode == WorldOpcode.SmsgGossipMessage);

        kit.Services.BuyTrainerSpell(kit.Player, kit.Npc.Guid, TeachStrike);
        Assert.Contains(kit.Drain(), p => p.Opcode == WorldOpcode.SmsgTrainerBuyFailed);
        Assert.False(rig.Book.HasSpell(kit.Player, 100));
        Assert.Equal(1000u, kit.Player.Money);
    }

    [Fact]
    public void TradeSkillTrainer_TeachesAnyClass()
    {
        using var rig = new Rig(type: TrainerType.TradeSkills, trainerClass: 0);
        NpcServiceKit kit = rig.Kit;
        kit.Player.Money = 100;
        kit.Services.TrainerList(kit.Player, kit.Npc.Guid);
        ReadList(kit.Single(WorldOpcode.SmsgTrainerList), out uint type);
        Assert.Equal((uint)TrainerType.TradeSkills, type);
        kit.Services.BuyTrainerSpell(kit.Player, kit.Npc.Guid, TeachStrike);
        Assert.True(rig.Book.HasSpell(kit.Player, 100));
    }

    [Fact]
    public void Trainer_OutOfRangeOrDead_TeachesNothing()
    {
        using (var far = new Rig(distance: 10))
        {
            far.Kit.Player.Money = 100;
            far.Kit.Services.TrainerList(far.Kit.Player, far.Kit.Npc.Guid);
            Assert.False(far.Kit.Sent(WorldOpcode.SmsgTrainerList));
            far.Kit.Services.BuyTrainerSpell(far.Kit.Player, far.Kit.Npc.Guid, TeachStrike);
            AssertFailed(far.Kit, TeachStrike, 0);
            Assert.False(far.Book.HasSpell(far.Kit.Player, 100));
        }

        using var dead = new Rig();
        dead.Kit.Player.Money = 100;
        dead.Kit.Player.Health = 0;
        dead.Kit.Services.TrainerList(dead.Kit.Player, dead.Kit.Npc.Guid);
        Assert.False(dead.Kit.Sent(WorldOpcode.SmsgTrainerList));
        dead.Kit.Services.BuyTrainerSpell(dead.Kit.Player, dead.Kit.Npc.Guid, TeachStrike);
        Assert.False(dead.Book.HasSpell(dead.Kit.Player, 100));
        Assert.Equal(100u, dead.Kit.Player.Money);
    }

    [Fact]
    public void SpellSystemLearner_DescribesTeachingSpellsFromTheStoreAndDbc()
    {
        using var rig = new Rig();
        var learner = new SpellSystemLearner(() => rig.System, Abilities);
        Assert.Equal(new TrainerSpellInfo(102, 1, 100, 0, false, false, false), learner.DescribeTrainerSpell(TeachStrike2));
        Assert.Equal(101u, learner.DescribeTrainerSpell(TeachSlam)!.LearnedSpell);
        Assert.Null(learner.DescribeTrainerSpell(4242));
        Assert.True(learner.IsSpellFitByClassAndRace(rig.Kit.Player, 100));
        Assert.False(learner.IsSpellFitByClassAndRace(rig.Kit.Player, 103));
        Assert.Null(new SpellSystemLearner(() => null, Abilities).DescribeTrainerSpell(TeachStrike));
    }
}
