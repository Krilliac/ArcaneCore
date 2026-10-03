using System.Text.RegularExpressions;
using Xunit;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// A configuration key named in backticks in any hand-written page must exist in the catalog (as a key, or as a section that
/// has keys below it). A page that tells an operator to set a key the code does not read is the worst kind of drift, because the
/// operator's change silently does nothing. Keys that are designed but deliberately not delivered are listed below with the
/// reason, so the list shrinks as they are built.
/// </summary>
public sealed partial class DocKeyAuditTests
{
    /// <summary>Documented but not delivered (or a placeholder in prose), with the reason. Each must still be mentioned somewhere, or it is dead weight.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["AutoShot:FireWhileMoving"] = "docs/areas/hunter.md states that this option does not exist (retail behaviour only)",
        ["FeignDeath:PlayerCanBeResisted"] = "docs/areas/hunter.md states that this option does not exist",
        ["Traps:PvpOwnerRule"] = "docs/areas/hunter.md states that this option does not exist",
        ["Traps:OneActivePerSlot"] = "docs/areas/hunter.md states that this option does not exist",
        ["Combo:ClearOnSelectionChange"] = "docs/areas/rogue.md: a possible option whose choice is pending; not delivered",
        ["Cluster:Fencing:Enabled"] = "docs/integration/cluster-m15-0.md: a designed switch of the clustering work, not delivered",
        ["Bans:RequireNotHigherSecurityTarget"] = "docs/security/live-bans.md: designed and not delivered (the retail ban commands have no hierarchy check)",
        ["Conditions:Numbering"] = "docs/integration/npc-quest-fidelity.md: a dataset numbering option that is not implemented",
        ["Database:Upgrade:PostgresAtomic"] = "docs/integration/db-upgrade-tooling.md: a designed switch that was not built",
    };

    [GeneratedRegex(@"^[A-Z][A-Za-z0-9]*(?::[A-Za-z][A-Za-z0-9]*)+$")]
    private static partial Regex KeyShape();

    internal static bool Resolves(string token, IEnumerable<string> catalogPaths)
    {
        string head = token.Split(':')[0];
        if (ConfigExceptionTable.FrameworkSections.Contains(head, StringComparer.Ordinal))
        {
            return true;
        }

        foreach (string path in catalogPaths)
        {
            if (path == token || path.StartsWith(token + ":", StringComparison.Ordinal) || token.StartsWith(path + ":", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void Checker_AcceptsKeysAndSections_AndRejectsUnknownOnes()
    {
        string[] paths = ["World:Maps:GridUnload", "Bans:RealmId", "World:GmCommands:SecurityMap"];
        Assert.True(Resolves("World:Maps:GridUnload", paths));
        Assert.True(Resolves("World:Maps", paths));
        Assert.True(Resolves("World:GmCommands:SecurityMap:Player", paths));
        Assert.True(Resolves("Logging:LogLevel", paths));
        Assert.False(Resolves("World:Maps:GridUnlaod", paths));
        Assert.False(Resolves("Bans:Typo", paths));
        Assert.Matches(KeyShape(), "World:Maps:GridUnload");
        Assert.DoesNotMatch(KeyShape(), "World:");
    }

    [Fact]
    public void EveryConfigurationKeyNamedInTheDocs_ExistsInTheCatalog()
    {
        string root = RepoRoot.Find();
        string[] catalog = [.. ConfigProduction.Catalog.Entries.Select(e => e.Path), .. ConfigExceptionTable.AdHocKeys.Select(a => a.Path)];
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        var seenAllowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in MarkdownDocs.Files(root))
        {
            if (file.StartsWith("docs/reference/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach ((int line, string token) in MarkdownDocs.CodeSpans(File.ReadAllText(Path.Combine(root, file))))
            {
                string key = token.Trim().TrimEnd(':', '.', ',');
                if (!KeyShape().IsMatch(key))
                {
                    continue;
                }

                if (Allowed.ContainsKey(key))
                {
                    seenAllowed.Add(key);
                    continue;
                }

                if (!Resolves(key, catalog))
                {
                    unresolved.Add($"{key}   ({file}:{line})");
                }
            }
        }

        Assert.True(unresolved.Count == 0, "configuration keys named in docs that no options class reads (fix the doc, or add an allow-list reason):\n" + string.Join("\n", unresolved));
        Assert.Empty(Allowed.Keys.Except(seenAllowed));
    }
}
