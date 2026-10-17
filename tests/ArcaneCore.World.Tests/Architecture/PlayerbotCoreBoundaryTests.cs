using System.Text;
using Xunit;

namespace ArcaneCore.World.Tests.Architecture;

/// <summary>
/// Tier 2a boundary (docs/design/hot-reload-tiers.md, sections 4.1 and 4.4): core code (the world assembly outside
/// <c>ArcaneCore.World.Playerbots</c>) must not reference a Playerbots type, so the pack can later move into its own
/// collectible context. Core reaches the bots through contracts (<c>ArcaneCore.World.Contracts</c>) and core-owned types
/// (<c>ArcaneCore.World.Bots</c>).
/// <para>
/// The allowlist is explicit and may only shrink: a core type that starts referencing the pack fails, and so does an entry
/// that no longer references it (delete the entry). Each entry says what still has to move.
/// </para>
/// </summary>
public sealed class PlayerbotCoreBoundaryTests
{
    /// <summary>Core types (outermost full name) still allowed to reference the pack, and why. Shrink only.</summary>
    private static readonly SortedDictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        // Cluster/** belongs to another lane. Fix: read the running names from ManagedBotRegistry.RunningNames() instead of
        // ManagedPlayerbotFeature.Snapshot().
        ["ArcaneCore.World.Cluster.Tooling.ClusterCommands"] = "GetRunningBotNames reads ManagedPlayerbotFeature",
        // The options types (PlayerbotOptions and its nested and sibling option classes) still live in the pack. Config reload and
        // IOptions<T> are keyed by type, so they move to the contracts assembly as plain data (docs/design/hot-reload-tiers.md, 4.1).
        ["ArcaneCore.World.Reload.ConfigContentReloadable"] = "PlayerbotOptions (live config apply)",
        ["ArcaneCore.World.Reload.WorldConfigKeys"] = "the playerbot option types of the live config keys",
        ["ArcaneCore.World.Reload.WorldConfigView"] = "PlayerbotOptions (config view)",
    };

    private static string WorldAssemblyPath => typeof(WorldHost).Assembly.Location;

    [Fact]
    public void CoreReferencesNoPlayerbotTypeOutsideTheAllowlist()
    {
        (PackReferenceScanner scanner, SortedDictionary<string, SortedSet<string>> found) =
            PackReferenceScanner.Scan(WorldAssemblyPath, isPack => !isPack);
        Assert.True(scanner.TypesScanned > 1000, $"only {scanner.TypesScanned} core types were read");
        Assert.True(scanner.BodiesScanned > 10000, $"only {scanner.BodiesScanned} core method bodies were read");

        var report = new StringBuilder();
        foreach ((string core, SortedSet<string> packTypes) in found.Where(f => !Allowed.ContainsKey(f.Key)))
        {
            report.AppendLine($"{core} -> {string.Join(", ", packTypes)}");
        }

        foreach (string stale in Allowed.Keys.Where(k => !found.ContainsKey(k)))
        {
            report.AppendLine($"stale allowlist entry (no longer references the pack, delete it): {stale}");
        }

        Assert.True(report.Length == 0, "core types referencing Playerbots:" + Environment.NewLine + report);
    }

    /// <summary>
    /// The scanner sees references at all (a scan that stopped checking would pass the test above): with the party namespace
    /// as the "pack", the managed bot feature's reference to the party AI is found.
    /// </summary>
    [Fact]
    public void ScannerFindsKnownReferences()
    {
        (PackReferenceScanner scanner, SortedDictionary<string, SortedSet<string>> found) =
            PackReferenceScanner.Scan(WorldAssemblyPath, isPack => !isPack, packNamespace: PackReferenceScanner.PackNamespace + ".Party");
        Assert.True(scanner.TypesScanned > 1000);
        Assert.True(found.TryGetValue("ArcaneCore.World.Playerbots.ManagedPlayerbotFeature", out SortedSet<string>? refs),
            "the managed bot feature's references to the party namespace were not seen");
        Assert.Contains("ArcaneCore.World.Playerbots.Party.PlayerbotPartyAI", refs!);
    }
}
