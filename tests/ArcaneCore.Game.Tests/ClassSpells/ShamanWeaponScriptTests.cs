using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Spells.Shaman;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Items;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells;

/// <summary>
/// The shaman weapon imbues' scripts (vmangos scripts/spells/spell_shaman.cpp:19-44, SpellEffects.cpp:4544-4561). Flametongue Weapon rank 1's
/// enchantment proc 8026 is a DUMMY that deals Flametongue Attack 10444; Rockbiter Weapon's proc 20865 is a SCRIPT_EFFECT that adds threat. The
/// enchantment is a temporary weapon enchantment whose combat spell is the proc, as the client's SpellItemEnchantment.dbc defines it.
/// </summary>
public sealed class ShamanWeaponScriptTests : IDisposable
{
    private const uint FlametongueProc = 8026;
    private const uint RockbiterProc = 20865;
    private const uint RockbiterProcOdd = 20866;   // a rank whose value does not divide evenly
    private const uint ModThreatAllSchools = 49_704; // a +100% MOD_THREAT aura on every school
    private const uint FlametongueEnchant = 49_701;
    private const uint Weapon = 49_702;

    private readonly SpellTestKit _kit;
    private readonly Player _shaman;
    private readonly Player _enemy;

    public ShamanWeaponScriptTests()
    {
        _kit = new SpellTestKit(
            Spell(FlametongueProc, Effect(SpellEffectName.Dummy, 1000, SpellImplicitTarget.UnitEnemy)) with
            {
                School = SpellSchool.Fire, SpellFamilyName = 11, SpellFamilyFlags = 0x200000, RangeIndex = 4, Range = new SpellRange(0, 30),
                StartRecoveryCategory = 0, StartRecoveryTime = 0,
            },
            Spell(FlametongueProcScript.FlametongueAttack, Effect(SpellEffectName.SchoolDamage, 0, SpellImplicitTarget.UnitEnemy)) with
            {
                School = SpellSchool.Fire, SpellFamilyName = 11, SpellFamilyFlags = 0x200000, DamageClass = SpellDamageClass.Melee,
                RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0,
            },
            Spell(RockbiterProc, Effect(SpellEffectName.ScriptEffect, 10, SpellImplicitTarget.UnitEnemy)) with
            {
                SpellFamilyName = 11, RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0,
            },
            Spell(RockbiterProcOdd, Effect(SpellEffectName.ScriptEffect, 7, SpellImplicitTarget.UnitEnemy)) with
            {
                School = SpellSchool.Nature, SpellFamilyName = 11, RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0,
            },
            Spell(ModThreatAllSchools, Effect(SpellEffectName.ApplyAura, 100, aura: AuraType.ModThreat, misc: 0x7F)) with
            {
                Duration = new SpellDuration(-1, 0, -1), StartRecoveryCategory = 0, StartRecoveryTime = 0,
            });
        (_shaman, _) = _kit.AddPlayer(1);
        (_enemy, _) = _kit.AddPlayer(2, 2, 0);
        _enemy.MaxHealth = 10_000;
        _enemy.Health = 5_000;
        _kit.System.Units = new MapObjectResolver();
        SpellScriptDispatcher.Install(_kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
    }

    public void Dispose() => _kit.Dispose();

    private Item EquipImbuedWeapon(uint delayMs)
    {
        _kit.System.ItemEnchantments = new ItemEnchantmentCatalog([
            new ItemEnchantmentDefinition(FlametongueEnchant, [new ItemEnchantmentEffect(1, FlametongueProc, 100)])]);
        _shaman.Inventory.Templates = new ItemTemplateStore([new ItemTemplate
        {
            Entry = Weapon, Class = 2, SubClass = 4, InventoryType = 21, Delay = delayMs, Stackable = 1,
        }]);
        _shaman.Inventory.GuidAllocator = new ItemGuidAllocator();
        // Enchantment slot 1 (TEMP_ENCHANTMENT_SLOT) holds the imbue: (id, duration, charges).
        _shaman.Inventory.Load([new InventoryItemData(0, InventorySlots.MainHand, new ItemInstanceData
        {
            Guid = 49_703, Entry = Weapon, Enchantments = [0, 0, 0, FlametongueEnchant, 1_800_000, 0, .. new uint[15]],
        })]);
        return _shaman.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!;
    }

    private void Hit() => _kit.System.HandleItemCombatProc(new MeleeDamageInfo
    {
        Attacker = _shaman, Target = _enemy, AttackType = WeaponAttackType.BaseAttack, HitInfo = HitInfo.AffectsVictim,
        TargetState = VictimState.Normal, Outcome = MeleeHitOutcome.Normal,
    });

    [Theory]
    [InlineData(2000u, 20u)]   // (1000 + 0) * 0.01 * 2.0
    [InlineData(4000u, 40u)]
    public void FlametongueProc_DealsFlametongueAttack_ScaledByTheWeaponThatProcced(uint delay, uint expected)
    {
        EquipImbuedWeapon(delay);
        uint before = _enemy.Health;

        Hit();

        Assert.InRange(before - _enemy.Health, expected, expected + 1); // rand_dither of a whole number may round up by float error
    }

    private sealed class CastItemRecorder : ISpellCastObserver
    {
        public List<(uint Spell, Item? Item)> Casts { get; } = [];

        public void OnCast(SpellCast cast) => Casts.Add((cast.Spell.Id, cast.CastItem));
    }

    [Fact]
    public void FlametongueProc_CastsFlametongueAttack_WithTheWeaponThatProccedAsItsCastItem()
    {
        // vmangos spell_shaman.cpp:41: CastCustomSpell(target, 10444, ..., true, spell->m_CastItem).
        Item weapon = EquipImbuedWeapon(2000);
        var recorder = new CastItemRecorder();
        _kit.System.RegisterObserver(recorder);

        Hit();

        (uint _, Item? item) = Assert.Single(recorder.Casts, c => c.Spell == FlametongueProcScript.FlametongueAttack);
        Assert.Same(weapon, item);
    }

    [Fact]
    public void FlametongueProc_AddsThreePointEightFivePercentOfFireSpellDamagePerTenthOfASecond()
    {
        Assert.Equal((1000 + (3.85f * 100)) * 0.01f * 2.6f, FlametongueProcScript.Damage(1000, 100, 2.6f), 3);
    }

    [Fact]
    public void FlametongueProc_WithoutTheItem_DoesNothing()
    {
        uint before = _enemy.Health;

        _kit.System.CastSpell(_shaman, FlametongueProc, SpellCastTargets.ForUnit(_enemy.Guid), triggered: true);

        Assert.Equal(before, _enemy.Health);
    }

    [Fact]
    public void RockbiterProc_AddsValueTimesAttackTimeOfThreat_OnlyWhereTheShamanIsAlreadyOnTheList()
    {
        Map map = _kit.World.GetMap(0);
        var wolf = new CombatTestUnit();
        wolf.Relocate(3, 0, _shaman.Z, 0, 0);
        map.AddObject(wolf);
        map.Combat.Track(wolf);
        _shaman.SetUInt32(UpdateFields.UnitFieldBaseattacktime, 2600);

        _kit.System.CastSpell(_shaman, RockbiterProc, SpellCastTargets.ForUnit(wolf.Guid), triggered: true);
        Assert.Equal(0f, wolf.Combat.Threat.GetThreat(_shaman)); // not on the list: nothing

        wolf.Combat.Threat.AddThreat(_shaman, 5);
        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_shaman, RockbiterProc, SpellCastTargets.ForUnit(wolf.Guid), triggered: true));

        Assert.Equal(5f + (10 * 2.6f), wolf.Combat.Threat.GetThreat(_shaman), 3);
    }

    [Fact]
    public void RockbiterProc_TheThreatIsWholeNumberArithmetic_AddedWithNoThreatSpell()
    {
        // vmangos SpellEffects.cpp:4557-4558: addThreat(caster, damage * GetAttackTime(BASE_ATTACK) / 1000), uint32 arithmetic, no threat spell:
        // 7 * 2600 / 1000 = 18, not 18.2.
        Map map = _kit.World.GetMap(0);
        var wolf = new CombatTestUnit();
        wolf.Relocate(3, 0, _shaman.Z, 0, 0);
        map.AddObject(wolf);
        map.Combat.Track(wolf);
        _shaman.SetUInt32(UpdateFields.UnitFieldBaseattacktime, 2600);
        wolf.Combat.Threat.AddThreat(_shaman, 5);

        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_shaman, RockbiterProcOdd, SpellCastTargets.ForUnit(wolf.Guid), triggered: true));

        Assert.Equal(5f + 18f, wolf.Combat.Threat.GetThreat(_shaman), 3);
    }

    [Fact]
    public void RockbiterProc_IsNotScaledByTheShamansModThreatAuras_BecauseVmangosAddsItWithNoSchool()
    {
        // vmangos SpellEffects.cpp:4558 calls the 2-argument addThreat, which ThreatManager.h:192 forwards with SPELL_SCHOOL_MASK_NONE, and
        // Unit::ApplyTotalThreatModifier (Unit.cpp:7414-7415) returns the threat unchanged for an empty mask: Tranquil Air Totem, a cloak's
        // Subtlety or Fetish of the Sand Reaver leave Rockbiter threat alone.
        Map map = _kit.World.GetMap(0);
        var wolf = new CombatTestUnit();
        wolf.Relocate(3, 0, _shaman.Z, 0, 0);
        map.AddObject(wolf);
        map.Combat.Track(wolf);
        _shaman.SetUInt32(UpdateFields.UnitFieldBaseattacktime, 2600);
        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_shaman, ModThreatAllSchools, SpellCastTargets.ForUnit(_shaman.Guid), triggered: true));
        var modifiers = new SpellThreatModifiers(_kit.System);
        Assert.Equal(2f, modifiers.TotalThreatMultiplier(_shaman, (int)SpellSchool.Normal)); // the aura is live: physical threat would double
        Assert.Equal(2f, modifiers.TotalThreatMultiplier(_shaman, (int)SpellSchool.Nature));
        wolf.Combat.Threat.AddThreat(_shaman, 5);

        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_shaman, RockbiterProcOdd, SpellCastTargets.ForUnit(wolf.Guid), triggered: true));

        Assert.Equal(5f + 18f, wolf.Combat.Threat.GetThreat(_shaman), 3);
    }
}
