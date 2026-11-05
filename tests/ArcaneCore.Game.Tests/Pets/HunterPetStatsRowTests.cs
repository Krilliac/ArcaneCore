using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.WorldData.Pets;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// vmangos Pet::InitStatsForLevel reads pet_levelstats with creatureId = HUNTER_PET ? 1 : cinfo->entry (Pet.cpp:1288). Unproven until the integration gate.
/// </summary>
public sealed class HunterPetStatsRowTests
{
    private const uint CallSpell = 991201;

    private static PetLevelStats StatsRow(uint entry, uint health)
        => new(entry, 1, health, 0, 0, 0, 0, 1, 1, 1, 1, 1);

    [Theory]
    [InlineData(true, true, 700u)]   // hunter, shared row present: creature id 1 row
    [InlineData(true, false, 300u)]  // hunter, no shared row: own entry
    [InlineData(false, true, 300u)]  // not a hunter: the shared row is ignored
    public async Task PetStats_UseCreatureId1ForAHunter_AndTheOwnEntryOtherwise(bool hunter, bool withSharedRow, uint expectedHealth)
    {
        PetLevelStats[] rows = withSharedRow ? [StatsRow(1, 700), StatsRow(PetTestKit.PetEntry, 300)] : [StatsRow(PetTestKit.PetEntry, 300)];
        using PetTestKit kit = new([Spell(CallSpell, Effect(SpellEffectName.SummonPet, 0, misc: 0))], new PetContent(rows, []));
        (Player owner, _) = kit.AddPlayer(1);
        if (hunter)
        {
            owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        }

        kit.Service.LoadPersistence = (_, _) => Task.FromResult<PersistentPetSnapshot?>(
            new(1, 804, PetTestKit.PetEntry, 1, 0, 50, 0, 0, 1, [], []));
        await kit.Service.ReadCurrentPetAsync(owner);

        kit.Cast(owner, CallSpell);

        Creature pet = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(expectedHealth, pet.MaxHealth);
    }
}
