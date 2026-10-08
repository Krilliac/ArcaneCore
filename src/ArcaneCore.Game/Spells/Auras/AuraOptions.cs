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
    /// The damage break skips every aura whose spell has procFlags (vmangos <c>checkProcFlags</c>, Unit.cpp:735-745, 895-906): such auras end through
    /// the proc engine instead (their charges, the root, pacify-silence and fear break chances, docs/areas/procs.md). Default true, the vmangos
    /// rule, now that the proc engine exists. False removes procFlags auras on the damage break too (the engine's behaviour before procs existed).
    /// Checked against the build 5875 Spell.dbc: Polymorph, Sap, Gouge, Freezing Trap and druid Prowl carry no procFlags and break on the interrupt
    /// path either way; Wyvern Sting (TAKEN_ANY_DAMAGE) and the hunter pet's Prowl (melee flags, one charge) are the damage-cancel auras that do.
    /// </summary>
    public bool ProcEngineBreaksDamageAuras { get; set; } = true;

    /// <summary>
    /// Opt-in deviation: an aura that breaks on damage (AuraInterruptFlags DAMAGE) and procs on TAKEN_ANY_DAMAGE (Wyvern Sting's sleep) ends when its
    /// damage proc fires, as the spell's tooltip says ("Any damage will cancel the effect"). Default false, the vmangos behaviour: MOD_STUN has no proc
    /// handler (HandleNULLProc, UnitAuraProcHandler.cpp:51) and the proc spends only real charges (Unit.cpp:4330-4335), so an uncharged aura such as
    /// Wyvern Sting keeps going through the damage.
    /// </summary>
    public bool DamageProcCancelsAura { get; set; }
}
