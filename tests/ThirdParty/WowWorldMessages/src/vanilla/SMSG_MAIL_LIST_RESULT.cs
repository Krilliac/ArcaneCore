using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_MAIL_LIST_RESULT: VanillaServerMessage, IWorldMessage {
    public required List<Mail> Mails { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)Mails.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Mails) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 571, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 571, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_MAIL_LIST_RESULT> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        // ReSharper disable once UnusedVariable.Compiler
        var amountOfMails = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var mails = new List<Mail>();
        for (var i = 0; i < amountOfMails; ++i) {
            mails.Add(await Vanilla.Mail.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_MAIL_LIST_RESULT {
            Mails = mails,
        };
    }

    internal int Size() {
        var size = 0;

        // amount_of_mails: Generator.Generated.DataTypeInteger
        size += 1;

        // mails: Generator.Generated.DataTypeArray
        size += Mails.Sum(e => e.Size());

        return size;
    }

}

