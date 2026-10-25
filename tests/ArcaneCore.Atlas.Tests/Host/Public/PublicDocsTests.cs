using System.Reflection;
using System.Text.RegularExpressions;
using ArcaneCore.Atlas.Host.Public;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host.Public;

/// <summary>
/// docs/areas/atlas-public.md names every setting of the public listener. A page that tells an operator to set a key
/// <see cref="PublicOptions"/> does not bind is the worst kind of drift (the change silently does nothing), and a setting the page
/// omits cannot be configured on purpose. The World docs audit skips this page because its keys are Atlas's, so this test is the check.
/// </summary>
public sealed partial class PublicDocsTests
{
    /// <summary>Properties that are not operator settings (none today); each needs a reason here.</summary>
    private static readonly HashSet<string> NotSettings = new(StringComparer.Ordinal);

    [GeneratedRegex(@"^\|\s*(?<keys>`[^|]+?`)\s*\|", RegexOptions.Multiline)]
    private static partial Regex Row();

    private static string DocText()
    {
        for (string? dir = AppContext.BaseDirectory; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            string candidate = Path.Combine(dir, "docs", "areas", "atlas-public.md");
            if (File.Exists(candidate) && File.Exists(Path.Combine(dir, "ArcaneCore.slnx")))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException("docs/areas/atlas-public.md was not found above the test location; a docs check must fail, never skip");
    }

    private static string ConfigurationTable()
    {
        string text = DocText();
        int start = text.IndexOf("## Configuration", StringComparison.Ordinal);
        int end = text.IndexOf("## TLS", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "the Configuration section was not found");
        return text[start..end];
    }

    private static IEnumerable<string> Leaves(Type type, string prefix)
    {
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || NotSettings.Contains(prefix + property.Name))
            {
                continue;
            }

            Type t = property.PropertyType;
            if (t.IsClass && t != typeof(string) && !t.IsGenericType)
            {
                foreach (string leaf in Leaves(t, prefix + property.Name + ":"))
                {
                    yield return leaf;
                }
            }
            else
            {
                yield return prefix + property.Name;
            }
        }
    }

    [Fact]
    public void EveryKeyTheConfigurationTableNames_IsBoundByPublicOptions()
    {
        HashSet<string> real = new(Leaves(typeof(PublicOptions), string.Empty), StringComparer.OrdinalIgnoreCase);
        // A "Group:A / B" row shares its prefix between the two names.
        var documented = new List<string>();
        foreach (Match row in Row().Matches(ConfigurationTable()))
        {
            string prefix = string.Empty;
            foreach (string part in row.Groups["keys"].Value.Split('/'))
            {
                string key = part.Trim().Trim('`');
                int colon = key.LastIndexOf(':');
                if (colon >= 0)
                {
                    prefix = key[..(colon + 1)];
                }
                else
                {
                    key = prefix + key;
                }

                documented.Add(key);
            }
        }

        Assert.NotEmpty(documented);
        Assert.Empty(documented.Where(k => !real.Contains(k)).Distinct());
    }

    [Fact]
    public void EverySettingOfPublicOptions_IsInTheConfigurationTable()
    {
        string table = ConfigurationTable();

        string[] missing = [.. Leaves(typeof(PublicOptions), string.Empty).Where(k => !table.Contains("`" + k + "`", StringComparison.OrdinalIgnoreCase) && !MentionedInGroup(table, k))];

        Assert.Empty(missing);
    }

    /// <summary>A row such as <c>`Guest:MaxSessions` / `MaxSessionsPerIp`</c> names its second key without the group prefix.</summary>
    private static bool MentionedInGroup(string table, string key)
    {
        int colon = key.LastIndexOf(':');
        string leaf = colon < 0 ? key : key[(colon + 1)..];
        string prefix = colon < 0 ? string.Empty : key[..(colon + 1)];
        return table.Split('\n').Any(line => line.Contains("`" + leaf + "`", StringComparison.OrdinalIgnoreCase)
            && (prefix.Length == 0 || line.Contains("`" + prefix, StringComparison.OrdinalIgnoreCase)));
    }
}
