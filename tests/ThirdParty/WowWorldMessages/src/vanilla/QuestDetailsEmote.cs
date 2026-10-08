using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class QuestDetailsEmote {
    public required uint Emote { get; set; }
    public required uint EmoteDelay { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Emote, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(EmoteDelay, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<QuestDetailsEmote> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var emote = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var emoteDelay = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new QuestDetailsEmote {
            Emote = emote,
            EmoteDelay = emoteDelay,
        };
    }

}

