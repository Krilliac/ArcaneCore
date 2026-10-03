using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Social;

/// <summary>Chat anti-flood settings, bound from <see cref="SectionName"/> (restart-only); defaults are vmangos' (mangosd.conf.dist.in:1666-1668).</summary>
public sealed class ChatRestrictionOptions
{
    public const string SectionName = "World:Chat";

    /// <summary>ChatFlood.MessageCount: messages inside the delay window that mute the speaker; 0 disables flood protection.</summary>
    public int FloodMessageCount { get; set; } = 10;

    /// <summary>ChatFlood.MessageDelay (seconds): the minimum spacing for a message not to count.</summary>
    public int FloodMessageDelaySeconds { get; set; } = 1;

    /// <summary>ChatFlood.MuteTime (seconds).</summary>
    public int FloodMuteSeconds { get; set; } = 10;
}

/// <summary>The verdict on one chat message: allowed, or refused because the speaker is muted for the given seconds.</summary>
public readonly record struct ChatDecision(bool Allowed, long MuteRemainingSeconds);

/// <summary>
/// Chat mute and anti-flood (vmangos MasterPlayer::UpdateSpeakTime, MasterPlayerChat.cpp:10-37, and the gates of
/// HandleChatMessageOpcode, ChatHandler.cpp:221-247 and :417-430). Pure: the clock is injected as whole unix
/// seconds (time_t). The mute belongs to the session (vmangos WorldSession::m_muteTime) and is kept here per
/// account; a flood mute is memory only, as in vmangos. World thread.
/// </summary>
public sealed class ChatRestrictionService(ChatRestrictionOptions options, Func<long> unixNow)
{
    private readonly Dictionary<int, long> _muteUntil = [];
    private readonly Dictionary<uint, (long SpeakTime, int Count)> _speak = [];

    /// <summary>Seconds the account is still muted for, or 0.</summary>
    public long MuteRemaining(int accountId)
        => _muteUntil.TryGetValue(accountId, out long until) && until > unixNow() ? until - unixNow() : 0;

    /// <summary>Mute the account until <paramref name="untilUnix"/> (the seam for .mute and a stored mute).</summary>
    public void Mute(int accountId, long untilUnix) => _muteUntil[accountId] = untilUnix;

    /// <summary>End the account's mute.</summary>
    public void Unmute(int accountId) => _muteUntil.Remove(accountId);

    /// <summary>The session ended: its mute and the character's flood counter are gone (vmangos keeps both in memory).</summary>
    public void Forget(int accountId, uint characterId)
    {
        _muteUntil.Remove(accountId);
        _speak.Remove(characterId);
    }

    /// <summary>
    /// One chat message (ChatHandler.cpp:221-247): AFK, DND and addon messages are never gated or counted; a
    /// non-whisper message from a muted speaker is refused before it is counted; every other message is counted
    /// by <see cref="UpdateSpeakTime"/>. A whisper is not refused up front but, once its target is known
    /// (<paramref name="whisperTargetIsPlainPlayer"/>), only staff may be whispered while muted (:417-430).
    /// </summary>
    public ChatDecision Evaluate(
        int accountId, uint characterId, bool isStaff, ChatType type, Language language, bool? whisperTargetIsPlainPlayer = null)
    {
        if (language == Language.Addon || type is ChatType.Afk or ChatType.Dnd)
        {
            return new ChatDecision(true, 0);
        }

        long remaining = MuteRemaining(accountId);
        if (type != ChatType.Whisper && remaining > 0)
        {
            return new ChatDecision(false, remaining);
        }

        UpdateSpeakTime(accountId, characterId, isStaff);

        // A flood mute armed by this very message does not refuse it (vmangos processes the message that arms it).
        if (type == ChatType.Whisper && whisperTargetIsPlainPlayer == true && remaining > 0)
        {
            return new ChatDecision(false, remaining);
        }

        return new ChatDecision(true, 0);
    }

    /// <summary>vmangos MasterPlayer::UpdateSpeakTime (MasterPlayerChat.cpp:10-37), including its early return when flood protection is off.</summary>
    private void UpdateSpeakTime(int accountId, uint characterId, bool isStaff)
    {
        if (isStaff)
        {
            return;
        }

        long current = unixNow();
        (long speakTime, int count) = _speak.GetValueOrDefault(characterId);
        if (speakTime > current)
        {
            if (options.FloodMessageCount <= 0)
            {
                return;
            }

            count++;
            if (count >= options.FloodMessageCount)
            {
                // prevent overwrite mute time, if message send just before mutes set, for example.
                long newMute = current + options.FloodMuteSeconds;
                if (!_muteUntil.TryGetValue(accountId, out long existing) || existing < newMute)
                {
                    _muteUntil[accountId] = newMute;
                }

                count = 0;
            }
        }
        else
        {
            count = 0;
        }

        _speak[characterId] = (current + options.FloodMessageDelaySeconds, count);
    }

    /// <summary>vmangos secsToTimeString(secs, shortText = false, hoursOnly = false) (Util.cpp:197-250), trailing spaces and periods included.</summary>
    public static string FormatDuration(long seconds)
    {
        long secs = seconds % 60;
        long minutes = seconds % 3600 / 60;
        long hours = seconds % 86400 / 3600;
        long days = seconds / 86400;
        var text = new System.Text.StringBuilder();
        if (days != 0)
        {
            text.Append(days).Append(days == 1 ? " Day " : " Days ");
        }

        if (hours != 0)
        {
            text.Append(hours).Append(hours <= 1 ? " Hour " : " Hours ");
        }

        if (minutes != 0)
        {
            text.Append(minutes).Append(minutes == 1 ? " Minute " : " Minutes ");
        }

        if (secs != 0 || (days == 0 && hours == 0 && minutes == 0))
        {
            text.Append(secs).Append(secs <= 1 ? " Second." : " Seconds.");
        }

        return text.ToString();
    }

    /// <summary>mangos_string 705 (LANG_WAIT_BEFORE_SPEAKING): "You must wait %s before speaking again."</summary>
    public static string WaitMessage(long seconds) => $"You must wait {FormatDuration(seconds)} before speaking again.";
}
