using ArcaneCore.Kernel.Configuration.Conf;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// Turns the documentation catalog (<see cref="ConfigEntry"/>, built by reflecting over the options classes) into the
/// <see cref="ConfCatalog"/> that ships embedded in the Kernel assembly and drives the .conf.dist files. Pure and deterministic:
/// the same entries give the same catalog, in section then path order.
/// </summary>
internal static class ConfCatalogGenerator
{
    /// <summary>
    /// The catalog of the options-class entries plus the keys read by name (<paramref name="adHoc"/>): the command line and the .conf
    /// checks refuse every key the catalog lacks, so a key read through <c>IConfiguration[...]</c> must be in it too.
    /// </summary>
    public static ConfCatalog Build(IEnumerable<ConfigEntry> entries, IEnumerable<AdHocKey>? adHoc = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return new ConfCatalog(entries
            .Select(e => ToKey(e))
            .Concat((adHoc ?? []).Select(a => ToKey(a)))
            .OrderBy(k => k.Section, StringComparer.Ordinal)
            .ThenBy(k => k.Path, StringComparer.Ordinal));
    }

    /// <summary>A key read by name: a scalar of the section named by its path prefix, restart-only (every one is read once at start).</summary>
    private static ConfKey ToKey(AdHocKey key)
    {
        string? raw = key.Default == "null" ? null : key.Default;
        return new ConfKey(
            key.Path,
            key.Path[..key.Path.LastIndexOf(':')],
            key.TypeText,
            ConfKey.Scalar,
            key.TypeText,
            null,
            raw,
            [],
            key.Daemons,
            "start",
            key.Meaning,
            ConfValue.AllowedText(key.TypeText, null),
            "docs/reference/configuration.md#keys-read-by-name");
    }

    private static ConfKey ToKey(ConfigEntry entry) => new(
        entry.Path,
        entry.Section,
        entry.TypeText,
        entry.Kind,
        entry.ElementType,
        entry.EnumValues,
        entry.RawDefault,
        entry.DefaultEntries ?? [],
        entry.Daemons,
        entry.Reload switch { "live" => "live", "restart" => "restart", _ => "start" },
        entry.Meaning.Replace("\\|", "|", StringComparison.Ordinal),
        AllowedOf(entry),
        "docs/reference/configuration.md#" + ConfigReferenceRenderer.Anchor(entry.Section));

    private static string AllowedOf(ConfigEntry entry)
    {
        if (entry.AllowedOverride is { } allowed) return allowed;
        switch (entry.Kind)
        {
            case ConfKey.Scalar:
                return ConfValue.AllowedText(entry.TypeText, entry.EnumValues);
            default:
                string noun = entry.Kind == ConfKey.Map ? "a map: one key per entry name" : "a list: numbered keys 0, 1, 2, ...";
                return entry.ElementType == "object"
                    ? noun + "; each entry is a group of keys"
                    : noun + "; each value is " + ConfValue.AllowedText(entry.ElementType, entry.EnumValues);
        }
    }
}
