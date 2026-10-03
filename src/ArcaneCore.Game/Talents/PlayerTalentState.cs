namespace ArcaneCore.Game.Talents;

/// <summary>
/// The talent state the service keeps beside a <see cref="Entities.Player"/> (a ConditionalWeakTable entry, so the
/// shared Player class is untouched): the respec economy and the spells a talent removal disabled. The used-point
/// count is not stored; it is derived from the spellbook, as vmangos derives m_usedTalentCount while spells load.
/// </summary>
public sealed class PlayerTalentState
{
    /// <summary>vmangos m_resetTalentsMultiplier / m_resetTalentsTime.</summary>
    public RespecState Respec { get; set; }

    /// <summary>Spells hidden by a talent removal until the talent is relearned (vmangos character_spell.disabled).</summary>
    public HashSet<uint> Disabled { get; } = [];
}
