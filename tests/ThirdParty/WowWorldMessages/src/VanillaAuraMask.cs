namespace WowWorldMessages.Vanilla;

public class AuraMask
{
    public const int AuraArraySize = 32;
    private readonly ushort[] _auras;

    public AuraMask(ushort[] auras)
    {
        _auras = auras;
    }

    public ushort Aura(int index) => _auras[index];

    public void SetAura(int index, ushort value) => _auras[index] = value;

    internal static async Task<AuraMask> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var mask = await stream.ReadUInt(cancellationToken).ConfigureAwait(false);

        var auras = new ushort[AuraArraySize];

        for (var i = 0; i < AuraArraySize; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                auras[i] = await stream.ReadUShort(cancellationToken).ConfigureAwait(false);
            }
        }

        return new AuraMask(auras);
    }

    internal async Task WriteAsync(Stream stream, CancellationToken cancellationToken)
    {
        uint mask = 0;
        for (var i = 0; i < AuraArraySize; i++)
        {
            if (_auras[i] != 0)
            {
                mask |= (uint)(1 << i);
            }
        }

        await stream.WriteUInt(mask, cancellationToken).ConfigureAwait(false);

        foreach (var aura in _auras)
        {
            if (aura != 0)
            {
                await stream.WriteUShort(aura, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal int Length()
    {
        var size = 4;

        for (var i = 0; i < AuraArraySize; i++)
        {
            if (_auras[i] != 0)
            {
                size += 2;
            }
        }

        return size;
    }
}