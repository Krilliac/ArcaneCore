using ArcaneCore.Game.Spells;
using ArcaneCore.World.Tests.ClientData;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Spells.ContractSweep;

/// <summary>
/// The sweep world has the creature templates that Transform and Mounted auras name (their misc value is a creature entry, and
/// vmangos HandleAuraTransform / HandleAuraMounted read only its display id). Without them every such spell logged "creature not
/// found", which the live server with its world database does not.
/// </summary>
[Collection(SpellContractSweepCollection.Name)]
public sealed class SweepCreatureContentTests(ITestOutputHelper output)
{
    // 118 Polymorph (creature 16479), 10795 a mount (Ivory Raptor), 8324 Perm. Illusion HE Hunter (creature 6029: no row in the retail world
    // database, so the sweep fills it with a stand-in like every other entry a Transform aura names).
    private const uint Polymorph = 118;
    private const uint Mount = 10795;
    private const uint PermIllusion = 8324;

    [RealClientDbcFact]
    public void TransformAndMountSpells_FindTheirCreature_InTheSweepWorld()
    {
        SpellSweepCatalog catalog = SpellSweepCatalog.Shared;
        SpellInfo[] spells = [.. new[] { Polymorph, Mount, PermIllusion }.Select(id => catalog.Store.Get(id) ?? throw new InvalidOperationException($"Spell.dbc has no {id}"))];
        using var rig = new SpellSweepRig(new SpellStore([.. catalog.Spells, SweepHelperSpells.SelfDummySpell()], [], []), RealClientDbcFactAttribute.DbcDirectory);
        rig.TakeFaults();
        SweepResult result = new SpellSweepRunner(rig, catalog) { Contracts = "aj" }.Run(spells, spells.Length, full: false);
        SweepViolation[] displayLogs = [.. result.Violations.Where(v => v.Category == SweepCategory.ErrorLog
            && (v.Detail.Contains("TransformDisplayFeature", StringComparison.Ordinal) || v.Detail.Contains("MountDisplayFeature", StringComparison.Ordinal)))];
        output.WriteLine(string.Join('\n', displayLogs.Select(v => v.Key)));

        Assert.DoesNotContain(displayLogs, v => v.SpellId == Polymorph);
        Assert.DoesNotContain(displayLogs, v => v.SpellId == Mount);
        Assert.DoesNotContain(displayLogs, v => v.SpellId == PermIllusion);
    }
}
