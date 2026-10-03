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
}
