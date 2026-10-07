using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Combat;

public sealed partial class MapCombat
{
    /// <summary>
    /// Raised after SELF_RESURRECT (<c>SelfResurrectEffect</c>) has restored the player and removed the body, so the world can persist the
    /// result (a 0% effect completes the death-state transition at health zero, so subscribers must not gate on <c>Unit.IsAlive</c>).
    /// </summary>
    public event Action<Player>? PlayerSelfResurrected;

    internal void NotifySelfResurrected(Player player) => PlayerSelfResurrected?.Invoke(player);
}
