namespace WowWorldMessages.Vanilla;

[Flags]
public enum ChannelFlags : byte {
    None = 0,
    Custom = 1,
    Trade = 4,
    NotLfg = 8,
    General = 16,
    City = 32,
    Lfg = 64,
    Voice = 128,
}

