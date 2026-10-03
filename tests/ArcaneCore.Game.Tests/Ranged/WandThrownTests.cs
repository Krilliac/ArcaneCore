using System.Buffers.Binary;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Ranged;

/// <summary>
/// Ranged lane S05: wand Shoot and the single-shot ranged abilities. The spell shapes copy classic-db spell_template: wand Shoot 5019
/// (Attributes 0x12, Ex2 0x20, Ex3 0x408000, category 351, effect 17, damage class magic, school 0), Auto Shot 75 (class ranged,
/// Ex3 0x8000), Throw 2764 (Attributes 0x410012, Ex3 0x8000, category 76, interrupt flags 15, effect 58, NOT auto-repeat).
/// vmangos: wands roll on the RANGED hit and crit tables (SpellCaster.cpp:215-232, 732-737; Unit.cpp:5231-5239), deal the school of the
/// wielded wand for priest, mage and warlock (Spell.cpp:65-72), and crit for 1.5 (SpellCriticalDamageBonus keys on DmgClass,
/// SpellCaster.cpp:960-976) while Auto Shot crits for 2.
/// </summary>
public sealed class WandThrownTests
{
    private const uint Shoot = 5019;
    private const uint AutoShot = 75;
    private const uint Throw = 2764;

    private const uint FireWand = 94401;
    private const uint ArcaneWand = 94402;
    private const uint PhysicalWand = 94403;
    private const uint ThrownStack = 94404;
    private const uint ThrownSingle = 94405;

    private static readonly ItemTemplate[] Templates =
    [
        new() { Entry = FireWand, Class = 2, SubClass = 19, DisplayId = 302, InventoryType = 26, Delay = 1500, MaxDurability = 40, Damages = [new ItemDamage(10, 20, 2)] },
        new() { Entry = ArcaneWand, Class = 2, SubClass = 19, DisplayId = 303, InventoryType = 26, Delay = 1500, MaxDurability = 40, Damages = [new ItemDamage(10, 20, 6)] },
        new() { Entry = PhysicalWand, Class = 2, SubClass = 19, DisplayId = 304, InventoryType = 26, Delay = 1500, MaxDurability = 40, Damages = [new ItemDamage(10, 20, 0)] },
        new() { Entry = ThrownStack, Class = 2, SubClass = 16, DisplayId = 305, InventoryType = 25, Delay = 2000, Stackable = 200, AmmoType = 4, Damages = [new ItemDamage(5, 7, 0)] },
        new() { Entry = ThrownSingle, Class = 2, SubClass = 16, DisplayId = 306, InventoryType = 25, Delay = 2000, MaxDurability = 30, AmmoType = 4, Damages = [new ItemDamage(5, 7, 0)] },
    ];

    private static SpellInfo Row(uint id, uint attributes, uint ex2, uint ex3, uint category, SpellInterruptFlags interrupt, SpellEffectName effect, SpellDamageClass damageClass) => Spell(id, Effect(effect, 5, SpellImplicitTarget.UnitEnemy)) with
    {
        Attributes = (SpellAttributes)attributes,
        AttributesEx2 = (SpellAttributesEx2)ex2,
        AttributesEx3 = ex3,
        Category = category,
        InterruptFlags = interrupt,
        DamageClass = damageClass,
        RangeIndex = 4,
        Range = new SpellRange(0, 35),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private sealed class Rig : IDisposable
    {
        public Rig(Class cls = Class.Mage)
        {
            Kit = new SpellTestKit(
                Row(Shoot, 0x12, 0x20, 0x408000, 351, (SpellInterruptFlags)15, SpellEffectName.WeaponDamageNoschool, SpellDamageClass.Magic),
                Row(AutoShot, 0x50012, 0x20, 0x8000, 0, SpellInterruptFlags.Movement, SpellEffectName.WeaponDamage, SpellDamageClass.Ranged),
                Row(Throw, 0x410012, 0, 0x8000, 76, (SpellInterruptFlags)15, SpellEffectName.WeaponDamage, SpellDamageClass.Ranged));
            Player = TestPlayers.Add(Kit, 1, cls, cls == Class.Warrior ? PowerType.Rage : PowerType.Mana);
            Target = TestPlayers.Add(Kit, 2, Class.Warrior, PowerType.Rage, x: 15, y: 1);
            Target.Health = 100_000;
            Target.MaxHealth = 100_000;
            Player.Inventory.Templates = new ItemTemplateStore([.. ItemTestData.Templates, .. Templates]);
            Player.Inventory.GuidAllocator = new ItemGuidAllocator();
            Player.SetUInt32(UpdateFields.UnitFieldRangedattacktime, 2000);
            Player.SetFloat(UpdateFields.UnitFieldMinrangeddamage, 50f);
            Player.SetFloat(UpdateFields.UnitFieldMaxrangeddamage, 50f);
            Kit.Spellbook.Teach(Player, Shoot, AutoShot, Throw);
        }

        public SpellTestKit Kit { get; }

        public Player Player { get; }

        public Player Target { get; }

        public FakeSession Session => (FakeSession)Player.Session;

        public void Equip(uint entry, uint count = 1)
        {
            Item item = ItemTestData.Give(Player.Inventory, entry, count);
            Assert.Equal(InventoryResult.Ok, Player.Inventory.CanEquipItem(InventorySlots.NullSlot, out byte dest, item.Template, item, swap: false));
            Player.Inventory.RemoveItem(item.BagSlot, item.Slot);
            Player.Inventory.EquipItem(dest, item);
        }

        public void Dispose() => Kit.Dispose();
    }

    private static SpellSchool LoggedSchool(FakeSession session)
    {
        byte[] log = Packets(session, WorldOpcode.SmsgSpellnonmeleedamagelog).Last();
        int offset = 0;
        for (int i = 0; i < 2; i++)
        {
            byte mask = log[offset++];
            offset += System.Numerics.BitOperations.PopCount(mask);
        }

        offset += 8; // spell id, damage
        return (SpellSchool)log[offset];
    }

    // --- hit and crit tables --------------------------------------------------------------------

    [Fact]
    public void WandShoot_RollsTheRangedTable_NotTheMagicOne()
    {
        using var rig = new Rig();
        var rules = new VanillaSpellCombatRules();
        SpellInfo wand = rig.Kit.Store.Get(Shoot)!;

        var outcomes = new Dictionary<SpellMissInfo, int>();
        for (int i = 0; i < 4000; i++)
        {
            SpellMissInfo result = rules.RollHit(rig.Kit.System, rig.Player, rig.Target, wand);
            outcomes[result] = outcomes.GetValueOrDefault(result) + 1;
        }

        // The magic table can only answer RESIST; the ranged weapon table answers MISS.
        Assert.False(outcomes.ContainsKey(SpellMissInfo.Resist), "a wand does not roll the magic hit table");
        Assert.True(outcomes.GetValueOrDefault(SpellMissInfo.Miss) > 0, "the ranged table misses");
    }

    [Fact]
    public void WandShoot_UsesTheRangedCritChance()
    {
        using var rig = new Rig();
        rig.Player.SetFloat(UpdateFields.PlayerRangedCritPercentage, 10f);
        var rules = new VanillaSpellCombatRules();

        float wand = rules.CritChance(rig.Kit.System, rig.Player, rig.Target, rig.Kit.Store.Get(Shoot)!);
        float autoShot = rules.CritChance(rig.Kit.System, rig.Player, rig.Target, rig.Kit.Store.Get(AutoShot)!);

        Assert.True(wand > 0f, "a school-0 magic class spell has no crit of its own: the ranged crit must be used");
        Assert.Equal(autoShot, wand, 0.001f);
    }

    [Fact]
    public void CritDamage_WandIsOneAndAHalf_AutoShotIsDouble()
    {
        using var rig = new Rig();
        var rules = new VanillaSpellCombatRules();

        Assert.Equal(1.5f, rules.CritMultiplier(rig.Kit.Store.Get(Shoot)!));
        Assert.Equal(2.0f, rules.CritMultiplier(rig.Kit.Store.Get(AutoShot)!));
    }

    // --- the wand school ------------------------------------------------------------------------

    [Theory]
    [InlineData(FireWand, SpellSchool.Fire)]
    [InlineData(ArcaneWand, SpellSchool.Arcane)]
    [InlineData(PhysicalWand, SpellSchool.Normal)]
    public void AMagesWand_DealsTheSchoolOfTheWand(uint wand, SpellSchool expected)
    {
        using var rig = new Rig();
        rig.Equip(wand);

        SpellCastResult result = rig.Kit.System.CastSpell(rig.Player, Shoot, SpellCastTargets.ForUnit(rig.Target.Guid), triggered: true);

        Assert.Equal(SpellCastResult.CastOk, result);
        Assert.Equal(expected, LoggedSchool(rig.Session));
    }

    [Fact]
    public void AClassThatIsNotAWandUser_KeepsTheSpellsOwnSchool()
    {
        using var rig = new Rig(Class.Warrior);
        rig.Equip(FireWand); // not equippable by a warrior in the real game; the school rule keys on the class only

        rig.Kit.System.CastSpell(rig.Player, Shoot, SpellCastTargets.ForUnit(rig.Target.Guid), triggered: true);

        Assert.Equal(SpellSchool.Normal, LoggedSchool(rig.Session));
    }

    [Fact]
    public void TheWandDamageIsTheRangedFieldPlusTheEffectValue()
    {
        using var rig = new Rig();
        rig.Equip(FireWand);

        rig.Kit.System.CastSpell(rig.Player, Shoot, SpellCastTargets.ForUnit(rig.Target.Guid), triggered: true);

        Assert.Equal(55u, 100_000 - rig.Target.Health);
    }

    // --- Throw is a single shot, not an auto-repeat ---------------------------------------------

    [Fact]
    public void Throw_IsNotAutoRepeat_AndItsCooldownIsTheRangedAttackTime()
    {
        using var rig = new Rig(Class.Warrior);
        SpellInfo spell = rig.Kit.Store.Get(Throw)!;
        Assert.False(RangedSpellFacts.IsAutoRepeatRanged(spell));
        Assert.True(RangedSpellFacts.IsAutoRepeatRanged(rig.Kit.Store.Get(AutoShot)!));
        Assert.True(RangedSpellFacts.IsAutoRepeatRanged(rig.Kit.Store.Get(Shoot)!));
        rig.Equip(ThrownStack, 20);

        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.HandleCastRequest(rig.Player, Throw, SpellCastTargets.ForUnit(rig.Target.Guid)));
        rig.Kit.Advance(1000);
        Assert.Null(rig.Kit.System.GetState(rig.Player.Guid)?.AutoRepeatCast); // one throw, no slot
        uint thrown = 100_000 - rig.Target.Health;
        Assert.True(thrown > 0, "the throw landed");
        uint stack = rig.Player.Inventory.GetItemCount(ThrownStack);

        Assert.Equal(SpellCastResult.NotReady, rig.Kit.System.HandleCastRequest(rig.Player, Throw, SpellCastTargets.ForUnit(rig.Target.Guid)));

        rig.Kit.Advance(1500); // 2000 ms ranged attack time since the cast
        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.HandleCastRequest(rig.Player, Throw, SpellCastTargets.ForUnit(rig.Target.Guid)));
        rig.Kit.Advance(1000);
        Assert.True(rig.Player.Inventory.GetItemCount(ThrownStack) < stack, "one thrown weapon is used per throw");
    }

    [Fact]
    public void ThrowUnderRapidFire_UsesTheHastedRangedTime()
    {
        using var rig = new Rig(Class.Warrior);
        rig.Equip(ThrownStack, 20);
        rig.Player.Combat.ApplyAttackTimePercentMod(WeaponAttackType.RangedAttack, 100f, apply: true); // +100%: 2000 -> 1000 ms

        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.HandleCastRequest(rig.Player, Throw, SpellCastTargets.ForUnit(rig.Target.Guid)));
        rig.Kit.Advance(1200);

        Assert.Equal(SpellCastResult.CastOk, rig.Kit.System.HandleCastRequest(rig.Player, Throw, SpellCastTargets.ForUnit(rig.Target.Guid)));
    }
}
