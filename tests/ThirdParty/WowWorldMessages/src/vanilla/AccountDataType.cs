namespace WowWorldMessages.Vanilla;

public enum AccountDataType : byte {
    GlobalConfigCache = 0,
    PerCharacterConfigCache = 1,
    GlobalBindingsCache = 2,
    PerCharacterBindingsCache = 3,
    GlobalMacrosCache = 4,
    PerCharacterMacrosCache = 5,
    PerCharacterLayoutCache = 6,
    PerCharacterChatCache = 7,
    NumAccountDataTypes = 8,
}

