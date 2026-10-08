namespace WowWorldMessages.Vanilla;

[Flags]
public enum EmoteFlags : byte {
    Talk = 8,
    Question = 16,
    Exclamation = 32,
    Shout = 64,
    Laugh = 128,
}

