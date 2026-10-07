using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Npc.NpcServiceKit;

namespace ArcaneCore.Game.Tests.Npc;

public sealed class TrainerQuoteTests
{
    [Fact]
    public void QuoteUsesDiscountedCostAndHasNoSideEffects()
    {
        using var rig = new QuoteRig(0.9f);
        rig.Kit.Player.Money = 9;
        uint before = rig.Kit.Player.Money;

        ClassTrainerQuote? quote = rig.Kit.Services.GetClassTrainerQuote(rig.Kit.Player, rig.Kit.Npc);

        Assert.Equal(new ClassTrainerQuote(7001, 7002, 9), quote);
        Assert.Equal(before, rig.Kit.Player.Money);
        Assert.Equal(0, rig.Kit.Sink.CharacterChanges);
        Assert.Empty(rig.Kit.Drain());
    }

    [Fact]
    public void QuoteExcludesKnownWrongClassAndPrerequisiteRows()
    {
        using var rig = new QuoteRig(1);
        rig.Book.Teach(rig.Kit.Player, 7002);
        Assert.Null(rig.Kit.Services.GetClassTrainerQuote(rig.Kit.Player, rig.Kit.Npc, 7001));

        rig.Book.ForgetSpell(rig.Kit.Player, 7002);
        Assert.Null(rig.Kit.Services.GetClassTrainerQuote(rig.Kit.Player, rig.Kit.Npc, 7003));

        rig.Kit.Npc = rig.Kit.Npc with { TrainerClass = (byte)Class.Mage };
        Assert.Null(rig.Kit.Services.GetClassTrainerQuote(rig.Kit.Player, rig.Kit.Npc));
    }

    [Fact]
    public void QuoteRequiresLoadedMatchingClassTrainerAndKnownMetadata()
    {
        using var rig = new QuoteRig(1);
        NpcInfo nonTrainer = rig.Kit.Npc with { NpcFlags = NpcFlags.Gossip };
        Assert.Null(rig.Kit.Services.GetClassTrainerQuote(rig.Kit.Player, nonTrainer));
        Assert.Null(rig.Kit.Services.GetClassTrainerQuote(rig.Kit.Player, rig.Kit.Npc with { TrainerType = TrainerType.TradeSkills }));

        var unknown = new NpcInfo(rig.Kit.Npc.Guid, 99999, rig.Kit.Npc.SpawnId, NpcFlags.Trainer,
            rig.Kit.Npc.MapId, rig.Kit.Npc.X, rig.Kit.Npc.Y, rig.Kit.Npc.Z, rig.Kit.Npc.BoundingRadius,
            true, false, false, false, 0, TrainerType.Class, (byte)Class.Warrior);
        Assert.Null(rig.Kit.Services.GetClassTrainerQuote(rig.Kit.Player, unknown));
        rig.Kit.Services.StateOf(rig.Kit.Player)!.Loaded = false;
        Assert.Null(rig.Kit.Services.GetClassTrainerQuote(rig.Kit.Player, rig.Kit.Npc));
    }

    [Fact]
    public void MetadataSnapshotsAreDetachedAndUnknownEntriesReturnNull()
    {
        var source = new NpcTemplateMetadata { Entry = 7, TrainerType = TrainerType.Class, TrainerClass = (byte)Class.Warrior };
        var lookup = new NpcTemplateMetadataLookup(new EmptyLookup(), [source]);
        source.TrainerClass = (byte)Class.Mage;

        NpcTemplateMetadata snapshot = Assert.Single(lookup.TrainerMetadata);
        snapshot.TrainerClass = (byte)Class.Mage;
        Assert.Equal((byte)Class.Warrior, lookup.MetadataFor(7)!.TrainerClass);
        Assert.Null(lookup.MetadataFor(8));
    }

    private sealed class EmptyLookup : ICreatureLookup
    {
        public NpcInfo? Find(ArcaneCore.Game.Entities.Player player, ObjectGuid guid) => null;
    }

    private sealed class QuoteRig : IDisposable
    {
        public QuoteRig(float discount)
        {
            System = new SpellSystem(new SpellStore(Spells, [], []), () => 0u, spellbook: Book);
            var learner = new SpellSystemLearner(() => System, new SkillLineAbilityCatalog([]));
            NpcContent content = NpcContent.Empty with
            {
                TrainerSpells =
                [
                    new TrainerSpell { Entry = Entry, Spell = 7001, SpellCost = 10 },
                    new TrainerSpell { Entry = Entry, Spell = 7003, SpellCost = 10, ReqSkill = 1, ReqSkillValue = 10 },
                ],
            };
            Kit = new NpcServiceKit(NpcFlags.Trainer | NpcFlags.Gossip, content,
                new QuestNpcDependencies(Spells: learner, Reputation: new NpcVendorServiceTests.FixedReputation(discount)));
            Kit.Npc = Kit.Npc with { TrainerClass = (byte)Kit.Player.Class };
            Kit.Player.Money = 100;
        }

        private static SpellInfo[] Spells =>
        [
            SpellTestKit.Spell(7001, SpellTestKit.Effect(SpellEffectName.LearnSpell, 0, trigger: 7002)),
            SpellTestKit.Spell(7002) with { SpellLevel = 1 },
            SpellTestKit.Spell(7003, SpellTestKit.Effect(SpellEffectName.LearnSpell, 0, trigger: 7004)),
            SpellTestKit.Spell(7004) with { SpellLevel = 1 },
        ];

        public MemorySpellbook Book { get; } = new();
        public SpellSystem System { get; }
        public NpcServiceKit Kit { get; }
        public void Dispose() => Kit.Dispose();
    }
}
