using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_PET_MODE: VanillaServerMessage, IWorldMessage {
    public required ulong Guid { get; set; }
    public required Vanilla.PetReactState ReactState { get; set; }
    public required Vanilla.PetCommandState CommandState { get; set; }
    /// <summary>
    /// vmangos sets to 0.
    /// </summary>
    public required byte Unknown1 { get; set; }
    public required Vanilla.PetEnabled PetEnabled { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)ReactState, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)CommandState, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Unknown1, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)PetEnabled, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, 14, 378, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, 14, 378, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_PET_MODE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var reactState = (Vanilla.PetReactState)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var commandState = (Vanilla.PetCommandState)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var unknown1 = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var petEnabled = (Vanilla.PetEnabled)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return new SMSG_PET_MODE {
            Guid = guid,
            ReactState = reactState,
            CommandState = commandState,
            Unknown1 = unknown1,
            PetEnabled = petEnabled,
        };
    }

}

