using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using ArcaneCore.Data.Content.ClientEffects;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.World.Gm.Events;

/// <summary>
/// The client tables <c>.fx</c> checks ids against and <c>.fx lookup</c> searches (build-5875 SoundEntries, ZoneMusic,
/// CinematicSequences, SpellVisualKit, SpellVisualEffectName and WorldStateUI). Each is null until
/// <see cref="LiveFxDataFeature"/> loads it from <c>World:GmCommands:LiveFxDbcDirectory</c>; a null table leaves that id
/// kind unchecked (the behaviour without client data). Attached to the <see cref="WorldRuntime"/> as a weak side table.
/// <para>Thread affinity: written once at startup (before the world thread runs), read on the world thread.</para>
/// </summary>
public sealed class LiveFxData
{
    private static readonly ConditionalWeakTable<WorldRuntime, LiveFxData> Attached = new();

    public DbcTable<SoundEntry>? Sounds { get; set; }

    public DbcTable<ZoneMusicEntry>? ZoneMusic { get; set; }

    public DbcTable<CinematicSequence>? Cinematics { get; set; }

    public DbcTable<SpellVisualKitEntry>? VisualKits { get; set; }

    public DbcTable<SpellVisualEffectName>? VisualEffects { get; set; }

    public DbcTable<WorldStateUIEntry>? WorldStates { get; set; }

    /// <summary>The data of <paramref name="world"/> (empty, i.e. unchecked, on first use).</summary>
    public static LiveFxData Of(WorldRuntime world) => Attached.GetValue(world, _ => new LiveFxData());
}

/// <summary>The <c>.fx lookup</c> searches over <see cref="LiveFxData"/>: pure functions from a query to reply lines.</summary>
public static class LiveFxLookup
{
    /// <summary>The most match lines one lookup prints; a final line counts the rest.</summary>
    public const int MaxLines = 20;

    /// <summary>The line after <see cref="MaxLines"/> matches: how many were left out.</summary>
    public const string MoreText = "... {0} more match(es); narrow the search.";

    /// <summary>The reply when nothing matched.</summary>
    public const string NoMatchText = "No {0} matches '{1}'.";

    /// <summary>The reply when the table a lookup needs was not loaded.</summary>
    public const string NotLoadedText = "{0} is not loaded (set World:GmCommands:LiveFxDbcDirectory).";

    /// <summary>The lookup kinds, in help order.</summary>
    public static IReadOnlyList<string> Kinds { get; } = ["sound", "music", "cinematic", "visual", "worldstate"];

    /// <summary>The reply lines of <c>.fx lookup <paramref name="kind"/> <paramref name="query"/></c>; null for an unknown kind.</summary>
    public static IReadOnlyList<string>? Run(LiveFxData data, string kind, string query)
    {
        ArgumentNullException.ThrowIfNull(data);
        query = query.Trim();
        bool numeric = uint.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out uint id);
        return kind.ToLowerInvariant() switch
        {
            "sound" => data.Sounds is not { } sounds ? NotLoaded(SoundEntriesDbcReader.FileName) : Lines("sound", query, sounds.Rows
                .Where(s => numeric ? s.Id == id : Has(s.Name, query) || s.Files.Any(f => Has(f, query)))
                .Select(SoundLine)),
            "music" => data.ZoneMusic is not { } music ? NotLoaded(ZoneMusicDbcReader.FileName) : Lines("music set", query, music.Rows
                .Where(m => numeric ? m.Id == id : Has(m.SetName, query))
                .Select(m => string.Format(CultureInfo.InvariantCulture, "ZoneMusic #{0} {1}: day sound #{2}{3}, night sound #{4}{5}",
                    m.Id, m.SetName, m.DaySound, SoundName(data, m.DaySound), m.NightSound, SoundName(data, m.NightSound)))),
            "cinematic" => data.Cinematics is not { } cinematics ? NotLoaded(CinematicSequencesDbcReader.FileName) : Lines("cinematic", query, cinematics.Rows
                .Where(c => numeric ? c.Id == id : Has(SoundName(data, c.SoundId), query))
                .Select(c => string.Format(CultureInfo.InvariantCulture, "Cinematic #{0}: sound #{1}{2}, cameras {3}",
                    c.Id, c.SoundId, SoundName(data, c.SoundId), c.Cameras.Count == 0 ? "none" : string.Join(",", c.Cameras)))),
            "visual" => data.VisualKits is not { } kits ? NotLoaded(SpellVisualKitDbcReader.FileName) : Lines("spell visual kit", query, kits.Rows
                .Where(k => numeric ? k.Id == id : k.Effects.Any(e => EffectMatches(data, e, query)) || Has(SoundName(data, k.SoundId), query))
                .Select(k => string.Format(CultureInfo.InvariantCulture, "Kit #{0}: anim {1}, effects {2}, sound #{3}{4}",
                    k.Id, k.AnimId, k.Effects.Count == 0 ? "none" : string.Join(", ", k.Effects.Select(e => EffectName(data, e))), k.SoundId, SoundName(data, k.SoundId)))),
            "worldstate" => data.WorldStates is not { } states ? NotLoaded(WorldStateUIDbcReader.FileName) : Lines("world state", query, states.Rows
                .Where(w => numeric ? w.Id == id || ReadsField(w, id) : Has(w.Text, query) || Has(w.Tooltip, query) || Has(w.Icon, query) || Has(w.ExtendedUI, query))
                .Select(WorldStateLine)),
            _ => null,
        };
    }

    private static bool Has(string text, string query) => query.Length > 0 && text.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string SoundLine(SoundEntry s)
    {
        string first = s.Files.Count == 0 ? "no files" : (s.Directory.Length > 0 ? s.Directory + "\\" : string.Empty) + s.Files[0];
        string more = s.Files.Count > 1 ? string.Format(CultureInfo.InvariantCulture, " (+{0} more files)", s.Files.Count - 1) : string.Empty;
        return string.Format(CultureInfo.InvariantCulture, "Sound #{0} {1} (type {2}): {3}{4}", s.Id, s.Name, s.SoundType, first, more);
    }

    /// <summary>
    /// Whether the row reads world-state <paramref name="field"/>: named in its text or tooltip as <c>%{field}w</c> (how the
    /// client formats a world-state value), or as its state variable or an extended-UI variable.
    /// </summary>
    private static bool ReadsField(WorldStateUIEntry row, uint field)
    {
        string token = "%" + field.ToString(CultureInfo.InvariantCulture) + "w";
        return row.StateVariable == field || row.ExtendedUIStateVariables.Contains(field)
            || row.Text.Contains(token, StringComparison.Ordinal) || row.Tooltip.Contains(token, StringComparison.Ordinal);
    }

    private static string WorldStateLine(WorldStateUIEntry w)
    {
        var line = new StringBuilder();
        line.Append(CultureInfo.InvariantCulture, $"WorldStateUI #{w.Id} (map {w.MapId}, area {w.AreaId}): {OneLine(w.Text)} | {OneLine(w.Tooltip)}");
        if (w.StateVariable != 0)
        {
            line.Append(CultureInfo.InvariantCulture, $" [icon field {w.StateVariable}]");
        }

        if (w.ExtendedUI.Length > 0)
        {
            line.Append(CultureInfo.InvariantCulture, $" [{w.ExtendedUI} fields {string.Join(",", w.ExtendedUIStateVariables)}]");
        }

        return line.ToString();
    }

    /// <summary>" Name" of a sound id when SoundEntries is loaded and has it, else empty.</summary>
    private static string SoundName(LiveFxData data, uint id)
        => id != 0 && data.Sounds?.Find(id) is { } sound ? " " + sound.Name : string.Empty;

    private static bool EffectMatches(LiveFxData data, uint effect, string query)
        => data.VisualEffects?.Find(effect) is { } name && (Has(name.Name, query) || Has(name.FileName, query));

    private static string EffectName(LiveFxData data, uint effect)
        => data.VisualEffects?.Find(effect) is { } name ? string.Format(CultureInfo.InvariantCulture, "{0} (#{1})", name.Name, effect) : "#" + effect.ToString(CultureInfo.InvariantCulture);

    /// <summary>Chat replies split on line breaks; a client string keeps to one line.</summary>
    private static string OneLine(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            builder.Append(c is '\r' or '\n' ? ' ' : c);
        }

        return builder.ToString();
    }

    private static string[] NotLoaded(string file) => [string.Format(CultureInfo.InvariantCulture, NotLoadedText, file)];

    private static List<string> Lines(string what, string query, IEnumerable<string> matches)
    {
        var lines = new List<string>(MaxLines + 1);
        int total = 0;
        foreach (string line in matches)
        {
            if (total++ < MaxLines)
            {
                lines.Add(line);
            }
        }

        if (total == 0)
        {
            lines.Add(string.Format(CultureInfo.InvariantCulture, NoMatchText, what, query));
        }
        else if (total > MaxLines)
        {
            lines.Add(string.Format(CultureInfo.InvariantCulture, MoreText, total - MaxLines));
        }

        return lines;
    }
}
