namespace WowWorldMessages.Vanilla;

[Flags]
public enum ChannelMemberFlags : byte {
    None = 0,
    Owner = 1,
    Moderator = 4,
    Voiced = 8,
    Muted = 16,
    Custom = 32,
    MicrophoneMute = 64,
}

