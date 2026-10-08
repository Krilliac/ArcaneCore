using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Crafting;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Tests.Items;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Progression;

/// <summary>Item scores under each build's weights, weapon damage per second, random-suffix stats and the quest reward choice.</summary>
public sealed class PlayerbotItemScoreTests
{
    private static ItemTemplate Armor(uint entry, InventoryType type, int armor, params (ItemStatType Type, int Value)[] stats)
        => new()
        {
            Entry = entry, Class = (uint)ItemClass.Armor, InventoryType = (uint)type, Armor = armor, Stackable = 1,
            Stats = [.. stats.Select(stat => new ItemStat((uint)stat.Type, stat.Value))],
        };

    private static ItemTemplate Weapon(uint entry, InventoryType type, uint subClass, float min, float max, uint delay, params (ItemStatType Type, int Value)[] stats)
        => new()
        {
            Entry = entry, Class = (uint)ItemClass.Weapon, SubClass = subClass, InventoryType = (uint)type, Delay = delay, Stackable = 1,
            Damages = [new ItemDamage(min, max, 0)], Stats = [.. stats.Select(stat => new ItemStat((uint)stat.Type, stat.Value))],
        };

    [Fact]
    public void EachBuildRanksTheSameTwoItemsByItsOwnStats()
    {
        ItemTemplate strength = Armor(1, InventoryType.Chest, 100, (ItemStatType.Strength, 10));
        ItemTemplate intellect = Armor(2, InventoryType.Chest, 100, (ItemStatType.Intellect, 10));
        ItemTemplate stamina = Armor(3, InventoryType.Chest, 100, (ItemStatType.Stamina, 10));
        ItemTemplate spirit = Armor(4, InventoryType.Chest, 100, (ItemStatType.Intellect, 6), (ItemStatType.Spirit, 8));

        Assert.True(Score(strength, PlayerbotStatWeights.TwoHandStrength) > Score(intellect, PlayerbotStatWeights.TwoHandStrength));
        Assert.True(Score(intellect, PlayerbotStatWeights.Caster) > Score(strength, PlayerbotStatWeights.Caster));
        Assert.True(Score(stamina, PlayerbotStatWeights.ShieldTank) > Score(intellect, PlayerbotStatWeights.ShieldTank));
        Assert.True(Score(spirit, PlayerbotStatWeights.Healer) > Score(intellect, PlayerbotStatWeights.Healer));
        Assert.True(Score(spirit, PlayerbotStatWeights.Caster) < Score(intellect, PlayerbotStatWeights.Caster));
        // Every warrior, mage and priest build's own weights agree with the class-level expectation.
        foreach (PlayerbotTalentBuild build in PlayerbotTalentBuilds.For(Class.Warrior))
            Assert.True(Score(strength, build.Weights) > Score(intellect, build.Weights), build.Name);
        foreach (PlayerbotTalentBuild build in PlayerbotTalentBuilds.For(Class.Mage))
            Assert.True(Score(intellect, build.Weights) > Score(strength, build.Weights), build.Name);

        static float Score(ItemTemplate template, PlayerbotStatWeights weights) => PlayerbotItemScore.Score(template, weights);
    }

    [Fact]
    public void WeaponDps_IsTheAverageDamageOverTheSwingTime_AndCountsByWeaponKind()
    {
        ItemTemplate slow = Weapon(10, InventoryType.TwoHandWeapon, 8, 20, 40, 3000);
        Assert.Equal(10f, PlayerbotItemScore.WeaponDps(slow), 3);
        ItemTemplate fast = Weapon(11, InventoryType.TwoHandWeapon, 8, 30, 50, 2000);
        Assert.True(PlayerbotItemScore.Score(fast, PlayerbotStatWeights.TwoHandStrength) > PlayerbotItemScore.Score(slow, PlayerbotStatWeights.TwoHandStrength));
        // A caster's staff with intellect beats a harder-hitting one without; a hunter values the bow's damage, not a sword's.
        ItemTemplate staff = Weapon(12, InventoryType.TwoHandWeapon, 10, 10, 20, 3000, (ItemStatType.Intellect, 8));
        Assert.True(PlayerbotItemScore.Score(staff, PlayerbotStatWeights.Caster) > PlayerbotItemScore.Score(fast, PlayerbotStatWeights.Caster));
        ItemTemplate bow = Weapon(13, InventoryType.Ranged, 2, 10, 20, 3000);
        ItemTemplate sword = Weapon(14, InventoryType.Weapon, 7, 10, 20, 3000);
        Assert.True(PlayerbotItemScore.Score(bow, PlayerbotStatWeights.Hunter) > PlayerbotItemScore.Score(sword, PlayerbotStatWeights.Hunter));
        // A shield tank discounts a two-hander; a two-hand build discounts a one-hander.
        Assert.Equal(0.5f, PlayerbotItemScore.StyleFactor(fast, PlayerbotStatWeights.ShieldTank));
        Assert.Equal(0.8f, PlayerbotItemScore.StyleFactor(sword, PlayerbotStatWeights.TwoHandStrength));
    }

    [Fact]
    public void RandomSuffixStats_CountThroughTheEnchantmentCatalog_AndABrokenItemScoresNothing()
    {
        ItemTemplate suffixed = Armor(20, InventoryType.Legs, 50) with { RandomProperty = 1, MaxDurability = 30 };
        Item item = Item.Create(1, suffixed, ObjectGuid.Empty);
        item.SetUInt32(UpdateFields.ItemFieldMaxdurability, 30);
        item.Durability = 30;
        const uint OfTheBear = 5001;
        item.SetUInt32(UpdateFields.ItemFieldEnchantment + (3 * 3), OfTheBear); // a random property enchantment slot
        var bear = new SpellItemEnchantment(OfTheBear, [(uint)EnchantEffectType.Stat, (uint)EnchantEffectType.Stat, 0],
            [5, 5, 0], [(uint)ItemStatType.Strength, (uint)ItemStatType.Stamina, 0], "of the Bear", 0, 0);
        Func<uint, SpellItemEnchantment?> catalog = id => id == OfTheBear ? bear : null;

        float plain = PlayerbotItemScore.Score(item, PlayerbotStatWeights.TwoHandStrength, null);
        float withSuffix = PlayerbotItemScore.Score(item, PlayerbotStatWeights.TwoHandStrength, catalog);
        Assert.Equal(plain + (5 * 2.0f) + (5 * 0.6f), withSuffix, 3);
        item.Durability = 0;
        Assert.Equal(0f, PlayerbotItemScore.Score(item, PlayerbotStatWeights.TwoHandStrength, catalog));
    }

    /// <summary>
    /// The reward choice on a live warrior: the usable item that gains most over what is worn, not choice 0; a cloth caster
    /// item the warrior could wear but gains little from loses to a weapon upgrade; with nothing usable the richest choice wins.
    /// </summary>
    [Fact]
    public async Task QuestRewardChoice_PrefersTheBestUpgrade_ElseTheHighestSellPrice()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(PlayerbotEquipmentTests.Sword(5001, 2, 4, 2000));
        items.Templates.Templates.Add(Armor(5002, InventoryType.Wrists, 5, (ItemStatType.Intellect, 3)) with { AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue, SellPrice = 900 });
        items.Templates.Templates.Add(PlayerbotEquipmentTests.Sword(5003, 9, 15, 2000) with { SellPrice = 100 });
        items.Templates.Templates.Add(Armor(5004, InventoryType.Head, 10) with { AllowableClass = 1u << 7, SellPrice = 50 });   // mages only
        items.Templates.Templates.Add(Armor(5005, InventoryType.Feet, 10) with { AllowableClass = 1u << 7, SellPrice = 70 });
        using IDisposable scope = items.Use();
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                PlayerbotEquipmentTests.EquipFromBags(player, 5001);
                PlayerbotStatWeights weights = PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low).Weights;
                Assert.Equal(1u, PlayerbotItemScore.ChooseQuestReward(player, [5002, 5003, 0, 0, 0, 0], [1, 1, 0, 0, 0, 0], weights));
                Assert.Equal(1u, PlayerbotItemScore.ChooseQuestReward(player, [5004, 5005, 0, 0, 0, 0], [1, 1, 0, 0, 0, 0], weights));
                Assert.Equal(0u, PlayerbotItemScore.ChooseQuestReward(player, [5004, 5005, 0, 0, 0, 0], [3, 1, 0, 0, 0, 0], weights));
                Assert.Equal(0u, PlayerbotItemScore.ChooseQuestReward(player, [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0], weights));
                Quest quest = new QuestStore(new QuestContent([new QuestTemplate
                {
                    Entry = 1, RewChoiceItemId1 = 5002, RewChoiceItemCount1 = 1, RewChoiceItemId2 = 5003, RewChoiceItemCount2 = 1,
                }], [], [])).Get(1)!;
                Assert.Equal(1u, PlayerbotQuestGoals.RewardChoice(player, quest));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }
}
