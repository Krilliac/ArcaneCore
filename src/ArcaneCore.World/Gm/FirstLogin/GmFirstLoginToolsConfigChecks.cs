using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Gm.FirstLogin;

/// <summary>Validate the opt-in before host construction, listener binding, or schema writes.</summary>
public sealed class GmFirstLoginToolsConfigChecks : IConfigCheck
{
    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        try
        {
            GmFirstLoginToolsOptions.Bind(configuration).Validate();
            return [];
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or FormatException)
        {
            return [new ConfigIssue(ConfigSeverity.Error, GmFirstLoginToolsOptions.SectionName,
                "Invalid GM first-login tools policy.",
                "Use Enabled true/false, a defined staff MinimumSecurity, and unique reviewed spell IDs (maximum 64 per list).")];
        }
    }
}
