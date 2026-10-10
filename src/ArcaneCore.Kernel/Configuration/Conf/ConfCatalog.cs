using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArcaneCore.Kernel.Configuration.Conf;

/// <summary>One default entry of a sequence or map key, relative to the key (<c>0.Name</c> or <c>Administrator</c>); <see cref="Text"/> values are written quoted.</summary>
public sealed record ConfDefaultEntry(string Key, string Value, bool Text);

/// <summary>
/// One configuration key as the .conf tooling knows it. <see cref="Kind"/> is <c>scalar</c>, <c>sequence</c> or <c>map</c>;
/// <see cref="Default"/> is null for "unset"; <see cref="Reload"/> is <c>live</c>, <c>restart</c> or <c>start</c>.
/// </summary>
public sealed record ConfKey(
    string Path,
    string Section,
    string Type,
    string Kind,
    string ElementType,
    IReadOnlyList<string>? EnumValues,
    string? Default,
    IReadOnlyList<ConfDefaultEntry> DefaultEntries,
    [property: JsonConverter(typeof(JsonStringEnumConverter<ConfigDaemon>))] ConfigDaemon Daemons,
    string Reload,
    string Meaning,
    string Allowed,
    string Docs)
{
    public const string Scalar = "scalar";
    public const string Sequence = "sequence";
    public const string Map = "map";

    public bool IsFor(ConfigDaemon daemon) => (Daemons & daemon) != 0;

    public bool IsCollection => Kind != Scalar;
}

/// <summary>How a key found in a .conf file relates to the catalog.</summary>
public enum ConfKeyStatus
{
    /// <summary>A key of this daemon.</summary>
    Known,

    /// <summary>Below a sequence or map key of this daemon (an index or a dictionary key).</summary>
    ChildOfCollection,

    /// <summary>A host framework key (<c>Logging:LogLevel:*</c>, <c>HostOptions:*</c>): open, not checked.</summary>
    Framework,

    /// <summary>A section (it has keys below it) used as a key.</summary>
    Section,

    /// <summary>A key the other daemon reads.</summary>
    OtherDaemon,

    /// <summary>Not a key of either daemon.</summary>
    Unknown,
}

/// <summary>The classification of one path.</summary>
public readonly record struct ConfKeyClass(ConfKeyStatus Status, ConfKey? Key);

/// <summary>
/// The catalog of every configuration key, generated from the options classes by the docs tests
/// (<c>ARCANECORE_UPDATE_DOCS=1 dotnet test tests/ArcaneCore.World.Tests --filter Docs</c>) and embedded in this assembly,
/// so both daemons can generate the .conf files, check a .conf file for typos and validate command lines without reflecting over
/// types they do not reference. Immutable; safe from any thread.
/// </summary>
public sealed class ConfCatalog
{
    private const string ResourceName = "ArcaneCore.Kernel.Configuration.Conf.conf-catalog.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Lazy<ConfCatalog> EmbeddedCatalog = new(LoadEmbedded);

    private readonly Dictionary<string, ConfKey> _byPath;

    public ConfCatalog(IEnumerable<ConfKey> keys)
    {
        Keys = [.. keys];
        _byPath = new Dictionary<string, ConfKey>(StringComparer.OrdinalIgnoreCase);
        foreach (ConfKey key in Keys)
        {
            _byPath[key.Path] = key;
        }
    }

    /// <summary>The catalog embedded in this assembly.</summary>
    public static ConfCatalog Embedded => EmbeddedCatalog.Value;

    public IReadOnlyList<ConfKey> Keys { get; }

    public static ConfCatalog FromJson(string json)
    {
        CatalogFile? file = JsonSerializer.Deserialize<CatalogFile>(json, Json);
        return new ConfCatalog(file?.Keys ?? throw new InvalidDataException("the configuration catalog is empty"));
    }

    public string ToJson() => JsonSerializer.Serialize(new CatalogFile(Keys), Json) + "\n";

    public IEnumerable<ConfKey> KeysFor(ConfigDaemon daemon) => Keys.Where(k => k.IsFor(daemon));

    public ConfKey? Find(string path) => _byPath.GetValueOrDefault(path);

    /// <summary>The key as an operator writes it: dotted, unless a segment itself contains a dot (<c>Logging:LogLevel:Microsoft.EntityFrameworkCore</c>).</summary>
    public static string Display(string path) => path.Contains('.', StringComparison.Ordinal) ? path : path.Replace(':', '.');

    /// <summary>The conf file of the daemon.</summary>
    public static string FileName(ConfigDaemon daemon) => daemon == ConfigDaemon.Realm ? "realm.conf" : "world.conf";

    /// <summary>The daemon's lower-case name.</summary>
    public static string DaemonName(ConfigDaemon daemon) => daemon == ConfigDaemon.Realm ? "realm" : "world";

    public ConfKeyClass Classify(string path, ConfigDaemon daemon)
    {
        if (_byPath.TryGetValue(path, out ConfKey? exact))
        {
            return new ConfKeyClass(exact.IsFor(daemon) ? ConfKeyStatus.Known : ConfKeyStatus.OtherDaemon, exact);
        }

        if (IsFrameworkKey(path))
        {
            return new ConfKeyClass(ConfKeyStatus.Framework, null);
        }

        foreach (ConfKey key in Keys)
        {
            if (key.IsCollection && path.StartsWith(key.Path + ":", StringComparison.OrdinalIgnoreCase))
            {
                return new ConfKeyClass(key.IsFor(daemon) ? ConfKeyStatus.ChildOfCollection : ConfKeyStatus.OtherDaemon, key);
            }
        }

        ConfKey? otherDaemonKey = null;
        foreach (ConfKey key in Keys)
        {
            if (key.Path.StartsWith(path + ":", StringComparison.OrdinalIgnoreCase))
            {
                if (key.IsFor(daemon))
                {
                    return new ConfKeyClass(ConfKeyStatus.Section, null);
                }

                otherDaemonKey ??= key;
            }
        }

        return otherDaemonKey is not null ? new ConfKeyClass(ConfKeyStatus.OtherDaemon, otherDaemonKey) : new ConfKeyClass(ConfKeyStatus.Unknown, null);
    }

    /// <summary>True for the open host framework keys: everything under <c>HostOptions</c> and <c>Logging</c> except the <c>Logging:ArcaneCore</c> section, which is checked.</summary>
    public static bool IsFrameworkKey(string path)
        => path.StartsWith("HostOptions:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("hostBuilder:", StringComparison.OrdinalIgnoreCase)
        || (path.StartsWith("Logging:", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("Logging:ArcaneCore:", StringComparison.OrdinalIgnoreCase));

    /// <summary>The closest key of the daemon (by edit distance, case-insensitive) within max(2, length/5), or null.</summary>
    public string? Suggest(string path, ConfigDaemon daemon)
    {
        int limit = Math.Max(2, path.Length / 5);
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (ConfKey key in KeysFor(daemon))
        {
            int distance = EditDistance(path, key.Path, bestDistance < limit ? bestDistance : limit);
            if (distance <= limit && distance < bestDistance)
            {
                best = key.Path;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>Levenshtein distance, ignoring case; stops early and returns more than <paramref name="limit"/> once the distance must exceed it.</summary>
    internal static int EditDistance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit)
        {
            return limit + 1;
        }

        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            int rowMin = current[0];
            char ca = char.ToUpperInvariant(a[i - 1]);
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = ca == char.ToUpperInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }

            if (rowMin > limit)
            {
                return limit + 1;
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static ConfCatalog LoadEmbedded()
    {
        using Stream stream = typeof(ConfCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"the embedded resource {ResourceName} is missing from {typeof(ConfCatalog).Assembly.GetName().Name}");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    private sealed record CatalogFile(IReadOnlyList<ConfKey> Keys);
}
