using ArcaneCore.Game;
using Xunit;

namespace ArcaneCore.World.Tests.Architecture;

/// <summary>
/// The feature-namespace dependency freeze of <c>ArcaneCore.Game</c> (plan Wave 0b, finding #9). The edges are read from the compiled
/// assembly's metadata and IL (<see cref="PackReferenceScanner.ScanSegmentEdges"/>), not from <c>using</c> lines, so a
/// <c>global::</c>-qualified or fully qualified reference counts like any other. A namespace is a feature namespace
/// <c>ArcaneCore.Game.&lt;Top&gt;</c>; deeper namespaces collapse into their first segment.
/// <para>
/// Spells is the hub of the cycles: it references most features and most features reference it. The partners below are the CURRENT
/// set and may only shrink. A new cycle through Spells fails, and so does a listed pair that no longer exists (delete the entry, so
/// the freeze tightens).
/// </para>
/// </summary>
public sealed class GameFeatureCycleFreezeTests
{
    private const string Root = "ArcaneCore.Game";

    private const string Hub = "Spells";

    /// <summary>The features that reference Spells while Spells references them (both directions present today).</summary>
    private static readonly SortedSet<string> AllowedSpellsCycles = new(StringComparer.Ordinal)
    {
        "Combat",
        "Crafting",
        "Creatures",
        "Death",
        "Entities",
        "GameObjects",
        "Honor",
        "Instances",
        "Items",
        "Locomotion",
        "Maps",
        "Pets",
        "Progression",
        "Ranged",
        "Reputation",
        "Skills",
        "Stats",
        "Totems",
    };

    private static string GamePath => typeof(ObjectGuid).Assembly.Location;

    [Fact]
    public void SpellsCycles_AreExactlyTheFrozenAllowlist()
    {
        (PackReferenceScanner scanner, SortedDictionary<string, SortedSet<string>> edges) = PackReferenceScanner.ScanSegmentEdges(GamePath, Root);
        Assert.True(scanner.TypesScanned > 2000, $"only {scanner.TypesScanned} Game types were read");
        Assert.True(scanner.BodiesScanned > 10000, $"only {scanner.BodiesScanned} Game method bodies were read");

        SortedSet<string> current = [.. CyclicPairs(edges).Where(p => p.A == Hub || p.B == Hub).Select(p => p.A == Hub ? p.B : p.A)];
        string[] added = [.. current.Except(AllowedSpellsCycles)];
        string[] stale = [.. AllowedSpellsCycles.Except(current)];

        Assert.True(added.Length == 0,
            $"New dependency cycle(s) between ArcaneCore.Game.{Hub} and: {string.Join(", ", added)}. Spells and these features now reference each other. "
            + "Do not add them to AllowedSpellsCycles: break the cycle instead (move the shared type to a lower namespace, or reach the other side through an interface, "
            + "a delegate or an event). The allowlist may only shrink.");
        Assert.True(stale.Length == 0,
            $"AllowedSpellsCycles lists cycle(s) that no longer exist: {string.Join(", ", stale)}. Delete the entries, so the freeze tightens.");
    }

    /// <summary>
    /// The detector sees edges at all (a scan that found nothing would pass the freeze above): the Spells to Combat and Combat to
    /// Spells references are real, and so is every edge a listed cycle needs, in both directions.
    /// </summary>
    [Fact]
    public void Detector_FindsKnownEdgesInBothDirections()
    {
        (_, SortedDictionary<string, SortedSet<string>> edges) = PackReferenceScanner.ScanSegmentEdges(GamePath, Root);
        Assert.Contains("Combat", edges[Hub]);
        Assert.Contains(Hub, edges["Combat"]);
        foreach (string partner in AllowedSpellsCycles)
        {
            Assert.True(edges[Hub].Contains(partner) && edges[partner].Contains(Hub), $"Spells <-> {partner} is not seen in both directions");
        }
    }

    /// <summary>Segments collapse (no dotted names, no root-level types as a segment) and a feature never has an edge to itself.</summary>
    [Fact]
    public void Edges_AreKeyedByTheFirstSegmentOnly()
    {
        (_, SortedDictionary<string, SortedSet<string>> edges) = PackReferenceScanner.ScanSegmentEdges(GamePath, Root);
        Assert.All(edges, e =>
        {
            Assert.DoesNotContain('.', e.Key);
            Assert.DoesNotContain(e.Key, e.Value);
            Assert.All(e.Value, to => Assert.DoesNotContain('.', to));
        });
    }

    [Fact]
    public void CyclicPairs_ReportsOnlyMutualEdges_EachPairOnce()
    {
        var edges = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal)
        {
            ["A"] = new(StringComparer.Ordinal) { "B", "C" },
            ["B"] = new(StringComparer.Ordinal) { "A" },
            ["C"] = new(StringComparer.Ordinal) { "D" },
        };

        Assert.Equal([("A", "B")], CyclicPairs(edges));
        Assert.Empty(CyclicPairs(new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal)));
    }

    private static List<(string A, string B)> CyclicPairs(SortedDictionary<string, SortedSet<string>> edges)
    {
        var pairs = new List<(string, string)>();
        foreach ((string a, SortedSet<string> targets) in edges)
        {
            foreach (string b in targets)
            {
                if (string.CompareOrdinal(a, b) < 0 && edges.TryGetValue(b, out SortedSet<string>? back) && back.Contains(a))
                {
                    pairs.Add((a, b));
                }
            }
        }

        return pairs;
    }
}
