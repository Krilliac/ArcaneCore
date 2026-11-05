using ArcaneCore.Game.Spells;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// The spell contract baseline triage (2026-11): every Dummy spell the sweep used to baseline as unhandled is either listed with a reference
/// citation (<see cref="DeliberatelyInertSpells"/> or <see cref="QaSpellConsumers"/>) or kept in the baseline on purpose. These tests pin which,
/// so a spell cannot drift from one to the other without the baseline file changing with it.
/// </summary>
public sealed class PlayerbotQaSpellBaselineTriageTests
{
    private static string[] BaselineLines()
        => File.ReadAllLines(Path.Combine(RepositorySource.RequireRoot(), "tests", "ArcaneCore.World.Tests", "Spells", "ContractSweep", "spell-contract-baseline.txt"));

    private static bool InBaseline(uint spellId)
        => BaselineLines().Any(l => l.StartsWith("a.unhandled", StringComparison.Ordinal) && l.Split('\t') is { Length: > 1 } parts && parts[1] == spellId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void SpawnAura19502_AndMarker5302_AreInert_WithAReferenceCitation()
    {
        Assert.True(DeliberatelyInertSpells.TryGet(19502, out InertSpell? sickly));
        Assert.Equal(InertSpellReason.SpawnAura, sickly.Reason);
        Assert.Contains("12296", sickly.Citation, StringComparison.Ordinal);
        Assert.Contains("npcs_special.cpp", sickly.Citation, StringComparison.Ordinal);

        Assert.True(DeliberatelyInertSpells.TryGet(5302, out InertSpell? defensive));
        Assert.Equal(InertSpellReason.Marker, defensive.Reason);
        Assert.Matches("vmangos.*Unit.cpp", defensive.Citation);
    }

    [Theory]
    [InlineData(16378u, "UsePath")]
    [InlineData(21914u, "UsePath")]
    [InlineData(21917u, "UsePath")]
    [InlineData(84u, "UsePath")]
    [InlineData(23852u, "UsePath")]
    [InlineData(24746u, "AuraReader")]
    [InlineData(18800u, "UsePath")]
    [InlineData(22690u, "UsePath")]
    public void TheTriagedSpells_AreConsumed_AndNotAlsoInert(uint spellId, string kind)
    {
        Assert.True(QaSpellConsumers.TryGet(spellId, out QaSpellConsumer? consumer));
        Assert.Equal(kind, consumer.Kind.ToString());
        Assert.False(DeliberatelyInertSpells.TryGet(spellId, out _));
    }

    [Theory]
    [InlineData(16378u)]
    [InlineData(21914u)]
    [InlineData(21917u)]
    [InlineData(84u)]
    [InlineData(23852u)]
    [InlineData(24746u)]
    [InlineData(18800u)]
    [InlineData(22690u)]
    [InlineData(19502u)]
    [InlineData(5302u)]
    public void TheTriagedSpells_HaveNoLeftoverBaselineLine(uint spellId) => Assert.False(InBaseline(spellId));

    /// <summary>Decision N-unsettled (2026-11-05): only azerothcore or TrinityCore evidence exists, so these three stay unlisted and baselined.</summary>
    [Theory]
    [InlineData(17619u)]
    [InlineData(30012u)]
    [InlineData(6277u)]
    public void TheUnsettledSpells_StayBaselined_AndUnlisted(uint spellId)
    {
        Assert.False(DeliberatelyInertSpells.TryGet(spellId, out _));
        Assert.False(QaSpellConsumers.TryGet(spellId, out _));
        Assert.True(InBaseline(spellId));
    }
}
