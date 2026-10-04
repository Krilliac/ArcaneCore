using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Casters;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;
using static ArcaneCore.Game.Tests.SpellMods.ModTestSupport;

namespace ArcaneCore.Game.Tests.SpellMods;

/// <summary>
/// The mod sites the cast-pipeline and amount tests do not reach: the party area-aura radius (vmangos AreaAura::AreaAura,
/// SpellAuras.cpp:420-422), the random-near-caster radius (Spell.cpp:2058-2062), and MULTIPLE_VALUE on the periodic health
/// leech (SpellAuras.cpp:6008) and mana leech (:6171-6175) ticks. Each test pins the unmodified value next to the modified one.
/// </summary>
public sealed class AuraAndTargetingModTests
{
    private const uint PartyBuff = 944001;
    private const uint RadiusMod = 944002;
    private const uint RandomBlast = 944003;
    private const uint DrainLife = 944004;
    private const uint DrainMana = 944005;
    private const uint MultiplyMod = 944006;

    private sealed class RecordingSink : IDamageSink
    {
        public List<uint> Healing { get; } = [];

        public uint DealSpellDamage(Unit caster, Unit victim, SpellInfo spell, uint damage, bool periodic) => damage;

        public uint Heal(Unit caster, Unit target, SpellInfo spell, uint amount)
        {
            Healing.Add(amount);
            return amount;
        }
    }

    private static SpellInfo Drain(uint id, AuraType aura) => InFamily(Spell(
        id, Effect(SpellEffectName.ApplyAura, aura == AuraType.PeriodicLeech ? 8 : 10, SpellImplicitTarget.UnitEnemy, aura, amplitude: 1000)
            with { MultipleValue = 0.5f }) with
    {
        AttributesEx = SpellAttributesEx.IsChanneled,
        Duration = new SpellDuration(5000, 0, 5000),
        School = SpellSchool.Shadow,
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    });

    private static SpellTestKit Kit()
    {
        var kit = new SpellTestKit(
            InFamily(Spell(PartyBuff, Effect(SpellEffectName.ApplyAreaAuraParty, 0, aura: AuraType.Dummy) with { Radius = 10 }) with
            {
                Duration = new SpellDuration(-1, 0, -1),
                SpellVisual = 1,
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            }),
            Pct(RadiusMod, SpellModOp.Radius, 25),
            InFamily(Spell(RandomBlast, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemyNearCaster) with { Radius = 8 }) with
            {
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            }),
            Drain(DrainLife, AuraType.PeriodicLeech),
            Drain(DrainMana, AuraType.PeriodicManaLeech),
            Pct(MultiplyMod, SpellModOp.MultipleValue, 100));
        CasterSpellModules.Register(kit.System, null);
        return kit;
    }

    [Fact]
    public void PartyAreaAura_RadiusPctMod_WidensTheAuraRadius()
    {
        using SpellTestKit kit = Kit();
        var groups = new FakeGroups();
        kit.System.Groups = groups;
        (Player caster, _) = kit.AddPlayer(1);
        (Player member, _) = kit.AddPlayer(2, 12, 0);
        groups.Parties.Add([caster.Guid, member.Guid]);

        kit.System.CastSpell(caster, PartyBuff, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(100);
        Assert.Empty(kit.System.GetAuras(member));   // radius 10 < distance 12

        kit.System.LearnSpell(caster, RadiusMod);
        kit.Advance(100);
        Assert.Single(kit.System.GetAuras(member));  // radius 10 * 1.25 = 12.5 >= 12
    }

    [Fact]
    public void RandomNearCaster_RadiusPctMod_WidensTheSearch()
    {
        using SpellTestKit kit = Kit();
        var relations = new FakeRelations();
        kit.System.Relations = relations;
        WorldCollision.Of(kit.World).Install(new FakeLineOfSight());
        (Player caster, _) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 9.5f, 0);
        relations.Hostile.Add(enemy.Guid);
        kit.World.RunTick(0);

        kit.System.CastSpell(caster, RandomBlast, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(enemy.MaxHealth, enemy.Health);   // radius 8 < distance 9.5

        kit.System.LearnSpell(caster, RadiusMod);
        kit.System.CastSpell(caster, RandomBlast, SpellCastTargets.ForSelf(), triggered: true);
        Assert.True(enemy.Health < enemy.MaxHealth);   // radius 8 * 1.25 = 10
    }

    [Fact]
    public void PeriodicHealthLeech_MultipleValueMod_ScalesTheTickHeal()
    {
        using SpellTestKit kit = Kit();
        CasterSpellModules.Register(kit.System);
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 10, 0);
        var sink = new RecordingSink();
        kit.System.Damage = sink;
        kit.Spellbook.Teach(caster, DrainLife);

        kit.System.HandleCastRequest(caster, DrainLife, SpellCastTargets.ForUnit(target.Guid));
        kit.Advance(1000);
        kit.System.LearnSpell(caster, MultiplyMod);
        kit.Advance(1000);

        // Damage 8 per tick, multiple 0.5 -> heals 4; with +100% the multiple is 1.0 -> heals 8.
        Assert.Equal([4u, 8u], sink.Healing);
    }

    [Fact]
    public void PeriodicManaLeech_MultipleValueMod_ScalesTheManaGain()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 10, 0);
        target.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        target.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);
        target.SetUInt32(UpdateFields.UnitFieldPower1, 100);
        caster.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);
        caster.SetUInt32(UpdateFields.UnitFieldPower1, 0);
        kit.Spellbook.Teach(caster, DrainMana);

        kit.System.HandleCastRequest(caster, DrainMana, SpellCastTargets.ForUnit(target.Guid));
        kit.Advance(1000);
        Assert.Equal(5u, SpellSystem.GetPower(caster, PowerType.Mana));   // 10 drained * 0.5

        kit.System.LearnSpell(caster, MultiplyMod);
        kit.Advance(1000);
        Assert.Equal(15u, SpellSystem.GetPower(caster, PowerType.Mana));  // + 10 drained * 1.0
    }
}
