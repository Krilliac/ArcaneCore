using System.Text.Json;
using ArcaneCore.Kernel.Cluster.Transfer;

namespace ArcaneCore.Data.Characters.Transfers;

/// <summary>
/// The one JSON encoding of a <see cref="PlayerTransferBundle"/>: the transfer journal stores it (<see cref="EfTransferDecisionStore"/>) and the
/// cluster control channel carries it in the Suspend reply (<c>ClusterControlWire</c>), so both sides encode and decode with the same options and a
/// bundle that crossed the wire is byte-identical to the one the journal holds. Thread-safe: the options are read-only after construction.
/// </summary>
public static class PlayerTransferBundleJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General);

    /// <summary>Serialises <paramref name="bundle"/> to a JSON string (what the journal column holds).</summary>
    public static string Serialize(PlayerTransferBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return JsonSerializer.Serialize(bundle, Options);
    }

    /// <summary>Serialises <paramref name="bundle"/> to UTF-8 JSON bytes (what the control wire carries).</summary>
    public static byte[] SerializeToUtf8(PlayerTransferBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return JsonSerializer.SerializeToUtf8Bytes(bundle, Options);
    }

    /// <summary>Parses a journal column. Throws <see cref="JsonException"/> on malformed JSON and <see cref="InvalidOperationException"/> on a null document.</summary>
    public static PlayerTransferBundle Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<PlayerTransferBundle>(json, Options)
            ?? throw new InvalidOperationException("the transfer bundle JSON is null");
    }

    /// <summary>Parses UTF-8 JSON bytes from the control wire. Same failures as <see cref="Deserialize(string)"/>.</summary>
    public static PlayerTransferBundle Deserialize(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<PlayerTransferBundle>(utf8Json, Options)
            ?? throw new InvalidOperationException("the transfer bundle JSON is null");
}
