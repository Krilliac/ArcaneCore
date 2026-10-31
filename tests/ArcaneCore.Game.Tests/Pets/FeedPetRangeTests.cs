using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// Feed Pet 6991 needs the pet within the spell's range (10 yd, range index 7) and in the owner's line of sight; otherwise the cast fails and the
/// food stays (vmangos Spell::EffectFeedPet, SpellEffects.cpp:5108-5118).
/// </summary>
public sealed class FeedPetRangeTests
{
    private const uint FeedPet = 6991;
    private const uint FeedPetEffect = 1539;
    private const uint Meat = 90_501;

    private static readonly ItemTemplate[] Items =
    [
        new() { Entry = Meat, Class = 0, Name = "Test Meat", DisplayId = 1, Stackable = 20, FoodType = 1, ItemLevel = 5 },
    ];

    private static (PetTestKit Kit, Player Player, Creature Pet, Item Food) Rig()
    {
        var kit = new PetTestKit(
        [
            Spell(FeedPet, Effect(SpellEffectName.FeedPet, 0, SpellImplicitTarget.None, trigger: FeedPetEffect)) with
            {
                RangeIndex = 7,
                Range = new SpellRange(0, 10),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(FeedPetEffect, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            {
                Duration = new SpellDuration(10_000, 0, 10_000),
            },
        ]);
        (Player player, _) = kit.AddPlayer(1, 5, 5);
        player.Inventory.Templates = new ItemTemplateStore(Items, []);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        Item food = ItemTestData.Give(player.Inventory, Meat, 3);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(player, PetTestKit.PetSpell));
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        pet.Level = 5;
        return (kit, player, pet, food);
    }

    private static SpellCastResult Feed(PetTestKit kit, Player player, Item food)
    {
        SpellCastResult result = kit.Spells.System.CastSpell(
            player, FeedPet, new SpellCastTargets { Mask = SpellCastTargetFlags.Item, Item = food.Guid }, triggered: false);
        if (kit.Spells.System.SpellBatchWindowMs != 0)
        {
            kit.Spells.Advance(kit.Spells.System.SpellBatchWindowMs);
        }

        return result;
    }

    [Fact]
    public void ThePetNearAndInSight_IsFed()
    {
        (PetTestKit kit, Player player, Creature pet, Item food) = Rig();
        using PetTestKit owned = kit;
        pet.Relocate(player.X + 3f, player.Y, player.Z);

        Assert.Equal(SpellCastResult.CastOk, Feed(kit, player, food));

        Assert.Equal(2u, player.Inventory.GetItemCount(Meat));
        Assert.True(kit.Spells.System.HasAura(player, FeedPetEffect));
    }

    [Fact]
    public void ThePetOutOfRange_FailsOutOfRange_AndKeepsTheFood()
    {
        (PetTestKit kit, Player player, Creature pet, Item food) = Rig();
        using PetTestKit owned = kit;
        pet.Relocate(player.X + 40f, player.Y, player.Z);

        Assert.Equal(SpellCastResult.OutOfRange, Feed(kit, player, food));

        Assert.Equal(3u, player.Inventory.GetItemCount(Meat));
        Assert.False(kit.Spells.System.HasAura(player, FeedPetEffect));
    }

    [Fact]
    public void ThePetBehindAWall_FailsLineOfSight_AndKeepsTheFood()
    {
        (PetTestKit kit, Player player, Creature pet, Item food) = Rig();
        using PetTestKit owned = kit;
        pet.Relocate(player.X + 6f, player.Y, player.Z);
        WorldCollision.Of(kit.Spells.World).Install(lineOfSight: new FakeLineOfSight { WallX = player.X + 3f });

        Assert.Equal(SpellCastResult.LineOfSight, Feed(kit, player, food));

        Assert.Equal(3u, player.Inventory.GetItemCount(Meat));
        Assert.False(kit.Spells.System.HasAura(player, FeedPetEffect));
    }
}
