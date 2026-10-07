using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.ItemUse;

/// <summary>
/// The combat weapon switch (vmangos Player::EquipItem, Player.cpp:10340-10369, patch 1.7.0 note): equipping a weapon while alive and in combat
/// starts the weapon change timer and the global cooldown of spell 6119 (1.5 s; 6123, 1.0 s, for a rogue) and tells the client with
/// SMSG_SPELL_COOLDOWN; while the timer runs another weapon cannot be equipped in combat (Player::CanEquipItem, Player.cpp:9710-9711:
/// EQUIP_ERR_CANT_DO_RIGHT_NOW).
/// </summary>
public sealed class WeaponSwapCooldownTests
{
    private const uint SwordA = 94_000;
    private const uint SwordB = 94_001;
    private const uint SwordC = 94_002;

    private static SpellInfo Switch(uint id, uint ms) => SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.Dummy, 0)) with
    {
        StartRecoveryCategory = SpellConstants.GlobalCooldownCategory, StartRecoveryTime = ms,
    };

    private static (SpellTestKit Kit, Player Player, FakeSession Session) Create(bool inCombat, Class playerClass = Class.Warrior)
    {
        var kit = new SpellTestKit(Switch(SpellSystem.WeaponSwitchCooldownSpell, 1500), Switch(SpellSystem.RogueWeaponSwitchCooldownSpell, 1000));
        (Player player, FakeSession session) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)playerClass);
        player.Inventory.Templates = new ItemTemplateStore(
            [.. new[] { SwordA, SwordB, SwordC }.Select(e => new ItemTemplate { Entry = e, Class = 2, SubClass = 7, Name = $"Sword {e}", DisplayId = 1, InventoryType = 13, Delay = 2000, Damages = [new ItemDamage(1, 3, 0)] })], []);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        new ItemEquipSpells(kit.System).Attach(player);
        foreach (uint entry in new[] { SwordA, SwordB, SwordC })
        {
            Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(entry, 1, out _));
        }

        if (inCombat)
        {
            player.UnitFlags |= UnitFlags.InCombat;
        }

        session.Clear();
        return (kit, player, session);
    }

    private static byte BackpackSlotOf(Player player, uint entry)
    {
        for (byte slot = InventorySlots.ItemStart; slot < InventorySlots.ItemEnd; slot++)
        {
            if (player.Inventory.GetItem(InventorySlots.Bag0, slot)?.Entry == entry)
            {
                return slot;
            }
        }

        throw new InvalidOperationException($"{entry} is not in the backpack");
    }

    private static uint MainHand(Player player) => player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)?.Entry ?? 0;

    private static void Equip(Player player, uint entry) => player.Inventory.AutoEquipItem(InventorySlots.Bag0, BackpackSlotOf(player, entry));

    private static List<(uint Spell, uint Ms)> Cooldowns(FakeSession session)
    {
        var result = new List<(uint, uint)>();
        foreach (byte[] packet in SpellTestKit.Packets(session, WorldOpcode.SmsgSpellCooldown))
        {
            var reader = new PacketReader(packet);
            reader.ReadUInt64();
            while (reader.Remaining >= 8)
            {
                result.Add((reader.ReadUInt32(), reader.ReadUInt32()));
            }
        }

        return result;
    }

    [Fact]
    public void InCombat_WeaponEquip_StartsTheSwitchCooldown_AndRefusesAnotherWeaponUntilItRunsOut()
    {
        (SpellTestKit kit, Player player, FakeSession session) = Create(inCombat: true);
        using SpellTestKit owner = kit;
        Equip(player, SwordA);
        Assert.Equal(SwordA, MainHand(player));
        Assert.Equal([(SpellSystem.WeaponSwitchCooldownSpell, 0u)], Cooldowns(session));
        Assert.True(kit.System.IsWeaponChangeLocked(player));

        session.Clear();
        Equip(player, SwordB);
        Assert.Equal(SwordA, MainHand(player));
        byte[] failure = Assert.Single(SpellTestKit.Packets(session, WorldOpcode.SmsgInventoryChangeFailure));
        Assert.Equal((byte)InventoryResult.CantDoRightNow, failure[0]);
        Assert.Empty(Cooldowns(session));

        kit.Advance(1500);
        Assert.False(kit.System.IsWeaponChangeLocked(player));
        session.Clear();
        Equip(player, SwordB);
        Assert.Equal(SwordB, MainHand(player));
        Assert.Equal([(SpellSystem.WeaponSwitchCooldownSpell, 0u)], Cooldowns(session));
    }

    [Fact]
    public void InCombat_TheSwitchStartsTheGlobalCooldown()
    {
        (SpellTestKit kit, Player player, _) = Create(inCombat: true);
        using SpellTestKit owner = kit;
        Equip(player, SwordA);
        Assert.False(kit.System.IsGlobalCooldownReady(player, SpellConstants.GlobalCooldownCategory));
        kit.Advance(1500);
        Assert.True(kit.System.IsGlobalCooldownReady(player, SpellConstants.GlobalCooldownCategory));
    }

    [Fact]
    public void Rogue_UsesTheOneSecondSpell()
    {
        (SpellTestKit kit, Player player, FakeSession session) = Create(inCombat: true, Class.Rogue);
        using SpellTestKit owner = kit;
        Equip(player, SwordA);
        Assert.Equal([(SpellSystem.RogueWeaponSwitchCooldownSpell, 0u)], Cooldowns(session));
        kit.Advance(1000);
        Assert.False(kit.System.IsWeaponChangeLocked(player));
    }

    [Fact]
    public void OutOfCombat_WeaponsSwapFreely()
    {
        (SpellTestKit kit, Player player, FakeSession session) = Create(inCombat: false);
        using SpellTestKit owner = kit;
        Equip(player, SwordA);
        Equip(player, SwordB);
        Equip(player, SwordC);
        Assert.Equal(SwordC, MainHand(player));
        Assert.Empty(Cooldowns(session));
        Assert.False(kit.System.IsWeaponChangeLocked(player));
    }

    [Fact]
    public void Dead_PlayerStartsNoSwitchCooldown()
    {
        (SpellTestKit kit, Player player, FakeSession session) = Create(inCombat: true);
        using SpellTestKit owner = kit;
        player.Health = 0;
        Equip(player, SwordA);
        Assert.Empty(Cooldowns(session));
        Assert.False(kit.System.IsWeaponChangeLocked(player));
    }
}
