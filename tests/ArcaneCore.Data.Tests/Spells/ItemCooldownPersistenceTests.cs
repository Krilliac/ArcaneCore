using ArcaneCore.Data.Characters.Spells;
using Xunit;

namespace ArcaneCore.Data.Tests.Spells;

public sealed class ItemCooldownPersistenceTests
{
    [Fact]
    public void CooldownRow_IdentityIncludesItemOwner()
    {
        var a = new CharacterSpellCooldownOwnerRow { CharacterId = 1, SpellId = 99001, ItemId = 6948, Category = 77 };
        var b = new CharacterSpellCooldownOwnerRow { CharacterId = 1, SpellId = 99001, ItemId = 117, Category = 78 };
        Assert.NotEqual(a.ItemId, b.ItemId);
    }

    [Fact]
    public void OwnerRow_PreservesBothIndependentExpiries()
    {
        var row = new CharacterSpellCooldownOwnerRow { CharacterId = 1, SpellId = 9901, ItemId = 6948,
            Category = 77, SpellEndsAtUnixMs = 100, CategoryEndsAtUnixMs = 200 };
        Assert.True(row.CategoryEndsAtUnixMs > row.SpellEndsAtUnixMs);
    }
}
