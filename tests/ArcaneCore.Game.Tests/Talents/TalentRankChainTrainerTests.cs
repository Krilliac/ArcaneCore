using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Talents;
using ArcaneCore.Game.Tests.Npc;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Talents;

/// <summary>
/// Trainer-sold higher ranks of single-rank talent abilities (Pyroblast 11366 -> 12505 -> 12522) have forward_spellid 0 in
/// SkillLineAbility.dbc; the reference gets the link from spell_chain prev_spell, without which a trainer sells the rank to a
/// player who lacks the talent and a respec leaves the purchased rank behind.
/// </summary>
public sealed class TalentRankChainTrainerTests
{
    private const uint Pyroblast1 = 11366;
    private const uint Pyroblast2 = 12505;
    private const uint Pyroblast3 = 12522;

    private static SkillLineAbilityRecord[] Rows(params uint[] spells) =>
        [.. spells.Select((s, i) => new SkillLineAbilityRecord((uint)(i + 1), 8, s, 0, 0, 0, 0, 0, 0, 0))];

    [Fact]
    public void SkillLineCatalogLinksTalentAbilityRanksTheDbcLacks()
    {
        var catalog = new SkillLineAbilityCatalog(Rows(Pyroblast1, Pyroblast2, Pyroblast3));

        Assert.Equal(Pyroblast1, catalog.PreviousRank(Pyroblast2));
        Assert.Equal(Pyroblast2, catalog.PreviousRank(Pyroblast3));
        Assert.Equal(0u, catalog.PreviousRank(Pyroblast1));
    }

    [Fact]
    public void RankChainsLinkTalentAbilityRanksForTheTrainerState()
    {
        var chains = new SpellRankChains(Rows(Pyroblast1, Pyroblast2, Pyroblast3));

        Assert.Equal(Pyroblast1, chains.Previous(Pyroblast2));
        Assert.Equal(Pyroblast1, chains.First(Pyroblast3));
        Assert.Equal(3, chains.Rank(Pyroblast3));
    }

    [Fact]
    public void RespecRankChainSeesThePurchasedHigherRank()
    {
        var chain = new SkillLineRankChain(new SkillLineAbilityCatalog(Rows(Pyroblast1, Pyroblast2)));

        Assert.Equal(Pyroblast1, chain.PreviousRank(Pyroblast2));
    }

    [Fact]
    public void DbcForwardLinkWinsOverTheSupplement()
    {
        // A DBC row says 12505 follows 99999; the supplement (11366 -> 12505) must not override it.
        var catalog = new SkillLineAbilityCatalog(
        [
            new SkillLineAbilityRecord(1, 8, 99999, 0, 0, 0, Pyroblast2, 0, 0, 0),
            new SkillLineAbilityRecord(2, 8, Pyroblast1, 0, 0, 0, 0, 0, 0, 0),
            new SkillLineAbilityRecord(3, 8, Pyroblast2, 0, 0, 0, 0, 0, 0, 0),
        ]);

        Assert.Equal(99999u, catalog.PreviousRank(Pyroblast2));
    }

    [Fact]
    public void SupplementNeedsBothRanksToBeAbilities()
    {
        var catalog = new SkillLineAbilityCatalog(Rows(Pyroblast2));

        Assert.Equal(0u, catalog.PreviousRank(Pyroblast2));
    }

    private const uint Consecration1 = 26573;
    private const uint Consecration2 = 20116;
    private const uint Consecration5 = 20924;
    private const uint NaturesGrasp2 = 16689;
    private const uint NaturesGrasp3 = 16811;
    private const uint NaturesGrasp6 = 17329;
    private const uint TeachPyroblast2 = 9900;

    [Fact]
    public void ConsecrationAndNaturesGraspRanksLinkTheTalentSpell()
    {
        var catalog = new SkillLineAbilityCatalog(Rows(Consecration1, Consecration2, 20922, 20923, Consecration5));
        Assert.Equal(Consecration1, catalog.PreviousRank(Consecration2));
        Assert.Equal(20923u, catalog.PreviousRank(Consecration5));

        // Rank 3 and later have no DBC link; the DBC only links 16810 -> 16689.
        var grasp = new SpellRankChains(Rows(16810, NaturesGrasp2, NaturesGrasp3, 16812, 16813, NaturesGrasp6));
        Assert.Equal(16810u, grasp.Previous(NaturesGrasp3));
        Assert.Equal(16813u, grasp.Previous(NaturesGrasp6));
    }

    private sealed class TrainerRig : IDisposable
    {
        public TrainerRig()
        {
            SpellInfo[] spells =
            [
                SpellTestKit.Spell(Pyroblast1), SpellTestKit.Spell(Pyroblast2),
                SpellTestKit.Spell(TeachPyroblast2, SpellTestKit.Effect(SpellEffectName.LearnSpell, 1, trigger: Pyroblast2)),
            ];
            var system = new SpellSystem(new SpellStore(spells, [], []), () => 0u, spellbook: Book);
            var learner = new SpellSystemLearner(() => system, new SkillLineAbilityCatalog(Rows(Pyroblast1, Pyroblast2)));
            NpcContent content = NpcContent.Empty with
            {
                TrainerSpells = [new TrainerSpell { Entry = NpcServiceTestKit.Entry, Spell = TeachPyroblast2, SpellCost = 10 }],
            };
            Kit = new NpcServiceTestKit(NpcFlags.Trainer | NpcFlags.Gossip, content,
                new QuestNpcDependencies(Spells: learner, Reputation: new NpcVendorServiceTests.FixedReputation(1f)));
            Kit.Npc = Kit.Npc with { TrainerType = TrainerType.TradeSkills, TrainerClass = 0 };
            Kit.Player.Level = 24;
            Kit.Player.Money = 100;
        }

        public MemorySpellbook Book { get; } = new();

        public NpcServiceTestKit Kit { get; }

        public byte State()
        {
            Kit.Drain();
            Kit.Services.TrainerList(Kit.Player, Kit.Npc.Guid);
            var r = new PacketReader(Kit.Single(WorldOpcode.SmsgTrainerList));
            r.ReadUInt64();
            r.ReadUInt32();
            Assert.Equal(1u, r.ReadUInt32());
            Assert.Equal(TeachPyroblast2, r.ReadUInt32());
            return r.ReadByte();
        }

        public void Dispose() => Kit.Dispose();
    }

    /// <summary>One trainer row teaching the learned spell (through teaching spell 900000 + learned) over a catalog of the ability rows.</summary>
    private sealed class ChainRig : IDisposable
    {
        private readonly uint _teaching;

        public ChainRig(uint learned, params uint[] abilities)
        {
            _teaching = 900000 + learned;
            SpellInfo[] spells =
            [
                .. abilities.Append(learned).Distinct().Select(a => SpellTestKit.Spell(a)),
                SpellTestKit.Spell(_teaching, SpellTestKit.Effect(SpellEffectName.LearnSpell, 1, trigger: learned)),
            ];
            var system = new SpellSystem(new SpellStore(spells, [], []), () => 0u, spellbook: Book);
            var learner = new SpellSystemLearner(() => system, new SkillLineAbilityCatalog(Rows(abilities)));
            NpcContent content = NpcContent.Empty with
            {
                TrainerSpells = [new TrainerSpell { Entry = NpcServiceTestKit.Entry, Spell = _teaching, SpellCost = 10 }],
            };
            Kit = new NpcServiceTestKit(NpcFlags.Trainer | NpcFlags.Gossip, content,
                new QuestNpcDependencies(Spells: learner, Reputation: new NpcVendorServiceTests.FixedReputation(1f)));
            Kit.Npc = Kit.Npc with { TrainerType = TrainerType.TradeSkills, TrainerClass = 0 };
            Kit.Player.Level = 60;
            Kit.Player.Money = 100;
            Learned = learned;
        }

        public uint Learned { get; }

        public MemorySpellbook Book { get; } = new();

        public NpcServiceTestKit Kit { get; }

        public byte State()
        {
            Kit.Drain();
            Kit.Services.TrainerList(Kit.Player, Kit.Npc.Guid);
            var r = new PacketReader(Kit.Single(WorldOpcode.SmsgTrainerList));
            r.ReadUInt64();
            r.ReadUInt32();
            Assert.Equal(1u, r.ReadUInt32());
            Assert.Equal(_teaching, r.ReadUInt32());
            return r.ReadByte();
        }

        public void Know(uint spell) => Book.LearnSpell(Kit.Player, spell);

        public void Buy() => Kit.Services.BuyTrainerSpell(Kit.Player, Kit.Npc.Guid, _teaching);

        public void Dispose() => Kit.Dispose();
    }

    [Theory]
    [InlineData(20116u, 26573u, new[] { 26573u, 20116u, 20922u, 20923u, 20924u })]
    [InlineData(16811u, 16810u, new[] { 16810u, 16689u, 16811u, 16812u, 16813u, 17329u })]
    [InlineData(27681u, 14752u, new[] { 14752u, 14818u, 27681u })]
    [InlineData(17354u, 16190u, new[] { 16190u, 17354u, 17359u })]
    public void TrainerSellsTalentAbilityRankOnlyAfterTheTalentSpell(uint rank, uint required, uint[] abilities)
    {
        using var rig = new ChainRig(rank, abilities);
        Assert.Equal((byte)TrainerSpellState.Red, rig.State());
        rig.Buy();
        Assert.False(rig.Book.HasSpell(rig.Kit.Player, rank));
        Assert.Equal(100u, rig.Kit.Player.Money);

        rig.Know(required);
        Assert.Equal((byte)TrainerSpellState.Green, rig.State());
        rig.Buy();
        Assert.True(rig.Book.HasSpell(rig.Kit.Player, rank));
    }

    [Theory]
    [InlineData(25898u, 20217u)]
    [InlineData(25899u, 20914u)]
    public void TrainerHonoursSpellChainReqSpell(uint spell, uint required)
    {
        using var rig = new ChainRig(spell, spell);
        Assert.Equal(required, SpellChainRequirements.RequiredFor(spell));
        Assert.Equal((byte)TrainerSpellState.Red, rig.State());
        rig.Buy();
        Assert.False(rig.Book.HasSpell(rig.Kit.Player, spell));

        rig.Know(required);
        Assert.Equal((byte)TrainerSpellState.Green, rig.State());
        rig.Buy();
        Assert.True(rig.Book.HasSpell(rig.Kit.Player, spell));
    }

    [Fact]
    public void TrainerRefusesPyroblastRank2WithoutTheTalentSpell()
    {
        using var rig = new TrainerRig();
        Assert.Equal((byte)TrainerSpellState.Red, rig.State());

        rig.Kit.Services.BuyTrainerSpell(rig.Kit.Player, rig.Kit.Npc.Guid, TeachPyroblast2);
        Assert.False(rig.Book.HasSpell(rig.Kit.Player, Pyroblast2));
        Assert.Equal(100u, rig.Kit.Player.Money);

        rig.Book.LearnSpell(rig.Kit.Player, Pyroblast1);
        Assert.Equal((byte)TrainerSpellState.Green, rig.State());
        rig.Kit.Services.BuyTrainerSpell(rig.Kit.Player, rig.Kit.Npc.Guid, TeachPyroblast2);
        Assert.True(rig.Book.HasSpell(rig.Kit.Player, Pyroblast2));
    }

    [Fact]
    public void RespecWithTheRealChainRemovesThePurchasedPyroblastRank()
    {
        using var kit = new SpellTestKit(
        [
            .. TalentRig.Spells(), TalentRig.ActiveSpell(Pyroblast1), TalentRig.ActiveSpell(Pyroblast2),
        ]);
        var session = new FakeSession(1, AccountSecurity.Player);
        var player = TestWorld.CreatePlayer(1, 0, 0, session);
        player.Level = 20;
        kit.World.AddPlayer(player);
        kit.World.RunTick(0);
        player.Money = 100 * TalentRig.Gold;
        TalentCatalog catalog = new(
            [new TalentTabRecord(1, 1u << 0, 0)],
            [new TalentRecord(5, 1, 0, 0, [Pyroblast1, 0, 0, 0, 0], 0, 0, 0)]);
        using var service = new TalentService(catalog, kit.System, new TalentOptions(), () => TalentRig.Now)
        {
            Sink = new RecordingTalentSink(),
            RankChain = new SkillLineRankChain(new SkillLineAbilityCatalog(Rows(Pyroblast1, Pyroblast2))),
            KnownSpells = p => kit.Spellbook.Spells.GetValueOrDefault(p.Guid) ?? [],
        };
        service.InitTalentForLevel(player);
        Assert.True(service.LearnTalent(player, 5, 0));
        kit.Spellbook.Teach(player, Pyroblast2);

        Assert.True(service.ResetTalents(player, noCost: true));

        Assert.False(kit.Spellbook.HasSpell(player, Pyroblast1));
        Assert.False(kit.Spellbook.HasSpell(player, Pyroblast2));
    }
}
