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
using Microsoft.Extensions.Configuration;
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

    [Theory]
    [InlineData(Class.Warrior, 20, 3u)]
    [InlineData(Class.Warrior, 40, 4u)]
    [InlineData(Class.Hunter, 30, 2u)]
    [InlineData(Class.Hunter, 40, 3u)]
    public void Gear_PrefersTheBestTrainedArmorType_AndFallsBackWhenItIsAbsent(Class cls, byte level, uint preferred)
    {
        uint lower = preferred - 1;
        ItemTemplate strongerLower = Piece(1, InventoryType.Chest, itemLevel: level, strength: 20) with { SubClass = lower };
        ItemTemplate trained = Piece(2, InventoryType.Chest, itemLevel: level, strength: 8) with { SubClass = preferred };
        Func<ItemTemplate, bool> canUse = t => t.SubClass <= preferred;

        Assert.Equal(2u, PickFor(BoostGearPlanner.Plan([strongerLower, trained], Weights(strength: 1f), cls, false, canUse), InventorySlots.Chest)!.Entry);
        Assert.Equal(1u, PickFor(BoostGearPlanner.Plan([strongerLower], Weights(strength: 1f), cls, false, canUse), InventorySlots.Chest)!.Entry);
    }

    [Fact]
    public void Gear_OrderIsDeterministic_ScoreThenItemLevelThenEntry()
    {
        // Strength is worth 0.01 and item level 0.01 per point (PlayerbotItemScore.ItemLevelWeight), so these two score exactly the same
        // (2 * 0.01f against 0.01f + 0.01f, both exact doublings): the higher item level wins even with the higher entry.
        PlayerbotStatWeights weights = Weights(strength: 0.01f);
        ItemTemplate highLevel = Piece(9, InventoryType.Head, itemLevel: 2, armor: 1);   // armor weighs 0 here; it only makes the piece give something
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
    [InlineData("blue", true)]
    [InlineData("white", true)]
    [InlineData("epic", false)]
    [InlineData("grey", false)]
    [InlineData("requiredLevel", false)]
    [InlineData("itemLevel", false)]
    [InlineData("randomProperty", true)]
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
    public void Pool_AdmitsOnlyWhiteToBlueObtainableLevelAppropriateEquipment(string defect, bool admitted)
    {
        const byte level = 14;
        ItemTemplate good = Piece(100, InventoryType.Chest, itemLevel: level) with { RequiredLevel = level };
        ItemTemplate item = defect switch
        {
            "blue" => good with { Quality = 3 },
            "white" => good with { Quality = 1 },
            "epic" => good with { Quality = 4 },
            "grey" => good with { Quality = 0 },
            "requiredLevel" => good with { RequiredLevel = level + 1 },
            "itemLevel" => good with { ItemLevel = level + BoostGearPlanner.ItemLevelHeadroom + 1 },
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

    [Fact]
    public void Pool_AdmitsAnItemFiveLevelsAboveTheCharacter_WhenItsRequiredLevelIsMet()
    {
        // ClassicDB Outrider's Bow (19558) is item level 63 and requires level 58. vmangos Player::CanUseItem
        // checks RequiredLevel, not ItemLevel (Player.cpp:10058-10101). The boost keeps a five-level near-level cap.
        ItemTemplate bow = Piece(19558, InventoryType.Ranged, itemLevel: 63) with { RequiredLevel = 58 };
        HashSet<uint> sources = [bow.Entry];
        Assert.True(BoostGearPlanner.IsPoolItem(bow, 58, sources));
        Assert.False(BoostGearPlanner.IsPoolItem(bow with { ItemLevel = 64 }, 58, sources));
        Assert.False(BoostGearPlanner.IsPoolItem(bow with { RequiredLevel = 59 }, 58, sources));
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
            Piece(vendorChest, InventoryType.Chest, itemLevel: 2, armor: 1),   // a plain piece that gives a little armor
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

    [Fact]
    public void Pool_DefaultCapIsBlue_TheCapIsConfigurable_AndGreyIsNeverAdmitted()
    {
        ItemTemplate piece = Piece(7, InventoryType.Head, itemLevel: 14) with { RequiredLevel = 14 };
        HashSet<uint> sources = [7];

        Assert.Equal(3u, BoostGearPlanner.DefaultMaxQuality);
        Assert.True(BoostGearPlanner.IsPoolItem(piece with { Quality = 3 }, 14, sources));                  // a dungeon blue
        Assert.False(BoostGearPlanner.IsPoolItem(piece with { Quality = 3 }, 14, sources, maxQuality: 2));  // World:GmCommands:Boost:MaxQuality = 2
        Assert.True(BoostGearPlanner.IsPoolItem(piece with { Quality = 4 }, 14, sources, maxQuality: 4));
        Assert.False(BoostGearPlanner.IsPoolItem(piece with { Quality = 0 }, 14, sources, maxQuality: 4));

        Assert.Equal(3u, new CharacterBoostOptions().EffectiveMaxQuality);
        Assert.Equal(4u, new CharacterBoostOptions { MaxQuality = 9 }.EffectiveMaxQuality);
        Assert.Equal(1u, new CharacterBoostOptions { MaxQuality = 0 }.EffectiveMaxQuality);
        var bound = CharacterBoostOptions.Bind(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["World:GmCommands:Boost:MaxQuality"] = "2" }).Build());
        Assert.Equal(2u, bound.MaxQuality);
    }

    private static BoostSuffix Suffix(int id, string name, params (ItemStatType Type, int Value)[] stats)
        => new(id, name, [.. stats.Select(s => new ItemStat((uint)s.Type, s.Value))]);

    [Fact]
    public void Suffix_IsTheRollThatSuitsTheBuild_WithTheLowerIdOnATie()
    {
        BoostSuffix bear = Suffix(1001, "of the Bear", (ItemStatType.Strength, 8), (ItemStatType.Stamina, 8));
        BoostSuffix owl = Suffix(1002, "of the Owl", (ItemStatType.Intellect, 8), (ItemStatType.Spirit, 8));
        BoostSuffix eagle = Suffix(1003, "of the Eagle", (ItemStatType.Stamina, 8), (ItemStatType.Intellect, 8));
        BoostSuffix monkey = Suffix(1004, "of the Monkey", (ItemStatType.Agility, 8), (ItemStatType.Stamina, 8));
        BoostSuffix[] all = [owl, monkey, bear, eagle];

        Assert.Equal(bear, BoostGearPlanner.ChooseSuffix(all, PlayerbotStatWeights.ShieldTank));
        Assert.Equal(bear, BoostGearPlanner.ChooseSuffix(all, PlayerbotStatWeights.TwoHandStrength));
        Assert.Equal(monkey, BoostGearPlanner.ChooseSuffix(all, PlayerbotStatWeights.DualWieldAgility));
        Assert.Equal(eagle, BoostGearPlanner.ChooseSuffix(all, PlayerbotStatWeights.Caster));    // 8 * 0.8 + 8 * 1.5 beats 8 * 1.5 + 8 * 0.5
        Assert.Equal(owl, BoostGearPlanner.ChooseSuffix(all, PlayerbotStatWeights.Healer));      // spirit weighs 1.0 for a healer
        Assert.Null(BoostGearPlanner.ChooseSuffix([], PlayerbotStatWeights.Caster));

        BoostSuffix twin = Suffix(999, "of Strength", (ItemStatType.Strength, 8), (ItemStatType.Stamina, 8));
        Assert.Equal(twin, BoostGearPlanner.ChooseSuffix([bear, twin], PlayerbotStatWeights.ShieldTank));
    }

    [Fact]
    public void Gear_RandomPropertyTemplate_CompetesWithItsBestSuffix_AndIsLeftOutWhenNoSuffixIsKnown()
    {
        // A random-suffix green ("Ridge Cleaver of the Bear"): no stats in the template, so without the suffix it scores item level only.
        ItemTemplate random = Piece(50, InventoryType.Chest, itemLevel: 20, armor: 100) with { RandomProperty = 5 };
        ItemTemplate fixedStats = Piece(51, InventoryType.Chest, itemLevel: 20, strength: 4, armor: 100);
        BoostSuffix bear = Suffix(1001, "of the Bear", (ItemStatType.Strength, 6), (ItemStatType.Stamina, 6));
        BoostSuffix owl = Suffix(1002, "of the Owl", (ItemStatType.Intellect, 6), (ItemStatType.Spirit, 6));
        IReadOnlyList<BoostSuffix> Rolls(ItemTemplate t) => t.RandomProperty == 5 ? [owl, bear] : [];

        BoostGearPlan withSuffixes = BoostGearPlanner.Plan([random, fixedStats], PlayerbotStatWeights.ShieldTank, Class.Warrior, false, _ => true, suffixes: Rolls);
        Assert.Equal(50u, PickFor(withSuffixes, InventorySlots.Chest)!.Entry);
        Assert.Equal(bear, withSuffixes.Suffixes[InventorySlots.Chest]);

        // A caster wants the Owl roll, which is still worth more to it than four strength.
        BoostGearPlan caster = BoostGearPlanner.Plan([random, fixedStats], PlayerbotStatWeights.Caster, Class.Mage, false, _ => true, suffixes: Rolls);
        Assert.Equal(50u, PickFor(caster, InventorySlots.Chest)!.Entry);
        Assert.Equal(owl, caster.Suffixes[InventorySlots.Chest]);

        // No suffix source (no ItemRandomProperties / SpellItemEnchantment data), or none for the group: the item would be created statless.
        BoostGearPlan without = BoostGearPlanner.Plan([random, fixedStats], PlayerbotStatWeights.ShieldTank, Class.Warrior, false, _ => true);
        Assert.Equal(51u, PickFor(without, InventorySlots.Chest)!.Entry);
        Assert.Empty(without.Suffixes);
        BoostGearPlan none = BoostGearPlanner.Plan([random], PlayerbotStatWeights.ShieldTank, Class.Warrior, false, _ => true, suffixes: _ => []);
        Assert.Contains(InventorySlots.Chest, none.EmptySlots);
    }

    [Fact]
    public void Gear_APieceThatGivesNothing_IsNeverOffered_EvenWhenTheSlotStaysEmpty()
    {
        // The level-20 kit of the real world wore "Red Winter Hat" (item level 1, no armor or stat) and a grey item-level-1 necklace.
        ItemTemplate hat = Piece(21524, InventoryType.Head, itemLevel: 1);
        ItemTemplate necklace = Piece(9333, InventoryType.Neck, itemLevel: 1);
        ItemTemplate trinket = Piece(60, InventoryType.Trinket, itemLevel: 20) with { Spells = [new ItemSpell(100, 0, 0, 0, 0, 0, 0)] };

        Assert.False(BoostGearPlanner.GivesAnything(hat));
        Assert.True(BoostGearPlanner.GivesAnything(hat with { Armor = 1 }));
        Assert.True(BoostGearPlanner.GivesAnything(hat with { RandomProperty = 5 }, Suffix(1, "of Stamina", (ItemStatType.Stamina, 3))));
        Assert.True(BoostGearPlanner.GivesAnything(trinket));   // an on-use or equip spell counts

        BoostGearPlan plan = BoostGearPlanner.Plan([hat, necklace, trinket], Weights(strength: 1f), Class.Warrior, false, _ => true);
        Assert.Contains(InventorySlots.Head, plan.EmptySlots);
        Assert.Contains(InventorySlots.Neck, plan.EmptySlots);
        Assert.Equal(60u, PickFor(plan, InventorySlots.Trinket1)!.Entry);
    }

    [Fact]
    public void Suffixes_ReadTheStatEffectsOfTheGroupsProperties_FromTheTwoCatalogs()
    {
        // item_enchantment_template group 5 rolls properties 1001 and 1002 (a duplicate row and an unknown property are ignored);
        // their SpellItemEnchantment rows add +8 Strength / +8 Stamina and +7 Intellect plus a fire resistance effect (not a stat).
        var properties = new ItemRandomPropertyCatalog(
            [new ItemRandomPropertyRecord(1001, "of the Bear", [74, 75, 0]), new ItemRandomPropertyRecord(1002, "of Intellect", [76, 0, 0])],
            [new ItemEnchantmentChance(5, 1001, 40f), new ItemEnchantmentChance(5, 1001, 10f), new ItemEnchantmentChance(5, 1002, 40f),
                new ItemEnchantmentChance(5, 4040, 10f), new ItemEnchantmentChance(6, 1002, 100f)]);
        var enchants = new ArcaneCore.Kernel.Crafting.EnchantCatalog(
        [
            new ArcaneCore.Kernel.Crafting.SpellItemEnchantment(74, [5, 0, 0], [8, 0, 0], [(uint)ItemStatType.Strength, 0, 0], "+8 Strength", 0, 0),
            new ArcaneCore.Kernel.Crafting.SpellItemEnchantment(75, [5, 0, 0], [8, 0, 0], [(uint)ItemStatType.Stamina, 0, 0], "+8 Stamina", 0, 0),
            new ArcaneCore.Kernel.Crafting.SpellItemEnchantment(76, [5, 4, 0], [7, 10, 0], [(uint)ItemStatType.Intellect, 2, 0], "+7 Intellect", 0, 0),
        ]);
        BoostSuffixes suffixes = BoostSuffixes.TryCreate(properties, enchants, _ => null)!;

        IReadOnlyList<BoostSuffix> rolls = suffixes.For(Piece(1, InventoryType.Chest) with { RandomProperty = 5 });
        Assert.Equal([1001, 1002], rolls.Select(r => r.PropertyId).ToArray());
        Assert.Equal("of the Bear", rolls[0].Name);
        Assert.Equal([new ItemStat((uint)ItemStatType.Strength, 8), new ItemStat((uint)ItemStatType.Stamina, 8)], rolls[0].Stats.ToArray());
        Assert.Equal([new ItemStat((uint)ItemStatType.Intellect, 7)], rolls[1].Stats.ToArray());
        Assert.Empty(suffixes.For(Piece(2, InventoryType.Chest)));                                    // no random property
        Assert.Empty(suffixes.For(Piece(3, InventoryType.Chest) with { RandomProperty = 77 }));       // a group with no rows

        Assert.Null(BoostSuffixes.TryCreate(properties, ArcaneCore.Kernel.Crafting.EnchantCatalog.Empty, _ => null));
        Assert.Null(BoostSuffixes.TryCreate(ItemRandomPropertyCatalog.Empty, enchants, _ => null));
        Assert.Null(BoostSuffixes.TryCreate(null, enchants, _ => null));
    }

    [Fact]
    public void Sources_CountWorldAndDungeonDrops_ThroughCreatureAndReferenceLoot_ButNotTheReferenceRowsThemselves()
    {
        // A world drop (creature_loot_template), a dungeon boss's shared table (reference_loot_template reached through a negative
        // mincountOrRef) and the reference row itself (item 0 or an informational item, never a source).
        var loot = new LootContent(
        [
            (LootTableKind.Creature, new LootStoreRow(100, 9001, 0.5f, 0, 1, 1)),
            (LootTableKind.Creature, new LootStoreRow(200, 9002, 100f, 0, -24000, 1)),
            (LootTableKind.Reference, new LootStoreRow(24000, 9003, 20f, 1, 1, 1)),
        ], []);
        var npcs = new NpcStore(NpcContent.Empty);
        var quests = new QuestStore(new QuestContent([], [], []));

        HashSet<uint> sources = BoostItemSources.Build(npcs, quests, loot);
        Assert.Contains(9001u, sources);
        Assert.Contains(9003u, sources);
        Assert.DoesNotContain(9002u, sources);
    }
}
