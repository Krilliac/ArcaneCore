using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.Kernel.Configuration.Conf;

/// <summary>
/// Checks the keys of the daemon's .conf file against the catalog: an unknown key is a warning naming the file, the line and
/// the closest known key (a typo that would otherwise silently do nothing); a key of the other daemon is a warning naming its file;
/// a value of the wrong type is an error with file:line; an unquoted value containing " #" is a warning (there are no inline comments).
/// Only keys that come from the .conf file are checked: appsettings.json and environment variables are not listed. With
/// <c>Startup:Strict</c> the warnings fail start-up like errors.
/// </summary>
public sealed class ConfKeyCheck(ConfCatalog catalog, ConfigDaemon daemon) : IConfigCheck
{
    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        if (configuration is not IConfigurationRoot root)
        {
            yield break;
        }

        string daemonName = ConfCatalog.DaemonName(daemon);
        string otherFile = ConfCatalog.FileName(daemon == ConfigDaemon.Realm ? ConfigDaemon.World : ConfigDaemon.Realm);
        foreach (ConfConfigurationProvider provider in root.Providers.OfType<ConfConfigurationProvider>())
        {
            ConfDocument? document = provider.Document;
            if (document is null)
            {
                continue;
            }

            foreach (ConfEntry entry in document.Entries)
            {
                string where = $"{document.FileName}:{entry.Line}";
                string display = ConfCatalog.Display(entry.Path);
                ConfKeyClass classification = catalog.Classify(entry.Path, daemon);
                switch (classification.Status)
                {
                    case ConfKeyStatus.Unknown:
                        string? suggestion = catalog.Suggest(entry.Path, daemon);
                        yield return new ConfigIssue(
                            ConfigSeverity.Warning,
                            entry.Path,
                            $"({where}) not a key the {daemonName} daemon reads. "
                                + (suggestion is null ? "No close match." : $"Did you mean {ConfCatalog.Display(suggestion)}?"),
                            suggestion is null ? $"remove the line or check the key against docs/reference/configuration.md" : $"correct the spelling to {ConfCatalog.Display(suggestion)} or remove the line");
                        break;
                    case ConfKeyStatus.OtherDaemon:
                        yield return new ConfigIssue(
                            ConfigSeverity.Warning,
                            entry.Path,
                            $"({where}) {display} is read by the other daemon, not the {daemonName} daemon.",
                            $"move the line to {otherFile}");
                        break;
                    case ConfKeyStatus.Section:
                        yield return new ConfigIssue(
                            ConfigSeverity.Warning,
                            entry.Path,
                            $"({where}) {display} is a section, not a key; nothing is read from it.",
                            $"write the full key of the setting, for example {ConfCatalog.Display(catalog.KeysFor(daemon).First(k => k.Path.StartsWith(entry.Path + ":", StringComparison.OrdinalIgnoreCase)).Path)}");
                        break;
                    case ConfKeyStatus.Known or ConfKeyStatus.ChildOfCollection when classification.Key is { } key:
                        string? problem = ConfValue.Validate(entry.Value, key.IsCollection ? key.ElementType : key.Type, key.EnumValues);
                        if (problem is not null)
                        {
                            yield return new ConfigIssue(ConfigSeverity.Error, entry.Path, $"({where}) {problem}", $"correct the value of {display} (allowed: {key.Allowed})");
                        }

                        break;
                }

                if (!entry.Quoted && entry.Value.Contains(" #", StringComparison.Ordinal))
                {
                    yield return new ConfigIssue(
                        ConfigSeverity.Warning,
                        entry.Path,
                        $"({where}) the value contains ' #', which is part of the value: there are no trailing comments in a .conf file.",
                        "move the comment to its own line, or put the value in double quotes if the '#' belongs to it");
                }
            }
        }
    }
}
