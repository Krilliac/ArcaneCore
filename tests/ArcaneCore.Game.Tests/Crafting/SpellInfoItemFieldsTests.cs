using ArcaneCore.Game.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Crafting;

public sealed class SpellInfoItemFieldsTests
{
    [Fact]
    public void DefaultSpellInfo_HasNoReagentsOrTools()
    {
        var spell = new SpellInfo { Id = 1 };

        Assert.Empty(spell.Reagents);
        Assert.Empty(spell.Totems);
    }

    [Fact]
    public void Reagents_AreAssignableAndKeepOrder()
    {
        var spell = new SpellInfo { Id = 2, Reagents = [new SpellReagent(5, 2), new SpellReagent(3, 1)] };

        Assert.Equal(5u, spell.Reagents[0].ItemId);
        Assert.Equal(1u, spell.Reagents[1].Count);
    }
}
