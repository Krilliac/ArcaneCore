using ArcaneCore.Game.Social;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>
/// The chat mute and anti-flood rules (vmangos MasterPlayerChat.cpp:10-37 UpdateSpeakTime, ChatHandler.cpp:221-247 and
/// :417-430, Util.cpp:197-250 secsToTimeString). The clock is whole unix seconds, like time_t.
/// </summary>
public sealed class ChatRestrictionServiceTests
{
    private const int Account = 7;
    private const uint Character = 70;
    private long _now = 1_000_000;

    private ChatRestrictionService Service(ChatRestrictionOptions? options = null) => new(options ?? new ChatRestrictionOptions(), () => _now);

    private static ChatDecision Say(ChatRestrictionService s, bool staff = false)
        => s.Evaluate(Account, Character, staff, ChatType.Say, Language.Common);

    [Fact] // MasterPlayerChat.cpp:16-31: the first message only arms speakTime; the mute fires on message N+1
    public void WithTheDefaultTenMessages_TheEleventhWithinTheWindowArmsTheMute_AndTheTwelfthIsBlocked()
    {
        ChatRestrictionService s = Service();

        for (int i = 1; i <= 11; i++)
        {
            Assert.True(Say(s).Allowed, $"message {i}");
        }

        ChatDecision twelfth = Say(s);
        Assert.False(twelfth.Allowed);
        Assert.Equal(10, twelfth.MuteRemainingSeconds); // ChatFlood.MuteTime
    }

    [Fact] // a message after the delay resets the counter
    public void ASlowSpeaker_IsNeverMuted()
    {
        ChatRestrictionService s = Service();

        for (int i = 0; i < 100; i++)
        {
            Assert.True(Say(s).Allowed);
            _now += 2; // beyond ChatFlood.MessageDelay (1 s)
        }
    }

    [Fact] // the window slides: the delay is measured from the last message
    public void ABurstThatPausesBeyondTheDelay_ResetsTheCount()
    {
        ChatRestrictionService s = Service();
        for (int i = 0; i < 9; i++)
        {
            Assert.True(Say(s).Allowed);
        }

        _now += 2;

        for (int i = 0; i < 9; i++)
        {
            Assert.True(Say(s).Allowed);
        }

        Assert.True(Say(s).Allowed);
    }

    [Fact] // :19-20 "if (!max_count) return"
    public void AMessageCountOfZero_DisablesFloodProtection()
    {
        ChatRestrictionService s = Service(new ChatRestrictionOptions { FloodMessageCount = 0 });

        for (int i = 0; i < 500; i++)
        {
            Assert.True(Say(s).Allowed);
        }
    }

    [Fact] // :11-13 staff are exempt from flood control, not from a mute
    public void Staff_AreNeverFloodMuted_ButAnExplicitMuteStillApplies()
    {
        ChatRestrictionService s = Service();
        for (int i = 0; i < 100; i++)
        {
            Assert.True(Say(s, staff: true).Allowed);
        }

        s.Mute(Account, _now + 60);

        Assert.False(Say(s, staff: true).Allowed);
    }

    [Fact] // :27-29 "prevent overwrite mute time"
    public void AFloodMute_NeverShortensALongerMute()
    {
        ChatRestrictionService s = Service();
        s.Mute(Account, _now + 300);
        // Muted messages are not counted, so provoke the flood path through whispers (counted, never blocked up front).
        for (int i = 0; i < 12; i++)
        {
            s.Evaluate(Account, Character, false, ChatType.Whisper, Language.Common, whisperTargetIsPlainPlayer: null);
        }

        Assert.Equal(300, s.MuteRemaining(Account));
    }

    [Fact] // ChatHandler.cpp:236-241: a muted message is refused before UpdateSpeakTime, so it does not count
    public void MutedMessages_AreRefusedWithTheRemainingTime_AndAreNotCounted()
    {
        ChatRestrictionService s = Service();
        s.Mute(Account, _now + 5);

        for (int i = 0; i < 50; i++)
        {
            Assert.False(Say(s).Allowed);
        }

        _now += 5;
        Assert.True(Say(s).Allowed); // the mute ended; 50 refused messages left no flood count behind
        for (int i = 0; i < 9; i++)
        {
            Assert.True(Say(s).Allowed);
        }
    }

    [Theory] // :221-226 AFK and DND updates are neither gated nor counted; addon data skips flood control (:156-158)
    [InlineData(ChatType.Afk, Language.Universal)]
    [InlineData(ChatType.Dnd, Language.Universal)]
    [InlineData(ChatType.Guild, Language.Addon)]
    [InlineData(ChatType.Party, Language.Addon)]
    public void AfkDndAndAddonMessages_AreNeverGatedOrCounted(ChatType type, Language language)
    {
        ChatRestrictionService s = Service();
        s.Mute(Account, _now + 600);

        for (int i = 0; i < 100; i++)
        {
            Assert.True(s.Evaluate(Account, Character, false, type, language).Allowed);
        }

        s.Unmute(Account);
        for (int i = 0; i < 10; i++)
        {
            Assert.True(Say(s).Allowed); // nothing was counted
        }
    }

    [Fact] // :417-430 a muted player may only whisper staff
    public void AMutedWhisper_IsBlockedToAPlainPlayer_ButAllowedToStaff()
    {
        ChatRestrictionService s = Service();
        s.Mute(Account, _now + 60);

        Assert.False(s.Evaluate(Account, Character, false, ChatType.Whisper, Language.Common, whisperTargetIsPlainPlayer: true).Allowed);
        Assert.True(s.Evaluate(Account, Character, false, ChatType.Whisper, Language.Common, whisperTargetIsPlainPlayer: false).Allowed);
        Assert.True(s.Evaluate(Account, Character, false, ChatType.Whisper, Language.Common, whisperTargetIsPlainPlayer: null).Allowed); // unknown target: the core answers "not found"
    }

    [Fact] // :221-247 whispers are counted by UpdateSpeakTime like every other message; the whisper that arms the mute is itself refused (:235 runs before the :417-428 check)
    public void Whispers_CountTowardFlood_AndTheArmingWhisperToAPlayerIsRefused()
    {
        ChatRestrictionService s = Service();

        for (int i = 0; i < 10; i++)
        {
            Assert.True(s.Evaluate(Account, Character, false, ChatType.Whisper, Language.Common, whisperTargetIsPlainPlayer: true).Allowed);
        }

        ChatDecision arming = s.Evaluate(Account, Character, false, ChatType.Whisper, Language.Common, whisperTargetIsPlainPlayer: true);
        Assert.False(arming.Allowed);
        Assert.Equal(10, arming.MuteRemainingSeconds);
        Assert.False(Say(s).Allowed);
    }

    [Fact] // the same arming whisper to staff goes through (:419 only gates SEC_PLAYER targets)
    public void TheArmingWhisper_ToStaff_IsAllowed()
    {
        ChatRestrictionService s = Service();

        for (int i = 0; i < 11; i++)
        {
            Assert.True(s.Evaluate(Account, Character, false, ChatType.Whisper, Language.Common, whisperTargetIsPlainPlayer: false).Allowed);
        }

        Assert.False(Say(s).Allowed);
    }

    [Fact]
    public void Forget_DropsTheSessionsMuteAndFloodCount()
    {
        ChatRestrictionService s = Service();
        for (int i = 0; i < 11; i++)
        {
            Say(s);
        }

        Assert.False(Say(s).Allowed);

        s.Forget(Account, Character);

        Assert.True(Say(s).Allowed);
    }

    [Theory] // Util.cpp:197-250, with its trailing spaces and periods, verbatim
    [InlineData(0, "0 Second.")]
    [InlineData(1, "1 Second.")]
    [InlineData(2, "2 Seconds.")]
    [InlineData(59, "59 Seconds.")]
    [InlineData(60, "1 Minute ")]
    [InlineData(61, "1 Minute 1 Second.")]
    [InlineData(599, "9 Minutes 59 Seconds.")]
    [InlineData(3600, "1 Hour ")]
    [InlineData(7200, "2 Hours ")]
    [InlineData(86400, "1 Day ")]
    [InlineData(90061, "1 Day 1 Hour 1 Minute 1 Second.")]
    [InlineData(172800 + 7200, "2 Days 2 Hours ")]
    public void TheDurationIsFormattedLikeSecsToTimeString(long seconds, string expected)
        => Assert.Equal(expected, ChatRestrictionService.FormatDuration(seconds));

    [Fact] // mangos_string 705 (LANG_WAIT_BEFORE_SPEAKING)
    public void TheWaitMessage_IsTheClassicString()
        => Assert.Equal("You must wait 9 Minutes 59 Seconds. before speaking again.", ChatRestrictionService.WaitMessage(599));

    [Fact]
    public void TheOptionsDefaultToTheVmangosValues()
    {
        var options = new ChatRestrictionOptions();

        Assert.Equal((10, 1, 10), (options.FloodMessageCount, options.FloodMessageDelaySeconds, options.FloodMuteSeconds)); // mangosd.conf.dist.in:1666-1668
        Assert.Equal("World:Chat", ChatRestrictionOptions.SectionName);
    }
}
