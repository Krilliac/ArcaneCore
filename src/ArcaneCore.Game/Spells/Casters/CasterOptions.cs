namespace ArcaneCore.Game.Spells.Casters;

/// <summary>
/// Settings of the mage / priest / warlock rules, bound from the <c>Spells:Casters</c> configuration section.
/// Every default is the retail 1.12.1 behaviour; a switch exists only for a documented deviation.
/// </summary>
public sealed class CasterOptions
{
    public const string SectionName = "Spells:Casters";

    /// <summary>Spell power (+damage / +healing) options.</summary>
    public CasterBonusOptions Bonus { get; } = new();
}

/// <summary>Spell power options (<c>Spells:Casters:Bonus</c>).</summary>
public sealed class CasterBonusOptions
{
    /// <summary>
    /// Retail (true): +damage, +healing, Amplify/Dampen Magic and healing-taken auras change spell damage and
    /// healing (vmangos SpellCaster.cpp:1457-1700, Unit.cpp:5175-5385). False keeps the raw spell data amounts
    /// and exists only to debug against base points.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
