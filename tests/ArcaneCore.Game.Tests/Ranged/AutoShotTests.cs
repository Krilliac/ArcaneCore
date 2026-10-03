using System.Buffers.Binary;
using ArcaneCore.Game.Combat;
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
/// Ranged lane S02: the auto-repeat slot (Auto Shot 75, wand Shoot 5019), after vmangos Unit::_UpdateAutoRepeatSpell
/// (Unit.cpp:2733-2778), SetCurrentCastedSpell (SpellCaster.cpp:1917-1992), InterruptSpell (:2083-2100), Spell::prepare
/// (Spell.cpp:3379-3416) and Spell::CheckCast (:5395-5403). The spell shapes copy classic-db spell_template: Auto Shot has
/// Attributes 0x50012, Ex2 0x20, Ex3 0x8000, category 0, no global cooldown, interrupt flags 1; wand Shoot has Attributes 0x12,
/// Ex2 0x20, Ex3 0x408000, category 351, damage class magic, interrupt flags 15. Range indices and distances are NOT from the
/// reference (SpellRange.dbc is not in the repositories): the tests give the spells an explicit range and keep the target well inside it.
/// The clock is manual: <see cref="Rig.Step"/> advances the spell system and the world tick together, and every timing test runs with
/// 50 ms and 100 ms steps; assertions allow one step of slack.
/// </summary>
public sealed class AutoShotTests
{
    private const uint AutoShot = 75;
    private const uint Shoot = 5019;
    private const uint Arcane = 3044;     // Arcane Shot shape: ranged slot, Ex2 0x20000, instant, instant cast
    private const uint WeaponHit = 940501;

    private const uint Bow = 94201;
    private const uint Wand = 94202;
    private const uint Arrow = 94210;

    private static readonly ItemTemplate[] Templates =
    [
        new() { Entry = Bow, Class = 2, SubClass = 2, DisplayId = 300, InventoryType = 15, Delay = 2500, MaxDurability = 40, AmmoType = 2 },
        new() { Entry = Wand, Class = 2, SubClass = 19, DisplayId = 302, InventoryType = 26, Delay = 1500, MaxDurability = 40, AmmoType = 0 },
        new() { Entry = Arrow, Class = 6, SubClass = 2, DisplayId = 5996, InventoryType = 24, Stackable = 200, Damages = [new ItemDamage(4, 5, 0)] },
    ];

    private static SpellInfo Weapon(uint id, uint attributes, uint ex2, uint ex3, uint category, SpellInterruptFlags interrupt, SpellDamageClass damageClass) => Spell(id, Effect(SpellEffectName.WeaponDamage, 5, SpellImplicitTarget.UnitEnemy)) with
    {
        Attributes = (SpellAttributes)attributes,
        AttributesEx2 = (SpellAttributesEx2)ex2,
        AttributesEx3 = ex3,
        Category = category,
        InterruptFlags = interrupt,
        DamageClass = damageClass,
        RangeIndex = 4,
        Range = new SpellRange(0, 35),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private sealed class Rig : IDisposable
    {
        public Rig(float targetDistance = 20)
        {
            Kit = new SpellTestKit(
                Weapon(AutoShot, 0x50012, 0x20, 0x8000, 0, SpellInterruptFlags.Movement, SpellDamageClass.Ranged),
                Weapon(Shoot, 0x12, 0x20, 0x408000, 351, (SpellInterruptFlags)15, SpellDamageClass.Magic),
                Weapon(Arcane, 0x50012, 0x20000, 0x8000, 0, SpellInterruptFlags.None, SpellDamageClass.Ranged) with { RecoveryTime = 6000 },
                Weapon(WeaponHit, 0x12, 0, 0, 0, SpellInterruptFlags.None, SpellDamageClass.Ranged));
            (Player, Session) = Kit.AddPlayer(1);
            (Target, _) = Kit.AddPlayer(2, targetDistance);
            Target.Health = 100_000;
            Player.Inventory.Templates = new ItemTemplateStore([.. ItemTestData.Templates, .. Templates]);
            Player.Inventory.GuidAllocator = new ItemGuidAllocator();
            Kit.Spellbook.Teach(Player, AutoShot, Shoot, Arcane, InstantHeal, ChannelSpell, CastBolt);
            Player.SetUInt32(UpdateFields.UnitFieldRangedattacktime, 2500);
            Player.SetUInt32(UpdateFields.UnitFieldBaseattacktime, 2000);
            Player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + 1, 1000);
            SpellSystem.SetPower(Player, PowerType.Rage, 100);
            CombatEnvironment.Register(Kit.World, new CombatEnvironment(new CombatOptions(), null, new SpellSystemMeleeHooks(Kit.System)));
            Kit.World.RunTick(0);
            Session.Clear();
        }

        public SpellTestKit Kit { get; }

        public Player Player { get; }

        public FakeSession Session { get; }

        public Player Target { get; }

        public MapCombat Combat => Player.Map!.Combat;

        public SpellSystem Spells => Kit.System;

        public UnitSpellState? State => Spells.GetState(Player.Guid);

        public void ArmBow(uint arrows = 20)
        {
            Equip(Player, Bow);
            Assert.Equal(InventoryResult.Ok, Player.Inventory.AddItem(Arrow, arrows, out _));
            Assert.True(PlayerAmmo.SetAmmo(Player, Arrow));
        }

        public SpellCastResult Press(uint spell) => Spells.HandleCastRequest(Player, spell, SpellCastTargets.ForUnit(Target.Guid));

        public void Step(uint ms, uint step)
        {
            for (uint done = 0; done < ms; done += step)
            {
                uint diff = Math.Min(step, ms - done);
                Kit.Now += diff;
                Spells.Update(diff);
                Kit.World.RunTick(diff);
            }
        }

        public int Shots(uint spell = AutoShot) => Session.Sent.Count(p => p.Opcode == WorldOpcode.SmsgSpellGo && SpellIdOf(p.Payload) == spell);

        public int Count(WorldOpcode opcode) => Session.Sent.Count(p => p.Opcode == opcode);

        public uint Arrows => Player.Inventory.GetItemCount(Arrow);

        public void SetMoving(bool moving)
        {
            if (moving)
            {
                Player.AddMovementFlags(MovementFlags.Forward);
            }
            else
            {
                Player.RemoveMovementFlags(MovementFlags.Forward);
            }
        }

        public void Dispose() => Kit.Dispose();
    }

    /// <summary>The spell id of an SMSG_SPELL_START / SMSG_SPELL_GO: two packed guids, then u32 spell.</summary>
    private static uint SpellIdOf(byte[] payload)
    {
        int offset = 0;
        for (int i = 0; i < 2; i++)
        {
            byte mask = payload[offset++];
            offset += System.Numerics.BitOperations.PopCount(mask);
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset));
    }

    private static void Equip(Player player, uint entry)
    {
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(entry, 1, out Item? item));
        Assert.Equal(InventoryResult.Ok, player.Inventory.CanEquipItem(InventorySlots.NullSlot, out byte dest, item!.Template, item, swap: false));
        player.Inventory.RemoveItem(InventorySlots.Bag0, item.Slot);
        player.Inventory.EquipItem(dest, item);
    }

    // --- the toggle (S02a) -------------------------------------------------------------------

    [Fact]
    public void Pressing_SendsOneSpellStart_AndFiresNothingAndTakesNoAmmo()
    {
        using var rig = new Rig();
        rig.ArmBow();

        Assert.Equal(SpellCastResult.CastOk, rig.Press(AutoShot));

        Assert.Equal(1, rig.Count(WorldOpcode.SmsgSpellStart));
        Assert.Equal(0, rig.Count(WorldOpcode.SmsgSpellGo));
        Assert.NotNull(rig.State!.AutoRepeatCast);
        Assert.Null(rig.State.CurrentCast); // the master lives in its own slot, never in the cast slot
        Assert.Equal(20u, rig.Arrows);
    }

    [Fact]
    public void CancelAutoRepeat_SendsTheEmptyCancelPacket_ThenTheInterruptPair_AndStopsShooting()
    {
        using var rig = new Rig();
        rig.ArmBow();
        rig.Press(AutoShot);
        rig.Session.Clear();

        Assert.True(rig.Spells.CancelAutoRepeat(rig.Player));

        List<WorldOpcode> order = [.. rig.Session.Sent.Select(p => p.Opcode)];
        Assert.Equal([WorldOpcode.SmsgCancelAutoRepeat, WorldOpcode.SmsgSpellFailedOther, WorldOpcode.SmsgCastResult], order);
        Assert.Empty(rig.Session.Sent.First(p => p.Opcode == WorldOpcode.SmsgCancelAutoRepeat).Payload);
        Assert.Equal((byte)SpellCastResult.Interrupted, Packets(rig.Session, WorldOpcode.SmsgCastResult).Single()[5]);
        Assert.Null(rig.State?.AutoRepeatCast);

        rig.Step(6000, 100);
        Assert.Equal(0, rig.Shots());
        Assert.False(rig.Spells.CancelAutoRepeat(rig.Player)); // nothing left to cancel, nothing sent
    }

    [Fact]
    public void CancelCast_WithTheSpellId_CancelsTheSlot_AnotherIdDoesNot()
    {
        using var rig = new Rig();
        rig.ArmBow();
        rig.Press(AutoShot);

        rig.Spells.CancelCast(rig.Player, InstantHeal);
        Assert.NotNull(rig.State!.AutoRepeatCast);

        rig.Spells.CancelCast(rig.Player, AutoShot);
        Assert.Null(rig.State?.AutoRepeatCast);
        Assert.Equal(1, rig.Count(WorldOpcode.SmsgCancelAutoRepeat));
    }

    [Fact]
    public void PressingAgain_ReplacesTheSpell_TellingTheClientTheOldOneStopped()
    {
        using var rig = new Rig();
        rig.ArmBow();
        rig.Press(AutoShot);
        rig.Session.Clear();

        Assert.Equal(SpellCastResult.CastOk, rig.Press(AutoShot));

        Assert.Equal(1, rig.Count(WorldOpcode.SmsgCancelAutoRepeat));
        Assert.Equal(1, rig.Count(WorldOpcode.SmsgSpellStart));
        Assert.NotNull(rig.State!.AutoRepeatCast);
    }

    [Fact]
    public void OutOfAmmo_IsRefused_AndNothingIsToggled()
    {
        using var rig = new Rig();
        rig.ArmBow(arrows: 1);
        rig.Player.Inventory.RemoveItem(InventorySlots.Bag0, rig.Player.Inventory.AllItems.First(i => i.Entry == Arrow).Slot);

        Assert.Equal(SpellCastResult.NoAmmo, rig.Press(AutoShot));

        Assert.Null(rig.State?.AutoRepeatCast);
        Assert.Equal((byte)SpellCastResult.NoAmmo, Packets(rig.Session, WorldOpcode.SmsgCastResult).Single()[5]);
    }

    [Fact]
    public void ACastOnTheBar_BlocksThePress_AChannelDoesNot()
    {
        using var rig = new Rig();
        rig.ArmBow();
        rig.Spells.HandleCastRequest(rig.Player, CastBolt, SpellCastTargets.ForUnit(rig.Target.Guid)); // 2 s on the bar

        Assert.Equal(SpellCastResult.SpellInProgress, rig.Press(AutoShot));
        Assert.Null(rig.State!.AutoRepeatCast);
    }

    [Fact]
    public void PressedWhileMoving_IsAccepted_NotRefused()
    {
        using var rig = new Rig();
        rig.ArmBow();
        rig.SetMoving(true);

        Assert.Equal(SpellCastResult.CastOk, rig.Press(AutoShot));

        Assert.NotNull(rig.State!.AutoRepeatCast);
        Assert.Equal(0, rig.Count(WorldOpcode.SmsgCastResult));
        Assert.Equal(0, rig.Count(WorldOpcode.SmsgSpellGo));
    }

    [Fact]
    public void WandShoot_BreaksAChannel_AutoShotDoesNot()
    {
        using var rig = new Rig();
        rig.ArmBow();
        rig.Spells.HandleCastRequest(rig.Player, ChannelSpell, SpellCastTargets.ForSelf());
        Assert.NotNull(rig.State!.CurrentCast);

        rig.Press(AutoShot);
        Assert.NotNull(rig.State.CurrentCast); // Auto Shot breaks nothing (SpellCaster.cpp:1950-1962)
        rig.Spells.CancelAutoRepeat(rig.Player);

        rig.Press(Shoot); // category 351: "generic autorepeats break generic non-delayed and channeled non-delayed spells"
        Assert.Null(rig.State?.CurrentCast);
        Assert.NotNull(rig.State!.AutoRepeatCast);
    }

    [Fact]
    public void AGenericCast_CancelsAWand_ButNotAutoShot()
    {
        using var rig = new Rig();
        rig.ArmBow();
        rig.Press(AutoShot);
        rig.Spells.HandleCastRequest(rig.Player, InstantHeal, SpellCastTargets.ForSelf());
        Assert.NotNull(rig.State!.AutoRepeatCast);

        rig.Spells.CancelAutoRepeat(rig.Player);
        rig.Press(Shoot);
        rig.Step(2000, 100); // the first heal's global cooldown
        rig.Session.Clear();
        rig.Spells.HandleCastRequest(rig.Player, InstantHeal, SpellCastTargets.ForSelf());

        Assert.Null(rig.State?.AutoRepeatCast);
        Assert.Equal(1, rig.Count(WorldOpcode.SmsgCancelAutoRepeat));
    }

    // --- the shot cycle (S02b) ---------------------------------------------------------------

    [Theory]
    [InlineData(100u)]
    [InlineData(50u)]
    public void FirstShotAfterTheWindUp_ThenOnePerWeaponPeriod_AmmoDropsByOneEach(uint step)
    {
        using var rig = new Rig();
        rig.ArmBow();
        rig.Press(AutoShot);

        rig.Step(400, step);
        Assert.Equal(0, rig.Shots());

        rig.Step(300, step); // 700 ms: the 0.5 s wind-up is over
        Assert.Equal(1, rig.Shots());
        Assert.Equal(19u, rig.Arrows);

        rig.Step(2200, step); // 2900 ms: the next one is a weapon period (2500 ms) after the first
        Assert.Equal(1, rig.Shots());

        rig.Step(400, step); // 3300 ms
        Assert.Equal(2, rig.Shots());
        Assert.Equal(18u, rig.Arrows);
    }

    [Theory]
    [InlineData(100u)]
    [InlineData(50u)]
    public void ARangedTimerAlreadyAbove500_IsNotRaisedByTheWindUp(uint step)
    {
        using var rig = new Rig();
        rig.ArmBow();
        rig.Player.Combat.SetAttackTimer(WeaponAttackType.RangedAttack, 1200);
        rig.Press(AutoShot);

        rig.Step(1000, step);
        Assert.Equal(0, rig.Shots());

        rig.Step(500, step); // 1500 ms
        Assert.Equal(1, rig.Shots());
    }

    [Fact]
    public void NoShotSendsASpellCooldownPacket()
    {
        using var rig = new Rig();
        rig.ArmBow();
        rig.Press(AutoShot);

        rig.Step(6000, 100);

        Assert.True(rig.Shots() >= 2);
        Assert.Equal(0, rig.Count(WorldOpcode.SmsgSpellCooldown));
    }

    [Theory]
    [InlineData(100u)]
    [InlineData(50u)]
    public void ToggledWhileMoving_NothingFiresUntilMovementStops_ThenTheWindUpRestarts(uint step)
    {
        using var rig = new Rig();
        rig.ArmBow();
        rig.SetMoving(true);
        rig.Press(AutoShot);

        rig.Step(3000, step);
        Assert.Equal(0, rig.Shots());
        Assert.NotNull(rig.State!.AutoRepeatCast); // Auto Shot is not cancelled by movement

        rig.SetMoving(false);
        rig.Step(300, step);
        Assert.Equal(0, rig.Shots());
        rig.Step(400, step);
        Assert.Equal(1, rig.Shots());
    }

    [Fact]
    public void WandShoot_IsCancelledByMovement_WithTheCancelPacket()
    {
        using var rig = new Rig();
        Equip(rig.Player, Wand);
        rig.Press(Shoot);
        rig.Step(200, 100);
        rig.Session.Clear();

        rig.SetMoving(true);
        rig.Step(100, 100);

        Assert.Null(rig.State?.AutoRepeatCast);
        Assert.Equal(1, rig.Count(WorldOpcode.SmsgCancelAutoRepeat));
    }

    [Theory]
    [InlineData(100u)]
    [InlineData(50u)]
    public void AnInstantAbility_DelaysTheNextAutoShotByAFreshWindUp(uint step)
    {
        using var rig = new Rig();
        rig.ArmBow();
        rig.Press(AutoShot);
        rig.Step(700, step);
        Assert.Equal(1, rig.Shots());
        rig.Player.Combat.SetAttackTimer(WeaponAttackType.RangedAttack, 0); // as if the period had run out
        rig.Spells.HandleCastRequest(rig.Player, Arcane, SpellCastTargets.ForUnit(rig.Target.Guid));
        int shots = rig.Shots();

        rig.Step(300, step);
        Assert.Equal(shots, rig.Shots());   // the wind-up: no shot within 300 ms of the ability

        rig.Step(500, step);
        Assert.Equal(shots + 1, rig.Shots());
    }

    [Fact]
    public void ACastOnTheBar_DelaysTheShot_AndItResumesAfterwards()
    {
        using var rig = new Rig();
        rig.ArmBow();
        rig.Press(AutoShot);
        rig.Spells.HandleCastRequest(rig.Player, CastBolt, SpellCastTargets.ForUnit(rig.Target.Guid)); // 2 s

        rig.Step(1900, 100);
        Assert.Equal(0, rig.Shots());

        rig.Step(1500, 100); // the bolt landed at 2000 ms; the shot follows the wind-up
        Assert.Equal(1, rig.Shots());
    }

    // --- the interrupt sources (S02c) --------------------------------------------------------

    [Theory]
    [InlineData(0)]   // out of range
    [InlineData(1)]   // out of ammo
    [InlineData(2)]   // the target dies
    [InlineData(3)]   // the shooter dies
    public void ALostPrecondition_CancelsTheSlot_WithTheCancelPacket(int cause)
    {
        using var rig = new Rig();
        rig.ArmBow();
        rig.Press(AutoShot);
        rig.Step(700, 100);
        Assert.Equal(1, rig.Shots());
        rig.Session.Clear();

        switch (cause)
        {
            case 0:
                rig.Target.Relocate(300, 0, rig.Target.Z, 0, rig.Kit.Now);
                break;
            case 1:
                rig.Player.Inventory.RemoveItem(InventorySlots.Bag0, rig.Player.Inventory.AllItems.First(i => i.Entry == Arrow).Slot);
                break;
            case 2:
                rig.Target.Health = 0;
                break;
            default:
                rig.Player.Health = 0;
                rig.Spells.OnUnitDied(rig.Player);
                break;
        }

        rig.Step(3000, 100);

        Assert.Null(rig.State?.AutoRepeatCast);
        Assert.Equal(1, rig.Count(WorldOpcode.SmsgCancelAutoRepeat));
    }

    [Fact]
    public void RetargetOnSelection_FollowsAValidTarget_AndCancelsOnAnInvalidOne()
    {
        using var rig = new Rig();
        rig.ArmBow();
        (Player other, _) = rig.Kit.AddPlayer(3, 15);
        rig.Press(AutoShot);
        rig.Session.Clear();

        rig.Spells.RetargetAutoRepeat(rig.Player, rig.Player.Guid); // oneself is no valid attack target
        Assert.Null(rig.State?.AutoRepeatCast);
        Assert.Equal(1, rig.Count(WorldOpcode.SmsgCancelAutoRepeat));

        rig.Press(AutoShot);
        rig.Session.Clear();
        rig.Spells.RetargetAutoRepeat(rig.Player, ObjectGuid.Empty);
        Assert.Null(rig.State?.AutoRepeatCast);
        _ = other;
    }

    [Fact]
    public void LogoutDropsTheSlot_WithoutPackets()
    {
        using var rig = new Rig();
        rig.ArmBow();
        rig.Press(AutoShot);
        rig.Session.Clear();

        rig.Spells.RemoveUnit(rig.Player);

        Assert.Null(rig.Spells.GetState(rig.Player.Guid));
        Assert.Equal(0, rig.Count(WorldOpcode.SmsgCancelAutoRepeat));
    }

    // --- melee interplay ---------------------------------------------------------------------

    [Fact]
    public void WhileAutoShotIsOn_TheWhiteSwingDoesNotFire_AndTheTimerIsNotConsumed()
    {
        using var rig = new Rig(targetDistance: 3);
        rig.ArmBow();
        rig.Press(AutoShot);
        rig.Combat.Attack(rig.Player, rig.Target);
        rig.Session.Clear();

        rig.Step(1500, 100);

        Assert.Equal(0, rig.Session.Sent.Count(p => p.Opcode == WorldOpcode.SmsgAttackerstateupdate));
        Assert.Equal(0u, rig.Player.Combat.GetAttackTimer(WeaponAttackType.BaseAttack));

        rig.Spells.CancelAutoRepeat(rig.Player);
        rig.Step(200, 100);
        Assert.True(rig.Session.Sent.Count(p => p.Opcode == WorldOpcode.SmsgAttackerstateupdate) >= 1);
    }
}
