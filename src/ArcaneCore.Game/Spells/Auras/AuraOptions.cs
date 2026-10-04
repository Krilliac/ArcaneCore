namespace ArcaneCore.Game.Spells;

/// <summary>
/// The <c>Auras</c> configuration section (docs/areas/aura-engine.md). Every default is the retail 1.12.1 behaviour of
/// vmangos; a switch exists only where this server deliberately offers another behaviour.
/// </summary>
public sealed class AuraOptions
{
    public const string SectionName = "Auras";

    /// <summary>
    /// Deliver every missed periodic tick in one update (the engine's behaviour before the periodic timing slice). Retail
    /// (vmangos Aura::Update, SpellAuras.cpp:553-572) delivers at most one tick per update, so a lag spike drops ticks
    /// instead of bursting them. Default false.
    /// </summary>
    public bool PeriodicCatchUp { get; set; }

    /// <summary>
    /// Harmful auras keep counting down while their owner is offline (the cmangos rule this engine used before). Retail
    /// (vmangos Player::LoadAura, Player.cpp:15363-15372) subtracts the offline time only from spells with
    /// SPELL_ATTR_EX4_AURA_EXPIRES_OFFLINE (Deserter), so every other aura resumes with the time it had at logout. Default false.
    /// </summary>
    public bool HarmfulAurasExpireOffline { get; set; }

    /// <summary>
    /// Set once a proc engine exists that breaks procFlags crowd control on damage (vmangos Unit.cpp:688-692); the damage break then skips
    /// every aura whose spell has procFlags (vmangos <c>checkProcFlags</c>, Unit.cpp:735-745, 895-906). Default false: this engine has no proc
    /// engine, so the damage break itself keeps removing procFlags auras, otherwise Polymorph, Sap, Gouge and Freezing Trap could not be
    /// broken. In that mode <see cref="SpellSystem.DamageBreakExemptSpells"/> (Wyvern Sting and Prowl, which vmangos leaves alone because
    /// of their procFlags) are still spared, so they do not break on the hit their own effect causes.
    /// </summary>
    public bool ProcEngineBreaksDamageAuras { get; set; }
}
