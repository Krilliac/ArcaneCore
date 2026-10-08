using System.Numerics;

namespace ArcaneCore.Game.Transports;

/// <summary>
/// The Catmull-Rom spline a ship transport follows between two stops (vmangos <c>Movement::Spline&lt;double&gt;</c>,
/// Movement/spline/spline.cpp and spline.impl.h; re-implemented, no code copied). Control points are kept with the
/// "virtual" points the evaluator needs on either side; segment <c>i</c> runs from point <c>i</c> to point <c>i + 1</c>
/// and is valid for <c>First &lt;= i &lt; Last</c>. Segment lengths are measured with three chords per segment
/// (<c>STEPS_PER_SEGMENT</c>) and summed in double precision, as vmangos does; positions are single precision.
/// Immutable after construction; safe to share between threads.
/// </summary>
public sealed class TransportSpline
{
    /// <summary>vmangos <c>SplineBase::STEPS_PER_SEGMENT</c>: chords per segment when measuring its length.</summary>
    public const int StepsPerSegment = 3;

    private readonly Vector3[] _points;
    private readonly double[] _lengths;

    private TransportSpline(Vector3[] points, int first, int last)
    {
        _points = points;
        First = first;
        Last = last;

        // vmangos Spline::initLengths: lengths[First] stays 0 and lengths[i + 1] accumulates SegLength(i).
        _lengths = new double[last + 1];
        double length = 0;
        for (int i = first; i < last; i++)
        {
            length += SegmentLength(i);
            _lengths[i + 1] = length;
        }
    }

    /// <summary>The first valid segment index (vmangos <c>index_lo</c>).</summary>
    public int First { get; }

    /// <summary>One past the last valid segment index (vmangos <c>index_hi</c>).</summary>
    public int Last { get; }

    /// <summary>
    /// vmangos <c>SplineBase::InitCatmullRom</c>, non-cyclic: the controls at 1..count, a virtual point before the first
    /// (<c>controls[0].lerp(controls[1], -1)</c>) and a copy of the last after it. Segments 1..count-1 are valid.
    /// </summary>
    public static TransportSpline CatmullRom(ReadOnlySpan<Vector3> controls)
    {
        if (controls.Length < 2)
        {
            throw new ArgumentException("a Catmull-Rom spline needs at least two control points", nameof(controls));
        }

        int count = controls.Length;
        var points = new Vector3[count + 2];
        controls.CopyTo(points.AsSpan(1));
        points[0] = Lerp(controls[0], controls[1], -1f);
        points[count + 1] = controls[count - 1];
        return new TransportSpline(points, 1, count);
    }

    /// <summary>
    /// vmangos <c>init_spline_custom</c> with <c>SplineRawInitializer</c> (TransportMgr.cpp:83-100): the points as given,
    /// segments <c>1 .. points.Length - 2</c>. The caller supplies the virtual end points.
    /// </summary>
    public static TransportSpline Raw(Vector3[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Length < 4)
        {
            throw new ArgumentException("a raw Catmull-Rom spline needs at least four points", nameof(points));
        }

        return new TransportSpline((Vector3[])points.Clone(), 1, points.Length - 2);
    }

    /// <summary>The length between segment boundaries <paramref name="first"/> and <paramref name="last"/> (vmangos <c>Spline::length(first, last)</c>).</summary>
    public double Length(int first, int last) => _lengths[last] - _lengths[first];

    /// <summary>The whole length (vmangos <c>Spline::length()</c>).</summary>
    public double TotalLength => _lengths[Last];

    /// <summary>The position at fraction <paramref name="u"/> of segment <paramref name="index"/> (vmangos <c>EvaluateCatmullRom</c>).</summary>
    public Vector3 Evaluate(int index, float u)
    {
        CheckIndex(index);
        return Evaluate(index - 1, u, derivative: false);
    }

    /// <summary>The derivative at fraction <paramref name="u"/> of segment <paramref name="index"/> (vmangos <c>EvaluateDerivativeCatmullRom</c>).</summary>
    public Vector3 Derivative(int index, float u)
    {
        CheckIndex(index);
        return Evaluate(index - 1, u, derivative: true);
    }

    /// <summary>G3D <c>Vector3::lerp</c>: <c>a + (b - a) * alpha</c>.</summary>
    public static Vector3 Lerp(Vector3 a, Vector3 b, float alpha) => a + ((b - a) * alpha);

    // vmangos SegLengthCatmullRom: three chords, accumulated in double.
    private float SegmentLength(int index)
    {
        Vector3 current = _points[index];
        double length = 0;
        for (int i = 1; i <= StepsPerSegment; i++)
        {
            Vector3 next = Evaluate(index - 1, (float)i / StepsPerSegment, derivative: false);
            length += (next - current).Length();
            current = next;
        }

        return (float)length;
    }

    // vmangos C_Evaluate / C_Evaluate_Derivative with s_catmullRomCoeffs: the row vector (t^3, t^2, t, 1), or its derivative
    // (3t^2, 2t, 1, 0), times the coefficient matrix gives the four weights of points p0..p3.
    private Vector3 Evaluate(int p0, float t, bool derivative)
    {
        float a, b, c, d;
        if (derivative)
        {
            a = 3f * t * t;
            b = 2f * t;
            c = 1f;
            d = 0f;
        }
        else
        {
            a = t * t * t;
            b = t * t;
            c = t;
            d = 1f;
        }

        // Rows of s_catmullRomCoeffs: (-0.5, 1.5, -1.5, 0.5), (1, -2.5, 2, -0.5), (-0.5, 0, 0.5, 0), (0, 1, 0, 0).
        float w0 = (a * -0.5f) + (b * 1f) + (c * -0.5f) + (d * 0f);
        float w1 = (a * 1.5f) + (b * -2.5f) + (c * 0f) + (d * 1f);
        float w2 = (a * -1.5f) + (b * 2f) + (c * 0.5f) + (d * 0f);
        float w3 = (a * 0.5f) + (b * -0.5f) + (c * 0f) + (d * 0f);
        return (_points[p0] * w0) + (_points[p0 + 1] * w1) + (_points[p0 + 2] * w2) + (_points[p0 + 3] * w3);
    }

    private void CheckIndex(int index)
    {
        if (index < First || index >= Last)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"segment must be in [{First}, {Last})");
        }
    }
}
