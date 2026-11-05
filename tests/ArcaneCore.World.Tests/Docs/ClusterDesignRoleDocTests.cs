using System.Text.RegularExpressions;
using ArcaneCore.World.Cluster.Social;
using Xunit;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// The role list that <c>docs/CLUSTERING_DESIGN.md</c> gives for a World process ("a World process hosts `A` and `B` only") must equal the
/// <see cref="ClusterNodeRole"/> values that <see cref="ClusterNodeOptions.Validate"/> accepts, so a refused role (Social, Economy, Gateway)
/// cannot be documented as hosted again.
/// </summary>
public sealed class ClusterDesignRoleDocTests
{
    private const string DocPath = "docs/CLUSTERING_DESIGN.md";

    private static readonly Regex HostsSentence = new(@"a World process hosts ([^.]*?) only", RegexOptions.CultureInvariant);
    private static readonly Regex CodeSpan = new(@"`([^`]+)`", RegexOptions.CultureInvariant);

    /// <summary>The role names in the doc's sentence, sorted; a missing sentence or an empty list throws.</summary>
    internal static string[] ParseDocRoles(string doc)
    {
        Match sentence = HostsSentence.Match(doc.Replace("\r\n", "\n").Replace('\n', ' '));
        Assert.True(sentence.Success, $"{DocPath} no longer has the sentence 'a World process hosts `...` only'.");
        string[] roles = CodeSpan.Matches(sentence.Groups[1].Value).Select(m => m.Groups[1].Value).OrderBy(r => r, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(roles);
        return roles;
    }

    /// <summary>The roles whose options pass Validate on a World process with the map worker enabled, sorted.</summary>
    internal static string[] AcceptedRoles()
        => Enum.GetValues<ClusterNodeRole>()
            .Where(role => !new ClusterNodeOptions { Role = role }.Validate(workerEnabled: true).Any())
            .Select(role => role.ToString())
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void DocRoleListEqualsTheRolesClusterNodeOptionsAccepts()
    {
        Assert.Equal(AcceptedRoles(), ParseDocRoles(RepoRoot.ReadText(DocPath)));
    }

    [Fact]
    public void DocRoleListIsRejectedWhenARefusedRoleIsAddedBack()
    {
        string doc = RepoRoot.ReadText(DocPath);
        string drifted = HostsSentence.Replace(doc.Replace("\r\n", "\n"), m => m.Value.Replace(" only", ", `Social` only", StringComparison.Ordinal), 1);
        Assert.NotEqual(AcceptedRoles(), ParseDocRoles(drifted));
    }

    [Fact]
    public void EveryRefusedRoleIsOutsideTheAcceptedList()
    {
        Assert.DoesNotContain(nameof(ClusterNodeRole.Social), AcceptedRoles());
        Assert.DoesNotContain(nameof(ClusterNodeRole.Economy), AcceptedRoles());
        Assert.DoesNotContain(nameof(ClusterNodeRole.Gateway), AcceptedRoles());
    }
}
