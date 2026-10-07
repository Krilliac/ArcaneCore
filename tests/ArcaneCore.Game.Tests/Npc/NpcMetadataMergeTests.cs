using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Npc.NpcServiceKit;

namespace ArcaneCore.Game.Tests.Npc;

public sealed class NpcMetadataMergeTests
{
    [Fact]
    public void ImportedClassTrainerMetadataUsesTrainerListAcceptanceAndRefusal()
    {
        const uint teacher = 991001;
        const uint learned = 991002;
        SpellInfo[] spells =
        [
            SpellTestKit.Spell(teacher, SpellTestKit.Effect(SpellEffectName.LearnSpell, 0, trigger: learned)),
            SpellTestKit.Spell(learned) with { SpellLevel = 1 },
        ];
        var system = new SpellSystem(new SpellStore(spells, [], []), () => 0u);
        var learner = new SpellSystemLearner(() => system, new SkillLineAbilityCatalog([]));
        NpcContent content = NpcContent.Empty with
        {
            TrainerSpells = [new TrainerSpell { Entry = NpcServiceKit.Entry, Spell = teacher, SpellCost = 10 }],
        };
        using var kit = new NpcServiceKit(NpcFlags.Trainer | NpcFlags.Gossip, content,
            new QuestNpcDependencies(Spells: learner));
        var lookup = new NpcTemplateMetadataLookup(kit.Lookup,
            [new NpcTemplateMetadata { Entry = Entry, TrainerType = TrainerType.Class, TrainerClass = (byte)Class.Warrior }]);
        kit.Npc = lookup.Find(kit.Player, kit.Npc.Guid)!;
        kit.Services.TrainerList(kit.Player, kit.Npc.Guid);
        Assert.Contains(kit.Drain(), packet => packet.Opcode == WorldOpcode.SmsgTrainerList);

        var mageLookup = new NpcTemplateMetadataLookup(kit.Lookup,
            [new NpcTemplateMetadata { Entry = Entry, TrainerType = TrainerType.Class, TrainerClass = (byte)Class.Mage }]);
        kit.Npc = mageLookup.Find(kit.Player, kit.Npc.Guid)!;
        kit.Services.TrainerList(kit.Player, kit.Npc.Guid);
        Assert.DoesNotContain(kit.Drain(), packet => packet.Opcode == WorldOpcode.SmsgTrainerList);
    }
}
