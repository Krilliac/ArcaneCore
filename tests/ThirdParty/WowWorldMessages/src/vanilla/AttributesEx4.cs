namespace WowWorldMessages.Vanilla;

[Flags]
public enum AttributesEx4 : uint {
    None = 0,
    NoCastLog = 1,
    ClassTriggerOnlyOnTarget = 2,
    AuraExpiresOffline = 4,
    NoHelpfulThreat = 8,
    NoHarmfulThreat = 16,
    AllowClientTargeting = 32,
    CannotBeStolen = 64,
    AllowCastWhileCasting = 128,
    IgnoreDamageTakenModifiers = 256,
    CombatFeedbackWhenUsable = 512,
    WeaponSpeedCostScaling = 1024,
    NoPartialImmunity = 2048,
}

