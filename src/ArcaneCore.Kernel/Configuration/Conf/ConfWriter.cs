using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace ArcaneCore.Kernel.Configuration.Conf;

/// <summary>
/// Renders the .conf files from the catalog: <see cref="WriteDefaults"/> is the shipped <c>.conf.dist</c> (and the output of
/// <c>write-config --defaults</c>); <see cref="WriteCurrent"/> is the effective configuration of a running profile. The
/// output uses LF line endings and is byte-stable for the same input. Pure functions, no I/O.
/// </summary>
public static class ConfWriter
{
    private const int Width = 100;
    private const string RegenerateCommand = "ARCANECORE_UPDATE_DOCS=1 dotnet test tests/ArcaneCore.World.Tests --filter Docs";
    private const string Rule = "###############################################################################";

    /// <summary>The documented defaults of every key of the daemon.</summary>
    public static string WriteDefaults(ConfCatalog catalog, ConfigDaemon daemon) => Render(catalog, daemon, null, includeSecrets: false);

    /// <summary>
    /// The effective configuration: every key of the daemon explicit, a key whose value differs from the default marked
    /// <c># changed from default</c>. Keys of the other daemon found in files are skipped and counted, unknown file keys are listed commented.
    /// Environment variables are never listed on their own, only when they override a catalog key. Secrets (see <see cref="ConfSecrets"/>)
    /// are masked unless <paramref name="includeSecrets"/> is true: the passwords of connection strings, the values of password/token/secret keys
    /// and any <c>Password=</c> component of an unknown key are replaced by <see cref="ConfSecrets.Placeholder"/>, and a masked key is never marked changed.
    /// </summary>
    public static string WriteCurrent(ConfCatalog catalog, ConfigDaemon daemon, IConfigurationRoot configuration, bool includeSecrets = false)
        => Render(catalog, daemon, configuration, includeSecrets);

    private static string Render(ConfCatalog catalog, ConfigDaemon daemon, IConfigurationRoot? current, bool includeSecrets)
    {
        bool mask = current is not null && !includeSecrets;
        string name = ConfCatalog.DaemonName(daemon);
        string file = ConfCatalog.FileName(daemon);
        ConfKey[] keys = [.. catalog.KeysFor(daemon).OrderBy(k => k.Section, StringComparer.Ordinal).ThenBy(k => k.Path, StringComparer.Ordinal)];
        var sb = new StringBuilder();
        WriteHeader(sb, name, file, current is not null, includeSecrets);

        IGrouping<string, ConfKey>[] groups = [.. keys.GroupBy(k => TopLevel(k.Section), StringComparer.Ordinal)];
        sb.Append("# SECTIONS\n#\n");
        foreach (IGrouping<string, ConfKey> group in groups)
        {
            sb.Append("#   ").Append(group.Key.ToUpperInvariant()).Append('\n');
        }

        sb.Append("#\n").Append(Rule).Append("\n\n");

        foreach (IGrouping<string, ConfKey> group in groups)
        {
            sb.Append(Rule).Append('\n');
            sb.Append("# ").Append(group.Key.ToUpperInvariant()).Append('\n');
            sb.Append(Rule).Append("\n\n");
            foreach (IGrouping<string, ConfKey> section in group.GroupBy(k => k.Section, StringComparer.Ordinal))
            {
                sb.Append("#----------------------------------------------------------------------------\n");
                sb.Append("# ").Append(section.Key).Append('\n');
                sb.Append("#----------------------------------------------------------------------------\n\n");
                foreach (ConfKey key in section)
                {
                    WriteKey(sb, key, current, mask);
                }
            }
        }

        if (current is not null)
        {
            WriteFileKeys(sb, catalog, daemon, file, current, mask);
        }

        return sb.ToString();
    }

    private static void WriteHeader(StringBuilder sb, string name, string file, bool isCurrent, bool includeSecrets)
    {
        sb.Append(Rule).Append('\n');
        sb.Append("# ArcaneCore ").Append(name).Append(" daemon configuration (").Append(isCurrent ? file : file + ".dist").Append(")\n#\n");
        if (isCurrent)
        {
            sb.Append("# Written by 'write-config --current': the effective configuration (appsettings.json, ").Append(file).Append(", environment and command line).\n");
            sb.Append("# A key whose value differs from the default carries '# changed from default'.\n");
            if (includeSecrets)
            {
                sb.Append("# WARNING: written with --include-secrets. This file contains the connection strings and passwords of the profile it was written\n");
                sb.Append("# from, unmasked: keep it out of version control and restrict who can read it.\n");
            }
            else
            {
                sb.Append("# Secrets are masked: passwords of connection strings and the values of password, token and secret keys are written as ").Append(ConfSecrets.Placeholder).Append(".\n");
                sb.Append("# Replace each ").Append(ConfSecrets.Placeholder).Append(" with the real value before using this file (or pass --include-secrets to a file target to write them).\n");
            }
        }
        else
        {
            sb.Append("# GENERATED from the configuration catalog. Do not edit this file by hand. Regenerate it:\n");
            sb.Append("#   ").Append(RegenerateCommand).Append('\n');
            sb.Append("# or write the same content with the daemon:  ArcaneCore.").Append(char.ToUpperInvariant(name[0])).Append(name[1..]).Append(" write-config --defaults ").Append(file).Append('\n');
            sb.Append("#\n# USAGE\n");
            sb.Append("#   Copy this file to ").Append(file).Append(" next to the daemon (the directory it starts in), name another file with --config <file>, or point the\n");
            sb.Append("#   environment variable ARCANECORE_CONFIG at a directory that holds world.conf and realm.conf. Keep the lines you change and delete the rest:\n");
            sb.Append("#   a key that is absent keeps the default shown here. Copying the whole file overrides every value of appsettings.json with the default.\n");
        }

        sb.Append("#\n# PRECEDENCE (lowest to highest)\n");
        sb.Append("#   appsettings.json < appsettings.{Environment}.json < ").Append(file).Append(" < user secrets (Development only) < environment variables < command line\n");
        sb.Append("#\n# SYNTAX\n");
        sb.Append("#   Key = Value        one key per line; '#' or ';' at the start of a line begins a comment; there are no trailing comments, because '#' and ';' can occur in values.\n");
        sb.Append("#   World.Port = 8085  a dot separates the levels of the key (World.Port is the configuration path World:Port); keys are not case-sensitive.\n");
        sb.Append("#   Name = \"text\"      text may be quoted; inside quotes only \\\" and \\\\ are escapes. Booleans are true or false; numbers use '.' as the decimal point.\n");
        sb.Append("#   List.0 = a         lists use numbered keys (List.0, List.1, ...). Entries you write are appended to the defaults of the list, they do not replace them.\n");
        sb.Append("#   Map.Name = value  dictionaries use the entry name as the last part of the key.\n");
        sb.Append("#   A key the daemon does not read is a start-up warning that names the closest key; a malformed line stops the daemon (exit code 78).\n");
        sb.Append("#   Documentation: docs/guide/configuration.md and docs/reference/configuration.md\n#\n");
        sb.Append(Rule).Append("\n\n");
    }

    private static string TopLevel(string section) => section.Split(':')[0];

    private static void WriteKey(StringBuilder sb, ConfKey key, IConfigurationRoot? current, bool mask)
    {
        sb.Append("#\n#    ").Append(ConfCatalog.Display(key.Path)).Append('\n');
        Labelled(sb, "Description", key.Meaning.Length > 0 ? key.Meaning : "(no description)");
        Labelled(sb, "Type", key.Type);
        Labelled(sb, "Default", DefaultText(key));
        Labelled(sb, "Allowed", key.Allowed);
        Labelled(sb, "Reload", ReloadText(key.Reload));
        Labelled(sb, "Docs", key.Docs);
        sb.Append("#\n");

        switch (key.Kind)
        {
            case ConfKey.Scalar:
                WriteScalar(sb, key, current, mask);
                break;
            case ConfKey.Sequence:
                WriteSequence(sb, key, current, mask);
                break;
            default:
                WriteMap(sb, key, current, mask);
                break;
        }

        sb.Append('\n');
    }

    private static void WriteScalar(StringBuilder sb, ConfKey key, IConfigurationRoot? current, bool mask)
    {
        string? effective = key.Default;
        if (current is not null)
        {
            string? configured = current[key.Path];
            if (!string.IsNullOrEmpty(configured) || (configured is not null && ConfValue.IsTextType(key.Type, key.EnumValues)))
            {
                effective = configured;
            }

            bool differs = (effective is null) != (key.Default is null)
                || (effective is not null && key.Default is not null && !ConfValue.Equivalent(effective, key.Default, key.Type, key.EnumValues));
            bool masked = false;
            if (mask && effective is not null)
            {
                string safe = ConfSecrets.Mask(key.Path, effective);
                masked = !string.Equals(safe, effective, StringComparison.Ordinal);
                effective = safe;
            }

            // A masked value is a placeholder, not the profile's value: it is never reported as a change, and the default is not echoed beside it.
            if (differs && !masked)
            {
                sb.Append("# changed from default: ").Append(key.Default is null ? "(not set)" : ScalarText(key, key.Default, quoteForDisplay: true)).Append('\n');
            }
        }

        string display = ConfCatalog.Display(key.Path);
        if (effective is null)
        {
            sb.Append("# ").Append(display).Append(" =\n");
            return;
        }

        AppendValueLine(sb, display, ScalarText(key, effective, quoteForDisplay: false), commented: false);
    }

    private static void WriteSequence(StringBuilder sb, ConfKey key, IConfigurationRoot? current, bool mask)
    {
        string display = ConfCatalog.Display(key.Path);
        SortedDictionary<string, string>? configured = current is null ? null : Flatten(current.GetSection(key.Path));
        if (configured is { Count: > 0 })
        {
            if (!SameEntries(configured, key.DefaultEntries))
            {
                sb.Append("# changed from default: ").Append(key.DefaultEntries.Count == 0 ? "(empty)" : "the default list shown in the comment block").Append('\n');
            }

            foreach (KeyValuePair<string, string> pair in configured.OrderBy(p => p.Key, EntryOrder.Instance))
            {
                string element = mask ? ConfSecrets.Mask(key.Path + ":" + pair.Key, pair.Value) : pair.Value;
                AppendValueLine(sb, FullKey(key, pair.Key), ElementText(key, element), commented: false);
            }

            return;
        }

        if (key.DefaultEntries.Count > 0)
        {
            sb.Append("# These entries are the default list. They are commented out because a list you write is appended to the default list, not\n");
            sb.Append("# substituted for it; uncomment them only to document the list, or set the keys of the entries you want to add.\n");
            foreach (ConfDefaultEntry entry in key.DefaultEntries)
            {
                AppendValueLine(sb, FullKey(key, entry.Key), entry.Text ? SafeQuote(entry.Value) : entry.Value, commented: true);
            }

            return;
        }

        if (key.ElementType == "object")
        {
            sb.Append("# Empty by default. Each entry is a group of keys: ").Append(display).Append(".0.<Field> = value, ").Append(display).Append(".1.<Field> = value, ...\n");
        }
        else
        {
            sb.Append("# Empty by default.\n# ").Append(display).Append(".0 =\n");
        }
    }

    private static void WriteMap(StringBuilder sb, ConfKey key, IConfigurationRoot? current, bool mask)
    {
        string display = ConfCatalog.Display(key.Path);
        var entries = new Dictionary<string, (string Value, bool Text)>(StringComparer.OrdinalIgnoreCase);
        foreach (ConfDefaultEntry entry in key.DefaultEntries)
        {
            entries[entry.Key] = (entry.Value, entry.Text);
        }

        if (current is not null)
        {
            SortedDictionary<string, string> configured = Flatten(current.GetSection(key.Path));
            if (configured.Count > 0 && !AllMatchDefaults(configured, key.DefaultEntries))
            {
                sb.Append("# changed from default: ").Append(key.DefaultEntries.Count == 0 ? "(empty)" : "the default entries shown in the comment block").Append('\n');
            }

            foreach (KeyValuePair<string, string> pair in configured)
            {
                entries[pair.Key] = (mask ? ConfSecrets.Mask(key.Path + ":" + pair.Key, pair.Value) : pair.Value, ConfValue.IsTextType(key.ElementType, key.EnumValues));
            }
        }

        if (entries.Count == 0)
        {
            sb.Append("# Empty by default.\n# ").Append(display).Append(".<Name> =\n");
            return;
        }

        foreach (KeyValuePair<string, (string Value, bool Text)> entry in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            AppendValueLine(sb, FullKey(key, entry.Key), entry.Value.Text ? SafeQuote(entry.Value.Value) : entry.Value.Value, commented: false);
        }
    }

    private static string FullKey(ConfKey key, string relative) => ConfCatalog.Display(key.Path + ":" + relative);

    private static string ElementText(ConfKey key, string value)
        => ConfValue.IsTextType(key.ElementType, key.EnumValues) ? SafeQuote(value) : value;

    private static void AppendValueLine(StringBuilder sb, string key, string value, bool commented)
        => sb.Append(commented ? "# " : string.Empty).Append(key).Append(" = ").Append(value).Append('\n');

    private static string ScalarText(ConfKey key, string value, bool quoteForDisplay)
    {
        if (!ConfValue.IsTextType(key.Type, key.EnumValues))
        {
            return value;
        }

        return quoteForDisplay ? "\"" + value + "\"" : SafeQuote(value);
    }

    private static string SafeQuote(string value)
        => value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal)
            ? throw new FormatException("a value with a line break cannot be written to a .conf file; set that key in appsettings.json")
            : ConfValue.Quote(value);

    private static string DefaultText(ConfKey key)
    {
        switch (key.Kind)
        {
            case ConfKey.Scalar:
                return key.Default is null ? "(not set)" : ScalarText(key, key.Default, quoteForDisplay: true);
            case ConfKey.Sequence:
                return key.DefaultEntries.Count == 0 ? "(empty list)" : string.Join("; ", key.DefaultEntries.Select(e => e.Key + " = " + e.Value));
            default:
                return key.DefaultEntries.Count == 0 ? "(empty)" : string.Join("; ", key.DefaultEntries.Select(e => e.Key + " = " + e.Value));
        }
    }

    private static string ReloadText(string reload) => reload switch
    {
        "live" => "Live (.reload config applies it to the running server)",
        "restart" => "Restart (.reload config keeps the old value and reports that a restart is needed)",
        _ => "Start-up (read once when the daemon starts)",
    };

    private static void Labelled(StringBuilder sb, string label, string text)
    {
        string prefix = "#        " + label + ": ";
        string indent = "#" + new string(' ', prefix.Length - 1);
        var line = new StringBuilder(prefix);
        bool first = true;
        foreach (string word in text.Replace("`", string.Empty, StringComparison.Ordinal).Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!first && line.Length + 1 + word.Length > Width)
            {
                sb.Append(line).Append('\n');
                line.Clear().Append(indent);
                first = true;
            }

            if (!first)
            {
                line.Append(' ');
            }

            line.Append(word);
            first = false;
        }

        sb.Append(line).Append('\n');
    }

    private static bool AllMatchDefaults(SortedDictionary<string, string> configured, IReadOnlyList<ConfDefaultEntry> defaults)
        => configured.All(pair => defaults.Any(d => string.Equals(d.Key, pair.Key, StringComparison.OrdinalIgnoreCase) && string.Equals(d.Value, pair.Value, StringComparison.Ordinal)));

    private static bool SameEntries(SortedDictionary<string, string> configured, IReadOnlyList<ConfDefaultEntry> defaults)
    {
        if (configured.Count != defaults.Count)
        {
            return false;
        }

        foreach (ConfDefaultEntry entry in defaults)
        {
            if (!configured.TryGetValue(entry.Key, out string? value) || !string.Equals(value, entry.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The leaf values below a section, keyed by the path relative to the section.</summary>
    private static SortedDictionary<string, string> Flatten(IConfigurationSection section)
    {
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Collect(section, section.Path.Length + 1, result);
        return result;
    }

    private static void Collect(IConfigurationSection section, int prefix, SortedDictionary<string, string> result)
    {
        foreach (IConfigurationSection child in section.GetChildren())
        {
            if (child.Value is not null)
            {
                result[child.Path[prefix..]] = child.Value;
            }

            Collect(child, prefix, result);
        }
    }

    private static void WriteFileKeys(StringBuilder sb, ConfCatalog catalog, ConfigDaemon daemon, string file, IConfigurationRoot configuration, bool mask)
    {
        var framework = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var otherDaemon = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IConfigurationProvider provider in configuration.Providers)
        {
            if (provider is not (ConfConfigurationProvider or JsonConfigurationProvider))
            {
                continue;
            }

            foreach (string path in LeafPaths(provider, null))
            {
                ConfKeyClass classification = catalog.Classify(path, daemon);
                switch (classification.Status)
                {
                    case ConfKeyStatus.OtherDaemon:
                        otherDaemon.Add(path);
                        break;
                    case ConfKeyStatus.Unknown or ConfKeyStatus.Section:
                        unknown[path] = Masked(path, configuration[path] ?? string.Empty, mask);
                        break;
                    case ConfKeyStatus.Framework:
                        framework[path] = Masked(path, configuration[path] ?? string.Empty, mask);
                        break;
                }
            }
        }

        if (framework.Count > 0)
        {
            sb.Append(Rule).Append("\n# HOST FRAMEWORK KEYS\n# Keys of the .NET host (logging levels per category, host options) found in the profile.\n").Append(Rule).Append("\n\n");
            foreach (KeyValuePair<string, string> pair in framework)
            {
                AppendValueLine(sb, ConfCatalog.Display(pair.Key), SafeQuote(pair.Value), commented: false);
            }

            sb.Append('\n');
        }

        if (unknown.Count > 0)
        {
            sb.Append(Rule).Append("\n# UNKNOWN KEYS\n# Found in the profile's files but not read by the ").Append(ConfCatalog.DaemonName(daemon))
                .Append(" daemon. They are kept here, commented out, so nothing is lost silently; check the spelling.\n").Append(Rule).Append("\n\n");
            foreach (KeyValuePair<string, string> pair in unknown)
            {
                AppendValueLine(sb, ConfCatalog.Display(pair.Key), pair.Value.Contains('\n', StringComparison.Ordinal) ? "(multi-line value)" : SafeQuote(pair.Value), commented: true);
            }

            sb.Append('\n');
        }

        if (otherDaemon.Count > 0)
        {
            string other = daemon == ConfigDaemon.Realm ? "world.conf" : "realm.conf";
            sb.Append("# ").Append(otherDaemon.Count).Append(" key(s) found in the profile's files belong to ").Append(other).Append(" and are not written to ").Append(file).Append(".\n");
        }
    }

    private static string Masked(string path, string value, bool mask) => mask ? ConfSecrets.Mask(path, value) : value;

    private static IEnumerable<string> LeafPaths(IConfigurationProvider provider, string? parent)
    {
        foreach (string child in provider.GetChildKeys([], parent).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string full = parent is null ? child : parent + ":" + child;
            if (provider.TryGet(full, out string? value) && value is not null)
            {
                yield return full;
            }

            foreach (string nested in LeafPaths(provider, full))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Orders list indexes numerically (2 before 10) and everything else ordinally.</summary>
    private sealed class EntryOrder : IComparer<string>
    {
        public static readonly EntryOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            string[] a = (x ?? string.Empty).Split(':');
            string[] b = (y ?? string.Empty).Split(':');
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                int c = int.TryParse(a[i], out int ia) && int.TryParse(b[i], out int ib)
                    ? ia.CompareTo(ib)
                    : string.Compare(a[i], b[i], StringComparison.Ordinal);
                if (c != 0)
                {
                    return c;
                }
            }

            return a.Length.CompareTo(b.Length);
        }
    }
}
