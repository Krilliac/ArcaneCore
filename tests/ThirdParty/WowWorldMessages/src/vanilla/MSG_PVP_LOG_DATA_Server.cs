using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

using BattlegroundEndStatusType = OneOf.OneOf<MSG_PVP_LOG_DATA_Server.BattlegroundEndStatusEnded, BattlegroundEndStatus>;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class MSG_PVP_LOG_DATA_Server: VanillaServerMessage, IWorldMessage {
    public class BattlegroundEndStatusEnded {
        public required Vanilla.BattlegroundWinner Winner { get; set; }
    }
    public required BattlegroundEndStatusType Status { get; set; }
    internal BattlegroundEndStatus StatusValue => Status.Match(
        _ => Vanilla.BattlegroundEndStatus.Ended,
        v => v
    );
    public required List<BattlegroundPlayer> Players { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteByte((byte)StatusValue, cancellationToken).ConfigureAwait(false);

        if (Status.Value is MSG_PVP_LOG_DATA_Server.BattlegroundEndStatusEnded battlegroundEndStatusEnded) {
            await w.WriteByte((byte)battlegroundEndStatusEnded.Winner, cancellationToken).ConfigureAwait(false);

        }

        await w.WriteUInt((uint)Players.Count, cancellationToken).ConfigureAwait(false);

        foreach (var v in Players) {
            await v.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 736, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 736, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MSG_PVP_LOG_DATA_Server> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        BattlegroundEndStatusType status = (Vanilla.BattlegroundEndStatus)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        if (status.Value is Vanilla.BattlegroundEndStatus.Ended) {
            var winner = (Vanilla.BattlegroundWinner)await r.ReadByte(cancellationToken).ConfigureAwait(false);

            status = new BattlegroundEndStatusEnded {
                Winner = winner,
            };
        }

        // ReSharper disable once UnusedVariable.Compiler
        var amountOfPlayers = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var players = new List<BattlegroundPlayer>();
        for (var i = 0; i < amountOfPlayers; ++i) {
            players.Add(await Vanilla.BattlegroundPlayer.ReadBodyAsync(r, cancellationToken).ConfigureAwait(false));
        }

        return new MSG_PVP_LOG_DATA_Server {
            Status = status,
            Players = players,
        };
    }

    internal int Size() {
        var size = 0;

        // status: Generator.Generated.DataTypeEnum
        size += 1;

        if (Status.Value is MSG_PVP_LOG_DATA_Server.BattlegroundEndStatusEnded battlegroundEndStatusEnded) {
            // winner: Generator.Generated.DataTypeEnum
            size += 1;

        }

        // amount_of_players: Generator.Generated.DataTypeInteger
        size += 4;

        // players: Generator.Generated.DataTypeArray
        size += Players.Sum(e => e.Size());

        return size;
    }

}

