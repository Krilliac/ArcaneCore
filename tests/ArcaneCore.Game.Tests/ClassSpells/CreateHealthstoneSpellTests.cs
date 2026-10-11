using ArcaneCore.Game.Crafting;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.Crafting;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.ClassSpells;

/// <summary>vmangos WarlockCreateHealthstoneScript, src/scripts/spells/spell_warlock.cpp:260-365.</summary>
public sealed class CreateHealthstoneSpellTests
{
    private const uint SoulShard = 6265;

    private static readonly (uint Spell, uint[] Items)[] Ranks =
    [
        (6201, [5512, 19004, 19005]),
        (6202, [5511, 19006, 19007]),
        (5699, [5509, 19008, 19009]),
        (11729, [5510, 19010, 19011]),
        (11730, [9421, 19012, 19013]),
    ];

    private static CraftingTestKit Rig()
    {
        SpellInfo[] spells = [.. Ranks.Select(rank => CraftingTestKit.Craft(rank.Spell,
            SpellTestKit.Effect(SpellEffectName.ScriptEffect, 0)) with
        {
            Reagents = [new SpellReagent(SoulShard, 1)],
        }),
            CraftingTestKit.Craft(18692, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            { Duration = new SpellDuration(-1, 0, -1) },
            CraftingTestKit.Craft(18693, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            { Duration = new SpellDuration(-1, 0, -1) },
        ];
        ItemTemplate[] items = [new ItemTemplate { Entry = SoulShard, Class = 7, Name = "Soul Shard", Stackable = 20, Quality = 1 },
            .. Ranks.SelectMany(rank => rank.Items).Select(id => new ItemTemplate
            { Entry = id, Class = 0, Name = "Healthstone", Stackable = 1, Quality = 1 })];
        var rig = new CraftingTestKit(spells, items);
        ReagentRules.Install(rig.System);
        SpellScriptDispatcher.Install(rig.System, SpellScriptRegistry.Discover(typeof(SpellSystem).Assembly));
        return rig;
    }

    [Theory]
    [InlineData(6201u, 5512u)]
    [InlineData(6202u, 5511u)]
    [InlineData(5699u, 5509u)]
    [InlineData(11729u, 5510u)]
    [InlineData(11730u, 9421u)]
    public void EachRank_ConsumesAShardAndCreatesItsHealthstone(uint spell, uint item)
    {
        using CraftingTestKit rig = Rig();
        rig.Give(SoulShard);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(spell));
        Assert.Equal(0u, rig.Inventory.GetItemCount(SoulShard));
        Assert.Equal(1u, rig.Inventory.GetItemCount(item));
    }

    [Theory]
    [InlineData(18692u, 19008u)]
    [InlineData(18693u, 19009u)]
    public void ImprovedHealthstoneAura_SelectsTheMatchingItem(uint talent, uint item)
    {
        using CraftingTestKit rig = Rig();
        rig.Give(SoulShard);
        rig.System.CastSpell(rig.Player, talent, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(5699));
        Assert.Equal(1u, rig.Inventory.GetItemCount(item));
        Assert.Equal(0u, rig.Inventory.GetItemCount(5509));
    }

    [Fact]
    public void FullBags_RefuseBeforeSpendingTheSoulShard()
    {
        using CraftingTestKit rig = Rig();
        rig.Give(SoulShard);
        while (rig.Inventory.AddItem(CraftingTestKit.Filler, 1, out _) == InventoryResult.Ok)
        {
        }

        Assert.Equal(SpellCastResult.DontReport, rig.Cast(5699));
        Assert.Equal(1u, rig.Inventory.GetItemCount(SoulShard));
        Assert.Equal(0u, rig.Inventory.GetItemCount(5509));
        Assert.Single(SpellTestKit.Packets(rig.Session, WorldOpcode.SmsgInventoryChangeFailure));
    }
}
