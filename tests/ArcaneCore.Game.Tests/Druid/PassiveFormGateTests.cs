using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Druid;

/// <summary>
/// vmangos Player::IsNeedCastPassiveLikeSpellAtLearn (Player.cpp:3748-3763): a form-bound passive is only cast in its
/// form. Spell data are the classic-db 1.12.1 rows: Feline Swiftness 17002 (Attributes 0x81d0 = passive, Stances 0x1,
/// ModIncreaseSpeed +14), Predatory Strikes 16972 (Attributes 0x1d0, no Stances, Dummy), Leader of the Pack 24932
/// (Attributes 0, Stances 0x91, area aura).
/// </summary>
public sealed class PassiveFormGateTests : IDisposable
{
    private const uint FelineSwiftness = 17002;
    private const uint PredatoryStrikes = 16972;
    private const uint LeaderOfThePack = 24932;
    private const uint OnlyCatNonPassive = 900501;

    private readonly SpellTestKit _kit;
    private readonly Player _player;

    public PassiveFormGateTests()
    {
        _kit = new SpellTestKit(
            Spell(FelineSwiftness, Effect(SpellEffectName.ApplyAura, 15, aura: AuraType.ModIncreaseSpeed)) with
            {
                Attributes = (SpellAttributes)0x81d0u,
                Stances = 1,
                Duration = new SpellDuration(-1, 0, -1),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(PredatoryStrikes, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.Dummy)) with
            {
                Attributes = (SpellAttributes)0x1d0u,
                Duration = new SpellDuration(-1, 0, -1),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(LeaderOfThePack, Effect(SpellEffectName.ApplyAura, 3, aura: AuraType.ModCritPercent)) with
            {
                Stances = 0x91,
                Duration = new SpellDuration(-1, 0, -1),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(OnlyCatNonPassive, Effect(SpellEffectName.ApplyAura, 3, aura: AuraType.ModCritPercent)) with
            {
                Stances = 1,
                Duration = new SpellDuration(-1, 0, -1),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            });
        (_player, _) = _kit.AddPlayer(1);
        _kit.System.RegisterCastCheck(new PassiveFormCastCheck(() => ShapeshiftFormCatalog.Retail));
    }

    public void Dispose() => _kit.Dispose();

    private bool Has(Player player, uint spell) => _kit.System.GetAuras(player).Any(h => h.Spell.Id == spell && !h.IsRemoved);

    private bool Has(uint spell) => Has(_player, spell);

    private void SetForm(byte form) => _player.SetByte(UpdateFields.UnitFieldBytes1, 2, form);

    [Fact]
    public void FelineSwiftness_LearnedInHumanoidForm_AddsNoAura()
    {
        _kit.System.CastLearnedPassive(_player, FelineSwiftness);

        Assert.False(Has(FelineSwiftness));
    }

    [Fact]
    public void FelineSwiftness_InCatForm_GetsTheAura()
    {
        SetForm(1);

        _kit.System.CastLearnedPassive(_player, FelineSwiftness);

        Assert.True(Has(FelineSwiftness));
    }

    [Theory]
    [InlineData(5)]    // bear
    [InlineData(3)]    // travel
    [InlineData(17)]   // battle stance
    public void FelineSwiftness_InAnotherForm_AddsNoAura(byte form)
    {
        SetForm(form);

        _kit.System.CastLearnedPassive(_player, FelineSwiftness);

        Assert.False(Has(FelineSwiftness));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void PredatoryStrikes_HasNoStances_IsAlwaysCast(byte form)
    {
        SetForm(form);

        _kit.System.CastLearnedPassive(_player, PredatoryStrikes);

        Assert.True(Has(PredatoryStrikes));
    }

    [Fact]
    public void ANonPassiveTriggeredSpellWithStances_IsNotVetoed()
    {
        // Leader of the Pack is cast by the form apply path as a non-passive spell (Stances 0x91 = cat, bear, dire bear).
        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_player, LeaderOfThePack, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_player, OnlyCatNonPassive, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(Has(LeaderOfThePack));
        Assert.True(Has(OnlyCatNonPassive));
    }

    [Fact]
    public void AVetoedPassive_SendsNoCastResultToTheClient()
    {
        (Player player, FakeSession session) = _kit.AddPlayer(2);

        _kit.System.CastLearnedPassive(player, FelineSwiftness);

        Assert.Empty(Packets(session, WorldOpcode.SmsgCastResult));
        Assert.False(Has(player, FelineSwiftness));
    }

    [Fact]
    public void ADirectTriggeredCast_ReportsOnlyShapeshift_ForAHumanoidCaster()
        => Assert.Equal(SpellCastResult.OnlyShapeshift, _kit.System.CastSpell(_player, FelineSwiftness, SpellCastTargets.ForSelf(), triggered: true));

    [Fact]
    public void IsNeedCastPassiveLikeSpellAtLearn_FollowsTheVmangosFormula()
    {
        SpellInfo noStances = _kit.Store.Get(PredatoryStrikes)!;
        SpellInfo cat = _kit.Store.Get(FelineSwiftness)!;
        SpellInfo allowNotShifted = cat with { AttributesEx2 = cat.AttributesEx2 | (SpellAttributesEx2)0x00080000u };
        SpellInfo castWhenLearned = cat with { AttributesEx = (SpellAttributesEx)0x80000000u };

        Assert.True(PassiveFormCastCheck.IsNeedCastPassiveLikeSpellAtLearn(noStances, 0, null));
        Assert.False(PassiveFormCastCheck.IsNeedCastPassiveLikeSpellAtLearn(cat, 0, null));
        Assert.True(PassiveFormCastCheck.IsNeedCastPassiveLikeSpellAtLearn(cat, 1, null));
        Assert.True(PassiveFormCastCheck.IsNeedCastPassiveLikeSpellAtLearn(allowNotShifted, 0, null));     // no form + ALLOW_WHILE_NOT_SHAPESHIFTED
        Assert.False(PassiveFormCastCheck.IsNeedCastPassiveLikeSpellAtLearn(allowNotShifted, 5, null));    // in a form it needs the Stances bit
        Assert.False(PassiveFormCastCheck.IsNeedCastPassiveLikeSpellAtLearn(castWhenLearned, 0, null));    // CAST_WHEN_LEARNED, but GetErrorAtShapeshiftedCast = ONLY_SHAPESHIFT
        Assert.True(PassiveFormCastCheck.IsNeedCastPassiveLikeSpellAtLearn(castWhenLearned, 1, null));
    }
}
