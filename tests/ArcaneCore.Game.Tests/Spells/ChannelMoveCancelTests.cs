using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Which channels a player's movement cancels. vmangos Spell::update (Spell.cpp:4027-4057): a spell with HIDE_CHANNEL_BAR is never
/// move-cancelled, and a channel that roots or stuns its own caster survives the move while the caster is rooted (the player may
/// move a little before the root is acknowledged); otherwise a channel with a moving interrupt flag is cancelled.
/// </summary>
public sealed class ChannelMoveCancelTests
{
    private const uint PlainChannel = 900901;
    private const uint HiddenBarChannel = 900902;
    private const uint SelfRootChannel = 900903;
    private const uint SelfStunChannel = 900904;
    private const uint EnemyRootChannel = 900905;
    private const uint HideChannelBar = 0x00002000;

    private static SpellInfo Channel(uint id, SpellEffectInfo effect) => Spell(id, effect) with
    {
        AttributesEx = SpellAttributesEx.IsChanneled,
        ChannelInterruptFlags = SpellAuraInterruptFlags.Moving,
        Duration = new SpellDuration(5000, 0, 5000),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
    };

    private static SpellTestKit NewKit() => new(
        Channel(PlainChannel, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)),
        Channel(HiddenBarChannel, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with { AttributesEx3 = HideChannelBar },
        Channel(SelfRootChannel, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModRoot)),
        Channel(SelfStunChannel, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModStun)),
        Channel(EnemyRootChannel, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)));

    private static bool IsChannelling(SpellTestKit kit, Unit caster, uint spellId)
        => kit.System.GetState(caster.Guid)?.CurrentCast is { State: SpellCastState.Casting } cast && cast.Spell.Id == spellId;

    private static void Walk(SpellTestKit kit, Player caster)
    {
        caster.ApplyMovement(new MovementInfo
        {
            Flags = MovementFlags.Forward, X = caster.X + 3, Y = caster.Y, Z = caster.Z, Orientation = caster.Orientation,
        }, kit.Now);
        kit.Advance(100);
    }

    [Fact]
    public void NormalChannel_IsCancelledByMovement()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, PlainChannel, SpellCastTargets.ForSelf(), triggered: false));
        Assert.True(IsChannelling(kit, caster, PlainChannel));

        Walk(kit, caster);

        Assert.False(IsChannelling(kit, caster, PlainChannel));
    }

    [Fact]
    public void HideChannelBarChannel_IsNotCancelledByMovement()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, HiddenBarChannel, SpellCastTargets.ForSelf(), triggered: false));
        Assert.True(IsChannelling(kit, caster, HiddenBarChannel));

        Walk(kit, caster);

        Assert.True(IsChannelling(kit, caster, HiddenBarChannel));
    }

    [Theory]
    [InlineData(SelfRootChannel)]
    [InlineData(SelfStunChannel)]
    public void SelfRootingChannel_SurvivesMovementWhileTheCasterIsRooted(uint spellId)
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, spellId, SpellCastTargets.ForSelf(), triggered: false));
        caster.SetRooted(true);

        Walk(kit, caster);

        Assert.True(IsChannelling(kit, caster, spellId));
    }

    [Theory]
    [InlineData(SelfRootChannel)]
    [InlineData(SelfStunChannel)]
    public void SelfRootingChannel_IsCancelledByMovement_WhenTheCasterIsNotRooted(uint spellId)
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, spellId, SpellCastTargets.ForSelf(), triggered: false));
        caster.SetRooted(false);

        Walk(kit, caster);

        Assert.False(IsChannelling(kit, caster, spellId));
    }

    [Fact]
    public void ChannelWithoutARootEffect_IsCancelledEvenWhenTheCasterIsRooted()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, EnemyRootChannel, SpellCastTargets.ForSelf(), triggered: false));
        caster.SetRooted(true);

        Walk(kit, caster);

        Assert.False(IsChannelling(kit, caster, EnemyRootChannel));
    }
}
