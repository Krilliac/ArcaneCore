using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Spells.Interrupts;

/// <summary>
/// Per-map edge detector for vmangos ENVIRONMENT_FLAG_HIGH_LIQUID (D:\refs\vmangos\src\game\Objects\Player.cpp:
/// 848-855, 20353-20419). Whenever a player's position changed since the last probe, ask the
/// <see cref="ILiquidProbe"/>; on a change of the flag, entering deep liquid removes auras and stops channels
/// carrying UNDER_WATER_CANCELS (0x80), leaving removes those carrying ABOVE_WATER_CANCELS (0x100). On the first
/// sighting the previous state is "not in deep liquid" (vmangos m_environmentFlags starts at 0), so a player first
/// probed in deep water gets the entering edge. The spell system is reached through a getter because
/// the spell feature builds it independently of the map.
/// </summary>
public sealed class LiquidAuraInterruptUpdater(Func<SpellSystem?> spells, ILiquidProbe probe) : IMapUpdater
{
    private readonly Dictionary<Player, State> _states = [];

    private readonly record struct State(float X, float Y, float Z, bool High);

    /// <summary>Raised on every flag change (player, entered). Other systems (breath/fatigue timers) may reuse it.</summary>
    public event Action<Player, bool>? HighLiquidChanged;

    public void Update(Map map, uint diffMs)
    {
        foreach (Player player in map.Players)
        {
            bool hadState = _states.TryGetValue(player, out State last);
            if (hadState && last.X == player.X && last.Y == player.Y && last.Z == player.Z)
            {
                continue;
            }

            bool high = probe.IsHighLiquid(map, player);
            _states[player] = new State(player.X, player.Y, player.Z, high);
            if (last.High != high)
            {
                OnChanged(player, high);
            }
        }
    }

    public void OnPlayerRemoved(Map map, Player player) => _states.Remove(player);

    private void OnChanged(Player player, bool entered)
    {
        if (spells() is { } system)
        {
            uint mask = entered ? AuraInterruptMask.UnderWater : AuraInterruptMask.AboveWater;
            system.InterruptChannelWithFlags(player, mask);
            system.RemoveAurasWithInterruptFlags(player, mask);
        }

        HighLiquidChanged?.Invoke(player, entered);
    }
}
