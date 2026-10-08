using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_ADDON_INFO: VanillaServerMessage, IWorldMessage {
    public required List<Addon> Addons { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        foreach (var v in Addons) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 751, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 751, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_ADDON_INFO> ReadBodyAsync(Stream r, uint bodySize, CancellationToken cancellationToken = default) {
        // ReSharper disable once InconsistentNaming
        var __size = 0;
        var addons = new List<Addon>();
        while (__size < bodySize) {
            addons.Add(await Vanilla.Addon.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
            __size += addons[^1].Size();
        }

        return new SMSG_ADDON_INFO {
            Addons = addons,
        };
    }

    internal int Size() {
        var size = 0;

        // addons: Generator.Generated.DataTypeArray
        size += Addons.Sum(e => e.Size());

        return size;
    }

}

