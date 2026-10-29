using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Which channels a player's movement cancels. vmangos Spell::update (Spell.cpp:4027-4057): a spell with HIDE_CHANNEL_BAR is never
/// move-cancelled, Stuck is exempt while falling far, and a channel that roots or stuns its own caster survives the move while the
/// caster has the Root flag or a root change pending (the player may move a little before the root is acknowledged); otherwise a
/// channel with a moving interrupt flag is cancelled.
/// </summary>
public sealed class ChannelMoveCancelTests
{
    private const uint PlainChannel = 900901;
    private const uint HiddenBarChannel = 900902;
    private const uint SelfRootChannel = 900903;
    private const uint SelfStunChannel = 900904;
    private const uint SelfDummyAuraChannel = 900905;
    private const uint EnemyRootChannel = 900906;
    private const uint EnemyStunChannel = 900907;
    private const uint StuckChannel = 900908;
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
        Channel(SelfDummyAuraChannel, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)),
        // The root and the stun are aimed at the channel target, not the caster, so the exemption must not apply.
        Channel(EnemyRootChannel, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModRoot)),
        Channel(EnemyStunChannel, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModStun)),
        Channel(StuckChannel, Effect(SpellEffectName.Stuck, 0, SpellImplicitTarget.UnitCaster)));

    private static bool IsChannelling(SpellTestKit kit, Unit caster, uint spellId)
        => kit.System.GetState(caster.Guid)?.CurrentCast is { State: SpellCastState.Casting } cast && cast.Spell.Id == spellId;

    private static void Walk(SpellTestKit kit, Player caster)
    {
        caster.ApplyMovement(new MovementInfo
        {
            Flags = caster.Movement.Flags | MovementFlags.Forward, X = caster.X + 3, Y = caster.Y, Z = caster.Z, Orientation = caster.Orientation,
        }, kit.Now);
        kit.Advance(100);
    }

    /// <summary>The client acknowledged every order: nothing is pending. The Root flag is whatever the acknowledgements left it.</summary>
    private static void AcknowledgeEverything(Player player)
    {
        foreach (PendingMovementChange change in player.Locomotion.Pending.Changes.ToArray())
        {
            Assert.True(player.Locomotion.Pending.TryAcknowledge(change.Counter, change.Type, change.Apply));
        }
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
    public void SelfRootingChannel_SurvivesMovement_WhileTheRootIsPendingAcknowledgement(uint spellId)
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, spellId, SpellCastTargets.ForSelf(), triggered: false));
        Assert.True(caster.IsRooted, "setup: the channel's own aura roots the caster");
        Assert.True(caster.Locomotion.Pending.HasPendingOfType(MovementChangeType.Root), "setup: the root awaits its acknowledgement");
        Assert.False(caster.Movement.HasFlag(MovementFlags.Root));

        Walk(kit, caster);

        Assert.True(IsChannelling(kit, caster, spellId));
    }

    [Theory]
    [InlineData(SelfRootChannel)]
    [InlineData(SelfStunChannel)]
    public void SelfRootingChannel_SurvivesMovement_OnceTheRootFlagIsSet(uint spellId)
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, spellId, SpellCastTargets.ForSelf(), triggered: false));
        AcknowledgeEverything(caster);
        MovementControl.ApplyReal(caster, MovementChangeType.Root, apply: true);
        Assert.True(caster.Movement.HasFlag(MovementFlags.Root));
        Assert.False(caster.Locomotion.Pending.HasPendingOfType(MovementChangeType.Root));

        Walk(kit, caster);

        Assert.True(IsChannelling(kit, caster, spellId));
    }

    [Theory]
    [InlineData(SelfRootChannel)]
    [InlineData(SelfStunChannel)]
    public void SelfRootingChannel_IsCancelledByMovement_WhenNoRootIsPendingAndTheFlagIsClear(uint spellId)
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, spellId, SpellCastTargets.ForSelf(), triggered: false));
        AcknowledgeEverything(caster); // the acknowledgements arrived without setting the Root flag
        Assert.False(caster.Movement.HasFlag(MovementFlags.Root));
        Assert.False(caster.Locomotion.Pending.HasPendingOfType(MovementChangeType.Root));

        Walk(kit, caster);

        Assert.False(IsChannelling(kit, caster, spellId));
    }

    [Fact]
    public void ChannelWithoutARootEffect_IsCancelledEvenWhenTheCasterIsRooted()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, SelfDummyAuraChannel, SpellCastTargets.ForSelf(), triggered: false));
        caster.SetRooted(true);
        Assert.True(caster.Locomotion.Pending.HasPendingOfType(MovementChangeType.Root));

        Walk(kit, caster);

        Assert.False(IsChannelling(kit, caster, SelfDummyAuraChannel));
    }

    [Theory]
    [InlineData(EnemyRootChannel)]
    [InlineData(EnemyStunChannel)]
    public void RootingChannel_AimedAtAnotherUnit_IsCancelledEvenWhileTheCasterIsRooted(uint spellId)
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, spellId, SpellCastTargets.ForSelf(), triggered: false));
        caster.SetRooted(true); // rooted with a root pending: only the effect's TargetA keeps the exemption from applying
        Assert.True(caster.Locomotion.Pending.HasPendingOfType(MovementChangeType.Root));

        Walk(kit, caster);

        Assert.False(IsChannelling(kit, caster, spellId));
    }

    [Fact]
    public void StuckChannel_SurvivesMovementWhileFallingFar_ButNotOtherwise()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, StuckChannel, SpellCastTargets.ForSelf(), triggered: false));
        Assert.True(IsChannelling(kit, caster, StuckChannel), "setup: the Stuck channel is running");
        caster.ApplyMovement(new MovementInfo
        {
            Flags = MovementFlags.FallingFar, X = caster.X + 3, Y = caster.Y, Z = caster.Z, Orientation = caster.Orientation,
        }, kit.Now);
        kit.Advance(100);
        Assert.True(IsChannelling(kit, caster, StuckChannel));

        caster.ApplyMovement(new MovementInfo
        {
            Flags = MovementFlags.Forward, X = caster.X + 3, Y = caster.Y, Z = caster.Z, Orientation = caster.Orientation,
        }, kit.Now);
        kit.Advance(100);

        Assert.False(IsChannelling(kit, caster, StuckChannel));
    }
}
