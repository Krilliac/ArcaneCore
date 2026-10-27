using ArcaneCore.Game.Crafting;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Spells.Warlock;
using ArcaneCore.Game.Tests.Crafting;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Create Healthstone (vmangos scripts/spells/spell_warlock.cpp:260-368, WarlockCreateHealthstoneScript). Spell rows follow build 5875: one
/// SCRIPT_EFFECT on the caster, no item type, one Soul Shard (6265) reagent; Improved Healthstone 18692 / 18693 are passive DUMMY auras. Item
/// templates follow vmangos item_template: every healthstone is maxcount 1, stackable 1. The rig installs the production pieces in production
/// order: the reagent pair, the SCRIPT_EFFECT owner module, then the script dispatcher.
/// </summary>
public sealed class CreateHealthstoneScriptTests
{
    private static readonly uint[] HealthstoneItems =
        [5512, 19004, 19005, 5511, 19006, 19007, 5509, 19008, 19009, 5510, 19010, 19011, 9421, 19012, 19013];

    private static ItemTemplate[] Templates() =>
    [
        new ItemTemplate { Entry = SoulShardRules.SoulShard, Class = 15, Name = "Soul Shard", DisplayId = 6, Stackable = 1, Quality = 1 },
        .. HealthstoneItems.Select(entry => new ItemTemplate
        {
            Entry = entry, Class = 0, SubClass = 6, Name = $"Healthstone {entry}", DisplayId = 8026, Stackable = 1, MaxCount = 1, Quality = 1,
        }),
    ];

    private static SpellInfo Create(uint id) =>
        CraftingTestKit.Craft(id, SpellTestKit.Effect(SpellEffectName.ScriptEffect, 1)) with
        {
            SpellFamilyName = SoulShardRules.WarlockFamily,
            Reagents = [new SpellReagent(SoulShardRules.SoulShard, 1)],
        };

    private static SpellInfo Talent(uint id) =>
        SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.ApplyAura, 10, aura: AuraType.Dummy)) with
        {
            Attributes = SpellAttributes.Passive,
            Duration = new SpellDuration(-1, 0, -1),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static CraftingTestKit Rig(uint talent = 0)
    {
        var rig = new CraftingTestKit(
            [
                Create(CreateHealthstoneScript.CreateHealthstoneMinor),
                Create(CreateHealthstoneScript.CreateHealthstoneLesser),
                Create(CreateHealthstoneScript.CreateHealthstone),
                Create(CreateHealthstoneScript.CreateHealthstoneGreater),
                Create(CreateHealthstoneScript.CreateHealthstoneMajor),
                Talent(CreateHealthstoneScript.ImprovedHealthstoneRank1),
                Talent(CreateHealthstoneScript.ImprovedHealthstoneRank2),
            ],
            Templates());
        ReagentRules.Install(rig.System);
        CreateItemSpells.Install(rig.System);
        new ScriptEffectModule().Register(rig.System);
        SpellScriptDispatcher.Install(rig.System, new SpellScriptRegistry([new CreateHealthstoneScript()]));
        if (talent != 0)
        {
            Assert.Equal(SpellCastResult.CastOk, rig.System.CastSpell(rig.Player, talent, SpellCastTargets.ForSelf(), triggered: true));
        }

        return rig;
    }

    /// <summary>Casts and, with spell batching on, advances one window so the held go resolves (window 0: no-op).</summary>
    private static SpellCastResult Cast(CraftingTestKit rig, uint spell)
    {
        SpellCastResult result = rig.Cast(spell);
        rig.Kit.Advance(rig.System.SpellBatchWindowMs);
        return result;
    }

    private static void GiveShards(CraftingTestKit rig, int count)
    {
        for (int i = 0; i < count; i++)
        {
            rig.Give(SoulShardRules.SoulShard);
        }
    }

    [Theory]
    [InlineData(CreateHealthstoneScript.CreateHealthstoneMinor, 0u, 5512u)]
    [InlineData(CreateHealthstoneScript.CreateHealthstoneLesser, 0u, 5511u)]
    [InlineData(CreateHealthstoneScript.CreateHealthstone, 0u, 5509u)]
    [InlineData(CreateHealthstoneScript.CreateHealthstoneGreater, 0u, 5510u)]
    [InlineData(CreateHealthstoneScript.CreateHealthstoneMajor, 0u, 9421u)]
    [InlineData(CreateHealthstoneScript.CreateHealthstoneMinor, CreateHealthstoneScript.ImprovedHealthstoneRank1, 19004u)]
    [InlineData(CreateHealthstoneScript.CreateHealthstoneLesser, CreateHealthstoneScript.ImprovedHealthstoneRank1, 19006u)]
    [InlineData(CreateHealthstoneScript.CreateHealthstone, CreateHealthstoneScript.ImprovedHealthstoneRank1, 19008u)]
    [InlineData(CreateHealthstoneScript.CreateHealthstoneGreater, CreateHealthstoneScript.ImprovedHealthstoneRank1, 19010u)]
    [InlineData(CreateHealthstoneScript.CreateHealthstoneMajor, CreateHealthstoneScript.ImprovedHealthstoneRank1, 19012u)]
    [InlineData(CreateHealthstoneScript.CreateHealthstoneMinor, CreateHealthstoneScript.ImprovedHealthstoneRank2, 19005u)]
    [InlineData(CreateHealthstoneScript.CreateHealthstoneLesser, CreateHealthstoneScript.ImprovedHealthstoneRank2, 19007u)]
    [InlineData(CreateHealthstoneScript.CreateHealthstone, CreateHealthstoneScript.ImprovedHealthstoneRank2, 19009u)]
    [InlineData(CreateHealthstoneScript.CreateHealthstoneGreater, CreateHealthstoneScript.ImprovedHealthstoneRank2, 19011u)]
    [InlineData(CreateHealthstoneScript.CreateHealthstoneMajor, CreateHealthstoneScript.ImprovedHealthstoneRank2, 19013u)]
    public void EachRank_CreatesItsHealthstone_TheImprovedOneWithTheTalent_AndTakesOneSoulShard(uint spell, uint talent, uint expected)
    {
        using CraftingTestKit rig = Rig(talent);
        GiveShards(rig, 3);

        Assert.Equal(SpellCastResult.CastOk, Cast(rig, spell));

        Assert.Equal(1u, rig.Inventory.GetItemCount(expected));
        Assert.Equal(1, rig.Inventory.AllItems.Count(item => HealthstoneItems.Contains(item.Entry)));
        Assert.Equal(2u, rig.Inventory.GetItemCount(SoulShardRules.SoulShard));   // the reagent path takes exactly one shard
        byte[] push = Assert.Single(SpellTestKit.Packets(rig.Session, WorldOpcode.SmsgItemPushResult));
        Assert.Equal(1, push[12]);   // created
    }

    [Fact]
    public void AHeldHealthstone_IsTheUniqueLimit_TheCastIsRefusedWithDontReport_AndNoShardIsTaken()
    {
        using CraftingTestKit rig = Rig();
        GiveShards(rig, 2);
        rig.Give(5509);

        Assert.Equal(SpellCastResult.DontReport, Cast(rig, CreateHealthstoneScript.CreateHealthstone));

        Assert.Equal(1u, rig.Inventory.GetItemCount(5509));
        Assert.Equal(2u, rig.Inventory.GetItemCount(SoulShardRules.SoulShard));
        byte[] error = Assert.Single(SpellTestKit.Packets(rig.Session, WorldOpcode.SmsgInventoryChangeFailure));
        Assert.Equal((byte)InventoryResult.CantCarryMoreOfThis, error[0]);
        byte[] reply = Assert.Single(SpellTestKit.Packets(rig.Session, WorldOpcode.SmsgCastResult));
        Assert.Equal((byte)SpellCastResult.DontReport, reply[5]);
    }

    [Fact]
    public void TheUniqueLimitIsPerItem_APlainHealthstoneDoesNotBlockTheImprovedOne()
    {
        // Each improved healthstone is its own entry with its own maxcount (vmangos item_template), so CanStoreNewItem only counts that entry.
        using CraftingTestKit rig = Rig(CreateHealthstoneScript.ImprovedHealthstoneRank2);
        GiveShards(rig, 1);
        rig.Give(5509);

        Assert.Equal(SpellCastResult.CastOk, Cast(rig, CreateHealthstoneScript.CreateHealthstone));

        Assert.Equal(1u, rig.Inventory.GetItemCount(19009));
        Assert.Equal(0u, rig.Inventory.GetItemCount(SoulShardRules.SoulShard));
    }

    [Fact]
    public void FullBags_RefuseWithTheEquipError_AndDontReport_AndConsumeNothing()
    {
        using CraftingTestKit rig = Rig();
        GiveShards(rig, 1);
        while (rig.Inventory.AddItem(CraftingTestKit.Filler, 1, out _) == InventoryResult.Ok)
        {
        }

        Assert.Equal(SpellCastResult.DontReport, Cast(rig, CreateHealthstoneScript.CreateHealthstoneMajor));

        Assert.Equal(0u, rig.Inventory.GetItemCount(9421));
        Assert.Equal(1u, rig.Inventory.GetItemCount(SoulShardRules.SoulShard));
        byte[] error = Assert.Single(SpellTestKit.Packets(rig.Session, WorldOpcode.SmsgInventoryChangeFailure));
        Assert.Equal((byte)InventoryResult.InventoryFull, error[0]);
    }

    [Fact]
    public void WithoutASoulShard_TheReagentCheckRefusesFirst()
    {
        using CraftingTestKit rig = Rig();

        Assert.Equal(SpellCastResult.ItemNotReady, Cast(rig, CreateHealthstoneScript.CreateHealthstone));

        Assert.Equal(0u, rig.Inventory.GetItemCount(5509));
    }

    [Fact]
    public void TheProductionRegistry_DiscoversTheScript_ForAllFiveRanks()
    {
        SpellScriptRegistry registry = SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly);
        foreach (uint id in new[] { 5699u, 6201u, 6202u, 11729u, 11730u })
        {
            Assert.IsType<CreateHealthstoneScript>(registry.Find(id));
        }
    }
}
