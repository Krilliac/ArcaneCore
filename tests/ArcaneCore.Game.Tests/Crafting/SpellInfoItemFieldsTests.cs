using ArcaneCore.Game.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Crafting;

public sealed class SpellInfoItemFieldsTests
{
    [Fact]
    public void DefaultSpellInfo_HasNoReagentsOrTools()
    {
        var spell = new SpellInfo { Id = 1 };

        // The eight Spell.dbc reagent slots are always there (vmangos Reagent[MAX_SPELL_REAGENTS]); an empty one names no item.
        Assert.Equal(SpellConstants.MaxReagents, spell.Reagents.Count);
        Assert.All(spell.Reagents, r => Assert.False(r.IsPresent));
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
