using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    private readonly ConditionalWeakTable<Player, StrongBox<uint>> _cannotBeDetectedUntil = new();

    /// <summary>Contested guard classification supplied from the world faction catalog.</summary>
    public Func<Unit, bool> IsContestedGuard { get; set; } = static _ => false;

    internal void SuppressCreatureDetection(Player player, uint durationMs)
        => _cannotBeDetectedUntil.AddOrUpdate(player, new StrongBox<uint>(unchecked(NowMs + durationMs)));

    /// <summary>Vanish's one-second creature detection grace period (vmangos Player::SetCannotBeDetectedTimer).</summary>
    public bool IsCreatureDetectionSuppressed(Player player)
        => _cannotBeDetectedUntil.TryGetValue(player, out StrongBox<uint>? until)
            && unchecked((int)(until.Value - NowMs)) > 0;
}
