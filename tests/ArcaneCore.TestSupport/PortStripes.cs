namespace ArcaneCore.TestSupport;

/// <summary>
/// Exclusive stripes of the loopback test port range, one per concurrently running test process. A claim is an open file with
/// <see cref="FileShare.None"/> in a directory under the temp path (an exclusive sharing mode on Windows, an flock on Linux), so it is
/// refused while any other process holds that stripe and is released by the OS when the holder exits or dies; the file itself stays
/// behind and is reused. Disposing a claim frees the stripe early. State: each claim owns one <see cref="FileStream"/>; it is not
/// thread-affine and <see cref="Dispose"/> may be called once from any thread.
/// </summary>
public sealed class PortStripeClaim : IDisposable
{
    /// <summary>The first port of the range that is divided into stripes.</summary>
    public const int RangeStart = 20000;

    /// <summary>The number of stripes; the range is <see cref="StripeCount"/> x <see cref="StripeSize"/> = 12000 ports (20000-31999).</summary>
    public const int StripeCount = 16;

    /// <summary>Ports per stripe. The largest test process (see LoopbackPorts) consumes well below this.</summary>
    public const int StripeSize = 750;

    private readonly FileStream _lock;

    private PortStripeClaim(int index, FileStream held)
    {
        Index = index;
        _lock = held;
    }

    /// <summary>The stripe number, 0 to <see cref="StripeCount"/> - 1.</summary>
    public int Index { get; }

    /// <summary>The first port of this stripe.</summary>
    public int FirstPort => RangeStart + (Index * StripeSize);

    /// <summary>
    /// Claims a free stripe, trying <paramref name="preferredIndex"/> first and then the others in order. Returns null when every stripe is held
    /// (more test processes than stripes) or the lock directory cannot be used; the caller then falls back to the whole range.
    /// </summary>
    public static PortStripeClaim? TryClaim(string directory, int preferredIndex)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        for (int i = 0; i < StripeCount; i++)
        {
            int index = (int)(((uint)preferredIndex + (uint)i) % StripeCount);
            try
            {
                var held = new FileStream(Path.Combine(directory, $"stripe-{index}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new PortStripeClaim(index, held);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Held by another process (or not ours to open): try the next stripe.
            }
        }

        return null;
    }

    public void Dispose() => _lock.Dispose();
}
