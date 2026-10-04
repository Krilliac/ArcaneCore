using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stats;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Druid;

/// <summary>
/// The stat side of the druid forms: vmangos Unit::GetAttackPowerFromStrengthAndAgility (StatSystem.cpp:194-296, cat agility
/// term and Predatory Strikes), Player::CalculateMinMaxDamage (:354-445, the level based weapon-less range),
/// Unit::CanUseEquippedWeapon (Unit.h:963-978) and Player::InitDataForForm (Player.cpp:18271-18312, attack times). A level 60
/// druid with strength 100 and agility 200: no form 2 * 100 - 20 = 180 melee attack power, Cat 60 * 1.5 (Predatory Strikes
/// 150) + 200 + 200 - 20 = 470, Bear 60 * 1.5 + 200 - 20 = 270.
/// </summary>
public sealed class FeralStatWiringTests : IDisposable
{
    private const uint CatForm = 768;
    private const uint BearForm = 5487;
    private const uint TravelForm = 783;
    private const uint PredatoryStrikes = 16972;
    private const uint Mace = 91201;

    private static readonly ItemTemplateStore s_store = new(
        [new() { Entry = Mace, Class = 2, SubClass = 4, Name = "Test Mace", DisplayId = 1, InventoryType = 13, Delay = 2600, MaxDurability = 50, Damages = [new ItemDamage(20, 40, 0)] }],
        []);

    private readonly SpellTestKit _kit;
    private readonly Player _player;
    private readonly ShapeshiftService _service;

    public FeralStatWiringTests() : this(false)
    {
    }

    private FeralStatWiringTests(bool resetFist)
    {
        _kit = new SpellTestKit(
            Form(CatForm, 1), Form(BearForm, 5), Form(TravelForm, 3),
            Spell(PredatoryStrikes, Effect(SpellEffectName.ApplyAura, 150, aura: AuraType.Dummy)) with
            {
                Attributes = (SpellAttributes)0x1d0u,
                SpellIconId = 1563,
                Duration = new SpellDuration(-1, 0, -1),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            });
        var character = new CharacterRecord
        {
            Id = 7, AccountId = 1, Name = "Druid", Race = (byte)Race.NightElf, Class = (byte)Class.Druid, Gender = (byte)Gender.Female,
            Level = 60, MapId = 0, ZoneId = 12, Z = 83.5f,
        };
        var appearance = new PlayerAppearance(
            DisplayId: 2222, FactionTemplate: 4, PowerType.Mana, BaseHealth: 60, BaseMana: 100,
            MaxHealth: 1000, MaxPower: 500, StartPower: 500, NextLevelXp: 400);
        _player = new Player(character, appearance, new FakeSession(1));
        _player.Inventory.Templates = s_store;
        _player.Inventory.GuidAllocator = new ItemGuidAllocator();
        _player.Inventory.Load([]);
        uint[] stats = [100, 200, 100, 100, 100];
        for (int i = 0; i < stats.Length; i++)
        {
            _player.SetUInt32(UpdateFields.UnitFieldStat0 + i, stats[i]);
        }

        _kit.World.AddPlayer(_player);
        _kit.World.RunTick(0);
        new PlayerStatSystem().Attach(_player);
        var listener = new FormStatListener(resetFist);
        listener.Attach(_kit.System);
        _service = new ShapeshiftService(_kit.System, ShapeshiftFormCatalog.Retail, new CombatOptions(), _ => []);
        _service.AddListener(listener);
        _service.Install();
    }

    private static SpellInfo Form(uint id, int form) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModShapeshift, misc: form)) with
    {
        Attributes = (SpellAttributes)0x50010u,
        Duration = new SpellDuration(-1, 0, -1),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    public void Dispose() => _kit.Dispose();

    private void Cast(uint spell)
    {
        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_player, spell, SpellCastTargets.ForSelf(), triggered: true));
    }

    private void Leave(uint form) => _kit.System.CancelAura(_player, form);

    private int MeleeAttackPower => _player.GetInt32(UpdateFields.UnitFieldAttackPower);

    private int RangedAttackPower => _player.GetInt32(UpdateFields.UnitFieldRangedAttackPower);

    private uint AttackTime(WeaponAttackType type) => _player.GetUInt32(UpdateFields.UnitFieldBaseattacktime + (int)type);

    private float MinDamage => _player.GetFloat(UpdateFields.UnitFieldMindamage);

    private float MaxDamage => _player.GetFloat(UpdateFields.UnitFieldMaxdamage);

    private Item EquipMace()
    {
        Item item = ItemTestData.Give(_player.Inventory, Mace);
        _player.Inventory.SwapItem(item.BagSlot, item.Slot, InventorySlots.Bag0, InventorySlots.MainHand);
        Assert.Same(item, _player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand));
        return item;
    }

    [Fact]
    public void NoForm_AttackPowerIsStrengthTimesTwoMinus20_RangedIsAgilityMinus10()
    {
        Assert.Equal(180, MeleeAttackPower);
        Assert.Equal(190, RangedAttackPower);
    }

    [Fact]
    public void CatForm_AttackPower470_WithPredatoryStrikes150_AndRangedAttackPowerZero()
    {
        _kit.System.CastLearnedPassive(_player, PredatoryStrikes);
        Assert.Equal(180, MeleeAttackPower);                     // Predatory Strikes only counts in a feral form (StatSystem.cpp:253-271)

        Cast(CatForm);

        Assert.Equal(470, MeleeAttackPower);
        Assert.Equal(0, RangedAttackPower);                      // StatSystem.cpp:201-217
    }

    [Fact]
    public void BearForm_AttackPower270_AndWithoutPredatoryStrikesTheLevelTermIsZero()
    {
        _kit.System.CastLearnedPassive(_player, PredatoryStrikes);
        Cast(BearForm);
        Assert.Equal(270, MeleeAttackPower);
        Leave(BearForm);
        _kit.System.RemoveAuras(_player, PredatoryStrikes);

        Cast(CatForm);

        Assert.Equal(380, MeleeAttackPower);                     // 0 + 200 + 200 - 20: the cat agility term of patch 1.7
    }

    [Fact]
    public void CatForm_DamageRangeIsTheLevelBasedFeralRange_NotTheWeapon()
    {
        _kit.System.CastLearnedPassive(_player, PredatoryStrikes);
        EquipMace();
        Assert.Equal(20f + (180f / 14f * 2.6f), MinDamage, 3);   // armed, no form: weapon 20-40 at speed 2.6

        Cast(CatForm);

        float apTerm = 470f / 14f * 1.0f;                        // attack speed 1.0 in Cat Form
        Assert.Equal(apTerm + (60 * 0.85f * 1.0f), MinDamage, 3);
        Assert.Equal(apTerm + (60 * 1.25f * 1.0f), MaxDamage, 3);
    }

    [Fact]
    public void BearForm_DamageRangeUsesTheTwoAndAHalfSecondAttackTime()
    {
        _kit.System.CastLearnedPassive(_player, PredatoryStrikes);

        Cast(BearForm);

        float apTerm = 270f / 14f * 2.5f;
        Assert.Equal(apTerm + (60 * 0.85f * 2.5f), MinDamage, 3);
        Assert.Equal(apTerm + (60 * 1.25f * 2.5f), MaxDamage, 3);
    }

    [Fact]
    public void AttackTimes_Cat1000_Bear2500_BothHands()
    {
        Cast(CatForm);
        Assert.Equal((1000u, 1000u), (AttackTime(WeaponAttackType.BaseAttack), AttackTime(WeaponAttackType.OffAttack)));

        Leave(CatForm);
        Cast(BearForm);
        Assert.Equal((2500u, 2500u), (AttackTime(WeaponAttackType.BaseAttack), AttackTime(WeaponAttackType.OffAttack)));
    }

    [Fact]
    public void LeavingAForm_WithAMaceEquipped_RestoresTheWeaponDelayAndTheFormlessAttackPower()
    {
        _kit.System.CastLearnedPassive(_player, PredatoryStrikes);
        EquipMace();
        Cast(CatForm);
        Assert.Equal(1000u, AttackTime(WeaponAttackType.BaseAttack));

        Leave(CatForm);

        Assert.Equal(2600u, AttackTime(WeaponAttackType.BaseAttack));
        Assert.Equal(180, MeleeAttackPower);
        Assert.Equal(20f + (180f / 14f * 2.6f), MinDamage, 3);
    }

    [Fact]
    public void LeavingAForm_Unarmed_KeepsTheFormTime_ByDefault_LikeVmangos()
    {
        Cast(CatForm);

        Leave(CatForm);

        Assert.Equal(1000u, AttackTime(WeaponAttackType.BaseAttack));     // vmangos literal (Player.cpp:5158-5172): the default
        Assert.Equal(1000u, AttackTime(WeaponAttackType.OffAttack));
    }

    [Fact]
    public void LeavingAForm_Unarmed_RestoresTheBaseAttackTime_WhenTheFistResetOptionIsOn()
    {
        using var reset = new FeralStatWiringTests(true);

        reset.Cast(BearForm);
        reset.Leave(BearForm);

        Assert.Equal(2000u, reset.AttackTime(WeaponAttackType.BaseAttack));   // opt-in deviation from vmangos
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(5, false)]
    [InlineData(8, false)]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(16, true)]
    [InlineData(17, true)]
    public void CanUseEquippedWeapon_IsFalseForEveryHandInCatBearAndDireBear(byte form, bool expected)
    {
        _player.SetByte(UpdateFields.UnitFieldBytes1, 2, form);

        foreach (WeaponAttackType type in new[] { WeaponAttackType.BaseAttack, WeaponAttackType.OffAttack, WeaponAttackType.RangedAttack })
        {
            Assert.Equal(expected, PlayerCombatSkills.CanUseEquippedWeapon(_player, type));
        }
    }

    [Fact]
    public void InCat_AMaceInTheMainHandIsNotAWeaponForParryOrSkill()
    {
        EquipMace();
        Assert.NotNull(PlayerCombatSkills.WeaponForAttack(_player, WeaponAttackType.BaseAttack, nonBroken: true, useable: true));

        Cast(CatForm);

        Assert.Null(PlayerCombatSkills.WeaponForAttack(_player, WeaponAttackType.BaseAttack, nonBroken: true, useable: true));
        Assert.NotNull(PlayerCombatSkills.WeaponForAttack(_player, WeaponAttackType.BaseAttack, nonBroken: true, useable: false));   // it is still worn
    }

    [Fact]
    public void PredatoryStrikesLearnedWhileInCat_RecomputesAttackPowerWithoutAFormChange()
    {
        Cast(CatForm);
        Assert.Equal(380, MeleeAttackPower);

        _kit.System.CastLearnedPassive(_player, PredatoryStrikes);
        Assert.Equal(470, MeleeAttackPower);

        _kit.System.RemoveAuras(_player, PredatoryStrikes);
        Assert.Equal(380, MeleeAttackPower);
    }

    [Fact]
    public void TravelForm_ChangesNoStat_AndLeavingItKeepsTheWeaponDelay()
    {
        EquipMace();

        Cast(TravelForm);

        Assert.Equal(180, MeleeAttackPower);
        Assert.Equal(2600u, AttackTime(WeaponAttackType.BaseAttack));
    }
}
