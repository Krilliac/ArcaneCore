using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class WorldState {
    public required uint State { get; set; }
    public required uint Value { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(State, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Value, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<WorldState> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var state = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var value = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new WorldState {
            State = state,
            Value = value,
        };
    }

}

