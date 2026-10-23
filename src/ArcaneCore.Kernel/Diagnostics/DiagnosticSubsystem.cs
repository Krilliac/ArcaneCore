namespace ArcaneCore.Kernel.Diagnostics;

/// <summary>
/// The one naming rule the diagnostics ledgers share. A fault site is written <c>subsystem.code</c> (for example
/// <c>writequeue.character-retry</c>); a site built at run time may use <c>subsystem:detail</c> or <c>subsystem/detail</c>. An anomaly's
/// subsystem is passed separately (<c>AnomalyLedger.Report(subsystem, code, ...)</c>). Both resolve to the same canonical subsystem
/// name through <see cref="Canonical"/>, so <c>.server faults summary</c>, <c>.server anomalies</c> and the self-diagnosis report group
/// one area under one name.
/// <para>Pure functions, no state; safe from any thread. Used on the failure and reporting paths only.</para>
/// </summary>
public static class DiagnosticSubsystem
{
    /// <summary>Old or variant spellings mapped to the canonical subsystem name.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["battlegrounds"] = "battleground",
        ["bg"] = "battleground",
        ["bots"] = "playerbot",
        ["characters"] = "character",
        ["creatures"] = "creature",
        ["features"] = "feature",
        ["game-events"] = "game-event",
        ["gameobjects"] = "gameobject",
        ["handlers"] = "handler",
        ["instance-data"] = "instance",
        ["instances"] = "instance",
        ["navtile"] = "nav",
        ["outdoorpvp"] = "outdoor-pvp",
        ["pets"] = "pet",
        ["server-mail"] = "mail",
        ["skills"] = "skill",
        ["spells"] = "spell",
        ["worldtick"] = "world",
    };

    /// <summary>
    /// The canonical subsystem of a fault site: the part before the first <c>'.'</c>, <c>':'</c> or <c>'/'</c>; a site with none of
    /// those is cut at its first <c>'-'</c> (older sites), and a single word is its own subsystem. The result goes through
    /// <see cref="Canonical"/>. Null, empty or blank is <c>unknown</c>.
    /// </summary>
    public static string OfSite(string? site)
    {
        if (string.IsNullOrWhiteSpace(site))
        {
            return "unknown";
        }

        string trimmed = site.Trim();
        int split = trimmed.IndexOfAny(['.', ':', '/']);
        if (split < 0)
        {
            split = trimmed.IndexOf('-');
        }

        return Canonical(split > 0 ? trimmed[..split] : trimmed);
    }

    /// <summary>A subsystem name trimmed, in lower case, with variant spellings replaced by the canonical one. Null or blank is <c>unknown</c>.</summary>
    public static string Canonical(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unknown";
        }

        string lower = name.Trim().ToLowerInvariant();
        return Aliases.TryGetValue(lower, out string? canonical) ? canonical : lower;
    }
}
