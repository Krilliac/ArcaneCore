using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Ranged;

/// <summary>
/// Ranged abilities in the cast pipeline: weapon and ammunition checks (vmangos Spell::CheckItems,
/// Spell.cpp:7390-7455), ammunition use (TakeAmmo, 5129-5171), the ammo trailer of SMSG_SPELL_START /
/// SMSG_SPELL_GO (4495-4620), the ranged attack time in the cooldown (Player.cpp:22193-22197) and the
/// ranged cast time (SpellEntry.cpp:485-514). Spells are synthetic: no Spell.dbc is available.
/// </summary>
public sealed class RangedCastPipelineTests
{
    private const uint Shot = 910001;          // weapon-damage ranged ability, instant
    private const uint ExoticShot = 910002;    // needs exotic ammo
    private const uint SchoolShot = 910003;    // ranged slot, school damage only
    private const uint SlowShot = 910004;      // Aimed Shot shaped: 3000 ms ability
    private const uint MultiShot = 910005;     // 10 s recovery, instant
    private const uint MultiNoTimers = 910006; // same, DO_NOT_RESET_COMBAT_TIMERS
    private const uint BareShot = 910007;      // no recovery of its own
    private const uint MeleeStrike = 910008;   // not ranged: cooldown unchanged
    private const uint Blind = 2094;           // a spell id that takes no ammo
    private const uint Heal = InstantHeal;

    private const uint Bow = 92001;
    private const uint Gun = 92002;
    private const uint Wand = 92003;
    private const uint ThrownStack = 92004;
    private const uint ThrownSingle = 92005;
    private const uint Arrow = 92010;
    private const uint Bullet = 92011;
    private const uint ExoticArrow = 92012;

    private static readonly ItemTemplate[] Templates =
    [
        new() { Entry = Bow, Class = 2, SubClass = 2, DisplayId = 300, InventoryType = 15, Delay = 2500, MaxDurability = 40 },
        new() { Entry = Gun, Class = 2, SubClass = 3, DisplayId = 301, InventoryType = 26, Delay = 2800, MaxDurability = 40 },
        new() { Entry = Wand, Class = 2, SubClass = 19, DisplayId = 302, InventoryType = 26, Delay = 1500, MaxDurability = 40 },
        new() { Entry = ThrownStack, Class = 2, SubClass = 16, DisplayId = 303, InventoryType = 25, Delay = 2000, Stackable = 200 },
        new() { Entry = ThrownSingle, Class = 2, SubClass = 16, DisplayId = 304, InventoryType = 25, Delay = 2000, MaxDurability = 30 },
        new() { Entry = Arrow, Class = 6, SubClass = 2, DisplayId = 5996, InventoryType = 24, Stackable = 200, Damages = [new ItemDamage(4, 5, 0)] },
        new() { Entry = Bullet, Class = 6, SubClass = 3, DisplayId = 5998, InventoryType = 24, Stackable = 200, Damages = [new ItemDamage(4, 5, 0)] },
        new() { Entry = ExoticArrow, Class = 6, SubClass = 2, DisplayId = 5990, InventoryType = 24, Stackable = 200, Flags = 0x8, Damages = [new ItemDamage(4, 5, 0)] },
    ];

    private static SpellInfo RangedSpell(uint id, SpellEffectInfo effect) => Spell(id, effect) with
    {
        Attributes = SpellAttributes.UsesRangedSlot,
        DamageClass = SpellDamageClass.Ranged,
        RangeIndex = 4,
        Range = new SpellRange(5, 35),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellEffectInfo Weapon() => Effect(SpellEffectName.WeaponDamage, 5, SpellImplicitTarget.UnitEnemy);

    private static SpellTestKit Kit() => new(
        RangedSpell(Shot, Weapon()),
        RangedSpell(ExoticShot, Weapon()) with { Attributes = SpellAttributes.UsesRangedSlot | (SpellAttributes)0x8 },
        RangedSpell(SchoolShot, Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.UnitEnemy)),
        RangedSpell(SlowShot, Weapon()) with { Attributes = SpellAttributes.UsesRangedSlot | SpellAttributes.IsAbility, CastTime = new SpellCastTime(3000, 0, 0) },
        RangedSpell(MultiShot, Weapon()) with { RecoveryTime = 10_000 },
        RangedSpell(MultiNoTimers, Weapon()) with { RecoveryTime = 10_000, AttributesEx2 = (SpellAttributesEx2)0x20000 },
        RangedSpell(BareShot, Weapon()),
        RangedSpell(MeleeStrike, Weapon()) with { Attributes = SpellAttributes.None, DamageClass = SpellDamageClass.Melee, RecoveryTime = 10_000, Range = new SpellRange(0, 35) },
        RangedSpell(Blind, Weapon()));

    private static (Player Player, FakeSession Session, Player Target) Shooter(SpellTestKit kit, float targetDistance = 20)
    {
        (Player player, FakeSession session) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, targetDistance);
        player.Inventory.Templates = new ItemTemplateStore([.. ItemTestData.Templates, .. Templates]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        kit.Spellbook.Teach(player, Shot, ExoticShot, SchoolShot, SlowShot, MultiShot, MultiNoTimers, BareShot, MeleeStrike, Blind);
        target.Health = 5000;
        return (player, session, target);
    }

    private static void Equip(Player player, uint entry, uint count = 1)
    {
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(entry, count, out Item? item));
        Assert.Equal(InventoryResult.Ok, player.Inventory.CanEquipItem(InventorySlots.NullSlot, out byte dest, item!.Template, item, swap: false));
        player.Inventory.RemoveItem(InventorySlots.Bag0, item.Slot);
        player.Inventory.EquipItem(dest, item);
    }

    private static void Arm(Player player, uint ammo, uint count = 20)
    {
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(ammo, count, out _));
        Assert.True(PlayerAmmo.SetAmmo(player, ammo));
    }

    private static SpellCastResult Fire(SpellTestKit kit, Player player, Player target, uint spell, bool triggered = true)
        => kit.System.CastSpell(player, spell, SpellCastTargets.ForUnit(target.Guid), triggered);

    // --- checks ----------------------------------------------------------------------------

    [Fact]
    public void NoRangedWeapon_FailsWithEquippedItem_AndSpendsNothing()
    {
        using var kit = Kit();
        (Player player, _, Player target) = Shooter(kit);
        Arm(player, Arrow);

        Assert.Equal(SpellCastResult.EquippedItem, Fire(kit, player, target, Shot));

        Assert.Equal(20u, player.Inventory.GetItemCount(Arrow));
        Assert.Equal(5000u, target.Health);
    }

    [Theory]
    [InlineData(Bow, 0u, SpellCastResult.NoAmmo)]          // no ammo selected
    [InlineData(Bow, Bullet, SpellCastResult.NoAmmo)]      // bullets in a bow
    [InlineData(Gun, Arrow, SpellCastResult.NoAmmo)]       // arrows in a gun
    [InlineData(Bow, Arrow, SpellCastResult.CastOk)]
    [InlineData(Gun, Bullet, SpellCastResult.CastOk)]
    public void Launchers_NeedMatchingAmmo(uint weapon, uint ammo, SpellCastResult expected)
    {
        using var kit = Kit();
        (Player player, _, Player target) = Shooter(kit);
        Equip(player, weapon);
        if (ammo != 0)
        {
            Arm(player, ammo);
        }

        Assert.Equal(expected, Fire(kit, player, target, Shot));
    }

    [Fact]
    public void AmmoSelectedButUsedUp_IsNoAmmo()
    {
        using var kit = Kit();
        (Player player, _, Player target) = Shooter(kit);
        Equip(player, Bow);
        Arm(player, Arrow, 1);
        player.Inventory.DestroyItemCount(Arrow, 1);

        Assert.Equal(SpellCastResult.NoAmmo, Fire(kit, player, target, Shot));
    }

    [Fact]
    public void ExoticAttribute_NeedsExoticAmmo()
    {
        using var kit = Kit();
        (Player player, _, Player target) = Shooter(kit);
        Equip(player, Bow);
        Arm(player, Arrow);
        Assert.Equal(SpellCastResult.NeedExoticAmmo, Fire(kit, player, target, ExoticShot));
        Assert.Equal(SpellCastResult.CastOk, Fire(kit, player, target, Shot));

        Arm(player, ExoticArrow);
        Assert.Equal(SpellCastResult.CastOk, Fire(kit, player, target, ExoticShot));
    }

    [Fact]
    public void Wands_NeedNoAmmo_AndThrownWeaponsAreTheirOwnAmmo()
    {
        using var kit = Kit();
        (Player player, _, Player target) = Shooter(kit);
        Equip(player, Wand);
        Assert.Equal(SpellCastResult.CastOk, Fire(kit, player, target, Shot));

        player.Inventory.RemoveItem(InventorySlots.Bag0, InventorySlots.Ranged);
        Equip(player, ThrownStack, 5);
        Assert.Equal(SpellCastResult.CastOk, Fire(kit, player, target, Shot));
    }

    [Fact]
    public void OnlyWeaponDamageSpellsAreChecked_AsInVmangos()
    {
        using var kit = Kit();
        (Player player, _, Player target) = Shooter(kit);

        Assert.Equal(SpellCastResult.CastOk, Fire(kit, player, target, SchoolShot)); // no weapon, no ammo: not a weapon-damage effect
    }

    [Fact]
    public void BrokenRangedWeapon_CountsAsNoWeapon()
    {
        using var kit = Kit();
        (Player player, _, Player target) = Shooter(kit);
        Equip(player, Bow);
        Arm(player, Arrow);
        player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Ranged)!.Durability = 0;

        Assert.Equal(SpellCastResult.EquippedItem, Fire(kit, player, target, Shot));
    }

    [Fact]
    public void TheAmmoCheckComesBeforeTheRangeCheck()
    {
        using var kit = Kit();
        (Player player, _, Player target) = Shooter(kit, targetDistance: 100);
        Equip(player, Bow);

        Assert.Equal(SpellCastResult.NoAmmo, Fire(kit, player, target, Shot));
    }

    [Fact]
    public void InfiniteAmmoMode_NeedsNoAmmo_ButStillNeedsAWeapon_AndConsumesNothing()
    {
        using var kit = Kit();
        kit.System.RangedOptions.Ammo.Mode = AmmoMode.Infinite;
        (Player player, _, Player target) = Shooter(kit);
        Assert.Equal(SpellCastResult.EquippedItem, Fire(kit, player, target, Shot));

        Equip(player, Bow);
        Assert.Equal(SpellCastResult.CastOk, Fire(kit, player, target, Shot));

        Arm(player, Arrow);
        Assert.Equal(SpellCastResult.CastOk, Fire(kit, player, target, Shot));
        Assert.Equal(20u, player.Inventory.GetItemCount(Arrow));
    }

    // --- ammunition use ----------------------------------------------------------------------

    [Fact]
    public void ACast_UsesExactlyOneArrow_AndAFailedCastUsesNone()
    {
        using var kit = Kit();
        (Player player, _, Player target) = Shooter(kit, targetDistance: 100);
        Equip(player, Bow);
        Arm(player, Arrow);

        Assert.Equal(SpellCastResult.OutOfRange, Fire(kit, player, target, Shot));
        Assert.Equal(20u, player.Inventory.GetItemCount(Arrow));

        target.Relocate(20, 0, 83.5f, 0, kit.Now);
        Assert.Equal(SpellCastResult.CastOk, Fire(kit, player, target, Shot));
        Assert.Equal(19u, player.Inventory.GetItemCount(Arrow));
        Assert.Equal(Arrow, PlayerAmmo.CurrentAmmoId(player));
    }

    [Fact]
    public void AmmoIsTakenWhenTheCastCompletes_AndAnEmptyQuiverAtThatPointFailsIt()
    {
        using var kit = Kit();
        (Player player, FakeSession session, Player target) = Shooter(kit);
        Equip(player, Bow);
        Arm(player, Arrow, 2);

        Assert.Equal(SpellCastResult.CastOk, Fire(kit, player, target, SlowShot, triggered: false));
        Assert.Equal(2u, player.Inventory.GetItemCount(Arrow)); // still winding up
        player.Inventory.DestroyItemCount(Arrow, 2);           // the quiver empties during the cast
        session.Clear();
        kit.Advance(4000);

        Assert.Empty(Packets(session, WorldOpcode.SmsgSpellGo));
        Assert.Equal((byte)SpellCastResult.NoAmmo, Packets(session, WorldOpcode.SmsgCastResult).Last()[5]);
        Assert.Equal(5000u, target.Health);
    }

    [Fact]
    public void SpellsThatTakeNoAmmo_AndWands_LeaveTheQuiverAlone()
    {
        using var kit = Kit();
        (Player player, _, Player target) = Shooter(kit);
        Equip(player, Bow);
        Arm(player, Arrow);

        Assert.Equal(SpellCastResult.CastOk, Fire(kit, player, target, Blind)); // 2094
        Assert.Equal(20u, player.Inventory.GetItemCount(Arrow));

        player.Inventory.RemoveItem(InventorySlots.Bag0, InventorySlots.Ranged);
        Equip(player, Wand);
        Assert.Equal(SpellCastResult.CastOk, Fire(kit, player, target, Shot));
        Assert.Equal(20u, player.Inventory.GetItemCount(Arrow));
    }

    [Fact]
    public void ThrownWeapons_UseTheStack_OrWearOut()
    {
        using var kit = Kit();
        (Player player, _, Player target) = Shooter(kit);
        Equip(player, ThrownStack, 5);
        Assert.Equal(SpellCastResult.CastOk, Fire(kit, player, target, Shot));
        Assert.Equal(4u, player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Ranged)!.Count);

        player.Inventory.RemoveItem(InventorySlots.Bag0, InventorySlots.Ranged);
        Equip(player, ThrownSingle);
        uint before = player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Ranged)!.Durability;
        Assert.Equal(SpellCastResult.CastOk, Fire(kit, player, target, Shot));
        // Wave-2 integration: the wear is the item-mechanics lane's PlayerInventory.DurabilityPointLossForEquipSlot (one point).
        Item single = Assert.IsType<Item>(player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Ranged));
        Assert.Equal(before - 1, single.Durability);
    }

    // --- packets ---------------------------------------------------------------------------

    private static (ushort Flags, uint Display, uint InvType, int Length) ReadSpellPacket(byte[] payload, bool go)
    {
        var reader = new PacketReader(payload);
        reader.ReadPackedGuid();
        reader.ReadPackedGuid();
        reader.ReadUInt32();
        ushort flags = reader.ReadUInt16();
        if (go)
        {
            reader.Skip(reader.ReadByte() * 8);
            int misses = reader.ReadByte();
            reader.Skip(misses * 9);
        }
        else
        {
            reader.ReadUInt32();
        }

        // targets: u16 mask, packed unit guid
        ushort mask = reader.ReadUInt16();
        if ((mask & (ushort)SpellCastTargetFlags.Unit) != 0)
        {
            reader.ReadPackedGuid();
        }

        if ((flags & (ushort)SpellCastFlags.Ammo) == 0)
        {
            return (flags, 0, 0, payload.Length);
        }

        return (flags, reader.ReadUInt32(), reader.ReadUInt32(), payload.Length);
    }

    [Fact]
    public void RangedCasts_CarryTheAmmoFlagAndTheProjectile_ForLaunchersAndThrownWeapons()
    {
        using var kit = Kit();
        (Player player, FakeSession session, Player target) = Shooter(kit);
        Equip(player, Bow);
        Arm(player, Arrow);

        Assert.Equal(SpellCastResult.CastOk, Fire(kit, player, target, SlowShot, triggered: false));
        var start = ReadSpellPacket(Packets(session, WorldOpcode.SmsgSpellStart).Single(), go: false);
        Assert.Equal((ushort)0x0022, start.Flags);                       // Unknown2 | Ammo (Spell.cpp:4495-4500)
        Assert.Equal((5996u, (uint)InventoryType.Ammo), (start.Display, start.InvType));

        kit.Advance(4000);
        var go = ReadSpellPacket(Packets(session, WorldOpcode.SmsgSpellGo).Single(), go: true);
        Assert.Equal((ushort)0x0120, go.Flags);                          // Unknown9 | Ammo (Spell.cpp:4533-4539)
        Assert.Equal((5996u, (uint)InventoryType.Ammo), (go.Display, go.InvType));

        player.Inventory.RemoveItem(InventorySlots.Bag0, InventorySlots.Ranged);
        Equip(player, ThrownStack, 5);
        session.Clear();
        Fire(kit, player, target, Shot);
        var thrown = ReadSpellPacket(Packets(session, WorldOpcode.SmsgSpellGo).Single(), go: true);
        Assert.Equal((303u, (uint)InventoryType.Thrown), (thrown.Display, thrown.InvType)); // the weapon is the projectile
    }

    [Fact]
    public void ARangedCastWithoutAmmoId_ReportsTheWeaponsOwnInventoryType()
    {
        using var kit = Kit();
        (Player player, FakeSession session, Player target) = Shooter(kit);
        Equip(player, Wand);

        Fire(kit, player, target, Shot);

        var go = ReadSpellPacket(Packets(session, WorldOpcode.SmsgSpellGo).Single(), go: true);
        Assert.Equal((0u, (uint)InventoryType.RangedRight), (go.Display, go.InvType)); // Spell.cpp:4565-4570
    }

    [Fact]
    public void NonRangedSpells_KeepTheirOldPackets()
    {
        using var kit = Kit();
        (Player player, FakeSession session, _) = Shooter(kit);
        kit.Spellbook.Teach(player, Heal);

        kit.System.HandleCastRequest(player, Heal, SpellCastTargets.ForSelf());

        Assert.Equal(
            SpellPackets.BuildSpellStart(player.Guid, player.Guid, Heal, SpellCastFlags.Unknown2, 0, SpellCastTargets.ForSelf()),
            Packets(session, WorldOpcode.SmsgSpellStart).Single());
        Assert.Equal(
            SpellPackets.BuildSpellGo(player.Guid, player.Guid, Heal, SpellCastFlags.Unknown9, [player.Guid], [], SpellCastTargets.ForSelf()),
            Packets(session, WorldOpcode.SmsgSpellGo).Single());
    }

    [Fact]
    public void SpellPackets_WriteTheGivenAmmoTrailer_AndZerosByDefault()
    {
        byte[] withAmmo = SpellPackets.BuildSpellGo(
            ObjectGuid.Player(1), ObjectGuid.Player(1), 5, SpellCastFlags.Unknown9 | SpellCastFlags.Ammo, [], [], SpellCastTargets.ForSelf(), new AmmoVisual(5996, 24));
        Assert.Equal(5996u, BinaryPrimitives.ReadUInt32LittleEndian(withAmmo.AsSpan(withAmmo.Length - 8)));
        Assert.Equal(24u, BinaryPrimitives.ReadUInt32LittleEndian(withAmmo.AsSpan(withAmmo.Length - 4)));

        byte[] defaults = SpellPackets.BuildSpellStart(
            ObjectGuid.Player(1), ObjectGuid.Player(1), 5, SpellCastFlags.Unknown2 | SpellCastFlags.Ammo, 0, SpellCastTargets.ForSelf());
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(defaults.AsSpan(defaults.Length - 8)));
    }

    // --- cooldown --------------------------------------------------------------------------

    private static bool ReadyAt(SpellTestKit kit, Player player, uint spell, uint start, uint offset)
    {
        kit.Now = start + offset;
        return kit.System.IsSpellReady(player, kit.Store.Get(spell)!);
    }

    [Fact]
    public void RangedAbilityCooldown_IncludesTheWeaponSpeed()
    {
        using var kit = Kit();
        (Player player, _, Player target) = Shooter(kit);
        Equip(player, Bow);
        Arm(player, Arrow);
        player.SetUInt32(UpdateFields.UnitFieldRangedattacktime, 2500);
        uint start = kit.Now;

        Assert.Equal(SpellCastResult.CastOk, Fire(kit, player, target, MultiShot, triggered: false));

        Assert.False(ReadyAt(kit, player, MultiShot, start, 12_499));
        Assert.True(ReadyAt(kit, player, MultiShot, start, 12_500));
    }

    [Fact]
    public void DoNotResetCombatTimers_LeavesTheOwnCooldownAlone()
    {
        using var kit = Kit();
        (Player player, _, Player target) = Shooter(kit);
        Equip(player, Bow);
        Arm(player, Arrow);
        player.SetUInt32(UpdateFields.UnitFieldRangedattacktime, 2500);
        uint start = kit.Now;

        Assert.Equal(SpellCastResult.CastOk, Fire(kit, player, target, MultiNoTimers, triggered: false));

        Assert.False(ReadyAt(kit, player, MultiNoTimers, start, 9_999));
        Assert.True(ReadyAt(kit, player, MultiNoTimers, start, 10_000));
    }

    [Fact]
    public void ARangedSpellWithoutRecoveryStillGetsTheAttackTime_AndTheClientIsToldForTriggeredCasts()
    {
        using var kit = Kit();
        (Player player, FakeSession session, Player target) = Shooter(kit);
        Equip(player, Bow);
        Arm(player, Arrow);
        player.SetUInt32(UpdateFields.UnitFieldRangedattacktime, 2500);
        uint start = kit.Now;
        session.Clear();

        Assert.Equal(SpellCastResult.CastOk, Fire(kit, player, target, BareShot));

        Assert.False(ReadyAt(kit, player, BareShot, start, 2_499));
        Assert.True(ReadyAt(kit, player, BareShot, start, 2_500));
        byte[] packet = Packets(session, WorldOpcode.SmsgSpellCooldown).Single();
        Assert.Equal(BareShot, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)));
        Assert.Equal(2500u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12)));
    }

    [Fact]
    public void MeleeAbilityCooldown_IsUnchanged()
    {
        using var kit = Kit();
        (Player player, _, Player target) = Shooter(kit);
        player.SetUInt32(UpdateFields.UnitFieldRangedattacktime, 2500);
        uint start = kit.Now;

        Assert.Equal(SpellCastResult.CastOk, Fire(kit, player, target, MeleeStrike, triggered: false));

        Assert.False(ReadyAt(kit, player, MeleeStrike, start, 9_999));
        Assert.True(ReadyAt(kit, player, MeleeStrike, start, 10_000));
    }

    // --- cast time -------------------------------------------------------------------------

    [Fact]
    public void CastTime_AutoRepeatGetsNoFlatHalfSecond_AbilitiesGetRangedHaste()
    {
        var shape = new SpellInfo { CastTime = new SpellCastTime(3000, 0, 0), Attributes = SpellAttributes.UsesRangedSlot | SpellAttributes.IsAbility };
        Assert.Equal(3500, shape.GetCastTime(60));                                                // Aimed Shot, no haste: 3.0 s + 0.5 s
        Assert.Equal(2000, shape.GetCastTime(60, 1.0f, autoRepeat: false, rangedHaste: 0.5f));    // (3.0 s x 0.5) + 0.5 s
        Assert.Equal(3000, shape.GetCastTime(60, 1.0f, autoRepeat: true, rangedHaste: 0.5f));     // auto-repeat: neither haste nor the half second

        var wand = new SpellInfo { CastTime = new SpellCastTime(1000, 0, 0), Attributes = SpellAttributes.UsesRangedSlot };
        Assert.Equal(1500, wand.GetCastTime(60));                                                 // not an ability: the cast speed scales it
        Assert.Equal(1000, wand.GetCastTime(60, 0.5f, autoRepeat: false, rangedHaste: 0.1f));     // (1000 x 0.5 cast speed) + 500; ranged haste is for abilities only

        var plain = new SpellInfo { CastTime = new SpellCastTime(3000, 0, 0) };
        Assert.Equal(3000, plain.GetCastTime(60, 1.0f, autoRepeat: true, rangedHaste: 0.1f));
    }

    [Fact]
    public void ACastedRangedAbility_UsesTheRangedAttackSpeedSeam()
    {
        using var kit = Kit();
        (Player player, FakeSession session, Player target) = Shooter(kit);
        Equip(player, Bow);
        Arm(player, Arrow);
        kit.System.RangedAttackSpeedPct = _ => 0.5f;
        session.Clear();

        Assert.Equal(SpellCastResult.CastOk, Fire(kit, player, target, SlowShot, triggered: false));

        byte[] start = Packets(session, WorldOpcode.SmsgSpellStart).Single();
        var reader = new PacketReader(start);
        reader.ReadPackedGuid();
        reader.ReadPackedGuid();
        reader.ReadUInt32();
        reader.ReadUInt16();
        Assert.Equal(2000u, reader.ReadUInt32()); // 3000 x 0.5 + 500
    }
}
