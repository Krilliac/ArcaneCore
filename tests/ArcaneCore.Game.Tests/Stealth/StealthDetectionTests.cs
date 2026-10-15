using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stats;
using ArcaneCore.Game.Stealth;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Stealth;

/// <summary>
/// Unit tests for <see cref="StealthOptions.IncludeStrengthContribution"/> in <see cref="StealthDetection"/>
/// and <see cref="ShapeshiftService.RefreshShapeshiftWeaponBoosts"/> for Elemental Sharpening Stone across feral forms.
/// </summary>
public sealed class StealthDetectionTests
{
    private const uint StealthAuraSpell = 910301;
    private const uint CatFormSpell = 768;
    private const uint TestMaceEntry = 910302;

    [Fact]
    public void Evaluate_DefaultOptions_IgnoresTargetStrength_MatchingVanilla112Behavior()
    {
        var inputWithoutStr = new StealthDetectionInput(
            Distance: 5f,
            DetectorStunned: false,
            DetectorIsPlayer: true,
            TargetIsPlayer: true,
            DetectorLevel: 60,
            TargetLevel: 60,
            TargetStealthTotal: 300,
            TargetStealthLevelMod: 0,
            DetectorDetectMod: 0,
            TargetInDetectorArc: true,
            TargetStrength: 0f);
        var inputWithStr = inputWithoutStr with { TargetStrength = 200f };

        Assert.False(StealthOptions.Default.IncludeStrengthContribution);

        StealthDetectionResult baseline = StealthDetection.Evaluate(inputWithoutStr, StealthOptions.Default);
        StealthDetectionResult withStrDefault = StealthDetection.Evaluate(inputWithStr, StealthOptions.Default);

        Assert.Equal(9.0f, baseline.VisibleDistance, 3);
        Assert.Equal(baseline.VisibleDistance, withStrDefault.VisibleDistance, 3);
    }

    [Fact]
    public void Evaluate_WhenIncludeStrengthContributionIsTrue_AddsPointThreeTimesStrengthToStealthSkill()
    {
        // TargetStrength = 100 -> +30 stealthSkill -> visibleDistance shifts by -30 * (1.5 / 5.0) = -9.0 yards (from 9.0 to 0.0).
        // TargetStrength = 50 -> +15 stealthSkill -> visibleDistance shifts by -15 * (1.5 / 5.0) = -4.5 yards (from 9.0 to 4.5).
        var input = new StealthDetectionInput(
            Distance: 6f,
            DetectorStunned: false,
            DetectorIsPlayer: true,
            TargetIsPlayer: true,
            DetectorLevel: 60,
            TargetLevel: 60,
            TargetStealthTotal: 300,
            TargetStealthLevelMod: 0,
            DetectorDetectMod: 0,
            TargetInDetectorArc: true,
            TargetStrength: 50f);

        var defaultResult = StealthDetection.Evaluate(input, new StealthOptions { IncludeStrengthContribution = false });
        var strengthResult = StealthDetection.Evaluate(input, new StealthOptions { IncludeStrengthContribution = true });

        Assert.Equal(9.0f, defaultResult.VisibleDistance, 3);
        Assert.True(defaultResult.Detected);

        Assert.Equal(4.5f, strengthResult.VisibleDistance, 3);
        Assert.False(strengthResult.Detected);
        Assert.True(strengthResult.Alert);
    }

    [Fact]
    public void CanDetectStealthOf_ReadsTargetUnitStrength_WhenIncludeStrengthContributionIsEnabled()
    {
        using var kit = new SpellTestKit(
            Spell(StealthAuraSpell, Effect(SpellEffectName.ApplyAura, 300, aura: AuraType.ModStealth)) with
            {
                Duration = new SpellDuration(-1, 0, -1),
                SpellVisual = 1,
            });
        kit.System.RegisterAura(AuraType.ModStealth, new AuraHandler(null, null));

        (Player detector, _) = kit.AddPlayer(1);
        (Player sneaker, _) = kit.AddPlayer(2, 2);
        detector.Level = 60;
        sneaker.Level = 60;
        sneaker.SetUInt32(UpdateFields.UnitFieldStat0, 50); // 50 Strength -> +15 stealth skill -> -4.5 yd visible distance

        kit.System.CastSpell(sneaker, StealthAuraSpell, SpellCastTargets.ForSelf(), triggered: true);
        detector.Relocate(0, 0, 83.5f, 0f, 0);
        sneaker.Relocate(6, 0, 83.5f, 0f, 0);

        StealthDetectionResult standard = StealthDetection.CanDetectStealthOf(
            kit.System,
            detector,
            sneaker,
            6f,
            new StealthOptions { IncludeStrengthContribution = false });
        StealthDetectionResult withStrength = StealthDetection.CanDetectStealthOf(
            kit.System,
            detector,
            sneaker,
            6f,
            new StealthOptions { IncludeStrengthContribution = true });

        Assert.Equal(9.0f, standard.VisibleDistance, 3);
        Assert.True(standard.Detected);

        Assert.Equal(4.5f, withStrength.VisibleDistance, 3);
        Assert.False(withStrength.Detected);
    }

    [Fact]
    public void RefreshShapeshiftWeaponBoosts_SuppressesElementalSharpeningStoneInFeralForm_AndRestoresOnExit()
    {
        var templates = new ItemTemplateStore(
            [new() { Entry = TestMaceEntry, Class = 2, SubClass = 4, Name = "Druid Mace", DisplayId = 1, InventoryType = 13, Delay = 2600, MaxDurability = 50, Damages = [new ItemDamage(20, 40, 0)] }],
            []);
        using var kit = new SpellTestKit(
            Spell(CatFormSpell, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModShapeshift, misc: (int)ArcaneCore.Game.Spells.ShapeshiftForm.Cat)) with
            {
                Attributes = (SpellAttributes)0x50010u,
                Duration = new SpellDuration(-1, 0, -1),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            });

        (Player druid, _) = kit.AddPlayer(1);
        druid.Inventory.Templates = templates;
        druid.Inventory.GuidAllocator = new ItemGuidAllocator();
        druid.Inventory.Load([]);

        Item mace = ItemTestData.Give(druid.Inventory, TestMaceEntry);
        druid.Inventory.SwapItem(mace.BagSlot, mace.Slot, InventorySlots.Bag0, InventorySlots.MainHand);
        mace.SetUInt32(UpdateFields.ItemFieldEnchantment + 3, ShapeshiftService.ElementalSharpeningStoneEnchantId);
        druid.SetFloat(UpdateFields.PlayerCritPercentage, 7.0f);

        var service = new ShapeshiftService(kit.System, ShapeshiftFormCatalog.Retail, new CombatOptions(), _ => []);
        service.Install();

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(druid, CatFormSpell, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(5.0f, druid.GetFloat(UpdateFields.PlayerCritPercentage), 3);

        kit.System.CancelAura(druid, CatFormSpell);
        Assert.Equal(7.0f, druid.GetFloat(UpdateFields.PlayerCritPercentage), 3);
    }
}
