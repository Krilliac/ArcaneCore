using ArcaneCore.Game.Totems;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.ClassSpells.Totems;

public sealed class SentryTotemAuraTests
{
    [Fact]
    public void DummyAura_ViewsAirTotem_AndRemovalRestoresTheOwnersCamera()
    {
        // vmangos Aura::HandleAuraDummy (SpellAuras.cpp:2182-2196); build-5875 spell 6495
        // summons an air totem as effect 0 and applies its DUMMY aura to the caster as effect 1.
        using var kit = new TotemTestKit();
        (var shaman, _) = kit.AddPlayer(1);

        Assert.Equal(0UL, shaman.GetUInt64(UpdateFields.PlayerFarsight));
        Assert.Equal(ArcaneCore.Game.Spells.SpellCastResult.CastOk, kit.Cast(shaman, TotemTestKit.SentryTotem));
        var totem = Assert.IsType<ArcaneCore.Game.Creatures.Creature>(kit.Totems.GetTotem(shaman, TotemSlot.Air));
        Assert.Equal(totem.Guid.Value, shaman.GetUInt64(UpdateFields.PlayerFarsight));

        kit.Spells.RemoveAuras(shaman, TotemTestKit.SentryTotem);
        Assert.Equal(0UL, shaman.GetUInt64(UpdateFields.PlayerFarsight));
    }

    [Fact]
    public void UnsummoningTheViewedTotem_RestoresTheOwnersCamera()
    {
        using var kit = new TotemTestKit();
        (var shaman, _) = kit.AddPlayer(1);
        Assert.Equal(ArcaneCore.Game.Spells.SpellCastResult.CastOk, kit.Cast(shaman, TotemTestKit.SentryTotem));
        var totem = Assert.IsType<ArcaneCore.Game.Creatures.Creature>(kit.Totems.GetTotem(shaman, TotemSlot.Air));
        Assert.Equal(totem.Guid.Value, shaman.GetUInt64(UpdateFields.PlayerFarsight));

        kit.Totems.UnsummonAll(shaman);
        Assert.Equal(0UL, shaman.GetUInt64(UpdateFields.PlayerFarsight));
    }
}
