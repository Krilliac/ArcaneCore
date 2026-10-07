using ArcaneCore.Game;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;

namespace ArcaneCore.MockClient.Scenarios;

/// <summary>Normal bounded approach to an observed corpse before loot interaction.</summary>
internal static class StartingZoneLootApproach
{
    internal static async Task ApproachAsync(ScenarioConnection connection, StartingZoneCombat.CombatBudget budget,
        ulong corpse, (float X, float Y, float Z) player, uint clientTime, CancellationToken cancellationToken)
    {
        if (corpse == 0)
            throw new MockProtocolException("loot corpse GUID is empty");
        if (!Finite(player.X, player.Y, player.Z))
            throw new MockProtocolException("loot approach player position is non-finite");

        MockPosition? observed = connection.PositionOf(corpse);
        if (observed is null || !Finite(observed.X, observed.Y, observed.Z))
            throw new MockProtocolException("loot corpse position was not observed");

        float distance = Distance(player.X, player.Y, player.Z, observed.X, observed.Y, observed.Z);
        if (distance > 25f)
            throw new MockProtocolException("loot corpse is outside the bounded approach range");
        if (distance <= 3f)
            return;

        int steps = Math.Max(1, (int)Math.Ceiling(distance / 7f));
        float sx = player.X, sy = player.Y, sz = player.Z;
        float orientation = MathF.Atan2(observed.Y - player.Y, observed.X - player.X);
        for (int index = 1; index <= steps; index++)
        {
            float t = index / (float)steps;
            float x = sx + ((observed.X - sx) * t);
            float y = sy + ((observed.Y - sy) * t);
            float z = sz + ((observed.Z - sz) * t);
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            clientTime = checked(clientTime + 1000);
            await connection.SendAsync(WorldOpcode.MsgMoveStop,
                StartingZoneProbe.Movement(x, y, z, orientation, clientTime), cancellationToken).ConfigureAwait(false);
            await budget.DrainQuietAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool Finite(float x, float y, float z)
        => float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z);

    private static float Distance(float ax, float ay, float az, float bx, float by, float bz)
        => MathF.Sqrt(MathF.Pow(ax - bx, 2) + MathF.Pow(ay - by, 2) + MathF.Pow(az - bz, 2));
}
