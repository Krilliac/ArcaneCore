using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Schema;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Crafting;
using ArcaneCore.Game.Crafting.Enchanting;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Crafting;
using ArcaneCore.Game.Tests.Skills;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Crafting;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.Items;
using ArcaneCore.Protocol;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Game.Tests.ContentImport;

/// <summary>
/// The two item-enchant tables end to end: a dump goes through <see cref="EnchantProcDumpImporter"/> into a real world database, the stores
/// read it back (as <c>SpellFeature</c> does at startup) and the game consumes it. Every test first runs the same scenario on an unfilled
/// table, so a pass shows the data did the work. The ids are retail (Instant Poison 8679 and Deadly Poison 2823; Fiery Weapon spell 13897,
/// enchantment 803); the dump rows are hand-written.
/// </summary>
public sealed class EnchantDataImportTests
{
    private const uint InstantPoison = 8679;
    private const uint DeadlyPoison = 2823;
    private const uint PoisonEnchant = 703;
    private const uint FieryWeaponSpell = 13897;
    private const uint FieryWeaponEnchant = 803;
    private const uint Weapon = 95404;

    private const string Dump = """
        CREATE TABLE `spell_enchant_charges` (`entry` INT UNSIGNED NOT NULL, `charges` INT UNSIGNED NOT NULL DEFAULT '0', PRIMARY KEY (`entry`));
        INSERT INTO `spell_enchant_charges` VALUES (8679, 40);
        INSERT INTO `spell_enchant_charges` VALUES (2823, 60);
        CREATE TABLE `spell_proc_item_enchant` (`entry` mediumint unsigned NOT NULL, `ppmRate` float NOT NULL DEFAULT '0', PRIMARY KEY (`entry`));
        INSERT INTO `spell_proc_item_enchant` VALUES (13897,6);
        """;

    private static async Task<WorldDbContext> ImportedWorldAsync()
    {
        var db = new WorldDbContext(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        var importer = new EnchantProcDumpImporter();
        importer.Read(new StringReader(Dump));
        await importer.WriteAsync(db, replace: true);
        db.ChangeTracker.Clear();
        return db;
    }

    private static EnchantCatalog Catalog() => new(
    [
        new SpellItemEnchantment(PoisonEnchant, [5, 0, 0], [4, 0, 0], [(uint)ItemStatType.Strength, 0, 0], "Poison", 0, 0),
    ]);

    private sealed class PoisonRig : IDisposable
    {
        public PoisonRig(ISpellEnchantChargesCatalog? charges)
        {
            SpellInfo[] spells =
            [
                Poison(InstantPoison),
                Poison(DeadlyPoison),
            ];
            ItemTemplate[] items =
            [
                new() { Entry = Weapon, Class = 2, SubClass = 7, Name = "Test Sword", DisplayId = 4, InventoryType = 13, Delay = 2000, MaxDurability = 50, Quality = 2, ItemLevel = 30, Damages = [new ItemDamage(5, 9, 0)] },
            ];
            Kit = new CraftingTestKit(spells, items);
            Kit.AttachSkills().Set(SkillIds.Blacksmithing, 10, 150, 1);
            Kit.Player.AttachEnchantments(new PlayerEnchantments(Kit.Player, Catalog(), Kit.System));
            Kit.Inventory.StatsApplier = new EnchantStatsApplier(Kit.Inventory.StatsApplier);
            new EnchantItemSpells(Catalog(), null, null).Install(Kit.System);
            if (charges is not null)
            {
                Kit.System.SpellEnchantCharges = charges;
            }

            Sword = Kit.Give(Weapon);
            Kit.Inventory.SwapItem(Sword.BagSlot, Sword.Slot, InventorySlots.Bag0, InventorySlots.MainHand);
        }

        public CraftingTestKit Kit { get; }

        public Item Sword { get; }

        private static SpellInfo Poison(uint id) =>
            CraftingTestKit.Craft(id, SpellTestKit.Effect(SpellEffectName.EnchantItemTemporary, 3600, SpellImplicitTarget.None, misc: (int)PoisonEnchant)) with
            {
                Targets = 0x10,
                EquippedItemClass = 2,
            };

        public SpellCastResult Apply(uint poison)
        {
            Kit.Session.Clear();
            Kit.Kit.Spellbook.Teach(Kit.Player, poison);
            var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.Item, Item = Sword.Guid };
            SpellCastResult result = Kit.Kit.System.HandleCastRequest(Kit.Player, poison, targets);
            if (Kit.System.SpellBatchWindowMs != 0)
            {
                Kit.Kit.Advance(Kit.System.SpellBatchWindowMs);
            }

            return result;
        }

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void APoisonAppliedWithoutTheTable_HasNoCharges_AndNeverRunsDown()
    {
        using var rig = new PoisonRig(charges: null);

        Assert.Equal(SpellCastResult.CastOk, rig.Apply(InstantPoison));

        Assert.Equal(PoisonEnchant, ItemEnchantments.Id(rig.Sword, EnchantSlots.Temporary));
        Assert.Equal(0u, ItemEnchantments.Charges(rig.Sword, EnchantSlots.Temporary));
        for (int i = 0; i < 100; i++)
        {
            Assert.False(rig.Sword.ConsumeEnchantmentCharge(EnchantSlots.Temporary));   // 0 charges: Item.ConsumeEnchantmentCharge returns at once
        }

        Assert.Equal(PoisonEnchant, ItemEnchantments.Id(rig.Sword, EnchantSlots.Temporary));
    }

    [Fact]
    public async Task APoisonAppliedAfterTheImport_GetsItsRetailCharges_AndRunsDown()
    {
        await using WorldDbContext db = await ImportedWorldAsync();
        IReadOnlyList<SpellEnchantCharges> loaded = await new EfSpellEnchantChargesStore(db).LoadAsync();
        using var rig = new PoisonRig(new SpellEnchantChargesCatalog(loaded));

        Assert.Equal(SpellCastResult.CastOk, rig.Apply(InstantPoison));

        Assert.Equal(PoisonEnchant, ItemEnchantments.Id(rig.Sword, EnchantSlots.Temporary));
        Assert.Equal(40u, ItemEnchantments.Charges(rig.Sword, EnchantSlots.Temporary));   // vmangos rows: Instant Poison 8679 = 40
        for (int used = 1; used <= 39; used++)
        {
            Assert.True(rig.Sword.ConsumeEnchantmentCharge(EnchantSlots.Temporary));
            Assert.Equal(40u - (uint)used, rig.Sword.EnchantmentCharges(EnchantSlots.Temporary));
        }

        // the last charge takes the poison off the weapon
        Assert.True(rig.Sword.ConsumeEnchantmentCharge(EnchantSlots.Temporary));
        Assert.Equal(0u, ItemEnchantments.Id(rig.Sword, EnchantSlots.Temporary));
        Assert.False(rig.Sword.ConsumeEnchantmentCharge(EnchantSlots.Temporary));
    }

    [Fact]
    public async Task TheChargesAreTheRowsOfTheSpellThatWasCast()
    {
        await using WorldDbContext db = await ImportedWorldAsync();
        IReadOnlyList<SpellEnchantCharges> loaded = await new EfSpellEnchantChargesStore(db).LoadAsync();
        using var rig = new PoisonRig(new SpellEnchantChargesCatalog(loaded));

        Assert.Equal(SpellCastResult.CastOk, rig.Apply(DeadlyPoison));

        Assert.Equal(60u, ItemEnchantments.Charges(rig.Sword, EnchantSlots.Temporary));
    }

    private static float FieryWeaponProcs(IReadOnlyList<ItemEnchantProc> procs, double roll)
    {
        // Fiery Weapon: an enchantment whose combat-spell effect carries amount 0 in SpellItemEnchantment.dbc, on a 2.0 s weapon.
        using var kit = new SpellTestKit(SpellTestKit.Spell(FieryWeaponSpell,
            SpellTestKit.Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)));
        (Player player, FakeSession session) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2, 0);
        kit.System.Random = new FixedRandom(roll);
        kit.System.ItemEnchantments = new ItemEnchantmentCatalog(
            [new ItemEnchantmentDefinition(FieryWeaponEnchant, [new ItemEnchantmentEffect(1, FieryWeaponSpell, 0)])], procs);
        player.Inventory.Templates = new ItemTemplateStore([new ItemTemplate { Entry = 49503, Class = 2, SubClass = 7,
            InventoryType = 21, Delay = 2000, Stackable = 1 }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([new InventoryItemData(0, InventorySlots.MainHand, new ItemInstanceData {
            Guid = 49504, Entry = 49503, Enchantments = [FieryWeaponEnchant, 0, 0, .. new uint[18]] })]);

        kit.System.HandleItemCombatProc(new MeleeDamageInfo { Attacker = player, Target = victim,
            AttackType = WeaponAttackType.BaseAttack, HitInfo = HitInfo.AffectsVictim,
            TargetState = VictimState.Normal, Outcome = MeleeHitOutcome.Normal });

        return session.Sent.Any(p => p.Opcode == WorldOpcode.SmsgSpellGo) ? 1f : 0f;
    }

    [Fact]
    public void FieryWeaponWithoutTheTable_UsesTheDelayOver600Fallback_AndMissesAtATenPercentRoll()
    {
        // fallback: Delay / 600 = 3.33 percent, so a roll of 10 misses
        Assert.Equal(0f, FieryWeaponProcs([], roll: 0.10));
        Assert.Equal(1f, FieryWeaponProcs([], roll: 0.03));
    }

    [Fact]
    public async Task FieryWeaponAfterTheImport_UsesTheRowsSixPpm_AndHitsAtATenPercentRoll()
    {
        await using WorldDbContext db = await ImportedWorldAsync();
        IReadOnlyList<ItemEnchantProc> procs = await new EfItemEnchantProcStore(db).LoadAsync();
        Assert.Equal(new ItemEnchantProc(FieryWeaponSpell, 6f), Assert.Single(procs));

        // row: 6 PPM on a 2.0 s weapon = 2000 * 6 / 600 = 20 percent, so a roll of 10 procs and a roll of 25 does not
        Assert.Equal(1f, FieryWeaponProcs(procs, roll: 0.10));
        Assert.Equal(0f, FieryWeaponProcs(procs, roll: 0.25));
    }

    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }
}
