namespace WowWorldMessages.Vanilla;

public enum ServerMessageType : uint {
    ShutdownTime = 1,
    RestartTime = 2,
    Custom = 3,
    ShutdownCancelled = 4,
    RestartCancelled = 5,
}

