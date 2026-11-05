using System.ComponentModel;
using ArcaneCore.Game.Economy;
using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Economy;

/// <summary>
/// The list <c>Economy:AuctionHouses</c>. Its entries are positional records, so the binder needs all three fields, and <c>EconomyFeature</c>
/// binds without <c>ErrorOnUnknownConfiguration</c>: an entry with a field missing or unreadable is dropped with no message, and the house quietly
/// keeps its default percentages (proved by <c>AuctionHouseConfigTests</c>). The raw keys are read here so that such an entry is an error instead
/// (exit 78). An entry that binds but cannot act (an id the rules never ask for, a percentage over 100) is a warning. Checked by <c>check-config</c>
/// and at every start.
/// </summary>
public sealed class EconomyConfigChecks : IConfigCheck
{
    /// <summary>A percentage above this is a typo: a cut over 100 would pay the seller a negative amount.</summary>
    private const uint MaxPercent = 100;

    private const string Key = EconomyFeature.SectionName + ":AuctionHouses";

    private static readonly string[] Fields = ["Id", "DepositPercent", "CutPercent"];

    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var issues = new List<ConfigIssue>();
        // The section name is spelled out (not the Key constant) so the source scan of the configuration-catalog tests reads the key by name.
        foreach (IConfigurationSection entry in configuration.GetSection(EconomyFeature.SectionName + ":AuctionHouses").GetChildren())
        {
            int errorsBefore = issues.Count;
            var values = new Dictionary<string, uint>(Fields.Length);
            foreach (string field in Fields)
            {
                string key = $"{Key}:{entry.Key}:{field}";
                string fix = $"set {Key}:{entry.Key}:Id, DepositPercent and CutPercent to whole numbers, or remove the entry";
                string? text = entry[field];
                if (string.IsNullOrWhiteSpace(text))
                {
                    issues.Add(new ConfigIssue(ConfigSeverity.Error, key, $"entry {entry.Key} has no {field}, so the whole entry would be dropped and the house would keep its default percentages.", fix));
                }
                else if (!BinderReads(text, out uint number))
                {
                    issues.Add(new ConfigIssue(ConfigSeverity.Error, key, $"'{text}' is not a whole number the configuration binder reads, so the whole entry would be dropped and the house would keep its default percentages.", fix));
                }
                else
                {
                    values[field] = number;
                }
            }

            if (issues.Count != errorsBefore)
            {
                continue; // dropped by the binder: nothing more to say about an entry that does not exist
            }

            if (values["Id"] is not (AuctionHouseRules.AllianceHouse or AuctionHouseRules.HordeHouse or AuctionHouseRules.NeutralHouse))
            {
                issues.Add(new ConfigIssue(ConfigSeverity.Warning, $"{Key}:{entry.Key}:Id",
                    $"entry {entry.Key} names house {values["Id"]}, which has no effect: auctioneers only use house {AuctionHouseRules.AllianceHouse} (Alliance), {AuctionHouseRules.HordeHouse} (Horde) and {AuctionHouseRules.NeutralHouse} (neutral).",
                    $"set {Key}:{entry.Key}:Id to {AuctionHouseRules.AllianceHouse}, {AuctionHouseRules.HordeHouse} or {AuctionHouseRules.NeutralHouse}, or remove the entry"));
            }

            foreach (string field in (string[])["DepositPercent", "CutPercent"])
            {
                if (values[field] > MaxPercent)
                {
                    issues.Add(new ConfigIssue(ConfigSeverity.Warning, $"{Key}:{entry.Key}:{field}",
                        $"entry {entry.Key} sets {field} to {values[field]}, which is over {MaxPercent}: a percentage of the price.",
                        $"set {Key}:{entry.Key}:{field} to a percentage from 0 to {MaxPercent}"));
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// Whether the configuration binder reads <paramref name="text"/> as an unsigned 32-bit number: the very converter it uses
    /// (<c>TypeDescriptor</c>'s <c>UInt32Converter</c>), so a sign, spaces or a hex prefix are judged as the binder judges them.
    /// </summary>
    private static bool BinderReads(string text, out uint number)
    {
        try
        {
            if (TypeDescriptor.GetConverter(typeof(uint)).ConvertFromInvariantString(text) is uint read)
            {
                number = read;
                return true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            number = 0; // BaseNumberConverter wraps every parse failure in a plain Exception; any failure means the binder drops the entry
            return false;
        }

        number = 0;
        return false;
    }
}
