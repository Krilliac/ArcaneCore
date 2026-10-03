namespace ArcaneCore.Game.Spells.Mods;

/// <summary>
/// Spell-modifier options (configuration section <see cref="SectionName"/>). Every default is the retail (vmangos, 1.12.1)
/// behaviour; a switch exists only for a deliberate deviation or a kill switch. Mutable so the host can bind it at startup
/// onto the engine the module installed.
/// </summary>
public sealed class SpellModOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Spells:Mods";

    /// <summary>
    /// Kill switch: false makes aura 107/108 inert again (no mod is registered, every value comes back unchanged), as before
    /// this area existed. Retail is true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Frost Warding (11189, 28332) and Improved Fire Ward (11094, 13043) carry their modifier in code, not in data: a flat
    /// RESIST_MISS_CHANCE with a literal class mask (vmangos SpellAuras.cpp:2117-2155, builds after 1.10.2). Retail is true.
    /// </summary>
    public bool HardcodedWardMods { get; set; } = true;

    /// <summary>
    /// Shadow Trance (17941) and Netherwind Focus (22008) start with one charge whatever the spell data says (vmangos
    /// SpellAuras.cpp:1090-1099). Retail is true.
    /// </summary>
    public bool CustomCharges { get; set; } = true;

    /// <summary>
    /// After a modifier is added or removed, permanent self-cast passives it affects are removed and cast again so an amount
    /// that read a modifier is recomputed (vmangos Aura::ReapplyAffectedPassiveAuras, SpellAuras.cpp:1005-1075). Retail is true.
    /// </summary>
    public bool ReapplyPassives { get; set; } = true;

    /// <summary>
    /// Patch 1.11: a flat CASTING_TIME mod (Nature's Grace) is not spent by a spell an instant-cast percent mod (Nature's
    /// Swiftness) already made instant (vmangos Player::ApplySpellMod, Player.cpp:22444-22453, builds after 1.10.2). Retail is true;
    /// false spends it anyway.
    /// </summary>
    public bool InstantCastKeepsFlatCastTimeCharge { get; set; } = true;

    /// <summary>
    /// Tell the client about every modifier change (SMSG_SET_FLAT_SPELL_MODIFIER / SMSG_SET_PCT_SPELL_MODIFIER, one packet per
    /// mask bit): the client needs them to show modified costs and cast bars (vmangos Player::SendSpellMod). Retail is true.
    /// </summary>
    public bool SendClientModifiers { get; set; } = true;

    /// <summary>
    /// The class-mask overlay file (<c>arcane-content-importer class-masks</c>): 64-bit masks for the modifier auras, because the
    /// spell DBC's EffectItemType is read as 32 bits. Unset means the DBC masks only (a warning at startup counts the modifier
    /// effects that then have no mask at all). A missing or malformed file fails startup.
    /// </summary>
    public string? ClassMaskFile { get; set; }
}
