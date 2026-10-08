using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class NpcTextUpdateEmote {
    public required uint Delay { get; set; }
    public required uint Emote { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Delay, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Emote, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<NpcTextUpdateEmote> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var delay = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var emote = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new NpcTextUpdateEmote {
            Delay = delay,
            Emote = emote,
        };
    }

}

