using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Npc;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Skills;

/// <summary>
/// Trainers with the skill system active: real skill values, free profession slots, the profession flags of
/// vmangos Player::GetTrainerSpellState and the not-trainable rule of IsSpellFitByClassAndRace.
/// </summary>
public sealed class TrainerSkillsTests
{
    private const uint TeachMining = 9100;     // teaches 2575 (first rank of a primary profession), SKILL_STEP effect for 186
    private const uint TeachMiningTwo = 9101;  // teaches 2576 (rank 2)
    private const uint TeachCooking = 9300;    // teaches 2550 (cooking: secondary, not a primary profession)
    private const uint TeachRiding = 9400;     // teaches 9401, whose skill row is not trainable

    private static readonly SpellInfo[] Spells =
    [
        SpellTestKit.Spell(TeachMining,
            SpellTestKit.Effect(SpellEffectName.LearnSpell, 1, trigger: SkillTestKit.MiningApprentice),
            SpellTestKit.Effect(SpellEffectName.SkillStep, 1, misc: (int)SkillIds.Mining)),
        SpellTestKit.Spell(TeachMiningTwo, SpellTestKit.Effect(SpellEffectName.LearnSpell, 1, trigger: SkillTestKit.MiningJourneyman)),
        SpellTestKit.Spell(SkillTestKit.MiningApprentice, SpellTestKit.Effect(SpellEffectName.TradeSkill, 1), SpellTestKit.Effect(SpellEffectName.Skill, 1, misc: (int)SkillIds.Mining)),
        SpellTestKit.Spell(SkillTestKit.MiningJourneyman, SpellTestKit.Effect(SpellEffectName.TradeSkill, 1), SpellTestKit.Effect(SpellEffectName.Skill, 2, misc: (int)SkillIds.Mining)),
        SpellTestKit.Spell(TeachCooking, SpellTestKit.Effect(SpellEffectName.LearnSpell, 1, trigger: SkillTestKit.CookingSpell)),
        SpellTestKit.Spell(SkillTestKit.CookingSpell, SpellTestKit.Effect(SpellEffectName.TradeSkill, 1), SpellTestKit.Effect(SpellEffectName.Skill, 1, misc: (int)SkillIds.Cooking)),
        SpellTestKit.Spell(TeachRiding, SpellTestKit.Effect(SpellEffectName.LearnSpell, 1, trigger: 9401)),
        SpellTestKit.Spell(9401),
    ];

    private static SkillCatalog Catalog()
    {
        SkillCatalog baseCatalog = SkillTestKit.Catalog();
        var lines = baseCatalog.Lines.ToList();
        lines.Add(new SkillLineRecord(SkillIds.Riding + 1, SkillCategories.Generic, "Hidden", 0));
        var learn = SpellLearnSkillTable.Build(
        [
            new SpellSkillEffect(SkillTestKit.MiningApprentice, 1, (int)SkillIds.Mining, 0, 1),
            new SpellSkillEffect(SkillTestKit.MiningJourneyman, 1, (int)SkillIds.Mining, 1, 1),
            new SpellSkillEffect(SkillTestKit.CookingSpell, 1, (int)SkillIds.Cooking, 0, 1),
        ]);
        return new SkillCatalog(
            lines,
            [
                .. SkillTestKit.Catalog().RaceClassInfos(SkillIds.Mining),
                .. SkillTestKit.Catalog().RaceClassInfos(SkillIds.Cooking),
                .. SkillTestKit.Catalog().RaceClassInfos(SkillIds.Blacksmithing),
                new SkillRaceClassInfoRecord(SkillIds.Riding + 1, uint.MaxValue, uint.MaxValue, SkillRaceClassFlags.NotTrainable, 0, 0),
            ],
            [new SkillTierRecord(21, Enumerable.Repeat(0u, 16).ToArray(), Enumerable.Repeat(75u, 16).ToArray())],
            [
                new SkillLineAbilityRecord(1, SkillIds.Mining, SkillTestKit.MiningApprentice, 0, 0, 0, SkillTestKit.MiningJourneyman, 0, 0, 0),
                new SkillLineAbilityRecord(2, SkillIds.Mining, SkillTestKit.MiningJourneyman, 0, 0, 0, 0, 0, 0, 0),
                new SkillLineAbilityRecord(3, SkillIds.Cooking, SkillTestKit.CookingSpell, 0, 0, 0, 0, 0, 0, 0),
                new SkillLineAbilityRecord(4, SkillIds.Riding + 1, 9401, 0, 0, 0, 0, 0, 0, 0),
            ],
            learn);
    }

    private sealed class Rig : IDisposable
    {
        public Rig(byte freeSlots = 2, params (uint Spell, uint ReqSkill, uint ReqSkillValue)[] rows)
        {
            Catalog = TrainerSkillsTests.Catalog();
            System = new SpellSystem(new SpellStore(Spells, [], []), () => 0u, spellbook: Book);
            var learner = new SpellSystemLearner(() => System, new SkillLineAbilityCatalog([]), () => Catalog);
            Learner = learner;
            NpcContent content = NpcContent.Empty with
            {
                TrainerSpells = [.. rows.Select(r => new TrainerSpell { Entry = NpcServiceKit.Entry, Spell = r.Spell, SpellCost = 10, ReqSkill = r.ReqSkill, ReqSkillValue = r.ReqSkillValue })],
            };
            Kit = new NpcServiceKit(NpcFlags.Trainer | NpcFlags.Gossip, content,
                new QuestNpcDependencies(Spells: learner, Reputation: new NpcVendorServiceTests.FixedReputation(1f)));
            Kit.Npc = Kit.Npc with { TrainerType = TrainerType.TradeSkills, TrainerClass = 0 };
            var host = new FakeSkillSpellHost { Cascade = Kit.Player };
            Skills = new PlayerSkills(Kit.Player, Catalog, new SkillOptions { MaxPrimaryTradeSkill = freeSlots }, host, new ScriptedSkillRandom());
            Kit.Player.AttachSkills(Skills);
            Skills.InitPrimaryProfessions();
        }

        public SkillCatalog Catalog { get; }

        public MemorySpellbook Book { get; } = new();

        public SpellSystem System { get; }

        public SpellSystemLearner Learner { get; }

        public NpcServiceKit Kit { get; }

        public PlayerSkills Skills { get; }

        public void Dispose() => Kit.Dispose();

        public Dictionary<uint, byte> States() => Rows().ToDictionary(p => p.Key, p => p.Value.State);

        /// <summary>Per listed spell: the state byte, the "can learn a primary profession" flag and the "is a primary first rank" flag.</summary>
        public Dictionary<uint, (byte State, uint CanLearn, uint FirstRank)> Rows()
        {
            Kit.Drain();
            Kit.Services.TrainerList(Kit.Player, Kit.Npc.Guid);
            var r = new PacketReader(Kit.Single(WorldOpcode.SmsgTrainerList));
            r.ReadUInt64();
            r.ReadUInt32();
            uint count = r.ReadUInt32();
            var states = new Dictionary<uint, (byte, uint, uint)>();
            for (int i = 0; i < count; i++)
            {
                uint spell = r.ReadUInt32();
                byte state = r.ReadByte();
                r.ReadUInt32();
                uint canLearn = r.ReadUInt32();
                uint firstRank = r.ReadUInt32();
                states[spell] = (state, canLearn, firstRank);
                r.ReadByte();
                r.ReadUInt32();
                r.ReadUInt32();
                r.ReadUInt32();
                r.ReadUInt32();
                r.ReadUInt32();
            }

            return states;
        }
    }

    [Fact]
    public void DescribeTrainerSpell_FlagsAPrimaryProfessionFirstRankFromTheCatalog()
    {
        using var rig = new Rig();
        TrainerSpellInfo first = rig.Learner.DescribeTrainerSpell(TeachMining)!;
        Assert.Equal((SkillTestKit.MiningApprentice, true, true, true), (first.LearnedSpell, first.LearnedIsPrimaryProfessionFirstRank, first.IsPrimaryProfessionLearn, first.TeachesPrimaryProfessionFirstRank));
        Assert.Equal(0u, first.ChainPrev);

        TrainerSpellInfo second = rig.Learner.DescribeTrainerSpell(TeachMiningTwo)!;
        Assert.Equal((SkillTestKit.MiningJourneyman, false, false, false), (second.LearnedSpell, second.LearnedIsPrimaryProfessionFirstRank, second.IsPrimaryProfessionLearn, second.TeachesPrimaryProfessionFirstRank));
        Assert.Equal(SkillTestKit.MiningApprentice, second.ChainPrev);

        // Cooking is a secondary profession: the profession flags stay off, so no slot is ever required.
        TrainerSpellInfo cooking = rig.Learner.DescribeTrainerSpell(TeachCooking)!;
        Assert.False(cooking.IsPrimaryProfessionLearn);
    }

    [Fact]
    public void ListEntry_PrimaryProfessionFirstRank_CannotBeLearnedWithoutAFreeSlot()
    {
        using var rig = new Rig(freeSlots: 2, (TeachMining, 0, 0), (TeachCooking, 0, 0));
        Assert.Equal(((byte)TrainerSpellState.Green, 1u, 1u), rig.Rows()[TeachMining]);

        rig.Skills.FreePrimaryProfessionPoints = 0;
        Dictionary<uint, (byte State, uint CanLearn, uint FirstRank)> states = rig.Rows();
        // GREEN_DISABLED goes out as green with the "can learn a primary profession" flag cleared (NpcPackets.TrainerList).
        Assert.Equal(((byte)TrainerSpellState.Green, 0u, 1u), states[TeachMining]);
    }

    [Fact]
    public void ListState_RequiredSkill_ReadsThePlayersSkillWithPermanentBonus_NotTemporary()
    {
        using var rig = new Rig(2, (TeachMiningTwo, SkillIds.Mining, 50));
        rig.Book.LearnSpell(rig.Kit.Player, SkillTestKit.MiningApprentice);
        rig.Skills.Set(SkillIds.Mining, 40, 75, 1);
        Assert.Equal((byte)TrainerSpellState.Red, rig.States()[TeachMiningTwo]);

        rig.Skills.ModifyBonus(SkillIds.Mining, 10);   // temporary (item) bonus: GetSkillValueBase ignores it
        Assert.Equal((byte)TrainerSpellState.Red, rig.States()[TeachMiningTwo]);

        rig.Skills.ModifyBonus(SkillIds.Mining, 10, permanent: true);
        Assert.Equal((byte)TrainerSpellState.Green, rig.States()[TeachMiningTwo]);
    }

    [Fact]
    public void NotTrainableSkills_AreNotOffered()
    {
        using var rig = new Rig(2, (TeachRiding, 0, 0), (TeachCooking, 0, 0));
        Dictionary<uint, byte> states = rig.States();
        Assert.DoesNotContain(TeachRiding, states.Keys);
        Assert.Contains(TeachCooking, states.Keys);
        Assert.False(rig.Learner.IsSpellFitByClassAndRace(rig.Kit.Player, 9401));
        Assert.True(rig.Learner.IsSpellFitByClassAndRace(rig.Kit.Player, SkillTestKit.CookingSpell));
        Assert.True(rig.Learner.IsSpellFitByClassAndRace(rig.Kit.Player, 123456));   // no ability: fits everyone
    }

    [Fact]
    public void SkillReads_FallBackToZeroWithoutAttachedSkills()
    {
        Player bare = TestWorld.CreatePlayer(9, 0, 0, new FakeSession());
        var learner = new SpellSystemLearner(() => null, new SkillLineAbilityCatalog([]), () => Catalog());
        Assert.Equal((0u, 0u, 0u), (learner.GetSkillValueBase(bare, SkillIds.Mining), learner.GetSkillValue(bare, SkillIds.Mining), learner.GetFreePrimaryProfessionPoints(bare)));
    }
}
