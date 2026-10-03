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
}
