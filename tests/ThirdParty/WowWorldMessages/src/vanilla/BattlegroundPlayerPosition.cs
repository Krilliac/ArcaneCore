using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class BattlegroundPlayerPosition {
    public required ulong Player { get; set; }
    public required float PositionX { get; set; }
    public required float PositionY { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Player, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(PositionX, cancellationToken).ConfigureAwait(false);

        await w.WriteFloat(PositionY, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<BattlegroundPlayerPosition> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var player = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        var positionX = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        var positionY = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

        return new BattlegroundPlayerPosition {
            Player = player,
            PositionX = positionX,
            PositionY = positionY,
        };
    }

}

