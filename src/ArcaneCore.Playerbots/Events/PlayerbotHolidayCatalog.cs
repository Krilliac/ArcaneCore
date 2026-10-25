using ArcaneCore.Game.WorldState.Events;
using static ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialEmotes;

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
/// emotes) is curated. Pure and stateless; any thread.
/// </summary>
internal static class PlayerbotHolidayCatalog
{
    /// <summary>The most <c>LinkedTo</c> links followed from an event to its root.</summary>
    internal const int MaxLinks = 4;

    private const uint CallToArmsFirst = 283, CallToArmsLast = 285;

    private static readonly uint[] DefaultEmotes = [TextEmotes.Cheer, TextEmotes.Wave, TextEmotes.Dance];

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

    /// <summary>
    /// The flavour of a holiday id; an unknown id gets the key <c>festival</c>, <paramref name="description"/> as its name and the default
    /// crowd (it gathers). Ids 62 and 376 are not in the classic-db data and not in this table.
    /// </summary>
    internal static PlayerbotFestivalFlavour Flavour(uint holidayId, string description) => holidayId switch
    {
        341 => new("midsummer", "Midsummer", [TextEmotes.Dance, TextEmotes.Cheer], true),
        141 => new("winterveil", "Winter Veil", [TextEmotes.Cheer, TextEmotes.Applaud], true),
        327 => new("lunar", "Lunar Festival", [TextEmotes.Bow, TextEmotes.Cheer], true),
        335 => new("love", "Love is in the Air", DefaultEmotes, true),
        181 => new("noblegarden", "Noblegarden", DefaultEmotes, true),
        201 => new("children", "Children's Week", DefaultEmotes, true),
        324 => new("hallows", "Hallow's End", [TextEmotes.Laugh, TextEmotes.Cheer], true),
        321 => new("harvest", "Harvest Festival", DefaultEmotes, true),
        374 or 375 => new("darkmoon", "Darkmoon Faire", [TextEmotes.Cheer, TextEmotes.Applaud, TextEmotes.Laugh], true),
        301 => new("fishing", "Fishing Extravaganza", DefaultEmotes, false),
        _ => new("festival", string.IsNullOrWhiteSpace(description) ? "the festival" : description.Trim(), DefaultEmotes, true),
    };
}
