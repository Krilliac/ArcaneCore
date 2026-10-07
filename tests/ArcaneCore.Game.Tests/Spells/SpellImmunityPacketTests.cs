using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules.Immunity;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class SpellImmunityPacketTests
{
    private const uint Immunity = 29801;
    private const uint Incoming = 29802;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EffectImmunity_IsReflectedInSpellGoAndSurvivingEffects(bool mixed)
    {
        SpellEffectInfo damage = Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy);
        SpellEffectInfo aura = Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.Dummy);
        SpellInfo incoming = Spell(Incoming, mixed ? [damage, aura] : [damage]) with
        {
            School = SpellSchool.Fire, RangeIndex = 4, Range = new SpellRange(0, 30),
            Duration = new SpellDuration(-1, 0, -1),
        };
        using var kit = new SpellTestKit(RuleTestSupport.Grant(Immunity, AuraType.EffectImmunity, 0, (int)SpellEffectName.SchoolDamage), incoming);
        kit.System.ApplicationRules.Add(new ImmunityApplicationRule());
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 5);
        var relations = new FakeRelations();
        relations.Hostile.Add(target.Guid);
        kit.System.Relations = relations;
        RuleTestSupport.Apply(kit, target, Immunity);
        uint health = target.Health;
        session.Clear();

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Incoming, SpellCastTargets.ForUnit(target.Guid), triggered: true));
        Assert.Equal(health, target.Health);
        Assert.Equal(mixed, kit.System.HasAura(target, Incoming));

        var reader = new PacketReader(Assert.Single(Packets(session, WorldOpcode.SmsgSpellGo)));
        _ = reader.ReadPackedGuid();
        _ = reader.ReadPackedGuid();
        Assert.Equal(Incoming, reader.ReadUInt32());
        _ = reader.ReadUInt16();
        Assert.Equal(mixed ? 1 : 0, reader.ReadByte());
        if (mixed)
        {
            Assert.Equal(target.Guid.Value, reader.ReadUInt64());
            Assert.Equal(0, reader.ReadByte());
        }
        else
        {
            Assert.Equal(1, reader.ReadByte());
            Assert.Equal(target.Guid.Value, reader.ReadUInt64());
            // vmangos Spell::WriteSpellGoTargets reports Immune2 when no effect survives.
            Assert.Equal((byte)SpellMissInfo.Immune2, reader.ReadByte());
        }
    }
}
