using System.Runtime.InteropServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.Game.Spells.Interrupts;

/// <summary>
/// Per-map edge detector for vmangos ENVIRONMENT_FLAG_HIGH_LIQUID (vmangos@0e3ff01 src/game/Objects/Player.cpp:
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

    private record struct State(float X, float Y, float Z, bool High);

    /// <summary>Raised on every flag change (player, entered). Other systems (breath/fatigue timers) may reuse it.</summary>
    public event Action<Player, bool>? HighLiquidChanged;

    public void Update(Map map, uint diffMs)
    {
        if (map is null || map.PlayerCount == 0)
        {
            return;
        }

        Player[] players = map.RentPlayerSnapshot(out int playerCount);
        try
        {
            for (int i = 0; i < playerCount; i++)
            {
                Player player = players[i];
                try
                {
                    if (player is null || !player.IsInWorld || player.Map is null || !ReferenceEquals(player.Map, map))
                    {
                        continue;
                    }

                    ref State state = ref CollectionsMarshal.GetValueRefOrAddDefault(_states, player, out bool hadState);
                    if (hadState && state.X == player.X && state.Y == player.Y && state.Z == player.Z)
                    {
                        continue;
                    }

                    bool previousHigh = state.High;
                    bool high = probe.IsHighLiquid(map, player);
                    state = new State(player.X, player.Y, player.Z, high);
                    if (previousHigh != high)
                    {
                        OnChanged(player, high);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("map.liquid-interrupt", ex);
                }
            }
        }
        finally
        {
            Map.ReturnPlayerSnapshot(players);
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
