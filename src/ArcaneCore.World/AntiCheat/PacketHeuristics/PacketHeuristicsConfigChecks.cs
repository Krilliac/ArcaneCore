using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.AntiCheat.PacketHeuristics;

/// <summary>
/// Startup check of the <c>World:PacketHeuristics</c> section (docs/areas/packet-heuristics.md), after <see cref="AntiCheatConfigChecks"/>: a value
/// that does not bind (a word where a number belongs) or that <see cref="PacketHeuristicsOptions.Validate"/> refuses (a ceiling out of range, a
/// replay exclusion that is not an opcode name) is an error (exit 78), so the daemon never starts with checks that silently fell back to
/// something else.
/// </summary>
public sealed class PacketHeuristicsConfigChecks : IConfigCheck
{
    /// <inheritdoc/>
    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        var options = new PacketHeuristicsOptions();
        try
        {
            configuration.GetSection(PacketHeuristicsOptions.SectionName).Bind(options);
        }
        catch (InvalidOperationException ex)
        {
            return [new ConfigIssue(ConfigSeverity.Error, PacketHeuristicsOptions.SectionName, $"a value does not fit its key ({ex.Message}).",
                "use true/false and whole numbers; ReplayExclude is a list of opcode names")];
        }

        var issues = new List<ConfigIssue>();
        foreach (string problem in options.Validate())
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, PacketHeuristicsOptions.SectionName, problem, "see docs/areas/packet-heuristics.md for the ranges"));
        }

        return issues;
    }
}
