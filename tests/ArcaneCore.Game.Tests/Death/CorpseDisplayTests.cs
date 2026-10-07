using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;
using Xunit;

namespace ArcaneCore.Game.Tests.Death;

/// <summary>
/// What a player's corpse shows (vmangos Player::CreateCorpse, Player.cpp:4719-4782): CORPSE_FIELD_ITEM + slot = the equipped
/// item's DisplayInfoID | InventoryType &lt;&lt; 24 for every equipment slot, CORPSE_FIELD_GUILD = the guild id, the hide helm and
/// hide cloak flags from PLAYER_FLAGS, and CORPSE_FLAG_LOOTABLE (0x20) in a battleground "to be able to remove insignia". A body put
/// back at login is built from the character the same way (Corpse::LoadFromDB reads the equipment cache, guild and flags of the
/// character row, Corpse.cpp:157-226).
/// </summary>
public sealed class CorpseDisplayTests
{
    private const long T = 1_700_000_000;
    private const uint WarsongGulch = 489;

    private static readonly MapContent Content = new(
        [
            new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new MapTemplate(WarsongGulch, 0, MapType.Battleground, 0, 20, 0, -1, 0, 0, "Warsong Gulch", ""),
        ],
        [],
        [],
        [],
        []);

    private sealed class FixedPresence(BattlegroundStatus? status) : IBattlegroundPresence
    {
        public BattlegroundStatus? MatchStatusOf(ObjectGuid player) => status;
    }

    private static uint Expected(uint entry) => ItemTestData.Get(entry).DisplayId | (ItemTestData.Get(entry).InventoryType << 24);

    private static Player Dressed()
    {
        (Player player, _) = ItemTestData.CreatePlayer();
        foreach (uint entry in new[] { ItemTestData.WornShortsword, ItemTestData.RecruitsPants, ItemTestData.RecruitsBoots, ItemTestData.WornWoodenShield })
        {
            Item item = ItemTestData.Give(player.Inventory, entry);
            player.Inventory.AutoEquipItem(item.BagSlot, item.Slot);
        }

        Assert.Equal(4, player.Inventory.Equipped.Count());
        return player;
    }

    [Fact]
    public void Corpse_ShowsEveryEquippedItem_AsDisplayIdAndInventoryType()
    {
        Player player = Dressed();

        Corpse corpse = Corpse.CreateFor(player, pvpDeath: false);

        Assert.Equal(Expected(ItemTestData.WornShortsword), corpse.GetUInt32(UpdateFields.CorpseFieldItem + InventorySlots.MainHand));
        Assert.Equal(Expected(ItemTestData.WornWoodenShield), corpse.GetUInt32(UpdateFields.CorpseFieldItem + InventorySlots.OffHand));
        Assert.Equal(Expected(ItemTestData.RecruitsPants), corpse.GetUInt32(UpdateFields.CorpseFieldItem + InventorySlots.Legs));
        Assert.Equal(Expected(ItemTestData.RecruitsBoots), corpse.GetUInt32(UpdateFields.CorpseFieldItem + InventorySlots.Feet));
        Assert.Equal(0u, corpse.GetUInt32(UpdateFields.CorpseFieldItem + InventorySlots.Head));
        Assert.Equal(0u, corpse.GetUInt32(UpdateFields.CorpseFieldItem + InventorySlots.Chest));
    }

    [Fact]
    public void Corpse_CarriesTheGuild_AndTheHideHelmAndCloakFlags()
    {
        Player player = Dressed();
        player.SetUInt32(UpdateFields.PlayerGuildid, 42);
        player.Flags |= PlayerFlags.HideHelm | PlayerFlags.HideCloak;

        Corpse corpse = Corpse.CreateFor(player, pvpDeath: false);

        Assert.Equal(42u, corpse.GetUInt32(UpdateFields.CorpseFieldGuild));
        Assert.Equal(Corpse.FlagUnk2 | Corpse.FlagHideHelm | Corpse.FlagHideCloak, corpse.GetUInt32(UpdateFields.CorpseFieldFlags));
    }

    [Fact]
    public void Corpse_WithoutHiddenGear_HasOnlyTheBaseFlag()
    {
        Player player = Dressed();
        player.Flags |= PlayerFlags.HideCloak;

        Corpse corpse = Corpse.CreateFor(player, pvpDeath: false);

        Assert.Equal(Corpse.FlagUnk2 | Corpse.FlagHideCloak, corpse.GetUInt32(UpdateFields.CorpseFieldFlags));
        Assert.Equal(0u, corpse.GetUInt32(UpdateFields.CorpseFieldGuild));
    }

    [Fact]
    public void RestoredBody_IsDressedLikeTheCharacter()
    {
        Player player = Dressed();
        player.SetUInt32(UpdateFields.PlayerGuildid, 7);
        player.Flags |= PlayerFlags.HideHelm;

        Corpse corpse = Corpse.CreateAt(player, 0, 1, 2, 3, 0, CorpseType.ResurrectablePve);

        Assert.Equal(Expected(ItemTestData.WornShortsword), corpse.GetUInt32(UpdateFields.CorpseFieldItem + InventorySlots.MainHand));
        Assert.Equal(7u, corpse.GetUInt32(UpdateFields.CorpseFieldGuild));
        Assert.Equal(Corpse.FlagUnk2 | Corpse.FlagHideHelm, corpse.GetUInt32(UpdateFields.CorpseFieldFlags));
    }

    private static (WorldRuntime World, Player Player) DeadPlayerOn(uint mapId)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        WorldMaps.Of(world).Load(Content);
        DeathHooks.Register(world, new DeathHooks(new DeathOptions(), new FixedDeathClock(T)));
        Map map = world.GetMap(mapId);
        map.Combat.Hooks = new TestCombatHooks();
        Player player = CombatTestKit.AddPlayer(world, 2, 10, 20, new FakeSession(2), mapId: mapId);
        world.RunTick(1);
        map.Combat.Kill(null, player);
        return (world, player);
    }

    [Fact]
    public void ReleasedSpirit_InABattleground_LeavesALootableCorpse()
    {
        (WorldRuntime world, Player player) = DeadPlayerOn(WarsongGulch);
        using (world)
        {
            Assert.True(player.Map!.Combat.RepopPlayer(player));

            Corpse corpse = player.Combat.Corpse!;
            Assert.Equal(Corpse.FlagUnk2 | Corpse.FlagLootable, corpse.GetUInt32(UpdateFields.CorpseFieldFlags));
        }
    }

    [Fact]
    public void ReleasedSpirit_BoundToAMatch_LeavesALootableCorpse()
    {
        (WorldRuntime world, Player player) = DeadPlayerOn(0);
        using (world)
        {
            Assert.True(DeathSeams.Of(world).TryRegisterBattlegrounds(new FixedPresence(BattlegroundStatus.InProgress)));
            Assert.True(player.Map!.Combat.RepopPlayer(player));

            Assert.Equal(Corpse.FlagUnk2 | Corpse.FlagLootable, player.Combat.Corpse!.GetUInt32(UpdateFields.CorpseFieldFlags));
        }
    }

    [Fact]
    public void ReleasedSpirit_OutsideBattlegrounds_LeavesACorpseThatIsNotLootable()
    {
        (WorldRuntime world, Player player) = DeadPlayerOn(0);
        using (world)
        {
            Assert.True(player.Map!.Combat.RepopPlayer(player));

            Assert.Equal(Corpse.FlagUnk2, player.Combat.Corpse!.GetUInt32(UpdateFields.CorpseFieldFlags));
        }
    }
}
