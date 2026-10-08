namespace WowWorldMessages.Vanilla;

public enum LogoutResult : uint {
    Success = 0,
    FailureInCombat = 1,
    FailureFrozenByGm = 2,
    FailureJumpingOrFalling = 3,
}

