using ArcaneCore.Game.Economy.AuctionBot;
using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Economy;

/// <summary>
/// The <c>AuctionHouseBot</c> section: a value that does not bind is an error; an out-of-range or inverted value is a warning naming
/// the value the bot uses instead (cMaNGOS GetMinMaxConfig falls back to the default the same way). Checked by <c>check-config</c> and
/// at every start.
/// </summary>
public sealed class AuctionBotConfigChecks : IConfigCheck
{
    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection section = configuration.GetSection(AuctionBotOptions.SectionName);
        var options = new AuctionBotOptions();
        try
        {
            section.Bind(options);
        }
        catch (InvalidOperationException ex)
        {
            return [new ConfigIssue(ConfigSeverity.Error, AuctionBotOptions.SectionName, ex.Message, "use whole numbers, true/false and comma-separated value lists")];
        }

        var issues = new List<ConfigIssue>();
        foreach (string fix in options.Normalize())
        {
            string key = fix[..fix.IndexOf(' ', StringComparison.Ordinal)];
            issues.Add(new ConfigIssue(ConfigSeverity.Warning, key, fix, "set a value in the documented range"));
        }

        foreach ((string name, string? text) in new[]
        {
            (nameof(AuctionBotOptions.ValuePoor), section[nameof(AuctionBotOptions.ValuePoor)]),
            (nameof(AuctionBotOptions.ValueNormal), section[nameof(AuctionBotOptions.ValueNormal)]),
            (nameof(AuctionBotOptions.ValueUncommon), section[nameof(AuctionBotOptions.ValueUncommon)]),
            (nameof(AuctionBotOptions.ValueRare), section[nameof(AuctionBotOptions.ValueRare)]),
            (nameof(AuctionBotOptions.ValueEpic), section[nameof(AuctionBotOptions.ValueEpic)]),
            (nameof(AuctionBotOptions.ValueLegendary), section[nameof(AuctionBotOptions.ValueLegendary)]),
            (nameof(AuctionBotOptions.ValueArtifact), section[nameof(AuctionBotOptions.ValueArtifact)]),
        })
        {
            if (text is null)
            {
                continue;
            }

            string[] parts = text.Split(',');
            if (parts.Length != AuctionBotOptions.ItemClassCount || parts.Any(p => !uint.TryParse(p.Trim(), out _)))
            {
                issues.Add(new ConfigIssue(ConfigSeverity.Warning, $"{AuctionBotOptions.SectionName}:{name}",
                    $"'{text}' is not {AuctionBotOptions.ItemClassCount} comma-separated whole percents; missing or unreadable values count as 0.",
                    "give one percent per item class 0..16"));
            }
        }

        return issues;
    }
}
