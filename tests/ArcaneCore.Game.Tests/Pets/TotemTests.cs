using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>Totems (SPELL_EFFECT_SUMMON_TOTEM and the four slot effects): vmangos Spell::EffectSummonTotem and Totem.cpp.</summary>
public sealed class TotemTests
{
    [Fact]
    public void SlotTotemSpell_SummonsAUnitCreatureOwnedByTheCaster()
    {
        using var kit = new PetTestKit();
        (Player caster, _) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(caster, FireTotemSpell));

        Creature totem = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(TotemEntry, totem.Entry);
        Assert.True(totem.IsTotem);
        Assert.False(totem.IsPet);

        // vmangos SpellEffects.cpp:4957 summons the totem with HIGHGUID_UNIT, never HIGHGUID_PET.
        Assert.Equal(HighGuid.Unit, totem.Guid.High);

        // SetOwner (Totem.cpp:152-158): creator, owner, faction and level come from the caster.
        Assert.Equal(caster.Guid, totem.OwnerGuid);
        Assert.Equal(caster.Guid, totem.CreatorGuid);
        Assert.Equal(caster.FactionTemplate, totem.FactionTemplate);
        Assert.Equal(caster.Level, totem.Level);
        Assert.Equal(FireTotemSpell, totem.GetUInt32(UpdateFields.UnitCreatedBySpell));

        // A player's totem is player controlled; a non-zero effect value is the totem's health (SpellEffects.cpp:4987-4991).
        Assert.NotEqual(0u, (uint)(totem.UnitFlags & UnitFlags.PlayerControlled));
        Assert.Equal(5u, totem.MaxHealth);
        Assert.Equal(5u, totem.Health);

        Assert.Same(totem, kit.Map.Pets!.GetTotem(caster, TotemSlots.Fire));
        Assert.Null(kit.Map.Pets!.GetTotem(caster, TotemSlots.Earth));
    }

    [Fact]
    public void TotemIsPlacedTwoYardsAwayAtTheSlotAngle()
    {
        using var kit = new PetTestKit();
        (Player caster, _) = kit.AddPlayer(1, 10, 20);
        caster.Orientation = 1.0f;

        kit.Cast(caster, FireTotemSpell);
        kit.Cast(caster, EarthTotemSpell);

        // SpellEffects.cpp:4957: angle = pi/4 - slot * pi/2; GetClosePoint distance = owner radius + 2 + totem radius.
        Creature fire = kit.Map.Pets!.GetTotem(caster, TotemSlots.Fire)!;
        Creature earth = kit.Map.Pets!.GetTotem(caster, TotemSlots.Earth)!;
        foreach ((Creature totem, float angle) in new[] { (fire, MathF.PI / 4), (earth, (MathF.PI / 4) - (MathF.PI / 2)) })
        {
            float distance = caster.BoundingRadius + 2.0f + totem.BoundingRadius;
            Assert.Equal(10 + (distance * MathF.Cos(1.0f + angle)), totem.X, 3);
            Assert.Equal(20 + (distance * MathF.Sin(1.0f + angle)), totem.Y, 3);
            Assert.Equal(caster.Z, totem.Z, 3);
            Assert.Equal(1.0f, totem.Orientation, 3);
        }
    }

    [Fact]
    public void SecondTotemInTheSameSlotReplacesTheFirst_OtherSlotsAndSlotlessTotemsCoexist()
    {
        using var kit = new PetTestKit();
        (Player caster, _) = kit.AddPlayer(1);

        kit.Cast(caster, FireTotemSpell);
        Creature first = Assert.Single(kit.Creatures.Creatures);

        kit.Cast(caster, EarthTotemSpell);
        Assert.Equal(2, kit.Creatures.Creatures.Count);

        kit.Cast(caster, FireTotemSpell);
        Assert.Equal(2, kit.Creatures.Creatures.Count);
        Assert.Null(kit.Creatures.FindCreature(first.Guid));
        Creature second = kit.Map.Pets!.GetTotem(caster, TotemSlots.Fire)!;
        Assert.NotSame(first, second);
        Assert.NotNull(kit.Map.Pets!.GetTotem(caster, TotemSlots.Earth));

        // SPELL_EFFECT_SUMMON_TOTEM has no slot (TOTEM_SLOT_NONE): it neither replaces nor is replaced.
        kit.Cast(caster, SlotlessTotemSpell);
        kit.Cast(caster, SlotlessTotemSpell);
        Assert.Equal(4, kit.Creatures.Creatures.Count);
        Assert.Same(second, kit.Map.Pets!.GetTotem(caster, TotemSlots.Fire));
    }

    [Fact]
    public void TotemDespawnsWhenItsDurationEnds()
    {
        using var kit = new PetTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        kit.Cast(caster, ShortTotemSpell);
        Creature totem = Assert.Single(kit.Creatures.Creatures);

        kit.Run(800);
        Assert.Same(totem, kit.Creatures.FindCreature(totem.Guid));

        kit.Run(300);
        Assert.Null(kit.Creatures.FindCreature(totem.Guid));
        Assert.Null(kit.Map.Pets!.GetTotem(caster, TotemSlots.Water));
        Assert.Empty(kit.Map.Pets!.Summons);
    }

    [Fact]
    public void TotemDisappearsWhenItsOwnerDies_OrLeavesTheMap()
    {
        using var kit = new PetTestKit();
        (Player dying, _) = kit.AddPlayer(1);
        (Player leaving, _) = kit.AddPlayer(2, 30, 30);
        kit.Cast(dying, FireTotemSpell);
        kit.Cast(leaving, FireTotemSpell);
        Assert.Equal(2, kit.Creatures.Creatures.Count);

        dying.Health = 0;
        kit.Run(100);
        Assert.Equal(TotemEntry, Assert.Single(kit.Creatures.Creatures).Entry);
        Assert.Null(kit.Map.Pets!.GetTotem(dying, TotemSlots.Fire));
        Assert.NotNull(kit.Map.Pets!.GetTotem(leaving, TotemSlots.Fire));

        kit.Map.RemovePlayer(leaving);
        Assert.Empty(kit.Creatures.Creatures);
    }

    [Fact]
    public void KilledTotemIsForgotten_AndItsSlotFreed()
    {
        using var kit = new PetTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        kit.Cast(caster, FireTotemSpell);
        Creature totem = Assert.Single(kit.Creatures.Creatures);

        kit.Map.Combat.Kill(null, totem);
        kit.Run(100);

        Assert.Null(kit.Map.Pets!.GetTotem(caster, TotemSlots.Fire));
        Assert.Empty(kit.Map.Pets!.Summons);
    }

    [Fact]
    public void Summoning_SendsTheSpawnAndDespawnAnimations_NeverATotemCreatedPacket()
    {
        using var kit = new PetTestKit();
        (Player caster, FakeSession session) = kit.AddPlayer(1);

        kit.Cast(caster, ShortTotemSpell);
        Creature totem = Assert.Single(kit.Creatures.Creatures);

        // Object::SendObjectSpawnAnim (Object.cpp:2352-2357): SMSG_GAMEOBJECT_SPAWN_ANIM with the full GUID.
        byte[] spawn = Assert.Single(Packets(session, WorldOpcode.SmsgGameobjectSpawnAnim));
        Assert.Equal(totem.Guid.Value, BitConverter.ToUInt64(spawn));
        Assert.Equal(8, spawn.Length);

        kit.Run(1300);
        byte[] despawn = Assert.Single(Packets(session, WorldOpcode.SmsgGameobjectDespawnAnim));
        Assert.Equal(totem.Guid.Value, BitConverter.ToUInt64(despawn));

        // SMSG_TOTEM_CREATED has no 1.12 form (wow_messages smsg_totem_created.md): it must not exist in the opcode table of this build's traffic.
        Assert.DoesNotContain(session.Sent, p => p.Opcode.ToString().Contains("Totem", StringComparison.Ordinal));
    }

    [Fact]
    public void TotemWithAMissingTemplateSummonsNothing()
    {
        using var kit = new PetTestKit([Spell(910100, Effect(SpellEffectName.SummonTotemSlot4, 5, misc: 999_999)) with
        {
            Duration = new SpellDuration(10_000, 0, 10_000),
        }]);
        (Player caster, _) = kit.AddPlayer(1);

        kit.Cast(caster, 910100);

        Assert.Empty(kit.Creatures.Creatures);
        Assert.Empty(kit.Map.Pets!.Summons);
    }
}
