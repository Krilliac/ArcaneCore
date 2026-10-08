namespace ArcaneCore.Kernel.WorldData.Creatures;

/// <summary>
/// One <c>broadcast_text</c> row: the localised line a creature says, with the chat type, language,
/// sound and emotes it carries. Only the columns the reference server reads are kept: cmangos-classic
/// <c>ObjectMgr::LoadBroadcastText</c> selects <c>Id, Text, Text1, ChatTypeID, LanguageID,
/// SoundEntriesID1, EmoteID1-3, EmoteDelay1-3</c> (Globals/ObjectMgr.cpp:7786-7821) and
/// <c>DoDisplayText</c> plays <c>soundId1</c> and <c>emoteIds[0]</c> (ObjectMgr.cpp:9978-10040).
/// </summary>
/// <param name="Id">The broadcast text id; EventAI text actions carry it as a positive parameter.</param>
/// <param name="Text">The male / default text (the <c>Text</c> column).</param>
/// <param name="FemaleText">The female text (the <c>Text1</c> column); empty when the same line is used.</param>
/// <param name="ChatType">cmangos <c>ChatType</c>: 0 say, 1 yell, 2 text emote, 3 boss emote, 4 whisper, 5 boss whisper, 6 zone yell, 7 zone emote.</param>
/// <param name="Language">Language.dbc id (0 universal).</param>
/// <param name="SoundId">SoundEntries.dbc id played with the line (0 none).</param>
/// <param name="EmoteIds">The three emote ids (EmoteID1-3); the first is played with the text.</param>
/// <param name="EmoteDelays">The three emote delays in ms (EmoteDelay1-3).</param>
public sealed record BroadcastText(
    uint Id,
    string Text,
    string FemaleText,
    byte ChatType,
    byte Language,
    uint SoundId,
    IReadOnlyList<uint> EmoteIds,
    IReadOnlyList<uint> EmoteDelays)
{
    /// <summary>The emote played with the line (<c>emoteIds[0]</c>, ObjectMgr.cpp:10000).</summary>
    public uint Emote => EmoteIds.Count > 0 ? EmoteIds[0] : 0;
}

/// <summary>
/// One <c>creature_ai_summons</c> row: a summon location EventAI's SUMMON_ID action points at
/// (cmangos-classic mangos.sql <c>creature_ai_summons</c>: id, position, orientation, spawntimesecs). <see cref="LifetimeMs"/> is the
/// spawntimesecs column, which holds milliseconds despite its name: cmangos hands it to SummonCreature as the despawn time
/// (CreatureEventAI.cpp:1018-1019), and the classic-db z2815 values are 10000 to 86400000.
/// </summary>
public sealed record CreatureAiSummon(uint Id, float X, float Y, float Z, float Orientation, uint LifetimeMs);

/// <summary>Every <c>broadcast_text</c> row, loaded once at startup and read-only afterwards.</summary>
public sealed class BroadcastTextCatalog
{
    public static readonly BroadcastTextCatalog Empty = new([]);

    private readonly Dictionary<uint, BroadcastText> _texts;

    public BroadcastTextCatalog(IEnumerable<BroadcastText> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        _texts = texts.ToDictionary(t => t.Id);
    }

    public int Count => _texts.Count;

    public BroadcastText? Find(uint id) => _texts.GetValueOrDefault(id);
}
