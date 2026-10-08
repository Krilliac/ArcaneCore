using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Totems;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.ClassSpells.Totems;

/// <summary>
/// Every totem stands rooted (class-scripts lane verification of the review item "totems are not rooted"): the summon roots it with the server movement
/// flag (TotemSystem.cs, Totem::Create/Summon keep it in place and TotemAI never moves it), passive and active alike, and it stays rooted through
/// the map updates.
/// </summary>
public sealed class TotemRootTests
{
    [Theory]
    [InlineData(TotemKit.EarthTotemSummon, TotemSlot.Earth)]
    [InlineData(TotemKit.ActiveSummon, TotemSlot.Fire)]
    public void ASummonedTotem_IsRooted_AndStaysRooted(uint summon, TotemSlot slot)
    {
        using var kit = new TotemKit();
        (Player shaman, _) = kit.AddPlayer(1, 100, 200);

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(shaman, summon));
        Creature totem = Assert.IsType<Creature>(kit.Totems.GetTotem(shaman, slot));
        Assert.True(totem.Movement.HasFlag(MovementFlags.Root));

        (float x, float y) = (totem.X, totem.Y);
        kit.Advance(5000);

        Assert.True(totem.Movement.HasFlag(MovementFlags.Root));
        Assert.Equal((x, y), (totem.X, totem.Y));
    }
}
