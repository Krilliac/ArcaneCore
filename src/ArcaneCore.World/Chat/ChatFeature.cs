using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Features;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Chat;

/// <summary>
/// Per-speaker chat state and the gates that read it (docs/areas/chat.md): the flood mute
/// (vmangos MasterPlayer::UpdateSpeakTime / Player::CanSpeak) and the whisper acceptance of game
/// masters (vmangos MasterPlayer::AcceptsWhispersFrom). State is in memory only, as vmangos' flood
/// state is: it ends with the session, so a relog clears a flood mute; account mutes come from
/// <see cref="IChatMuteSource"/>s. World thread only; state is keyed by the <see cref="Player"/>
/// object and goes away with it.
/// </summary>
public sealed class ChatFeature : IWorldFeature
{
    private readonly IConfiguration? _configuration;
    private readonly IChatMuteSource[] _muteSources;
    private readonly TimeProvider _clock;
    private readonly ConditionalWeakTable<Player, ChatState> _states = [];

    public ChatFeature(IConfiguration? configuration = null, IEnumerable<IChatMuteSource>? muteSources = null, TimeProvider? timeProvider = null)
    {
        _configuration = configuration;
        _muteSources = muteSources is null ? [] : [.. muteSources];
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The chat rules.</summary>
    public ChatOptions Options { get; } = new();

    /// <summary>Whole seconds since the Unix epoch (vmangos time(nullptr)).</summary>
    public long NowUnixSeconds => _clock.GetUtcNow().ToUnixTimeSeconds();

    public void Attach(WorldRuntime world) => _configuration?.GetSection(ChatOptions.SectionName).Bind(Options);

    /// <summary>The unix time until which <paramref name="player"/> cannot speak (vmangos WorldSession::m_muteTime): the latest of the flood mute and every source.</summary>
    public long MutedUntil(Player player)
    {
        long until = State(player).MuteUntil;
        foreach (IChatMuteSource source in _muteSources)
        {
            until = Math.Max(until, source.MutedUntilUnixSeconds(player));
        }

        return until;
    }

    /// <summary>vmangos Player::CanSpeak: <c>m_muteTime &lt;= time(nullptr)</c>.</summary>
    public bool CanSpeak(Player player) => MutedUntil(player) <= NowUnixSeconds;

    /// <summary>
    /// The text of the notification a muted speaker gets (mangos_string 705 with vmangos
    /// <c>secsToTimeString</c>), or null when the player can speak.
    /// </summary>
    public string? MuteNotice(Player player)
    {
        long now = NowUnixSeconds;
        long until = MutedUntil(player);
        return until <= now ? null : $"You must wait {ChatText.SecsToTimeString(until - now)} before speaking again.";
    }

    /// <summary>
    /// Mute <paramref name="player"/> for at least until <paramref name="untilUnixSeconds"/> (never
    /// shortens an existing mute, as vmangos UpdateSpeakTime "prevent overwrite mute time").
    /// </summary>
    public void MuteUntil(Player player, long untilUnixSeconds)
    {
        ChatState state = State(player);
        state.MuteUntil = Math.Max(state.MuteUntil, untilUnixSeconds);
    }

    /// <summary>
    /// vmangos MasterPlayer::UpdateSpeakTime (MasterPlayerChat.cpp:10-35), the anti-flood counter:
    /// staff are exempt; a message inside the delay window counts, and the count reaching
    /// <see cref="ChatOptions.FloodMessageCount"/> mutes the speaker for
    /// <see cref="ChatOptions.FloodMuteSeconds"/> (the message that trips the mute is still delivered,
    /// because the mute check precedes this call in the handler).
    /// </summary>
    public void UpdateSpeakTime(Player player)
    {
        if (player.Security > AccountSecurity.Player)
        {
            return;
        }

        ChatState state = State(player);
        long current = NowUnixSeconds;
        if (state.SpeakTime > current)
        {
            uint max = Options.FloodMessageCount;
            if (max == 0)
            {
                return;
            }

            state.SpeakCount++;
            if (state.SpeakCount >= max)
            {
                MuteUntil(player, current + Options.FloodMuteSeconds);
                state.SpeakCount = 0;
            }
        }
        else
        {
            state.SpeakCount = 0;
        }

        state.SpeakTime = current + Options.FloodMessageDelaySeconds;
    }

    /// <summary>
    /// vmangos MasterPlayer::IsAcceptWhispers: plain players always accept ("players always
    /// accept", Player.cpp:134); a staff account starts as <see cref="ChatOptions.GmWhisperingTo"/> says.
    /// </summary>
    public bool AcceptsWhispers(Player player) => State(player).AcceptsWhispers;

    /// <summary>vmangos Player::SetAcceptWhispers (<c>.whispers on|off</c>).</summary>
    public void SetAcceptWhispers(Player player, bool on) => State(player).AcceptsWhispers = on;

    /// <summary>
    /// vmangos MasterPlayer::AcceptsWhispersFrom (MasterPlayer.h:99): the receiver accepts whispers
    /// in general, or has whispered <paramref name="whisperer"/> first.
    /// </summary>
    public bool AcceptsWhispersFrom(Player receiver, ObjectGuid whisperer)
    {
        ChatState state = State(receiver);
        return state.AcceptsWhispers || (state.AllowedWhisperers?.Contains(whisperer) ?? false);
    }

    /// <summary>vmangos MasterPlayer::Whisper (end): a sender that does not accept whispers lets the receiver whisper back.</summary>
    public void NoteWhisperSent(Player sender, Player receiver)
    {
        ChatState state = State(sender);
        if (!state.AcceptsWhispers)
        {
            (state.AllowedWhisperers ??= []).Add(receiver.Guid);
        }
    }

    /// <summary>vmangos MasterPlayer::ClearAllowedWhisperers (<c>.whispers off</c>).</summary>
    public void ClearAllowedWhisperers(Player player) => State(player).AllowedWhisperers?.Clear();

    private ChatState State(Player player) => _states.GetValue(player, p => new ChatState(p.Security == AccountSecurity.Player || Options.GmWhisperingTo == 1));

    private sealed class ChatState(bool acceptsWhispers)
    {
        internal long MuteUntil;
        internal long SpeakTime;
        internal uint SpeakCount;
        internal bool AcceptsWhispers = acceptsWhispers;
        internal HashSet<ObjectGuid>? AllowedWhisperers;
    }
}
