namespace WowWorldMessages.Vanilla;

[Flags]
public enum CharacterRaceFlags : byte {
    None = 0,
    NotPlayable = 1,
    BareFeet = 2,
    CanCurrentFormMount = 4,
    Unknown2 = 8,
}

