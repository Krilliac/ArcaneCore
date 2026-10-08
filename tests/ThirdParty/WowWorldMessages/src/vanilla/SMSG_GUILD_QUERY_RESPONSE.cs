using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
// ReSharper disable once InconsistentNaming
public class SMSG_GUILD_QUERY_RESPONSE: VanillaServerMessage, IWorldMessage {
    public required uint Id { get; set; }
    public required string Name { get; set; }
    public const int RankNamesLength = 10;
    public required string[] RankNames { get; set; }
    public required uint EmblemStyle { get; set; }
    public required uint EmblemColor { get; set; }
    public required uint BorderStyle { get; set; }
    public required uint BorderColor { get; set; }
    public required uint BackgroundColor { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Id, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(Name, cancellationToken).ConfigureAwait(false);

        foreach (var v in RankNames) {
            await w.WriteCString(v, cancellationToken).ConfigureAwait(false);
        }

        await w.WriteUInt(EmblemStyle, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(EmblemColor, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(BorderStyle, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(BorderColor, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(BackgroundColor, cancellationToken).ConfigureAwait(false);

    }

    public async Task WriteEncryptedServerAsync(Stream w, IServerEncrypter encrypter, CancellationToken cancellationToken = default) {
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 85, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteUnencryptedServerAsync(Stream w, CancellationToken cancellationToken = default) {
        var encrypter = new NullCrypter();
        await encrypter.WriteServerHeaderAsync(w, (uint)Size() + 2, 85, cancellationToken).ConfigureAwait(false);

        await WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SMSG_GUILD_QUERY_RESPONSE> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var id = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var name = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var rankNames = new string[RankNamesLength];
        for (var i = 0; i < RankNamesLength; ++i) {
            rankNames[i] = await r.ReadCString(cancellationToken).ConfigureAwait(false);
        }

        var emblemStyle = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var emblemColor = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var borderStyle = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var borderColor = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var backgroundColor = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new SMSG_GUILD_QUERY_RESPONSE {
            Id = id,
            Name = name,
            RankNames = rankNames,
            EmblemStyle = emblemStyle,
            EmblemColor = emblemColor,
            BorderStyle = borderStyle,
            BorderColor = borderColor,
            BackgroundColor = backgroundColor,
        };
    }

    internal int Size() {
        var size = 0;

        // id: Generator.Generated.DataTypeInteger
        size += 4;

        // name: Generator.Generated.DataTypeCstring
        size += Name.Length + 1;

        // rank_names: Generator.Generated.DataTypeArray
        size += RankNames.Sum(e => e.Length + 1);

        // emblem_style: Generator.Generated.DataTypeInteger
        size += 4;

        // emblem_color: Generator.Generated.DataTypeInteger
        size += 4;

        // border_style: Generator.Generated.DataTypeInteger
        size += 4;

        // border_color: Generator.Generated.DataTypeInteger
        size += 4;

        // background_color: Generator.Generated.DataTypeInteger
        size += 4;

        return size;
    }

}

