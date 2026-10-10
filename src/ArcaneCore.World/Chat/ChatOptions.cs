namespace ArcaneCore.World.Chat;

/// <summary>
/// Realm rules for the chat gates (docs/areas/chat.md), bound from <see cref="SectionName"/> by
/// <see cref="ChatFeature"/>. Every default is retail 1.12 behaviour. Two vmangos extras that
/// the Blizzard realms never had, <see cref="FakeMessagePreventing"/> and <see cref="StrictLinkSeverity"/>,
/// are off by default (mangos-classic defaults them off too) and are opt-in at vmangos' values. These
/// options are read at each message, but they are not part of the <c>.reload config</c> registry
/// (restart to change them).
/// </summary>
public sealed class ChatOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "World:Chat";

    /// <summary>
    /// AddonChannel (vmangos mangosd.conf.dist.in:419, mangos-classic mangosd.conf.dist.in, default 1): when
    /// off, messages in the addon language are dropped (vmangos ChatHandler.cpp HandleChatMessageOpcode
    /// "Disabled addon channel?").
    /// </summary>
    public bool AddonChannel { get; set; } = true;

    /// <summary>
    /// ArcaneCore extension after AscEmu Management/WordFilter.cpp: apply the world <c>chat_word_filter</c> rows (schema 47) to player chat
    /// (censor or block) and to new character and pet names. Off by default: neither retail 1.12 nor vmangos/cMaNGOS filter chat text.
    /// The rows are reloaded with <c>.reload chat_word_filter</c>.
    /// </summary>
    public bool WordFilter { get; set; }

    /// <summary>
    /// ArcaneCore extension: apply the existing mute and flood gates to addon traffic before
    /// offering it to chat features. Uses the same counter, limits and staff exemption as spoken
    /// chat. Off by default: vmangos ChatHandler.cpp:165-236 explicitly exempts LANG_ADDON from
    /// both gates (verified at commit 0e3ff01e76d4758e8a7c3108b2717cc785ed56fa).
    /// </summary>
    public bool AddonMuteAndFloodControl { get; set; }

    /// <summary>
    /// ChatFlood.MessageCount (vmangos mangosd.conf.dist.in:1666, mangos-classic mangosd.conf.dist.in, default 10): how
    /// many messages inside the delay window mute the speaker; 0 disables flood control
    /// (vmangos MasterPlayer::UpdateSpeakTime, MasterPlayerChat.cpp:10).
    /// </summary>
    public uint FloodMessageCount { get; set; } = 10;

    /// <summary>ChatFlood.MessageDelay (vmangos mangosd.conf.dist.in:1667, default 1 second).</summary>
    public uint FloodMessageDelaySeconds { get; set; } = 1;

    /// <summary>ChatFlood.MuteTime (vmangos mangosd.conf.dist.in:1668, default 10 seconds).</summary>
    public uint FloodMuteSeconds { get; set; } = 10;

    /// <summary>
    /// ChatFakeMessagePreventing (vmangos mangosd.conf.dist.in:1663, World.cpp:756, vmangos default on, mangos-classic
    /// World.cpp:681 and this option default off; opt in with true): collapse every run of space, tab, bell and newline in a chat
    /// message into one space (vmangos stripLineInvisibleChars, shared/Util.cpp:134). Addon messages
    /// are not touched.
    /// </summary>
    public bool FakeMessagePreventing { get; set; }

    /// <summary>
    /// ChatStrictLinkChecking.Severity (vmangos mangosd.conf.dist.in:1664, World.cpp:758, vmangos default 2,
    /// mangos-classic World.cpp:683 and this option default 0; opt in with 2): 0 off, 1 only the pipe commands c/H/h/r and
    /// escaped pipes are allowed, 2 they must also come in the order c, H, h, h, r (vmangos
    /// ChatHandler::isValidChatMessage, Chat.cpp:2165-2208). vmangos' level 3 also checks every item,
    /// enchant and spell link against the DBC and item catalogs; those are not available to the chat
    /// handlers, so 3 behaves as 2. A message over 255 bytes or with a bad link is dropped.
    /// </summary>
    public int StrictLinkSeverity { get; set; }

    /// <summary>ChatStrictLinkChecking.Kick (vmangos mangosd.conf.dist.in:1665, default off): disconnect a player whose message fails the link check instead of just dropping it.</summary>
    public bool StrictLinkKick { get; set; }

    /// <summary>
    /// GM.WhisperingTo (vmangos mangosd.conf.dist.in:2531, World.cpp:672, Player.cpp:15132): whether a game
    /// master accepts whispers from plain players at login. 0 = no, 1 = yes. vmangos' default 2 means
    /// "the state saved at the last logout"; persisting it needs a Characters schema change that no
    /// lane has made, so 2 is not offered and the retail first-login state (0) is the default.
    /// </summary>
    public int GmWhisperingTo { get; set; }

    /// <summary>
    /// Path of the developer's own 1.12.1 ChatChannels.dbc (no client data ships with the server). When set, the built-in
    /// channels (ids, flags and every locale's name pattern) come from it, as vmangos loads them (DBCStores.cpp
    /// sChatChannelsStore, GetChannelEntryFor); a file that cannot be read or is not the build 5875 layout stops the start.
    /// Empty (the default): the six transcribed 1.12.1 rows, English names only, so a non-English client's General or
    /// Trade channel is created as a custom channel. Read at start.
    /// </summary>
    public string ChatChannelsDbcPath { get; set; } = string.Empty;

    /// <summary>
    /// Path of the developer's own 1.12.1 EmotesText.dbc. With it (and <see cref="EmotesDbcPath"/>), a text emote plays its
    /// animation and an unknown one is dropped, as vmangos HandleTextEmoteOpcode does with sEmotesTextStore; without it a
    /// text emote is only announced. Both paths or neither; a file that cannot be read stops the start. Read at start.
    /// </summary>
    public string EmotesTextDbcPath { get; set; } = string.Empty;

    /// <summary>Path of the developer's own 1.12.1 Emotes.dbc (whether an emote is a one-shot animation or a state, vmangos Unit::HandleEmote). See <see cref="EmotesTextDbcPath"/>.</summary>
    public string EmotesDbcPath { get; set; } = string.Empty;
}
