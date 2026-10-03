namespace ArcaneCore.World.Chat;

/// <summary>
/// Realm rules for the chat gates (docs/areas/chat.md), bound from <see cref="SectionName"/> by
/// <see cref="ChatFeature"/>. Every default is the vmangos / mangos-classic default, i.e. retail
/// 1.12 behaviour; anything that is not retail is off unless the operator turns it on. These
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
    /// GM.WhisperingTo (vmangos mangosd.conf.dist.in:2531, World.cpp:672, Player.cpp:15132): whether a game
    /// master accepts whispers from plain players at login. 0 = no, 1 = yes. vmangos' default 2 means
    /// "the state saved at the last logout"; persisting it needs a Characters schema change that no
    /// lane has made, so 2 is not offered and the retail first-login state (0) is the default.
    /// </summary>
    public int GmWhisperingTo { get; set; }
}
