using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Crafting;

/// <summary>
/// Crafting lane, slice spell-cast-seams. The vmangos cast order is TakePower, TakeReagents, TakeAmmo, HandleEffects
/// (Spells/Spell.cpp:3716-3718), and a cast from an item carries it (Spell::m_CastItem).
/// </summary>
public sealed class SpellCostSeamTests
{
    private const uint Costly = 9701;

    private static SpellInfo CostlySpell() =>
        SpellTestKit.Spell(Costly, SpellTestKit.Effect(SpellEffectName.Dummy, 0)) with
        {
            PowerType = (int)PowerType.Rage,
            ManaCost = 30,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private sealed class RecordingTaker(List<string> log, Player player) : ISpellCostTaker
    {
        public void TakeCost(SpellCast cast) => log.Add($"taker rage={SpellSystem.GetPower(player, PowerType.Rage)} item={cast.CastItem?.Entry}");
    }

    private sealed class VetoCheck : ISpellCastCheck
    {
        public SpellCheckPhase Phase => SpellCheckPhase.Items;

        public int Order => SpellCastCheckOrder.Equipment + 1;

        public SpellCastResult Check(in SpellCastCheckContext context) => SpellCastResult.Reagents;
    }

    private sealed class ItemSeenCheck(List<string> log) : ISpellCastCheck
    {
        public SpellCheckPhase Phase => SpellCheckPhase.Items;

        public int Order => SpellCastCheckOrder.Equipment + 1;

        public SpellCastResult Check(in SpellCastCheckContext context)
        {
            log.Add($"check item={context.CastItem?.Entry}");
            return SpellCastResult.CastOk;
        }
    }

    private static Item MakeItem(Player player) =>
        new(7001, new ItemTemplate { Entry = 2589, Name = "Linen Cloth", DisplayId = 1 }, player.Guid);

    /// <summary>A Linen Cloth in <paramref name="player"/>'s backpack: the cast pipeline refuses a cast item its caster does not hold.</summary>
    private static Item HeldItem(Player player)
    {
        player.Inventory.Templates = new ItemTemplateStore([new ItemTemplate { Entry = 2589, Name = "Linen Cloth", DisplayId = 1, Stackable = 20 }]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(2589, 1, out Item? item));
        return item!;
    }

    [Fact]
    public void Taker_RunsOnce_AfterPowerIsSpent_AndBeforeTheEffect()
    {
        using var kit = new SpellTestKit(CostlySpell());
        (Player player, _) = kit.AddPlayer(1);
        var log = new List<string>();
        kit.System.RegisterCostTaker(new RecordingTaker(log, player));
        kit.System.RegisterEffect(SpellEffectName.Dummy, _ => log.Add("effect"));
        kit.Spellbook.Teach(player, Costly);
        SpellSystem.SetPower(player, PowerType.Rage, 100);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(player, Costly, SpellCastTargets.ForSelf()));

        Assert.Equal(["taker rage=70 item=", "effect"], log);
    }

    [Fact]
    public void Taker_IsNotCalled_WhenACheckFails()
    {
        using var kit = new SpellTestKit(CostlySpell());
        (Player player, _) = kit.AddPlayer(1);
        var log = new List<string>();
        kit.System.RegisterCostTaker(new RecordingTaker(log, player));
        kit.System.RegisterCastCheck(new VetoCheck());
        kit.Spellbook.Teach(player, Costly);
        SpellSystem.SetPower(player, PowerType.Rage, 100);

        Assert.Equal(SpellCastResult.Reagents, kit.System.HandleCastRequest(player, Costly, SpellCastTargets.ForSelf()));

        Assert.Empty(log);
        Assert.Equal(100u, SpellSystem.GetPower(player, PowerType.Rage));
    }

    [Fact]
    public void RegisteringTheSameTakerTwice_Throws()
    {
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1);
        var taker = new RecordingTaker([], player);
        kit.System.RegisterCostTaker(taker);

        Assert.Throws<InvalidOperationException>(() => kit.System.RegisterCostTaker(taker));
    }

    [Fact]
    public void CastItemSpell_ExposesTheItem_ToChecksTakersAndHandlers()
    {
        using var kit = new SpellTestKit(CostlySpell());
        (Player player, _) = kit.AddPlayer(1);
        Item item = HeldItem(player);
        var log = new List<string>();
        kit.System.RegisterCastCheck(new ItemSeenCheck(log));
        kit.System.RegisterCostTaker(new RecordingTaker(log, player));
        kit.System.RegisterEffect(SpellEffectName.Dummy, ctx => log.Add($"effect item={ctx.Cast.CastItem?.Entry}"));
        SpellSystem.SetPower(player, PowerType.Rage, 100);

        // The spell is not in the spellbook: an item spell never needs to be known (Player::CastItemUseSpell).
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastItemSpell(player, item, Costly, SpellCastTargets.ForSelf()));

        Assert.Contains("check item=2589", log);
        Assert.Contains("taker rage=100 item=2589", log);   // an item cast takes no power (vmangos Spell::TakePower, Spell.cpp:5053)
        Assert.Contains("effect item=2589", log);
    }

    [Fact]
    public void CastItemSpell_UnknownSpell_IsNotFound()
    {
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.NotFound, kit.System.CastItemSpell(player, MakeItem(player), 424242, SpellCastTargets.ForSelf()));
    }
}
