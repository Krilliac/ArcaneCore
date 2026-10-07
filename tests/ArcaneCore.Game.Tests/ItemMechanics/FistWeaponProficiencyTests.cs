using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.ItemMechanics;

/// <summary>vmangos Player::CanUseItem (Player.cpp:10086-10093): a fist weapon needs the Fist Weapons skill (473), not Unarmed (162).</summary>
public sealed class FistWeaponProficiencyTests
{
    private const uint FistWeapon = 94104;  // weapon subclass 13

    private static readonly ItemTemplateStore Store = new(
        [
            .. Templates,
            new ItemTemplate { Entry = FistWeapon, Class = 2, SubClass = 13, Name = "Test Claw", DisplayId = 1, InventoryType = 13, Delay = 2000, MaxDurability = 20 },
        ], []);

    private sealed class SkillTable(Dictionary<uint, uint> skills) : IItemRequirements
    {
        public bool CanDualWield(PlayerInventory inventory) => false;

        public uint SkillValue(PlayerInventory inventory, uint skill) => skills.GetValueOrDefault(skill);

        public bool HasSpell(PlayerInventory inventory, uint spellId) => true;

        public byte HonorRank(PlayerInventory inventory) => 0;

        public uint ReputationRank(PlayerInventory inventory, uint faction) => 3;
    }

    private static (Player Player, FakeSession Session) Make(uint zone = 2557)
    {
        (Player player, FakeSession session) = CreatePlayer();
        player.Inventory.Templates = Store;
        player.Inventory.Load([]);
        player.ZoneId = zone;
        return (player, session);
    }

    [Fact]
    public void FistWeapon_NeedsTheFistWeaponSkill_NotUnarmed()
    {
        (Player player, _) = Make();
        PlayerInventory inv = player.Inventory;
        ItemTemplate claw = Store.Find(FistWeapon)!;

        inv.Requirements = new SkillTable(new() { [ItemSkills.Unarmed] = 1 });
        Assert.Equal(InventoryResult.NoRequiredProficiency, inv.CanUseItem(claw));

        inv.Requirements = new SkillTable(new() { [ItemSkills.Unarmed] = 1, [ItemSkills.FistWeapons] = 1 });
        Assert.Equal(InventoryResult.Ok, inv.CanUseItem(claw));
    }
}
