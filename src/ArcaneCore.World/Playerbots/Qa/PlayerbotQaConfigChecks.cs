using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// Startup check of <c>World:Playerbots:Qa</c> (docs/areas/playbots-qa.md): a value that does not bind or is out of range is an error
/// (exit 78); publishing without enabling the reporter, publishing with the token variable unset, and a <c>Token</c> key (which is never
/// read) are warnings. Only the presence of the environment variable is looked at, never its value.
/// </summary>
public sealed class PlayerbotQaConfigChecks : IConfigCheck
{
    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        var options = new PlayerbotQaOptions();
        try
        {
            PlayerbotQaOptions.ApplyConfiguration(options, configuration);
        }
        catch (InvalidOperationException ex)
        {
            return [new ConfigIssue(ConfigSeverity.Error, PlayerbotQaOptions.SectionName, $"a value does not fit its key ({ex.Message}).",
                "use true/false and whole numbers")];
        }

        var issues = new List<ConfigIssue>();
        foreach (string problem in options.Validate())
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, PlayerbotQaOptions.SectionName, problem, "see docs/areas/playbots-qa.md for the ranges"));
        }

        const string github = PlayerbotQaOptions.SectionName + ":GitHub";
        PlayerbotQaGitHubOptions git = options.GitHub;
        if (git.Publish && !git.Enabled)
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Warning, github + ":Publish", "Publish is set but GitHub:Enabled is false, so nothing is reported.",
                "set GitHub:Enabled true, or remove Publish"));
        }

        if (git is { Enabled: true, Publish: true } && git.RequiresToken() && PlayerbotQaGitHubOptions.IsValidVariableName(git.TokenEnvironmentVariable)
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(git.TokenEnvironmentVariable)))
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Warning, github + ":TokenEnvironmentVariable",
                $"the environment variable {git.TokenEnvironmentVariable} is not set, so findings stay pending.",
                $"set {git.TokenEnvironmentVariable} to a fine-grained token with Issues read and write on {git.Repository}"));
        }

        if (configuration.GetSection(github).GetChildren().Any(child => string.Equals(child.Key, "Token", StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Warning, github + ":Token",
                "a Token key is ignored: the token is only ever read from an environment variable.",
                $"remove the key and set the environment variable {git.TokenEnvironmentVariable}"));
        }

        return issues;
    }
}
