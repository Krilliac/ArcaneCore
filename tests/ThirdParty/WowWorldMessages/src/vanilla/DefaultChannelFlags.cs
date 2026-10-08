namespace WowWorldMessages.Vanilla;

[Flags]
public enum DefaultChannelFlags : uint {
    None = 0,
    Initial = 1,
    ZoneDependency = 2,
    Global = 4,
    Trade = 8,
    CityOnly = 16,
    CityOnly2 = 32,
    Defence = 65536,
    Unselected = 262144,
}

