using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>The seams of .pet learnspell / .pet unlearnspell (vmangos Pet::LearnSpell, Pet::UnlearnSpell; CharacterCommands.cpp:5258-5296).</summary>
public sealed class PetGmSpellTests
{
    private const uint BeastEntry = 5221;
    private const uint Bite = 923102;
    private const uint Passive = 923110;

    private static PetTestKit Kit() => new(
        extraSpells:
        [
            Spell(Bite, Effect(SpellEffectName.Dummy, 1)),
            Spell(Passive, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            {
                Attributes = SpellAttributes.Passive, Duration = new SpellDuration(-1, 0, -1), StartRecoveryCategory = 0, StartRecoveryTime = 0,
            },
        ],
        extraTemplates:
        [
            CreatureTestKit.Template(BeastEntry, b =>
            {
                b.MinLevel = 20;
                b.MaxLevel = 20;
                b.MinLevelHealth = 100;
                b.MaxLevelHealth = 100;
                b.Faction = 14;
            }) with { CreatureType = SummonService.CreatureTypeBeast, TypeFlags = SummonService.TypeFlagTameable, Family = 1 },
        ]);

    [Fact]
    public void LearnAndUnlearn_ChangeTheLivePet_AndResendItsSpells()
    {
        PetTestKit kit = Kit();
        (Player hunter, FakeSession session) = kit.AddPlayer(1);
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        hunter.Level = 30;
        Creature beast = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(BeastEntry)!, 3, 0, 0, 0);
        Creature pet = Assert.IsType<Creature>(kit.Service.TameCreature(hunter, beast, 1515));
        CharmInfo charm = pet.Summon!.Charm!;
        session.Clear();

        Assert.True(kit.Service.LearnSpellByCommand(pet, Bite));
        Assert.True(charm.HasSpell(Bite));
        Assert.Contains(session.Sent, p => p.Opcode == WorldOpcode.SmsgPetSpells);
        Assert.False(kit.Service.LearnSpellByCommand(pet, Bite));      // already known
        Assert.False(kit.Service.LearnSpellByCommand(pet, 999_999_9)); // no such spell

        Assert.True(kit.Service.LearnSpellByCommand(pet, Passive));
        Assert.True(kit.Spells.System.HasAura(pet, Passive));

        session.Clear();
        Assert.True(kit.Service.UnlearnSpellByCommand(pet, Passive));
        Assert.False(charm.HasSpell(Passive));
        Assert.False(kit.Spells.System.HasAura(pet, Passive));
        Assert.Contains(session.Sent, p => p.Opcode == WorldOpcode.SmsgPetSpells);
        Assert.True(kit.Service.UnlearnSpellByCommand(pet, Bite));
        Assert.False(kit.Service.UnlearnSpellByCommand(pet, Bite));
    }
}