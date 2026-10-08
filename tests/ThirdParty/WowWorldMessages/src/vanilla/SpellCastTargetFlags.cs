namespace WowWorldMessages.Vanilla;

[Flags]
public enum SpellCastTargetFlags : ushort {
    Self = 0,
    Unused1 = 1,
    Unit = 2,
    Unused2 = 4,
    Unused3 = 8,
    Item = 16,
    SourceLocation = 32,
    DestLocation = 64,
    ObjectUnk = 128,
    UnitUnk = 256,
    PvpCorpse = 512,
    UnitCorpse = 1024,
    Gameobject = 2048,
    TradeItem = 4096,
    String = 8192,
    Unk1 = 16384,
    Corpse = 32768,
}

