using System.Globalization;
using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Economy;

/// <summary>
/// The list <c>Economy:AuctionHouses</c>. Its entries are positional records, so the binder needs all three fields, and <c>EconomyFeature</c>
/// binds without <c>ErrorOnUnknownConfiguration</c>: an entry with a field missing or unreadable is dropped with no message, and the house quietly
/// keeps its default percentages (proved by <c>AuctionHouseConfigTests</c>). The raw keys are read here so that such an entry is an error instead
/// (exit 78). Checked by <c>check-config</c> and at every start.
/// </summary>
public sealed class EconomyConfigChecks : IConfigCheck
{
    private const string Key = EconomyFeature.SectionName + ":AuctionHouses";

    private static readonly string[] Fields = ["Id", "DepositPercent", "CutPercent"];

    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var issues = new List<ConfigIssue>();
        foreach (IConfigurationSection entry in configuration.GetSection(Key).GetChildren())
        {
            foreach (string field in Fields)
            {
                string key = $"{Key}:{entry.Key}:{field}";
                string fix = $"set {Key}:{entry.Key}:Id, DepositPercent and CutPercent to whole numbers, or remove the entry";
                string? text = entry[field];
                if (string.IsNullOrWhiteSpace(text))
                {
                    issues.Add(new ConfigIssue(ConfigSeverity.Error, key, $"entry {entry.Key} has no {field}, so the whole entry would be dropped and the house would keep its default percentages.", fix));
                }
                else if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    issues.Add(new ConfigIssue(ConfigSeverity.Error, key, $"'{text}' is not a whole number, so the whole entry would be dropped and the house would keep its default percentages.", fix));
                }
            }
        }

        return issues;
    }
}
