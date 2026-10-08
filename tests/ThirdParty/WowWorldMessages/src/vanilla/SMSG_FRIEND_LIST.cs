using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_FRIEND_LIST: VanillaServerMessage, IWorldMessage {
    public required List<Friend> Friends { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)Friends.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Friends) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 103, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 103, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_FRIEND_LIST> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        // ReSharper disable once UnusedVariable.Compiler
        var amountOfFriends = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var friends = new List<Friend>();
        for (var i = 0; i < amountOfFriends; ++i) {
            friends.Add(await Vanilla.Friend.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_FRIEND_LIST {
            Friends = friends,
        };
    }

    internal int Size() {
        var size = 0;

        // amount_of_friends: Generator.Generated.DataTypeInteger
        size += 1;

        // friends: Generator.Generated.DataTypeArray
        size += Friends.Sum(e => e.Size());

        return size;
    }

}

