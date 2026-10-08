using WowSrp.Header;
using WowWorldMessages.All;

namespace WowWorldMessages.All;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class Vector3d {
    public required float X { get; set; }
    public required float Y { get; set; }
    public required float Z { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteFloat(X, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(Y, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(Z, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<Vector3d> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var x = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        var y = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        var z = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        return new Vector3d {
            X = x,
            Y = y,
            Z = z,
        };
    }

}

