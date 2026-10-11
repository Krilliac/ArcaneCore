using ArcaneCore.Game;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.Gm.Character.Boost;
using ArcaneCore.World.Playerbots.Progression;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Character;

/// <summary>
/// The pure planners behind <c>.character boost</c>: the action-bar layout (<see cref="BoostActionBarPlanner"/>) and the gear
/// pool and per-slot choice (<see cref="BoostGearPlanner"/>). Button values are the vmangos / mangos-classic packing
/// <c>action | type &lt;&lt; 24</c> with ACTION_BUTTON_SPELL = 0x00, ACTION_BUTTON_MACRO = 0x40 and ACTION_BUTTON_ITEM = 0x80
/// (mangos-classic Player.h:150-154); Attack is spell 6603 and the warrior start bar is the Battle Stance page at button 72
/// (ClassicDB playercreateinfo_action rows (1,1,72,6603,0) and (1,1,73,78,0), cited on <see cref="BoostActionBarPlanner"/>). The
/// spell, skill and item ids below other than 6603 are fixtures that only have to be consistent with each other.
/// </summary>
public sealed class CharacterBoostPlannerTests
{
    private const uint WarriorFamily = 4;   // vmangos SpellDefines.h SPELLFAMILY_WARRIOR
    private const uint MageFamily = 3;      // SPELLFAMILY_MAGE
    private const uint ItemType = 0x80u << 24;
    private const uint MacroType = 0x40u << 24;

    private static Func<uint, SpellInfo?> Lookup(params SpellInfo[] spells)
    {
        Dictionary<uint, SpellInfo> byId = spells.ToDictionary(s => s.Id);
        return id => byId.GetValueOrDefault(id);
    }

    private static SpellInfo Spell(uint id, uint level, uint family = WarriorFamily, SpellAttributes attributes = 0, SpellAttributesEx2 ex2 = 0)
        => new() { Id = id, SpellLevel = level, SpellFamilyName = family, Attributes = attributes, AttributesEx2 = ex2 };

    private static SpellRankChains Chain(params (uint Spell, uint Forward)[] links)
        => new(links.Select((l, i) => new SkillLineAbilityRecord((uint)i + 1, 26, l.Spell, 0, 0, 0, l.Forward, 0, 0, 0)));

    private static uint[] Bar() => new uint[ArcaneCore.Game.Entities.Player.ActionButtonCount];

    private static uint[] Apply(uint[] bar, IEnumerable<BoostActionButtonWrite> writes)
    {
        uint[] result = [.. bar];
        foreach (BoostActionButtonWrite write in writes)
        {
            result[write.Button] = write.Packed;
        }

        return result;
    }

    [Fact]
    public void ActionBar_PlacesAttackFirst_ThenClassSpellsBySpellLevel_SkipsPassivesAndShapeshiftBarSpells()
    {
        var lookup = Lookup(
            Spell(300, 4),
            Spell(301, 1),
            Spell(302, 2, attributes: SpellAttributes.Passive),
            Spell(303, 3, ex2: (SpellAttributesEx2)0x10),            // SPELL_ATTR_EX2_USE_SHAPESHIFT_BAR
            Spell(304, 5, attributes: SpellAttributes.DoNotDisplay),
            Spell(305, 6, family: MageFamily),                        // another class's family
            Spell(306, 4));                                           // same level as 300: the lower id first

        // 307 is known but absent from the spell store: it is skipped, not placed as a bare id.
        IReadOnlyList<BoostActionButtonWrite> writes = BoostActionBarPlanner.Plan(
            [300, 301, 302, 303, 304, 305, 306, 307, BoostActionBarPlanner.Attack], lookup, SpellRankChains.Empty,
            WarriorFamily, 0, Bar());

        Assert.Equal(
            [new BoostActionButtonWrite(0, 6603), new BoostActionButtonWrite(1, 301), new BoostActionButtonWrite(2, 300), new BoostActionButtonWrite(3, 306)],
            writes);
    }

    [Fact]
    public void ActionBar_AttackIsOnlyPlacedWhenKnown_AndAClassWithoutAFamilyGetsNothingElse()
    {
        var lookup = Lookup(Spell(300, 1));
        Assert.Empty(BoostActionBarPlanner.Plan([300], lookup, SpellRankChains.Empty, 0, 0, Bar()));
        Assert.Equal([new BoostActionButtonWrite(0, 6603)], BoostActionBarPlanner.Plan([6603, 300], lookup, SpellRankChains.Empty, 0, 0, Bar()));
        Assert.Empty(BoostActionBarPlanner.Plan([], lookup, SpellRankChains.Empty, WarriorFamily, 0, Bar()));
    }

    [Fact]
    public void ActionBar_UpgradesLowerRanksInPlace_AndNeverDuplicates()
    {
        // 100 -> 101 -> 102 is one rank chain (SkillLineAbility forward links, vmangos LoadSpellChains).
        SpellRankChains ranks = Chain((100, 101), (101, 102), (102, 0));
        var lookup = Lookup(Spell(100, 4), Spell(101, 12), Spell(102, 20), Spell(200, 6));
        uint[] bar = Bar();
        bar[5] = 100;        // a rank 1 button the player placed
        bar[9] = 101;        // a second button with rank 2: also upgraded, never left behind

        IReadOnlyList<BoostActionButtonWrite> writes = BoostActionBarPlanner.Plan([100, 101, 102, 200], lookup, ranks, WarriorFamily, 0, bar);
        uint[] result = Apply(bar, writes);

        Assert.Equal(102u, result[5]);
        Assert.Equal(102u, result[9]);
        Assert.DoesNotContain(100u, result);
        Assert.DoesNotContain(101u, result);
        Assert.Single(result, v => v == 200u);
        Assert.Empty(BoostActionBarPlanner.Plan([100, 101, 102, 200], lookup, ranks, WarriorFamily, 0, result));   // settled
    }

    [Fact]
    public void ActionBar_PlacesOnlyTheHighestKnownRank()
    {
        SpellRankChains ranks = Chain((100, 101), (101, 102), (102, 0));
        var lookup = Lookup(Spell(100, 4), Spell(101, 12), Spell(102, 20));

        Assert.Equal([new BoostActionButtonWrite(0, 101)], BoostActionBarPlanner.Plan([100, 101], lookup, ranks, WarriorFamily, 0, Bar()));
    }

    [Fact]
    public void ActionBar_KeepsItemAndMacroButtons_AndOverflowsToPageTwo()
    {
        uint[] bar = Bar();
        for (int i = 0; i < 11; i++)
        {
            bar[i] = (i % 2 == 0 ? ItemType : MacroType) | (uint)(1000 + i);
        }

        bar[12] = ItemType | 2000;   // the overflow page is not empty either
        SpellInfo[] known = [.. Enumerable.Range(0, 20).Select(i => Spell((uint)(400 + i), (uint)(i + 1)))];

        IReadOnlyList<BoostActionButtonWrite> writes = BoostActionBarPlanner.Plan(
            [BoostActionBarPlanner.Attack, .. known.Select(s => s.Id)], Lookup(known), SpellRankChains.Empty, WarriorFamily, 0, bar);
        uint[] result = Apply(bar, writes);

        for (int i = 0; i < 11; i++)
        {
            Assert.Equal(bar[i], result[i]);           // item and macro buttons are untouched
        }

        Assert.Equal(ItemType | 2000, result[12]);
        Assert.Equal(6603u, result[11]);               // the one free primary button
        Assert.Equal(400u, result[13]);                // then the overflow page (buttons 12..23), skipping the occupied 12
        Assert.Equal(410u, result[23]);
        Assert.Equal(0u, result[24]);                  // 1 + 11 = 12 placed; the rest do not fit and nothing spills past page two
        Assert.DoesNotContain(writes, w => w.Button >= 24);
        Assert.Equal(12, writes.Count);
    }

    [Fact]
    public void ActionBar_Warrior_UsesTheBattleStancePageAt72()
    {
        Assert.Equal(72, BoostActionBarPlanner.PrimaryStart(Class.Warrior));
        foreach (Class other in new[] { Class.Paladin, Class.Hunter, Class.Rogue, Class.Priest, Class.Shaman, Class.Mage, Class.Warlock, Class.Druid })
        {
            Assert.Equal(0, BoostActionBarPlanner.PrimaryStart(other));
        }

        var lookup = Lookup(Spell(301, 1), Spell(302, 2));
        IReadOnlyList<BoostActionButtonWrite> writes = BoostActionBarPlanner.Plan(
            [6603, 301, 302], lookup, SpellRankChains.Empty, WarriorFamily, BoostActionBarPlanner.PrimaryStart(Class.Warrior), Bar());
        Assert.Equal([new BoostActionButtonWrite(72, 6603), new BoostActionButtonWrite(73, 301), new BoostActionButtonWrite(74, 302)], writes);

        // A full Battle Stance page spills into buttons 12.. (page two), never into the Defensive or Berserker pages.
        SpellInfo[] many = [.. Enumerable.Range(0, 14).Select(i => Spell((uint)(500 + i), (uint)(i + 1)))];
        uint[] result = Apply(Bar(), BoostActionBarPlanner.Plan([.. many.Select(s => s.Id)], Lookup(many), SpellRankChains.Empty, WarriorFamily, 72, Bar()));
        Assert.Equal(500u, result[72]);
        Assert.Equal(511u, result[83]);
        Assert.Equal(512u, result[12]);
        Assert.Equal(513u, result[13]);
        Assert.Equal(0u, result[84]);
    }

    [Fact]
    public void ActionBar_Plan_RejectsNullInputs()
    {
        Assert.Throws<ArgumentNullException>(() => BoostActionBarPlanner.Plan(null!, _ => null, SpellRankChains.Empty, 4, 0, Bar()));
        Assert.Throws<ArgumentNullException>(() => BoostActionBarPlanner.Plan([], null!, SpellRankChains.Empty, 4, 0, Bar()));
        Assert.Throws<ArgumentNullException>(() => BoostActionBarPlanner.Plan([], _ => null, null!, 4, 0, Bar()));
    }

    private static PlayerbotStatWeights Weights(float strength = 0f, float armor = 0f)
        => new("test", strength, 0f, 0f, 0f, 0f, armor, 0f, 0f, 0f, 0f, PlayerbotWeaponStyle.OneHandShield);

    private static ItemTemplate Piece(uint entry, InventoryType type, uint itemLevel = 1, int strength = 0, int armor = 0)
        => new()
        {
            Entry = entry, Name = $"piece-{entry}", Class = (uint)ItemClass.Armor, SubClass = ItemSubClasses.ArmorMisc,
            InventoryType = (uint)type, ItemLevel = itemLevel, Armor = armor, Quality = 2, Stackable = 1,
            AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue,
            Stats = strength == 0 ? [] : [new ItemStat((uint)ItemStatType.Strength, strength)],
        };

    private static ItemTemplate? PickFor(BoostGearPlan plan, byte slot)
        => plan.Picks.Where(p => p.Key == slot).Select(p => p.Value).SingleOrDefault();

    [Fact]
    public void Gear_OrderIsDeterministic_ScoreThenItemLevelThenEntry()
    {
        // Strength is worth 0.01 and item level 0.01 per point (PlayerbotItemScore.ItemLevelWeight), so these two score exactly the same
        // (2 * 0.01f against 0.01f + 0.01f, both exact doublings): the higher item level wins even with the higher entry.
        PlayerbotStatWeights weights = Weights(strength: 0.01f);
        ItemTemplate highLevel = Piece(9, InventoryType.Head, itemLevel: 2);
        ItemTemplate lowLevel = Piece(3, InventoryType.Head, itemLevel: 1, strength: 1);
        Assert.Equal(PlayerbotItemScore.Score(highLevel, weights), PlayerbotItemScore.Score(lowLevel, weights));

        foreach (ItemTemplate[] order in new[] { new[] { highLevel, lowLevel }, new[] { lowLevel, highLevel } })
        {
            Assert.Equal(9u, PickFor(BoostGearPlanner.Plan(order, weights, Class.Warrior, false, _ => true), InventorySlots.Head)!.Entry);
        }

        // A strictly higher score beats a higher item level.
        ItemTemplate strong = Piece(30, InventoryType.Head, itemLevel: 1, strength: 5);
        foreach (ItemTemplate[] order in new[] { new[] { highLevel, strong, lowLevel }, new[] { strong, lowLevel, highLevel } })
        {
            Assert.Equal(30u, PickFor(BoostGearPlanner.Plan(order, weights, Class.Warrior, false, _ => true), InventorySlots.Head)!.Entry);
        }

        // Identical score and item level: the lower entry, whatever the pool order.
        ItemTemplate twinA = Piece(8, InventoryType.Chest, itemLevel: 4, strength: 2);
        ItemTemplate twinB = Piece(5, InventoryType.Chest, itemLevel: 4, strength: 2);
        ItemTemplate twinC = Piece(6, InventoryType.Chest, itemLevel: 4, strength: 2);
        foreach (ItemTemplate[] order in new[] { new[] { twinA, twinB, twinC }, new[] { twinC, twinA, twinB }, new[] { twinB, twinC, twinA } })
        {
            Assert.Equal(5u, PickFor(BoostGearPlanner.Plan(order, weights, Class.Warrior, false, _ => true), InventorySlots.Chest)!.Entry);
        }
    }

    [Fact]
    public void Gear_RingsAndTrinkets_UseTwoDistinctEntries()
    {
        PlayerbotStatWeights weights = Weights(strength: 1f);
        ItemTemplate[] pool =
        [
            Piece(1, InventoryType.Finger, strength: 3), Piece(2, InventoryType.Finger, strength: 2), Piece(3, InventoryType.Finger, strength: 1),
            Piece(11, InventoryType.Trinket, strength: 3), Piece(12, InventoryType.Trinket, strength: 2),
        ];

        BoostGearPlan plan = BoostGearPlanner.Plan(pool, weights, Class.Warrior, false, _ => true);

        Assert.Equal(1u, PickFor(plan, InventorySlots.Finger1)!.Entry);
        Assert.Equal(2u, PickFor(plan, InventorySlots.Finger2)!.Entry);
        Assert.Equal(11u, PickFor(plan, InventorySlots.Trinket1)!.Entry);
        Assert.Equal(12u, PickFor(plan, InventorySlots.Trinket2)!.Entry);

        // One ring only: the first finger takes it and the second is reported empty, never a duplicate.
        BoostGearPlan single = BoostGearPlanner.Plan([Piece(1, InventoryType.Finger, strength: 3)], weights, Class.Warrior, false, _ => true);
        Assert.Equal(1u, PickFor(single, InventorySlots.Finger1)!.Entry);
        Assert.Null(PickFor(single, InventorySlots.Finger2));
        Assert.Contains(InventorySlots.Finger2, single.EmptySlots);
    }

    [Fact]
    public void Gear_SlotWithNoUsableCandidate_IsReportedEmpty_AndTheServersWearRuleIsHonoured()
    {
        PlayerbotStatWeights weights = Weights(strength: 1f);
        ItemTemplate forbidden = Piece(1, InventoryType.Head, strength: 9);
        ItemTemplate allowed = Piece(2, InventoryType.Head, strength: 1);

        BoostGearPlan plan = BoostGearPlanner.Plan([forbidden, allowed], weights, Class.Warrior, false, t => t.Entry != 1);
        Assert.Equal(2u, PickFor(plan, InventorySlots.Head)!.Entry);
        Assert.Contains(InventorySlots.Chest, plan.EmptySlots);
        Assert.DoesNotContain(InventorySlots.Head, plan.EmptySlots);

        // An empty pool leaves every slot but the off hand (skipped, not listed, only behind a two-hander) empty.
        Assert.Equal(BoostGearPlanner.SlotOrder.Length, BoostGearPlanner.Plan([], weights, Class.Warrior, false, _ => true).EmptySlots.Count);
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("quality", false)]
    [InlineData("requiredLevel", false)]
    [InlineData("itemLevel", false)]
    [InlineData("randomProperty", false)]
    [InlineData("deprecated", false)]
    [InlineData("notObtainable", false)]
    [InlineData("requiredSkill", false)]
    [InlineData("requiredSpell", false)]
    [InlineData("reputation", false)]
    [InlineData("honor", false)]
    [InlineData("startQuest", false)]
    [InlineData("duration", false)]
    [InlineData("questBound", false)]
    [InlineData("shirt", false)]
    [InlineData("tabard", false)]
    [InlineData("bag", false)]
    [InlineData("ammo", false)]
    [InlineData("noSource", false)]
    public void Pool_AdmitsOnlyGreenOrLowerObtainableLevelAppropriateEquipment(string defect, bool admitted)
    {
        const byte level = 14;
        ItemTemplate good = Piece(100, InventoryType.Chest, itemLevel: level) with { RequiredLevel = level };
        ItemTemplate item = defect switch
        {
            "quality" => good with { Quality = 3 },
            "requiredLevel" => good with { RequiredLevel = level + 1 },
            "itemLevel" => good with { ItemLevel = level + 1 },
            "randomProperty" => good with { RandomProperty = 5 },
            "deprecated" => good with { Flags = (uint)ItemTemplateFlags.Deprecated },
            "notObtainable" => good with { ExtraFlags = 0x04 },   // vmangos ItemPrototype.h ITEM_EXTRA_NOT_OBTAINABLE
            "requiredSkill" => good with { RequiredSkill = 164 },
            "requiredSpell" => good with { RequiredSpell = 1 },
            "reputation" => good with { RequiredReputationFaction = 72 },
            "honor" => good with { RequiredHonorRank = 1 },
            "startQuest" => good with { StartQuest = 1 },
            "duration" => good with { Duration = 3600 },
            "questBound" => good with { Bonding = 4 },            // BIND_QUEST_ITEM
            "shirt" => good with { InventoryType = (uint)InventoryType.Body },
            "tabard" => good with { InventoryType = (uint)InventoryType.Tabard },
            "bag" => good with { InventoryType = (uint)InventoryType.Bag },
            "ammo" => good with { InventoryType = (uint)InventoryType.Ammo },
            _ => good,
        };
        HashSet<uint> sources = defect == "noSource" ? [999] : [100];

        Assert.Equal(admitted, BoostGearPlanner.IsPoolItem(item, level, sources));
    }

    [Fact]
    public void Pool_AdmitsTheBoundaryLevelAndGreenQuality_AndRejectsNulls()
    {
        ItemTemplate edge = Piece(7, InventoryType.Head, itemLevel: 14) with { RequiredLevel = 14, Quality = 2 };
        Assert.True(BoostGearPlanner.IsPoolItem(edge, 14, new HashSet<uint> { 7 }));
        Assert.False(BoostGearPlanner.IsPoolItem(edge, 13, new HashSet<uint> { 7 }));
        Assert.Throws<ArgumentNullException>(() => BoostGearPlanner.IsPoolItem(null!, 14, new HashSet<uint>()));
        Assert.Throws<ArgumentNullException>(() => BoostGearPlanner.IsPoolItem(edge, 14, null!));
    }

    private const uint FireFestivalCrown = 23323;     // Crown of the Fire Festival, a head item rewarded by Midsummer quests
    private const uint FireFestivalMantle = 23324;    // Mantle of the Fire Festival, the shoulder item
    private const uint EventQuestId = 11580;          // a fixture id listed in game_event_quest
    private const uint PermanentQuestId = 11581;
    private const uint EventVendorEntry = 16000;      // spawns only through game_event_creature
    private const uint MixedVendorEntry = 16001;      // one permanent spawn and one event spawn
    private const uint PlainHead = 7100;
    private const uint EventVendorHead = 7101;
    private const uint MixedVendorHead = 7102;

    private static ItemTemplate Obtainable(uint entry, InventoryType type, int strength)
        => Piece(entry, type, itemLevel: 1, strength: strength) with { RequiredLevel = 1 };

    private static (NpcStore Npcs, QuestStore Quests, GameEventRows Rows, CreatureContent Creatures) EventFixture()
    {
        var npcs = new NpcStore(NpcContent.Empty with
        {
            VendorItems =
            [
                new VendorItem { Entry = EventVendorEntry, Item = EventVendorHead },
                new VendorItem { Entry = MixedVendorEntry, Item = MixedVendorHead },
                new VendorItem { Entry = 16002, Item = PlainHead },
            ],
        });
        var quests = new QuestStore(new QuestContent(
        [
            new QuestTemplate { Entry = EventQuestId, RewItemId1 = FireFestivalCrown, RewChoiceItemId1 = FireFestivalMantle },
            new QuestTemplate { Entry = PermanentQuestId, RewItemId1 = PlainHead },
        ], [], []));
        var rows = new GameEventRows
        {
            Quests = new Dictionary<ushort, IReadOnlyList<uint>> { [1] = new uint[] { EventQuestId } },
            // Event 1 spawns guids 501 and 503; event -2 REMOVES guid 502 during it (a permanent creature), which binds nothing.
            Creatures = new Dictionary<int, IReadOnlyList<uint>> { [1] = new uint[] { 501, 503 }, [-2] = new uint[] { 502 } },
        };
        var creatures = new CreatureContent([], [
            new CreatureSpawn { Guid = 501, Entry = EventVendorEntry, MapId = 0, X = 0, Y = 0, Z = 0 },
            new CreatureSpawn { Guid = 502, Entry = 16002, MapId = 0, X = 0, Y = 0, Z = 0 },
            new CreatureSpawn { Guid = 503, Entry = MixedVendorEntry, MapId = 0, X = 0, Y = 0, Z = 0 },
            new CreatureSpawn { Guid = 504, Entry = MixedVendorEntry, MapId = 0, X = 0, Y = 0, Z = 0 },
        ], [], [], []);
        return (npcs, quests, rows, creatures);
    }

    [Fact]
    public void EventBinding_NamesTheEventQuests_AndOnlyTheEntriesThatSpawnThroughAnEventAlone()
    {
        var (_, _, rows, creatures) = EventFixture();

        BoostEventBinding binding = BoostEventBinding.From(rows, creatures);

        Assert.Equal([EventQuestId], binding.Quests.ToArray());
        Assert.Equal([EventVendorEntry], binding.CreatureEntries.ToArray());   // the mixed vendor has a permanent spawn; guid 502 is a negative-event removal
    }

    [Fact]
    public void Sources_ExcludeEventQuestRewards_AndEventOnlyVendorWares_ButKeepEverythingElse()
    {
        var (npcs, quests, rows, creatures) = EventFixture();

        HashSet<uint> withEvents = BoostItemSources.Build(npcs, quests, LootContent.Empty, BoostEventBinding.From(rows, creatures));
        HashSet<uint> without = BoostItemSources.Build(npcs, quests, LootContent.Empty);

        // RED control: without the binding the event rewards are "obtainable", which is how the crown and mantle got equipped.
        Assert.Contains(FireFestivalCrown, without);
        Assert.Contains(FireFestivalMantle, without);
        Assert.Contains(EventVendorHead, without);

        Assert.DoesNotContain(FireFestivalCrown, withEvents);
        Assert.DoesNotContain(FireFestivalMantle, withEvents);
        Assert.DoesNotContain(EventVendorHead, withEvents);
        Assert.Contains(MixedVendorHead, withEvents);   // also sold by a creature that is there all year
        Assert.Contains(PlainHead, withEvents);         // sold by a permanent vendor and rewarded by a permanent quest
    }

    [Fact]
    public void Picker_NeverEquipsTheFireFestivalCrownOrMantle_WhenTheEventsBindThem()
    {
        var (npcs, quests, rows, creatures) = EventFixture();
        // The holiday pieces carry far better stats than the permanent ones, so a pool that admits them always picks them.
        ItemTemplate[] items =
        [
            Obtainable(FireFestivalCrown, InventoryType.Head, strength: 9),
            Obtainable(FireFestivalMantle, InventoryType.Shoulders, strength: 9),
            Obtainable(PlainHead, InventoryType.Head, strength: 1),
            Obtainable(7103, InventoryType.Shoulders, strength: 1),
        ];
        var shoulders = new VendorItem { Entry = 16002, Item = 7103 };
        npcs = new NpcStore(NpcContent.Empty with { VendorItems = [.. npcs.Content.VendorItems, shoulders] });
        PlayerbotStatWeights weights = Weights(strength: 1f);

        BoostGearPlan Plan(HashSet<uint> sources) => BoostGearPlanner.Plan(
            items.Where(t => BoostGearPlanner.IsPoolItem(t, 14, sources)), weights, Class.Warrior, false, _ => true);

        BoostGearPlan unbound = Plan(BoostItemSources.Build(npcs, quests, LootContent.Empty));
        Assert.Equal(FireFestivalCrown, PickFor(unbound, InventorySlots.Head)!.Entry);        // RED: the defect
        Assert.Equal(FireFestivalMantle, PickFor(unbound, InventorySlots.Shoulders)!.Entry);

        BoostGearPlan bound = Plan(BoostItemSources.Build(npcs, quests, LootContent.Empty, BoostEventBinding.From(rows, creatures)));
        Assert.Equal(PlainHead, PickFor(bound, InventorySlots.Head)!.Entry);
        Assert.Equal(7103u, PickFor(bound, InventorySlots.Shoulders)!.Entry);
    }
    [Fact]
    public void QuestOnlyCosmetics_AreNeverPicked_NotEvenAsTheLastResort_ButStatBearingQuestRewardsStay()
    {
        const uint trophyHead = 7200, trophyChest = 7201, rewardHead = 7202, vendorHead = 7203, vendorChest = 7204;
        // The trophies have armor but no stat, resistance, spell or damage, and out-score the plain pieces, so without the rule they win.
        ItemTemplate[] items =
        [
            Piece(trophyHead, InventoryType.Head, itemLevel: 14, armor: 5),
            Piece(vendorHead, InventoryType.Head, itemLevel: 2, strength: 1),
            Piece(rewardHead, InventoryType.Head, itemLevel: 1, strength: 1),
            Piece(trophyChest, InventoryType.Chest, itemLevel: 14, armor: 5),
            Piece(vendorChest, InventoryType.Chest, itemLevel: 2),
        ];
        var npcs = new NpcStore(NpcContent.Empty with { VendorItems = [new VendorItem { Entry = 1, Item = vendorHead }, new VendorItem { Entry = 1, Item = vendorChest }] });
        var quests = new QuestStore(new QuestContent(
        [
            new QuestTemplate { Entry = 1, RewItemId1 = trophyHead, RewChoiceItemId1 = trophyChest, RewChoiceItemId2 = rewardHead },
            new QuestTemplate { Entry = 2, RewItemId1 = vendorChest },   // also sold: not quest-only
        ], [], []));
        HashSet<uint> sources = BoostItemSources.Build(npcs, quests, LootContent.Empty);
        HashSet<uint> questOnly = BoostItemSources.QuestOnly(npcs, quests, LootContent.Empty);
        PlayerbotStatWeights weights = Weights(strength: 1f, armor: 1f);   // armor counts, so the statless trophies score well

        Assert.Equal([trophyHead, trophyChest, rewardHead], questOnly.Order().ToArray());

        BoostGearPlan Plan(IReadOnlySet<uint>? rule) => BoostGearPlanner.Plan(
            items.Where(t => BoostGearPlanner.IsPoolItem(t, 14, sources)), weights, Class.Warrior, false, _ => true, rule);

        // RED control: without the rule the trophies are the kit (they out-score the vendor head and the statless vendor chest).
        BoostGearPlan without = Plan(null);
        Assert.Equal(trophyChest, PickFor(without, InventorySlots.Chest)!.Entry);
        Assert.Equal(trophyHead, PickFor(without, InventorySlots.Head)!.Entry);

        BoostGearPlan with = Plan(questOnly);
        Assert.Equal(vendorChest, PickFor(with, InventorySlots.Chest)!.Entry);     // the plain vendor piece, not the trophy
        Assert.Equal(vendorHead, PickFor(with, InventorySlots.Head)!.Entry);       // a stat-bearing candidate exists: the trophy is dropped
        Assert.True(BoostGearPlanner.IsPoolItem(items[2], 14, sources) && !BoostGearPlanner.IsCosmeticQuestReward(items[2], questOnly)); // a quest-only piece with a stat stays eligible

        // No fallback: with only the trophy left for the slot, the slot is empty.
        BoostGearPlan alone = BoostGearPlanner.Plan([items[3]], weights, Class.Warrior, false, _ => true, questOnly);
        Assert.Null(PickFor(alone, InventorySlots.Chest));
        Assert.Contains(InventorySlots.Chest, alone.EmptySlots);
    }
}
