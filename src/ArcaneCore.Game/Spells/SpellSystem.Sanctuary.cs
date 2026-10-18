using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    private readonly ConditionalWeakTable<Player, StrongBox<uint>> _cannotBeDetectedUntil = new();

    /// <summary>Contested guard classification supplied from the world faction catalog.</summary>
    public Func<Unit, bool> IsContestedGuard { get; set; } = static _ => false;

    /// <summary>
    /// sGameEventMgr.IsActiveEvent for spell scripts (Valentine End Check 27657), supplied by the world's game event feature. Without one every
    /// event counts as running, so no script tears an event's auras down.
    /// </summary>
    public Func<ushort, bool> IsGameEventActive { get; set; } = static _ => true;

    internal void SuppressCreatureDetection(Player player, uint durationMs)
        => _cannotBeDetectedUntil.AddOrUpdate(player, new StrongBox<uint>(unchecked(NowMs + durationMs)));

    /// <summary>Vanish's one-second creature detection grace period (vmangos Player::SetCannotBeDetectedTimer).</summary>
    public bool IsCreatureDetectionSuppressed(Player player)
        => _cannotBeDetectedUntil.TryGetValue(player, out StrongBox<uint>? until)
            && unchecked((int)(until.Value - NowMs)) > 0;
}
