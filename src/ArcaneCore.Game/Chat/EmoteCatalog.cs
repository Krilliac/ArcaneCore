using ArcaneCore.Kernel.Social;

namespace ArcaneCore.Game.Chat;

/// <summary>
/// The text emotes (EmotesText.dbc) and the emotes they animate (Emotes.dbc) of the client, as vmangos sEmotesTextStore and
/// sEmotesStore. Built from the developer's own files; without them there is no catalog and a text emote plays no
/// animation (it is still announced).
/// </summary>
public sealed class EmoteCatalog
{
    /// <summary>vmangos SharedDefines.h Emote values that HandleTextEmoteOpcode does not animate.</summary>
    public const uint OneshotNone = 0;

    public const uint StateSleep = 12;

    public const uint StateSit = 13;

    public const uint StateKneel = 68;

    private readonly Dictionary<uint, uint> _textEmotes;
    private readonly Dictionary<uint, EmoteRow> _emotes;

    public EmoteCatalog(IEnumerable<TextEmoteRow> textEmotes, IEnumerable<EmoteRow> emotes)
    {
        ArgumentNullException.ThrowIfNull(textEmotes);
        ArgumentNullException.ThrowIfNull(emotes);
        _textEmotes = textEmotes.ToDictionary(t => t.Id, t => t.EmoteId);
        _emotes = emotes.ToDictionary(e => e.Id);
    }

    public int TextEmoteCount => _textEmotes.Count;

    /// <summary>vmangos sEmotesTextStore.LookupEntry: the emote a text emote plays; false for an unknown text emote.</summary>
    public bool TryGetTextEmote(uint textEmote, out uint emoteId) => _textEmotes.TryGetValue(textEmote, out emoteId);

    /// <summary>The Emotes.dbc row of an emote, or null.</summary>
    public EmoteRow? Emote(uint emoteId) => _emotes.GetValueOrDefault(emoteId);

    /// <summary>vmangos HandleTextEmoteOpcode: sleeping, sitting, kneeling and "none" are not animated (and cancel nothing).</summary>
    public static bool Animates(uint emoteId) => emoteId is not (StateSleep or StateSit or StateKneel or OneshotNone);
}
