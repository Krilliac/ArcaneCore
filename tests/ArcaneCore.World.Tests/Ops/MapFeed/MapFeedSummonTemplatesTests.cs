using System.Text.Json;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Pets;
using ArcaneCore.Kernel.WorldData.Totems;
using ArcaneCore.World.Ops.MapFeed;
using Xunit;
using static ArcaneCore.World.Tests.Ops.Control.WorldControlPipeTests;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// The static summon and totem template set that the <c>caps</c> answer carries as <c>summonTemplates</c> (docs/areas/atlas-public.md, "Guest data
/// policy"): the pure set computation, and the key on the wire.
/// </summary>
public sealed class MapFeedSummonTemplatesTests
{
    private static CreatureTemplate Template(uint entry, uint creatureType, uint typeFlags = 0)
        => new() { Entry = entry, Name = "T" + entry, CreatureType = creatureType, TypeFlags = typeFlags, DisplayIds = [1], MinLevelHealth = 1, MaxLevelHealth = 1 };

    [Fact]
    public void ATameableBeast_IsInTheSet_ButAPlainBeastAndATameableNonBeastAreNot()
    {
        var creatures = new CreatureContent(
            [Template(10, 1, 0x1), Template(11, 1, 0x2), Template(12, 7, 0x1), Template(13, 1, 0x3)], [], [], [], []);

        Assert.Equal([10u, 13u], MapFeedSummonTemplates.Compute(creatures, null, null));
    }

    [Fact]
    public void TheSet_IsTotemAndNonCombatPetTypes_TotemSpellRows_AndPetRows_SortedWithoutRepeats()
    {
        var creatures = new CreatureContent(
            [Template(50, 11), Template(60, 12), Template(70, 1), Template(80, 7), Template(10, 11)], [], [], [], []);
        var totems = new TotemContent([(50, 1000u), (20, 2000u)]);
        var pets = new PetContent(
            [new PetLevelStats(30, 1, 10, 0, 0, 1, 2, 1, 1, 1, 1, 1)],
            [new PetCreateSpells(30, [5u]), new PetCreateSpells(40, [6u])]);

        uint[] set = MapFeedSummonTemplates.Compute(creatures, totems, pets);

        Assert.Equal([10u, 20u, 30u, 40u, 50u, 60u], set); // 70 (beast) and 80 (humanoid) are ordinary creatures
    }

    [Fact]
    public void AnyMissingTable_ContributesNothing_AndNoTableGivesAnEmptySet()
    {
        Assert.Empty(MapFeedSummonTemplates.Compute(null, null, null));
        Assert.Equal([20u], MapFeedSummonTemplates.Compute(null, new TotemContent([(20, 1u)]), null));
        Assert.Equal([10u], MapFeedSummonTemplates.Compute(new CreatureContent([Template(10, 11)], [], [], [], []), TotemContent.Empty, PetContent.Empty));
    }

    [WindowsFact]
    public async Task Caps_CarriesSummonTemplatesAsAnArrayOfEntries_AndNoOrdinaryCreature()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Terrain = true, Content = true, Actions = _ => { } });
        FeedClient client = await world.ConnectAsync();

        JsonElement caps = await ActionWire.RequestAsync(client, """{"op":"caps","id":1}""", 1);

        Assert.True(caps.Ok(), caps.ToString());
        JsonElement templates = caps.GetProperty("summonTemplates");
        Assert.Equal(JsonValueKind.Array, templates.ValueKind);
        Assert.DoesNotContain(templates.EnumerateArray(), e => e.GetUInt32() == FeedWorld.WolfEntry); // a plain beast
        Assert.Contains(templates.EnumerateArray(), e => e.GetUInt32() == FeedWorld.TotemTemplateEntry); // proves the DI wiring resolved the creature content
    }
}
