using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Combat;

public sealed partial class MapCombat
{
    /// <summary>
    /// Non-lethal damage after <see cref="DealDamage"/> applied it (attacker, victim, damage,
    /// direct, meleeDamage; world thread). The spells area subscribes so weapon hits push back or
    /// interrupt casts and break damage-interruptible auras (docs/integration/spells-persistence.md).
    /// </summary>
    public event Action<Unit, Unit, uint, bool, bool>? DamageDealt;

    /// <summary>
    /// Damage is about to be applied, lethal or not (attacker, victim, damage after the duel clamp; world thread, never for
    /// self damage or zero damage). Unlike <see cref="DamageDealt"/> it also covers the killing blow and fires while the
    /// victim is still alive: vmangos records the victim's damage-taken history (Unit::UnitDamaged) before its lethal
    /// check (Unit.cpp:788-796, 825). The honor area attributes PvP kills from it.
    /// </summary>
    public event Action<Unit, Unit, uint>? DamageTaken;

    /// <summary>
    /// A white swing finished (attacker, victim; world thread): vmangos Unit::AttackerStateUpdate ends with
    /// RemoveAurasWithInterruptFlags(AURA_INTERRUPT_ATTACKING_CANCELS) on the attacker (Unit.cpp:2285). The spells
    /// area subscribes (docs/integration/rogue-aura-interrupt.md).
    /// </summary>
    public event Action<Unit, Unit>? MeleeSwingFinished;
}
