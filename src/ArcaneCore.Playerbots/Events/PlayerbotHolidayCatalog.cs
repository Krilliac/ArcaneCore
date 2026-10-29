using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.WorldState;

namespace ArcaneCore.World.Playerbots.Events;

/// <summary>
/// How a festival is told in speech and emotes: the <see cref="Key"/> of its line templates (<c>event.&lt;key&gt;.arrive</c>), the
/// <see cref="Name"/> a line says, the text emotes its crowd makes (1.12 <c>EmotesText</c> ids) and whether bots <see cref="Gather"/> at it.
/// </summary>
internal sealed record PlayerbotFestivalFlavour(string Key, string Name, IReadOnlyList<uint> Emotes, bool Gather);

/// <summary>
/// Which game events are festivals (docs/areas/playbots-social.md, World events). The game-event data is the authority on what runs and where;
/// this catalog only reads it: a festival <b>root</b> is an event with a client holiday id (Call to Arms aside, and any event the operator
/// excluded), a <b>member</b> is an event whose <c>LinkedTo</c> chain reaches a root (Hallow's End and its two sub-events 24 and 25 in classic-db
/// z2815). Event 21 (Winter Veil: Gifts) is not linked in the data and is not a member in slice 1; it is held for the event-quest stage (H6). Only the flavour (key, name,
/// emotes) is data: <see cref="PlayerbotFestivalFlavours"/>, the rows of <c>holiday_flavour</c>. Pure and stateless; any thread.
/// </summary>
internal static class PlayerbotHolidayCatalog
{
    /// <summary>The most <c>LinkedTo</c> links followed from an event to its root.</summary>
    internal const int MaxLinks = 4;

    private const uint CallToArmsFirst = 283, CallToArmsLast = 285;

    /// <summary>Whether the event is a festival root: a holiday id, not Call to Arms, not excluded.</summary>
    internal static bool IsRoot(GameEventDefinition definition, IReadOnlySet<ushort> excluded)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(excluded);
        return definition.HolidayId != 0 && definition.HolidayId is < CallToArmsFirst or > CallToArmsLast && !excluded.Contains(definition.Id);
    }

    /// <summary>
    /// The root of the festival event <paramref name="id"/> belongs to (the event itself when it is a root), or null: no such event, no root
    /// within <see cref="MaxLinks"/> links, a link that leaves the data or loops, or an excluded event on the way.
    /// </summary>
    internal static ushort? RootOf(ushort id, Func<ushort, GameEventDefinition?> find, IReadOnlySet<ushort> excluded)
    {
        ArgumentNullException.ThrowIfNull(find);
        ArgumentNullException.ThrowIfNull(excluded);
        ushort current = id;
        for (int links = 0; links <= MaxLinks; links++)
        {
            if (excluded.Contains(current) || find(current) is not { } definition) return null;
            if (IsRoot(definition, excluded)) return current;
            if (definition.LinkedTo == 0) return null;
            current = definition.LinkedTo;
        }

        return null;
    }

    /// <summary>
    /// The running events that count as festival members, each with its root; a member counts only while its root runs (the vmangos dialect
    /// has no <c>linkedTo</c> to stop a sub-event whose festival is off). The bells, the day and night events, the war-effort phases and
    /// Call to Arms are never in it.
    /// </summary>
    internal static IReadOnlyDictionary<ushort, ushort> ActiveMembers(IEnumerable<GameEventDefinition> events, IReadOnlySet<ushort> running, IReadOnlySet<ushort> excluded)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(running);
        var members = new Dictionary<ushort, ushort>();
        if (running.Count == 0) return members;
        Dictionary<ushort, GameEventDefinition> byId = [];
        foreach (GameEventDefinition definition in events) byId[definition.Id] = definition;
        Func<ushort, GameEventDefinition?> find = id => byId.GetValueOrDefault(id);
        foreach (ushort id in running)
            if (RootOf(id, find, excluded) is { } root && running.Contains(root)) members[id] = root;
        return members;
    }
}

/// <summary>
/// The festival flavours of <c>holiday_flavour</c> (inventory 19 of the rule-engine design): how each client holiday is told. Immutable;
/// any thread. A holiday the table does not name (or names with a blank name) gets the row of holiday id 0 with the event's description as its name; with no row 0 either
/// (a database without the shipped content) it is a gathering festival with the key <c>festival</c> and no crowd emotes.
/// </summary>
internal sealed class PlayerbotFestivalFlavours
{
    private const string FallbackKey = "festival";
    private const string FallbackName = "the festival";

    private readonly Dictionary<uint, PlayerbotFestivalFlavour> _byHoliday = [];
    private readonly PlayerbotFestivalFlavour? _default;

    internal PlayerbotFestivalFlavours(IEnumerable<HolidayFlavourRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (HolidayFlavourRecord record in records)
        {
            var flavour = new PlayerbotFestivalFlavour(record.FlavourKey, record.Name, Emotes(record), record.Gather);
            if (record.HolidayId == 0) _default = flavour;
            else _byHoliday[record.HolidayId] = flavour;
        }
    }

    /// <summary>No rows.</summary>
    internal static PlayerbotFestivalFlavours Empty { get; } = new([]);

    /// <summary>Whether the table has the row of holiday id 0.</summary>
    internal bool HasDefault => _default is not null;

    /// <summary>The number of named holidays (the default row not counted).</summary>
    internal int Count => _byHoliday.Count;

    /// <summary>
    /// The flavour of holiday <paramref name="holidayId"/>; one the table does not name takes the default row (or the built-in fallback) and
    /// is named by <paramref name="description"/>, trimmed, or "the festival" when that is blank.
    /// </summary>
    internal PlayerbotFestivalFlavour For(uint holidayId, string description)
    {
        PlayerbotFestivalFlavour? named = null;
        if (holidayId != 0 && _byHoliday.TryGetValue(holidayId, out named) && named.Name.Length > 0) return named;
        PlayerbotFestivalFlavour basis = named ?? _default ?? new PlayerbotFestivalFlavour(FallbackKey, string.Empty, [], true);
        return basis with { Name = string.IsNullOrWhiteSpace(description) ? FallbackName : description.Trim() };
    }

    private static uint[] Emotes(HolidayFlavourRecord record)
    {
        List<uint> emotes = new(3);
        foreach (uint emote in new[] { record.Emote1, record.Emote2, record.Emote3 })
            if (emote != 0) emotes.Add(emote);
        return [.. emotes];
    }
}
