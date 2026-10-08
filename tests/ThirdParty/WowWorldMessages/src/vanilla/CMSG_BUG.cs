using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class CMSG_BUG: VanillaClientMessage, IWorldMessage {
    /// <summary>
    /// cmangos/vmangos/mangoszero: If 0 received bug report, else received suggestion
    /// </summary>
    public required uint Suggestion { get; set; }
    public required string Content { get; set; }
    public required string BugType { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Suggestion, cancellationToken).ConfigureAwait(false);

        await w.WriteSizedCString(Content, cancellationToken).ConfigureAwait(false);

        await w.WriteSizedCString(BugType, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedClientAsync(Stream w, IClientEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 458, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedClientAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteClientHeaderAsync(w, (uint)Size() + 4, 458, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CMSG_BUG> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var suggestion = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var content = await r.ReadSizedCString(cancellationToken).ConfigureAwait(false);

        var bugType = await r.ReadSizedCString(cancellationToken).ConfigureAwait(false);

        return new CMSG_BUG {
            Suggestion = suggestion,
            Content = content,
            BugType = bugType,
        };
    }

    internal int Size() {
        var size = 0;

        // suggestion: Generator.Generated.DataTypeInteger
        size += 4;

        // content: Generator.Generated.DataTypeSizedCstring
        size += Content.Length + 5;

        // bug_type: Generator.Generated.DataTypeSizedCstring
        size += BugType.Length + 5;

        return size;
    }

}

