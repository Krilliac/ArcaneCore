using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    public event Action<Unit, byte>? VisibleAuraSlotChanged;
}
