using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Qa.QaSpellKit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// Spells the rotations already cast that the coverage book used to skip: wand Shoot (family 0, a start spell the mage, priest and warlock
/// rotations name, and the spell system auto-repeats) and the single-target heals a healing rotation picks from the book without naming them.
/// An NPC spell that only shares a name stays out.
/// </summary>
public sealed class PlayerbotQaSpellBookRulesTests
{
    private const uint Shoot = 5019, NpcShoot = 6660, LesserHeal = 2050, Rejuvenation = 774, Fireball = 133, NpcFireball = 9053, ArcaneHeal = 9901;

    [Fact]
    public void WandShootAndBookHeals_AreRotationSpells_AndNotGaps()
    {
        // Production rotations: mage/priest/warlock name Shoot; none of them names Lesser Heal or Rejuvenation (HealDirect / HealPeriodic).
        QaSpellCoverageReport report = PlayerbotQaSpellCoverage.Build(System(),
            [
                new TrainerSpell { Entry = 1, Spell = Fireball },
                new TrainerSpell { Entry = 1, Spell = LesserHeal },
                new TrainerSpell { Entry = 1, Spell = Rejuvenation },
                new TrainerSpell { Entry = 1, Spell = ArcaneHeal },
                new TrainerSpell { Entry = 9, Spell = NpcFireball },
                new TrainerSpell { Entry = 9, Spell = NpcShoot },
            ],
            [], QaData.T0);

        // Shoot, Lesser Heal, Rejuvenation, Fireball, Arcane Heal. Arcane Heal is a mage-family heal the mage rotation does not cast.
        // NPC Shoot and the family-0 Fireball stay out. None of the five has a gap.
        Assert.Equal((5, 4, 0), (report.Castable, report.RotationCastable, report.SpellsWithGaps));
        Assert.Empty(report.Gaps);
    }

    [Fact]
    public void AStartShoot_BelongsToTheClassesThatLearnIt()
    {
        AuraType unsupported = AuraSupport.Entries.First(e => e.Level == AuraSupportLevel.Unsupported).Type;
        SpellInfo shoot = Spell(Shoot, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, unsupported)) with
        {
            Name = "Shoot", SpellFamilyName = 0,
        };
        var system = new SpellSystem(new SpellStore([shoot],
            [
                ((byte)Race.Human, (byte)Class.Mage, Shoot),
                ((byte)Race.Human, (byte)Class.Priest, Shoot),
                ((byte)Race.Orc, (byte)Class.Warlock, Shoot),
                ((byte)Race.Human, (byte)Class.Warrior, Shoot), // a warrior starts with it and never names it
            ],
            []), () => 0);

        QaCoverageGap gap = Assert.Single(PlayerbotQaSpellCoverage.Build(system, [], [], QaData.T0).Gaps);

        uint mask = (1u << (int)Class.Mage) | (1u << (int)Class.Priest) | (1u << (int)Class.Warlock);
        Assert.Equal((Shoot, true, mask, QaSpellGaps.Aura), (gap.Spell, gap.Rotation, gap.ClassMask, gap.Code));
    }

    private static SpellSystem System()
    {
        SpellInfo[] spells =
        [
            Spell(Shoot, Effect(SpellEffectName.SchoolDamage, 1, SpellImplicitTarget.UnitEnemy)) with { Name = "Shoot", SpellFamilyName = 0 },
            Spell(NpcShoot, Effect(SpellEffectName.SchoolDamage, 1, SpellImplicitTarget.UnitEnemy)) with { Name = "Shoot", SpellFamilyName = 0 },
            Spell(LesserHeal, Effect(SpellEffectName.Heal, 15, SpellImplicitTarget.UnitFriend)) with { Name = "Lesser Heal", SpellFamilyName = 6, Rank = "Rank 1" },
            Spell(Rejuvenation, new SpellEffectInfo
            {
                Effect = SpellEffectName.ApplyAura, AuraType = AuraType.PeriodicHeal, BasePoints = 4, BaseDice = 1, DieSides = 1,
                TargetA = SpellImplicitTarget.UnitFriend, Amplitude = 3000,
            }) with { Name = "Rejuvenation", SpellFamilyName = 7, Rank = "Rank 1" },
            Spell(Fireball, Effect(SpellEffectName.SchoolDamage, 14, SpellImplicitTarget.UnitEnemy)) with { Name = "Fireball", SpellFamilyName = 3 },
            Spell(NpcFireball, Effect(SpellEffectName.Dummy, 1)) with { Name = "Fireball", SpellFamilyName = 0 },
            Spell(ArcaneHeal, Effect(SpellEffectName.Heal, 10, SpellImplicitTarget.UnitFriend)) with { Name = "Arcane Heal", SpellFamilyName = 3 },
        ];
        (byte Race, byte Class, uint SpellId)[] starts =
        [
            ((byte)Race.Human, (byte)Class.Mage, Shoot),
            ((byte)Race.Human, (byte)Class.Priest, Shoot),
            ((byte)Race.Orc, (byte)Class.Warlock, Shoot),
        ];
        return new SpellSystem(new SpellStore(spells, starts, []), () => 0);
    }
}
