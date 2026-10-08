using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_CHAR_ENUM: VanillaServerMessage, IWorldMessage {
    public required List<Character> Characters { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)Characters.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Characters) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 59, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 59, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_CHAR_ENUM> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        // ReSharper disable once UnusedVariable.Compiler
        var amountOfCharacters = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var characters = new List<Character>();
        for (var i = 0; i < amountOfCharacters; ++i) {
            characters.Add(await Vanilla.Character.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_CHAR_ENUM {
            Characters = characters,
        };
    }

    internal int Size() {
        var size = 0;

        // amount_of_characters: Generator.Generated.DataTypeInteger
        size += 1;

        // characters: Generator.Generated.DataTypeArray
        size += Characters.Sum(e => e.Size());

        return size;
    }

}

