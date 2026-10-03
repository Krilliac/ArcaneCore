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
/// with four parameters, a chance, flags, an inverse phase mask and up to three actions.
/// </summary>
public sealed record CreatureAiEvent
{
    public required uint Id { get; init; }

    public required uint CreatureId { get; init; }

    public required byte EventType { get; init; }

    /// <summary>Bit N set: the event does not run in phase N (cmangos event_inverse_phase_mask).</summary>
    public uint InversePhaseMask { get; init; }

    /// <summary>Percent chance to run when triggered (cmangos event_chance; 0 never runs).</summary>
    public byte Chance { get; init; } = 100;

    /// <summary>cmangos EFLAG_* (0x01 repeatable, 0x20 random action).</summary>
    public byte Flags { get; init; }

    public int Param1 { get; init; }

    public int Param2 { get; init; }

    public int Param3 { get; init; }

    public int Param4 { get; init; }

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
/// One <c>creature_ai_texts</c> row (cmangos-classic): a negative id, the text, the chat type
/// (0 say, 1 yell, 2 text emote, 3 boss emote, 4 whisper, 5 boss whisper, 6 zone yell), the
/// language and an optional emote played with it.
/// </summary>
public sealed record CreatureAiText(int Id, string Content, byte Type, uint Language, uint Emote);

/// <summary>EventAI content, loaded once at startup and read-only afterwards (world thread reads without locks).</summary>
public sealed class CreatureAiContent
{
    public static readonly CreatureAiContent Empty = new([], []);

    private readonly Dictionary<uint, IReadOnlyList<CreatureAiEvent>> _events;
    private readonly Dictionary<int, CreatureAiText> _texts;

    public CreatureAiContent(IEnumerable<CreatureAiEvent> events, IEnumerable<CreatureAiText> texts)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(texts);
        CreatureAiEvent[] all = [.. events];
        EventCount = all.Length;
        _events = all.GroupBy(e => e.CreatureId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CreatureAiEvent>)[.. g.OrderBy(e => e.Id)]);
        _texts = texts.ToDictionary(t => t.Id);
    }

    public int EventCount { get; }

    public int TextCount => _texts.Count;

    /// <summary>The EventAI rows of a creature entry, in id order.</summary>
    public IReadOnlyList<CreatureAiEvent> GetEvents(uint entry) => _events.GetValueOrDefault(entry) ?? [];

    public CreatureAiText? FindText(int id) => _texts.GetValueOrDefault(id);
}
