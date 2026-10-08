namespace WowWorldMessages.Vanilla;

[Flags]
public enum WeaponFlags : byte {
    WeaponNotAffectedByAnimation = 0,
    SheatheWeaponsAutomatically = 4,
    SheatheWeaponsAutomatically2 = 16,
    UnsheatheWeapons = 32,
}

