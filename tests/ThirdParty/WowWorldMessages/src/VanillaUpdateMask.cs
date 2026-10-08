namespace WowWorldMessages.Vanilla;

public partial class UpdateMask
{
    private IDictionary<ushort, uint> _values = new Dictionary<ushort, uint>();

    internal static async Task<UpdateMask> ReadAsync(Stream stream, CancellationToken cancellationToken) =>
        new()
        {
            _values = await UpdateMaskUtils.ReadAsyncValues(stream, cancellationToken).ConfigureAwait(false)
        };

    internal async Task WriteAsync(Stream stream, CancellationToken cancellationToken)
    {
        await UpdateMaskUtils.WriteAsync(stream, cancellationToken, _values).ConfigureAwait(false);
    }

    internal int Length()
    {
        return UpdateMaskUtils.Length(_values);
    }
}