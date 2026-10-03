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
}
