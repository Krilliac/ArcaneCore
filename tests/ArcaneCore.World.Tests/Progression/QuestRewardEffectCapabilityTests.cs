using ArcaneCore.Game.Spells;
using ArcaneCore.World.Progression;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Progression;

public sealed class QuestRewardEffectCapabilityTests
{
    [Theory]
    [InlineData(SpellEffectName.LearnSpell)]
    [InlineData(SpellEffectName.CreateItem)]
    public void PermanentRewardNeedsSettlementRecoveryEvenWithARegisteredHandler(SpellEffectName effect)
    {
        SpellSystem system = System(Spell(1, effect));
        system.RegisterEffect(effect, static _ => { });
        Assert.True(system.HasEffectHandler(effect));
        Assert.False(Effects(system).CanCastRewardSpell(1));
    }

    [Fact]
    public void CapabilityUsesTheActiveHandlerRegistryRatherThanAFixedEffectList()
    {
        SpellSystem system = System(Spell(1, SpellEffectName.PowerDrain));
        Assert.False(Effects(system).CanCastRewardSpell(1));
        system.RegisterEffect(SpellEffectName.PowerDrain, static _ => { });
        Assert.True(Effects(system).CanCastRewardSpell(1));
        Assert.False(Effects(system).CanCastRewardSpell(999));
    }

    [Theory]
    [InlineData(SpellEffectName.TeleportUnits)]
    [InlineData(SpellEffectName.Summon)]
    public void DestinationAndSummonOwnerNeedPreflightBeyondHandlerRegistration(SpellEffectName effect)
    {
        SpellSystem system = System(Spell(1, effect));
        system.RegisterEffect(effect, static _ => { });
        Assert.True(system.HasEffectHandler(effect));
        Assert.False(Effects(system).CanCastRewardSpell(1));
    }

    [Fact]
    public void TransientSupportedRewardRemainsAvailable()
    {
        SpellSystem system = System(Spell(1, SpellEffectName.Heal));
        Assert.True(Effects(system).CanCastRewardSpell(1));
    }

    [Fact]
    public void NestedRewardsRequireSupportedTransientEffectsAndRejectCycles()
    {
        SpellSystem system = System(
            Spell(1, SpellEffectName.TriggerSpell, trigger: 2), Spell(2, SpellEffectName.Heal),
            Spell(3, SpellEffectName.TriggerSpell, trigger: 4), Spell(4, SpellEffectName.LearnSpell),
            Spell(5, SpellEffectName.TriggerSpell, trigger: 6), Spell(6, SpellEffectName.TriggerSpell, trigger: 5),
            Spell(7, SpellEffectName.TriggerSpell, trigger: 999),
            Spell(8, SpellEffectName.TriggerMissile, trigger: 4));
        system.RegisterEffect(SpellEffectName.TriggerMissile, static _ => { });
        Assert.True(Effects(system).CanCastRewardSpell(1));
        Assert.False(Effects(system).CanCastRewardSpell(3));
        Assert.False(Effects(system).CanCastRewardSpell(5));
        Assert.False(Effects(system).CanCastRewardSpell(7));
        Assert.False(Effects(system).CanCastRewardSpell(8));
    }

    [Theory]
    [InlineData(SpellEffectName.ApplyAura)]
    [InlineData(SpellEffectName.ApplyAreaAuraParty)]
    public void AuraRequiresItsActiveHandlerAndBoundedLifetime(SpellEffectName effect)
    {
        // A synthetic aura type stays absent as the merged daemon gains built-in handlers.
        AuraType aura = (AuraType)255;
        SpellInfo finite = Spell(1, effect, aura: aura) with
        {
            Duration = new SpellDuration(5_000, 0, 5_000),
        };
        SpellSystem system = System(finite,
            finite with { Id = 2, Duration = new SpellDuration(-1, 0, -1) },
            finite with { Id = 3, Attributes = SpellAttributes.Passive });
        Assert.False(Effects(system).CanCastRewardSpell(1));
        system.RegisterAura(aura, new AuraHandler(null, null));
        Assert.True(Effects(system).CanCastRewardSpell(1));
        Assert.False(Effects(system).CanCastRewardSpell(2));
        Assert.False(Effects(system).CanCastRewardSpell(3));
    }

    [Fact]
    public void PeriodicTriggerCannotHideAPermanentGrant()
    {
        SpellInfo periodic = Spell(1, SpellEffectName.ApplyAura, trigger: 2, aura: AuraType.PeriodicTriggerSpell) with
        {
            Duration = new SpellDuration(5_000, 0, 5_000),
        };
        Assert.False(Effects(System(periodic, Spell(2, SpellEffectName.LearnSpell))).CanCastRewardSpell(1));
    }

    [Fact]
    public void EmptyMetadataAndUnhandledTargetsAreNotExecutableRewards()
    {
        SpellInfo unsupported = Spell(2, SpellEffectName.Heal) with
        {
            Effects = [new SpellEffectInfo { Effect = SpellEffectName.Heal, TargetA = (SpellImplicitTarget)255 }],
        };
        Assert.False(Effects(System(new SpellInfo { Id = 1 }, unsupported)).CanCastRewardSpell(1));
        Assert.False(Effects(System(unsupported)).CanCastRewardSpell(2));
    }

    private static QuestRewardEffects Effects(SpellSystem system) => new(system, () => [], NullLogger.Instance);
    private static SpellSystem System(params SpellInfo[] spells) => new(new SpellStore(spells, [], []), () => 0);
    private static SpellInfo Spell(uint id, SpellEffectName effect, uint trigger = 0, AuraType aura = AuraType.None)
        => new()
        {
            Id = id,
            Effects = [new SpellEffectInfo
            {
                Effect = effect, TargetA = SpellImplicitTarget.UnitCaster, TriggerSpell = trigger, AuraType = aura,
            }],
        };
}
