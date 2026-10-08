namespace WowWorldMessages;

public class NamedGuid
{
    public NamedGuid(ulong guid, string name)
    {
        if (guid == 0)
        {
            throw new ArgumentException($"guid can not be zero in constructor with name '{name}'");
        }

        Guid = guid;
        Name = name;
    }

    public NamedGuid()
    {
        Guid = 0;
        Name = null;
    }

    public ulong Guid { get; }
    public string? Name { get; }

    internal static async Task<NamedGuid> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var guid = await stream.ReadULong(cancellationToken).ConfigureAwait(false);
        if (guid == 0)
        {
            return new NamedGuid();
        }

        var name = await stream.ReadCString(cancellationToken).ConfigureAwait(false);
        return new NamedGuid(guid, name);
    }

    internal async Task WriteAsync(Stream stream, CancellationToken cancellationToken)
    {
        await stream.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        if (Guid == 0)
        {
            return;
        }

        await stream.WriteCString(Name!, cancellationToken).ConfigureAwait(false);
    }

    internal int Length()
    {
        if (Guid == 0)
        {
            return 8;
        }

        return 9 + Name!.Length;
    }
}