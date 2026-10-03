using ArcaneCore.Protocol;

namespace ArcaneCore.World.Handlers;

/// <summary>
/// Validates a client movement block before it is stored or relayed to observers.
/// <para>
/// Retail rules, ported from vmangos <c>WorldSession::VerifyMovementInfo</c>
/// (MovementHandler.cpp:1042-1061) and <c>MaNGOS::IsValidMapCoord</c> (GridDefines.h:175-203):
/// x/y finite and |c| &lt;= MAP_HALFSIZE - 0.5, |z| &lt;= 400000, orientation finite and
/// |o| &lt;= 4 pi; on a transport additionally |tx|, |ty| &lt;= 250 and |tz| &lt;= 100, and the
/// position plus transport offset must itself be a valid map coordinate.
/// </para>
/// <para>
/// Hardening that retail does not do (it only checks the fields above): every other float the
/// block carries (pitch, jump speeds, spline elevation, transport orientation) must be finite,
/// because they are stored and re-broadcast to every observer and a NaN or Infinity would be
/// relayed verbatim into other clients.
/// </para>
/// </summary>
public static class MovementValidator
{
    // MAP_HALFSIZE = (SIZE_OF_GRIDS * MAX_NUMBER_OF_GRIDS) / 2 with SIZE_OF_GRIDS = 533.33333f and
    // MAX_NUMBER_OF_GRIDS = 64 (GridDefines.h:38-59); the bound is MAP_HALFSIZE - 0.5.
    private static readonly double MapCoordLimit = (double)533.33333f * 64 / 2 - 0.5;

    private const double MaxZ = 400000;
    private static readonly double MaxOrientation = 4 * Math.PI;

    /// <summary>True when the movement block may be applied and relayed.</summary>
    public static bool IsValid(in MovementInfo m)
    {
        if (!IsValidMapCoord(m.X, m.Y, m.Z, m.Orientation))
        {
            return false;
        }

        if (m.HasFlag(MovementFlags.OnTransport))
        {
            if (!float.IsFinite(m.TransportX) || !float.IsFinite(m.TransportY) || !float.IsFinite(m.TransportZ)
                || !float.IsFinite(m.TransportOrientation))
            {
                return false;
            }

            // transports are size limited (vmangos MovementHandler.cpp:1048-1050)
            if (Math.Abs(m.TransportX) > 250 || Math.Abs(m.TransportY) > 250 || Math.Abs(m.TransportZ) > 100)
            {
                return false;
            }

            if (!IsValidMapCoord(
                    m.X + m.TransportX, m.Y + m.TransportY, m.Z + m.TransportZ, m.Orientation + m.TransportOrientation))
            {
                return false;
            }
        }

        // hardening: nothing non-finite may be stored or relayed
        return float.IsFinite(m.Pitch)
            && float.IsFinite(m.JumpZSpeed) && float.IsFinite(m.JumpCosAngle)
            && float.IsFinite(m.JumpSinAngle) && float.IsFinite(m.JumpXySpeed)
            && float.IsFinite(m.SplineElevation);
    }

    private static bool IsValidMapCoord(float x, float y, float z, float o)
        => float.IsFinite(x) && Math.Abs(x) <= MapCoordLimit
        && float.IsFinite(y) && Math.Abs(y) <= MapCoordLimit
        && Math.Abs(z) <= MaxZ
        && float.IsFinite(o) && Math.Abs(o) <= MaxOrientation;
}
