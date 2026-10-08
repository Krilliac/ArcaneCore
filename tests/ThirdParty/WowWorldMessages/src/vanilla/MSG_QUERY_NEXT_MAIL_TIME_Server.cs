using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_QUERY_NEXT_MAIL_TIME_Server: VanillaServerMessage, IWorldMessage {
    /// <summary>
    /// mangoszero sets 0 if has unread mail, -86400.0f (0xC7A8C000) if not
    /// vmangos sets 0 if has unread mail, -1.0f if not
    /// cmangos has the behavior of mangoszero except when there are unread mails. This is TODO.
    /// </summary>
    public required float UnreadMails { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteFloat(UnreadMails, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 6, 644, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 6, 644, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_QUERY_NEXT_MAIL_TIME_Server> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var unreadMails = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        return new MSG_QUERY_NEXT_MAIL_TIME_Server {
            UnreadMails = unreadMails,
        };
    }

}

