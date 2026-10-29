using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Inspect hold: the 1.12.1 client exposes another player's equipment only while the inspector is within 10 yards (3D), and the server
/// answers CMSG_INSPECT and MSG_INSPECT_HONOR_STATS only inside the same range. A bot that keeps walking its goal while a real player has
/// its inspection window open leaves that range and its gear reads empty. A lease (<see cref="HoldForInspector"/>, set by
/// <see cref="ManagedPlayerbotFeature"/> when a real player inspects the bot) makes the bot stand still, facing the inspector, for
/// <see cref="PlayerbotOptions.InspectHoldSeconds"/> after the latest request. The hold never drops the bot's intent, so it resumes its
/// walk when the lease ends. Memory only; world thread.
/// </summary>
internal sealed partial class PlayerbotBrain
{
    /// <summary>The hold ends when the inspector is farther than this (yards, 3D): it has walked away, the window is not in use.</summary>
    internal const float InspectHoldReleaseYards = 30f;

    private ObjectGuid _inspectBy;
    private uint _inspectUntilMs;
    private bool _inspectFaced;

    /// <summary>
    /// Hold the bot still until <paramref name="untilMs"/> (world clock, wraparound arithmetic) for <paramref name="inspector"/>. A renewal
    /// from the same inspector only extends the lease; another inspector replaces it. The bot's intent is kept.
    /// </summary>
    internal void HoldForInspector(ObjectGuid inspector, uint untilMs)
    {
        if (!_inspectBy.IsEmpty && _inspectBy == inspector)
        {
            if (unchecked((int)(untilMs - _inspectUntilMs)) > 0) _inspectUntilMs = untilMs;
            return;
        }

        _inspectBy = inspector;
        _inspectUntilMs = untilMs;
        _inspectFaced = false;
    }

    private void ClearInspectHold()
    {
        _inspectBy = default;
        _inspectFaced = false;
    }

    /// <summary>One think of an inspected bot; true when it owns the think (it stands still, facing the inspector).</summary>
    private bool TryHoldForInspector(Player player)
    {
        if (_inspectBy.IsEmpty) return false;
        if (unchecked((int)(_inspectUntilMs - _session.World.NowMs)) <= 0 || player.Combat.IsInCombat)
        {
            ClearInspectHold();
            return false;
        }

        if (player.Map?.FindPlayer(_inspectBy) is not { IsAlive: true } inspector || inspector.Selection != player.Guid)
        {
            ClearInspectHold();
            return false;
        }

        float dx = inspector.X - player.X, dy = inspector.Y - player.Y, dz = inspector.Z - player.Z;
        if ((dx * dx) + (dy * dy) + (dz * dz) > InspectHoldReleaseYards * InspectHoldReleaseYards)
        {
            ClearInspectHold();
            return false;
        }

        _stall.Reset();
        PlayerbotThinkProbe.Phase("inspect hold");
        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0)
        {
            PlayerbotMovementControl.Stop(_session, player);
            return true;
        }

        if (!_inspectFaced && PlayerbotMotion.Face(_session, player, MathF.Atan2(dy, dx))) _inspectFaced = true;
        return true;
    }
}
