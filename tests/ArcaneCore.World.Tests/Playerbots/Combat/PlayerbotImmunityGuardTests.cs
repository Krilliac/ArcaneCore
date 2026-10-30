using ArcaneCore.World.Playerbots.Combat;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Combat.RotationTestKit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// Creature-type filters use the CreatureType.dbc ids (4 elemental, 6 undead, 9 mechanical, 11 totem), and Hamstring is not recast on
/// a victim that is already rooted or slowed (vmangos PartyBotAI warrior branch).
/// </summary>
public sealed class PlayerbotImmunityGuardTests
{
    private static readonly PlayerbotHunterRotation Hunter = new();
    private static readonly PlayerbotWarriorRotation Warrior = new();

    private static string? HunterNext(uint creatureType)
        => Next(Hunter.InCombat(State(Hunter, [Hostile("Serpent Sting")], Self(3), Victim(20f, creatureType: creatureType), PlayerbotRole.MeleeDps)));

    private static string? WarriorNext(RotationUnit victim, string spell)
        => Next(Warrior.InCombat(State(Warrior, [Hostile(spell)], Self(1), victim, PlayerbotRole.MeleeDps)));

    [Theory]
    [InlineData(4u)]
    [InlineData(9u)]
    public void SerpentSting_SkipsElementalsAndMechanicals(uint creatureType) => Assert.Null(HunterNext(creatureType));

    [Theory]
    [InlineData(1u)]
    [InlineData(11u)]
    public void SerpentSting_IsCastOnOtherTypes(uint creatureType) => Assert.Equal("Serpent Sting", HunterNext(creatureType));

    [Theory]
    [InlineData(4u)]
    [InlineData(6u)]
    [InlineData(9u)]
    public void Rend_SkipsElementalsUndeadAndMechanicals(uint creatureType) => Assert.Null(WarriorNext(Victim(3f, creatureType: creatureType), "Rend"));

    [Theory]
    [InlineData(1u)]
    [InlineData(11u)]
    public void Rend_IsCastOnOtherTypes(uint creatureType) => Assert.Equal("Rend", WarriorNext(Victim(3f, creatureType: creatureType), "Rend"));

    [Fact]
    public void Hamstring_IsCastOnAFreeRunner() => Assert.Equal("Hamstring", WarriorNext(Victim(6f, moving: true), "Hamstring"));

    [Fact]
    public void Hamstring_IsNotCastOnARootedRunner() => Assert.Null(WarriorNext(Victim(6f, moving: true) with { IsRooted = true }, "Hamstring"));

    [Fact]
    public void Hamstring_IsNotCastOnASlowedRunner() => Assert.Null(WarriorNext(Victim(6f, moving: true) with { IsSlowed = true }, "Hamstring"));
}
