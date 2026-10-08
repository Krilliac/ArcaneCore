namespace ArcaneCore.Kernel.WorldData.Creatures;

/// <summary>
/// One action of an EventAI row (cmangos-classic <c>creature_ai_scripts.actionN_type</c> and
/// its three parameters). Values follow the cmangos EventAI documentation (doc/EventAI.txt);
/// the meanings ArcaneCore runs are listed in docs/areas/creature-ai.md.
/// </summary>
public readonly record struct CreatureAiAction(byte Type, int Param1, int Param2, int Param3)
{
    public bool IsEmpty => Type == 0;
}

/// <summary>
/// One EventAI row (cmangos-classic <c>creature_ai_scripts</c>): an event of <see cref="EventType"/>
/// with six parameters, a chance, flags, an inverse phase mask and up to three actions.
/// Row layout: mangos-classic <c>CreatureEventAIMgr::LoadCreatureEventAI_Scripts</c>
/// (src/game/AI/EventAI/CreatureEventAIMgr.cpp:211-269).
/// </summary>
public sealed record CreatureAiEvent
{
    public required uint Id { get; init; }

    /// <summary>
    /// The creature entry this row belongs to; 0 when the row is keyed by spawn guid
    /// (cmangos stores a negative <c>creature_id</c> as a spawn guid, CreatureEventAIMgr.cpp:233-252).
    /// </summary>
    public required uint CreatureId { get; init; }

    /// <summary>The spawn guid this row belongs to when <c>creature_id</c> was negative; 0 for entry-keyed rows.</summary>
    public uint CreatureGuid { get; init; }

    public required byte EventType { get; init; }

    /// <summary>Bit N set: the event does not run in phase N (cmangos event_inverse_phase_mask).</summary>
    public uint InversePhaseMask { get; init; }

    /// <summary>Percent chance to run when triggered (cmangos event_chance, adjusted to at most 100; 0 never runs).</summary>
    public byte Chance { get; init; } = 100;

    /// <summary>
    /// cmangos EFLAG_* bit set, kept at its full 32-bit width (the column is read with GetUInt32,
    /// CreatureEventAIMgr.cpp:267): 0x01 repeatable, 0x20 random action, 0x400 combat action, and so on.
    /// Almost every classic-db row carries 0x400 (1024 or 1025), so a narrower type loses data.
    /// </summary>
    public uint Flags { get; init; }

    public int Param1 { get; init; }

    public int Param2 { get; init; }

    public int Param3 { get; init; }

    public int Param4 { get; init; }

    public int Param5 { get; init; }

    public int Param6 { get; init; }

    public CreatureAiAction Action1 { get; init; }

    public CreatureAiAction Action2 { get; init; }

    public CreatureAiAction Action3 { get; init; }

    public IEnumerable<CreatureAiAction> Actions
    {
        get
        {
            yield return Action1;
            yield return Action2;
            yield return Action3;
        }
    }
}

/// <summary>
/// One text line EventAI can speak: a negative <c>creature_ai_texts</c> id, or a positive
/// <c>broadcast_text</c> id (which is what every classic-db text action carries). Chat type
/// (0 say, 1 yell, 2 text emote, 3 boss emote, 4 whisper, 5 boss whisper, 6 zone yell), language,
/// an optional emote and an optional sound.
/// </summary>
public sealed record CreatureAiText(int Id, string Content, byte Type, uint Language, uint Emote)
{
    /// <summary>SoundEntries.dbc id played with the line (0 none).</summary>
    public uint Sound { get; init; }

    /// <summary>The <c>broadcast_text</c> id this line was translated from (<c>creature_ai_texts.broadcast_text_id</c>); 0 none.</summary>
    public uint BroadcastTextId { get; init; }
}

/// <summary>CMaNGOS dbscript_random_templates type0: a signed text id and optional percentage.</summary>
public readonly record struct CreatureAiTextChoice(uint TemplateId, int TextId, uint Chance);

/// <summary>EventAI content, loaded once at startup and read-only afterwards (world thread reads without locks).</summary>
public sealed class CreatureAiContent
{
    public static readonly CreatureAiContent Empty = new([], []);

    private readonly Dictionary<uint, IReadOnlyList<CreatureAiEvent>> _events;
    private readonly IReadOnlyList<CreatureAiEvent> _allEvents;
    private readonly Dictionary<uint, IReadOnlyList<CreatureAiEvent>> _guidEvents;
    private readonly Dictionary<int, CreatureAiText> _texts;
    private readonly Dictionary<uint, CreatureAiSummon> _summons;
    private readonly Dictionary<uint, CreatureAiTextChoice[]> _textTemplates;

    public CreatureAiContent(
        IEnumerable<CreatureAiEvent> events,
        IEnumerable<CreatureAiText> texts,
        BroadcastTextCatalog? broadcastTexts = null,
        IEnumerable<CreatureAiSummon>? summons = null,
        EventAiDialect dialect = EventAiDialect.CMangos,
        IEnumerable<CreatureAiTextChoice>? textTemplates = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(texts);
        CreatureAiEvent[] all = [.. events];
        _allEvents = all;
        EventCount = all.Length;
        _events = all.Where(e => e.CreatureGuid == 0).GroupBy(e => e.CreatureId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CreatureAiEvent>)[.. g.OrderBy(e => e.Id)]);
        _guidEvents = all.Where(e => e.CreatureGuid != 0).GroupBy(e => e.CreatureGuid)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CreatureAiEvent>)[.. g.OrderBy(e => e.Id)]);
        _texts = texts.ToDictionary(t => t.Id);
        _summons = (summons ?? []).ToDictionary(s => s.Id);
        _textTemplates = (textTemplates ?? []).GroupBy(row => row.TemplateId)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.TextId).ToArray());
        foreach (var rows in _textTemplates.Values)
            if (rows.Select(row => row.TextId).Distinct().Count() != rows.Length)
                throw new ArgumentException("Invalid EventAI text template choices.", nameof(textTemplates));
        BroadcastTexts = broadcastTexts ?? BroadcastTextCatalog.Empty;
        Dialect = dialect;
    }

    public int EventCount { get; }

    /// <summary>All loaded rows, including those keyed by spawn guid, for coverage reporting.</summary>
    public IReadOnlyList<CreatureAiEvent> AllEvents => _allEvents;

    public int TextCount => _texts.Count;

    public int SummonCount => _summons.Count;

    /// <summary>The EventAI table dialect the rows use (the numbering of events, actions and flags).</summary>
    public EventAiDialect Dialect { get; }

    /// <summary>Every <c>broadcast_text</c> row (what positive text ids in EventAI actions refer to).</summary>
    public BroadcastTextCatalog BroadcastTexts { get; }

    /// <summary>The relay DB scripts EventAI's START_RELAY_SCRIPT action (53) runs (cmangos <c>dbscripts_on_relay</c>).</summary>
    public RelayScriptCatalog RelayScripts { get; init; } = RelayScriptCatalog.Empty;

    /// <summary>The EventAI rows of a creature entry, in id order.</summary>
    public IReadOnlyList<CreatureAiEvent> GetEvents(uint entry) => _events.GetValueOrDefault(entry) ?? [];

    /// <summary>The EventAI rows keyed to one spawn guid (a negative <c>creature_id</c>), in id order.</summary>
    public IReadOnlyList<CreatureAiEvent> GetGuidEvents(uint spawnGuid) => _guidEvents.GetValueOrDefault(spawnGuid) ?? [];

    /// <summary>A <c>creature_ai_summons</c> location (the SUMMON_ID action's parameter), or null.</summary>
    public CreatureAiSummon? FindSummon(uint id) => _summons.GetValueOrDefault(id);

    /// <summary>Explicit chances first; residual probability selects uniformly from chance-zero rows.</summary>
    public int SelectTemplateText(uint id, float percentRoll, Func<int, int> equalIndex)
    {
        ArgumentNullException.ThrowIfNull(equalIndex);
        if (!float.IsFinite(percentRoll) || percentRoll is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percentRoll));
        if (!_textTemplates.TryGetValue(id, out var rows)) return 0;
        ulong cumulative = 0;
        foreach (var row in rows.Where(row => row.Chance > 0))
        {
            cumulative += row.Chance;
            if (cumulative >= percentRoll) return row.TextId;
        }
        var equal = rows.Where(row => row.Chance == 0).ToArray();
        if (equal.Length == 0) return 0;
        int index = equalIndex(equal.Length);
        if ((uint)index >= equal.Length) throw new ArgumentOutOfRangeException(nameof(equalIndex));
        return equal[index].TextId;
    }

    /// <summary>
    /// The text for an EventAI text id: a negative id looks up <c>creature_ai_texts</c>; a positive id
    /// is a <c>broadcast_text</c> id and is returned in the same shape (male text, chat type, language,
    /// first emote, sound). Female text selection is left to the presentation layer.
    /// </summary>
    public CreatureAiText? FindText(int id)
    {
        if (id < 0)
        {
            return _texts.GetValueOrDefault(id);
        }

        return BroadcastTexts.Find((uint)id) is { } broadcast
            ? new CreatureAiText(id, broadcast.Text, broadcast.ChatType, broadcast.Language, broadcast.Emote)
            {
                Sound = broadcast.SoundId,
                BroadcastTextId = broadcast.Id,
            }
            : null;
    }
}
