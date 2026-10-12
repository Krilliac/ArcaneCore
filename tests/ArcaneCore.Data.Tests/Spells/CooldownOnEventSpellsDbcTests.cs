using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Tests.Skills;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Data.Tests.Spells;

/// <summary>
/// Which build-5875 spells carry Attributes 0x02000000 (SPELL_ATTR_COOLDOWN_ON_EVENT): the cooldown of each starts when its aura ends and the
/// client is told with SMSG_COOLDOWN_EVENT (SpellSystem.StartCooldownOnAuraEnd). The listing is written to the test output.
/// </summary>
public sealed class CooldownOnEventSpellsDbcTests(ITestOutputHelper output)
{
    private const uint CooldownOnEvent = 0x02000000;

    private static List<SpellTemplateRow> Flagged()
    {
        string dir = Environment.GetEnvironmentVariable(RealDbcFactAttribute.Variable)!;
        DbcFile spells = DbcFile.Load(Path.Combine(dir, "Spell.dbc"));
        var rows = new List<SpellTemplateRow>();
        for (int r = 0; r < spells.RecordCount; r++)
        {
            SpellTemplateRow row = SpellDbcImporter.ReadSpell(spells, r);
            if ((row.Attributes & CooldownOnEvent) != 0)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    [RealDbcFact]
    public void TheStealthFamily_CarriesTheAttribute_WithItsTenSecondCooldown()
    {
        List<SpellTemplateRow> flagged = Flagged();
        foreach (SpellTemplateRow row in flagged.OrderBy(s => s.Id))
        {
            output.WriteLine($"{row.Id} {row.SpellName} {row.Rank} recovery={row.RecoveryTime} category={row.Category} categoryRecovery={row.CategoryRecoveryTime}");
        }

        output.WriteLine($"count={flagged.Count}");
        foreach (uint id in new uint[] { 1784, 1785, 1786, 1787, 20580, 5215, 6783, 9913 })
        {
            SpellTemplateRow row = Assert.Single(flagged, s => s.Id == id);
            Assert.True(row.RecoveryTime == 10_000 || row.CategoryRecoveryTime == 10_000, $"{id} {row.SpellName}");
        }
    }
}
