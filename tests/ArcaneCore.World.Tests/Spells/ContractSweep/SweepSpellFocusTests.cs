using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Tests.ClientData;
using Xunit;

namespace ArcaneCore.World.Tests.Spells.ContractSweep;

/// <summary>
/// The sweep rig stages a spell focus object for a RequiresSpellFocus spell. The rig gets the map's object system after the first tick
/// (the game object feature installs it from a posted action), so a regression to fetching it early would make staging a silent no-op;
/// this test fails then.
/// </summary>
[Collection(SpellContractSweepCollection.Name)]
public sealed class SweepSpellFocusTests
{
    [RealClientDbcFact]
    public void StageSpellFocus_ForAFocusSpell_PlacesAnObjectAtTheCaster()
    {
        SpellSweepCatalog catalog = SpellSweepCatalog.Shared;
        SpellInfo focusSpell = catalog.Spells.FirstOrDefault(s => s.RequiresSpellFocus is 1351 or 1352)
            ?? throw new InvalidOperationException("Spell.dbc has no firework spell with focus 1351 or 1352");
        using var rig = new SpellSweepRig(new SpellStore([.. catalog.Spells, SweepHelperSpells.SelfDummySpell()], [], []), RealClientDbcFactAttribute.DbcDirectory);
        Player caster = rig.AddPlayer(Class.Warrior, 60);
        try
        {
            GameObject? focus = rig.StageSpellFocus(focusSpell, caster);

            Assert.NotNull(focus);
            Assert.Equal(SpellSweepRig.FocusEntryBase + focusSpell.RequiresSpellFocus, focus.Entry);
            Assert.Contains(focus, rig.Objects.GameObjects);
            rig.Objects.Remove(focus);
        }
        finally
        {
            rig.RemovePlayer(caster);
        }
    }

    [RealClientDbcFact]
    public void StageSpellFocus_ForASpellWithoutFocus_ReturnsNull()
    {
        SpellSweepCatalog catalog = SpellSweepCatalog.Shared;
        SpellInfo plain = catalog.Spells.First(s => s.RequiresSpellFocus == 0);
        using var rig = new SpellSweepRig(new SpellStore([.. catalog.Spells, SweepHelperSpells.SelfDummySpell()], [], []), RealClientDbcFactAttribute.DbcDirectory);
        Player caster = rig.AddPlayer(Class.Warrior, 60);
        try
        {
            Assert.Null(rig.StageSpellFocus(plain, caster));
        }
        finally
        {
            rig.RemovePlayer(caster);
        }
    }

    [RealClientDbcFact]
    public void StageSpellFocus_ForANonFireworkFocusSpell_ReturnsNull()
    {
        SpellSweepCatalog catalog = SpellSweepCatalog.Shared;
        SpellInfo other = catalog.Spells.First(s => s.RequiresSpellFocus != 0 && s.RequiresSpellFocus is not (1351 or 1352));
        using var rig = new SpellSweepRig(new SpellStore([.. catalog.Spells, SweepHelperSpells.SelfDummySpell()], [], []), RealClientDbcFactAttribute.DbcDirectory);
        Player caster = rig.AddPlayer(Class.Warrior, 60);
        try
        {
            Assert.Null(rig.StageSpellFocus(other, caster));
        }
        finally
        {
            rig.RemovePlayer(caster);
        }
    }
}
