namespace ArcaneCore.Kernel.Social;

/// <summary>
/// One row of the 1.12.1 client's ChatChannels.dbc (build 5875: <c>m_ID, m_flags, m_factionGroup, m_name_lang[8] + mask,
/// m_shortcut_lang[8] + mask</c>, vmangos DBCStructure.h ChatChannelsEntry and DBCfmt.h "nixssssssssxxxxxxxxxx").
/// <see cref="Patterns"/> are the eight locale name patterns as stored, a <c>%s</c> marking the zone name; an unused locale
/// is an empty string.
/// </summary>
public sealed record ChatChannelRow(uint Id, uint Flags, uint FactionGroup, IReadOnlyList<string> Patterns);
