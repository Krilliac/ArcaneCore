using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The SMSG_SPELL_GO gate (vmangos Spell::IsNeedSendToClient, Spell.cpp:7705-7709; SendSpellGo returns before the packet at :4532-4537): a cast is
/// shown when it has a spell visual, is channeled, has a missile speed, or is not triggered; an invisible triggered cast sends nothing, though a
/// miss of it is still reported by SMSG_SPELLLOGMISS.
/// </summary>
public sealed class SpellGoGateTests
{
    private const uint Invisible = 960001;
    private const uint Visible = 960002;
    private const uint Missile = 960003;
    private const uint Channel = 960004;
    private const uint InvisibleBolt = 960005;

    private static SpellTestKit Kit() => new(
        Spell(Invisible, Effect(SpellEffectName.Heal, 20)) with { SpellVisual = 0 },
        Spell(Visible, Effect(SpellEffectName.Heal, 20)) with { SpellVisual = 1 },
        Spell(Missile, Effect(SpellEffectName.Heal, 20)) with { SpellVisual = 0, Speed = 20f },
        Spell(Channel, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            SpellVisual = 0,
            AttributesEx = SpellAttributesEx.IsChanneled,
            Duration = new SpellDuration(5000, 0, 5000),
        },
        Spell(InvisibleBolt, Effect(SpellEffectName.SchoolDamage, 15, SpellImplicitTarget.UnitEnemy)) with
        {
            SpellVisual = 0,
            School = SpellSchool.Fire,
            DamageClass = SpellDamageClass.Magic,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        });

    private static int SpellGoCount(FakeSession session, uint spellId) => session.Sent
        .Where(p => p.Opcode == WorldOpcode.SmsgSpellGo)
        .Count(p =>
        {
            var reader = new PacketReader(p.Payload);
            reader.ReadPackedGuid(); // the cast item or the caster
            reader.ReadPackedGuid(); // the caster
            return reader.ReadUInt32() == spellId;
        });

    [Fact]
    public void AnInvisibleTriggeredCast_SendsNoSpellGo()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, Invisible, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Equal(0, SpellGoCount(session, Invisible));
    }

    [Fact]
    public void ATriggeredCastWithASpellVisual_IsShown()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, Visible, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Equal(1, SpellGoCount(session, Visible));
    }

    [Fact]
    public void ATriggeredCastWithAMissileSpeed_IsShown()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, Missile, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Equal(1, SpellGoCount(session, Missile));
    }

    [Fact]
    public void ATriggeredChannelWithoutAVisual_IsShown()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, Channel, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Equal(1, SpellGoCount(session, Channel));
    }

    [Fact]
    public void ANonTriggeredCastWithoutAVisual_IsShown()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, Invisible, SpellCastTargets.ForSelf(), triggered: false));
        kit.Advance(kit.System.SpellBatchWindowMs); // a non-triggered cast is held to the batch boundary when the run batches (the CI lane)

        Assert.Equal(1, SpellGoCount(session, Invisible));
    }

    [Fact]
    public void AMissOfAHiddenCast_StillSendsSpellLogMiss()
    {
        using SpellTestKit kit = Kit();
        kit.System.CombatRules = new FixedRules { Miss = SpellMissInfo.Resist };
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, InvisibleBolt, SpellCastTargets.ForUnit(target.Guid), triggered: true));

        Assert.Equal(0, SpellGoCount(session, InvisibleBolt));
        byte[] miss = Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgSpelllogmiss).Payload;
        Assert.Equal(InvisibleBolt, new PacketReader(miss).ReadUInt32());
        Assert.Equal((byte)SpellMissInfo.Resist, miss[^1]);
    }
}
