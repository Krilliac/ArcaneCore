using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_GUILD_EVENT: VanillaServerMessage, IWorldMessage {
    public required Vanilla.GuildEvent EventValue { get; set; }
    public required List<string> EventDescriptions { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)EventValue, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)EventDescriptions.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in EventDescriptions) {
            await w.WriteCString(v, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 146, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 146, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_GUILD_EVENT> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var eventValue = (Vanilla.GuildEvent)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfEvents = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var eventDescriptions = new List<string>();
        for (var i = 0; i < amountOfEvents; ++i) {
            eventDescriptions.Add(await r.ReadCString(cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_GUILD_EVENT {
            EventValue = eventValue,
            EventDescriptions = eventDescriptions,
        };
    }

    internal int Size() {
        var size = 0;

        // event_value: Generator.Generated.DataTypeEnum
        size += 1;

        // amount_of_events: Generator.Generated.DataTypeInteger
        size += 1;

        // event_descriptions: Generator.Generated.DataTypeArray
        size += EventDescriptions.Sum(e => e.Length + 1);

        return size;
    }

}

