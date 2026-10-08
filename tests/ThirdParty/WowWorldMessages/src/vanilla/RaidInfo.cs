using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class RaidInfo {
    public required Vanilla.Map Map { get; set; }
    public required uint ResetTime { get; set; }
    public required uint InstanceId { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)Map, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(ResetTime, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(InstanceId, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<RaidInfo> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var map = (Vanilla.Map)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var resetTime = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var instanceId = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new RaidInfo {
            Map = map,
            ResetTime = resetTime,
            InstanceId = instanceId,
        };
    }

}

