using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_WHO: VanillaServerMessage, IWorldMessage {
    public required uint OnlinePlayers { get; set; }
    public required List<WhoPlayer> Players { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt((uint)Players.Count, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(OnlinePlayers, cancellationToken).ConfigureAwait(false);

        foreach (var v in Players) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 99, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 99, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_WHO> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        // ReSharper disable once UnusedVariable.Compiler
        var listedPlayers = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var onlinePlayers = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var players = new List<WhoPlayer>();
        for (var i = 0; i < listedPlayers; ++i) {
            players.Add(await Vanilla.WhoPlayer.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new SMSG_WHO {
            OnlinePlayers = onlinePlayers,
            Players = players,
        };
    }

    internal int Size() {
        var size = 0;

        // listed_players: Generator.Generated.DataTypeInteger
        size += 4;

        // online_players: Generator.Generated.DataTypeInteger
        size += 4;

        // players: Generator.Generated.DataTypeArray
        size += Players.Sum(e => e.Size());

        return size;
    }

}

