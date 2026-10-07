using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Casters;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells.Casters;

/// <summary>
/// The creature-type terms of spell damage (vmangos SpellCaster::SpellDamageBonusDone, SpellCaster.cpp:1560-1700): MOD_DAMAGE_DONE_VERSUS (168)
/// multiplies, MOD_DAMAGE_DONE_CREATURE (59) adds a flat amount outside the coefficient, MOD_FLAT_SPELL_DAMAGE_VERSUS (180) adds to the advertised
/// benefit (inside the coefficient), each matched against the victim's creature type mask (a player is humanoid, mask 64); and a
/// MOD_DAMAGE_PERCENT_DONE or MOD_DAMAGE_DONE aura restricted to an item (Wand Specialization) does not touch spells (:1592-1600, :1709-1715).
/// </summary>
public sealed class SpellDamageVersusTests
{
    private const uint FireBolt = 4301;            // 100 fire, 3.0 s cast: coefficient 3000 / 3500
    private const uint HumanoidVersus10 = 4302;    // 168 +10% vs humanoids
    private const uint BeastVersus10 = 4303;       // 168 +10% vs beasts
    private const uint HumanoidCreature12 = 4304;  // 59 +12 vs humanoids
    private const uint HumanoidFlatSpell35 = 4305; // 180 +35 vs humanoids
    private const uint FireDamage35 = 4306;        // 13 +35 fire
    private const uint WandSpecialization = 4307;  // 79 +25% magic, wands only
    private const uint WandDamage35 = 4308;        // 13 +35 fire, wands only

    private sealed class RecordingSink : IDamageSink
    {
        public List<uint> Damage { get; } = [];

        public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic)
        {
            Damage.Add(damage);
            return damage;
        }

        public uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount) => amount;
    }

    private static SpellInfo Aura(uint id, AuraType type, int amount, int mask) => SpellTestKit.Spell(
        id, SpellTestKit.Effect(SpellEffectName.ApplyAura, amount, aura: type, misc: mask)) with
    {
        Duration = new SpellDuration(60000, 0, 60000),
        SpellVisual = 1,
    };

    private static SpellInfo WandOnly(SpellInfo spell) => spell with { EquippedItemClass = 2, EquippedItemSubClassMask = 1 << 19, Attributes = SpellAttributes.Passive };

    private static (SpellTestKit Kit, Player Caster, Player Target, RecordingSink Sink) Setup()
    {
        var kit = new SpellTestKit(
            SpellTestKit.Spell(FireBolt, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 100, SpellImplicitTarget.UnitEnemy)) with
            {
                CastTime = new SpellCastTime(3000, 0, 0),
                School = SpellSchool.Fire,
                DamageClass = SpellDamageClass.Magic,
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
            },
            Aura(HumanoidVersus10, AuraType.ModDamageDoneVersus, 10, 64),
            Aura(BeastVersus10, AuraType.ModDamageDoneVersus, 10, 1),
            Aura(HumanoidCreature12, AuraType.ModDamageDoneCreature, 12, 64),
            Aura(HumanoidFlatSpell35, AuraType.ModFlatSpellDamageVersus, 35, 64),
            Aura(FireDamage35, AuraType.ModDamageDone, 35, 1 << (int)SpellSchool.Fire),
            WandOnly(Aura(WandSpecialization, AuraType.ModDamagePercentDone, 25, 0x7E)),
            WandOnly(Aura(WandDamage35, AuraType.ModDamageDone, 35, 1 << (int)SpellSchool.Fire)));
        CasterSpellModules.Register(kit.System, null);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 10, 0);
        var sink = new RecordingSink();
        kit.System.Damage = sink;
        return (kit, caster, target, sink);
    }

    private static void Self(SpellTestKit kit, Unit caster, uint spell) => kit.System.CastSpell(caster, spell, SpellCastTargets.ForSelf(), triggered: true);

    private static uint Bolt(SpellTestKit kit, Player caster, Player target, RecordingSink sink)
    {
        sink.Damage.Clear();
        kit.System.CastSpell(caster, FireBolt, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        return Assert.Single(sink.Damage);
    }

    [Fact]
    public void DamageDoneVersus_MultipliesASpellAgainstTheMatchingCreatureTypeOnly()
    {
        (SpellTestKit kit, Player caster, Player target, RecordingSink sink) = Setup();
        using SpellTestKit _ = kit;
        Assert.Equal(100u, Bolt(kit, caster, target, sink));

        Self(kit, caster, BeastVersus10);
        Assert.Equal(100u, Bolt(kit, caster, target, sink));

        Self(kit, caster, HumanoidVersus10);
        Assert.Equal(110u, Bolt(kit, caster, target, sink));
    }

    [Fact]
    public void DamageDoneCreature_AddsAFlatAmountOutsideTheCoefficient()
    {
        (SpellTestKit kit, Player caster, Player target, RecordingSink sink) = Setup();
        using SpellTestKit _ = kit;
        Self(kit, caster, HumanoidCreature12);

        Assert.Equal(112u, Bolt(kit, caster, target, sink));
    }

    [Fact]
    public void FlatSpellDamageVersus_IsAdvertisedBenefitLikeSpellPower()
    {
        (SpellTestKit kit, Player caster, Player target, RecordingSink sink) = Setup();
        using SpellTestKit _ = kit;
        Self(kit, caster, FireDamage35);
        uint withSpellPower = Bolt(kit, caster, target, sink);
        kit.System.RemoveAuras(caster, FireDamage35);

        Self(kit, caster, HumanoidFlatSpell35);

        Assert.Equal(130u, withSpellPower);                          // 100 + 35 * 3000 / 3500
        Assert.Equal(withSpellPower, Bolt(kit, caster, target, sink));
    }

    [Fact]
    public void ItemRestrictedDamageAuras_DoNotBoostSpells()
    {
        (SpellTestKit kit, Player caster, Player target, RecordingSink sink) = Setup();
        using SpellTestKit _ = kit;
        Self(kit, caster, WandSpecialization);
        Self(kit, caster, WandDamage35);

        Assert.Equal(100u, Bolt(kit, caster, target, sink));
    }
}
