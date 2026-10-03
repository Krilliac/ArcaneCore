using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using Xunit;

namespace ArcaneCore.World.Tests.Security;

/// <summary>
/// Bounds from vmangos VerifyMovementInfo (MovementHandler.cpp:1042-1061) and IsValidMapCoord
/// (GridDefines.h:175-203): MAP_HALFSIZE - 0.5 = 17066.166, |z| &lt;= 400000, |o| &lt;= 4 pi,
/// transport |x|,|y| &lt;= 250 and |z| &lt;= 100.
/// </summary>
public sealed class MovementValidatorTests
{
    private static MovementInfo Ok() => new() { X = 100, Y = -200, Z = 30, Orientation = 1 };

    [Fact]
    public void OrdinaryMovement_IsValid() => Assert.True(MovementValidator.IsValid(Ok()));

    [Theory]
    [InlineData(17066.1f, true)]
    [InlineData(17066.2f, false)]
    [InlineData(-17066.1f, true)]
    [InlineData(-17066.2f, false)]
    [InlineData(float.NaN, false)]
    [InlineData(float.PositiveInfinity, false)]
    public void PositionX_IsBoundedByMapHalfSize(float x, bool valid)
    {
        MovementInfo m = Ok();
        m.X = x;
        Assert.Equal(valid, MovementValidator.IsValid(m));
        m = Ok();
        m.Y = x;
        Assert.Equal(valid, MovementValidator.IsValid(m));
    }

    [Theory]
    [InlineData(400000f, true)]
    [InlineData(400001f, false)]
    [InlineData(-400001f, false)]
    public void PositionZ_IsBounded(float z, bool valid)
    {
        MovementInfo m = Ok();
        m.Z = z;
        Assert.Equal(valid, MovementValidator.IsValid(m));
    }

    [Theory]
    [InlineData(12.5f, true)]    // 4 pi = 12.566
    [InlineData(12.6f, false)]
    [InlineData(-12.6f, false)]
    [InlineData(13f, false)]
    public void Orientation_IsBoundedByFourPi(float o, bool valid)
    {
        MovementInfo m = Ok();
        m.Orientation = o;
        Assert.Equal(valid, MovementValidator.IsValid(m));
    }

    [Theory]
    [InlineData(250f, 250f, 100f, true)]
    [InlineData(250.1f, 0f, 0f, false)]
    [InlineData(0f, -250.1f, 0f, false)]
    [InlineData(0f, 0f, 100.1f, false)]
    [InlineData(float.NaN, 0f, 0f, false)]
    public void TransportOffsets_AreBoundedOnlyWhileOnATransport(float tx, float ty, float tz, bool validOnTransport)
    {
        MovementInfo m = Ok();
        m.Flags = MovementFlags.OnTransport;
        m.TransportX = tx;
        m.TransportY = ty;
        m.TransportZ = tz;
        Assert.Equal(validOnTransport, MovementValidator.IsValid(m));

        // absolute continent coordinates can arrive when leaving a boat; ignored without the flag
        // (vmangos comment at MovementHandler.cpp:1048)
        m.Flags = MovementFlags.None;
        m.TransportX = 12345f;
        Assert.True(MovementValidator.IsValid(m));
    }

    [Fact]
    public void PositionPlusTransportOffset_MustStayInsideTheMap()
    {
        MovementInfo m = Ok();
        m.Flags = MovementFlags.OnTransport;
        m.X = 17066f;
        m.TransportX = 200f;
        Assert.False(MovementValidator.IsValid(m));
    }

    [Theory]
    [InlineData("pitch")]
    [InlineData("jumpz")]
    [InlineData("cos")]
    [InlineData("sin")]
    [InlineData("xy")]
    [InlineData("spline")]
    public void NonFiniteAuxiliaryFields_AreRejected(string field)
    {
        MovementInfo m = Ok();
        switch (field)
        {
            case "pitch": m.Pitch = float.NaN; break;
            case "jumpz": m.JumpZSpeed = float.PositiveInfinity; break;
            case "cos": m.JumpCosAngle = float.NaN; break;
            case "sin": m.JumpSinAngle = float.NegativeInfinity; break;
            case "xy": m.JumpXySpeed = float.NaN; break;
            default: m.SplineElevation = float.NaN; break;
        }

        Assert.False(MovementValidator.IsValid(m));
    }
}
