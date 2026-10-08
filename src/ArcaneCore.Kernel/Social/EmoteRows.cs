namespace ArcaneCore.Kernel.Social;

/// <summary>One row of the 1.12.1 client's EmotesText.dbc (vmangos EmotesTextEntry: m_ID and m_emoteID, the animation it plays).</summary>
public sealed record TextEmoteRow(uint Id, uint EmoteId);

/// <summary>
/// One row of the 1.12.1 client's Emotes.dbc (vmangos EmotesEntry: m_ID, m_EmoteFlags, m_EmoteSpecProc = how the emote is
/// shown, 0 a one-shot command and 1 or 2 a state, m_EmoteSpecProcParam = the stand state).
/// </summary>
public sealed record EmoteRow(uint Id, uint Flags, uint EmoteType, uint StandState);
