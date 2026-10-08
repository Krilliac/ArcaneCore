using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class WhoPlayer {
    public required string Name { get; set; }
    public required string Guild { get; set; }
    public required uint Level { get; set; }
    public required Vanilla.Class ClassType { get; set; }
    public required Vanilla.Race Race { get; set; }
    public required Vanilla.Area Area { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteCString(Name, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(Guild, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Level, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)ClassType, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Race, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Area, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<WhoPlayer> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var name = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var guild = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var level = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var classType = (Vanilla.Class)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var race = (Vanilla.Race)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var area = (Vanilla.Area)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        return new WhoPlayer {
            Name = name,
            Guild = guild,
            Level = level,
            ClassType = classType,
            Race = race,
            Area = area,
        };
    }

    internal int Size() {
        var size = 0;

        // name: Generator.Generated.DataTypeCstring
        size += Name.Length + 1;

        // guild: Generator.Generated.DataTypeCstring
        size += Guild.Length + 1;

        // level: Generator.Generated.DataTypeLevel32
        size += 4;

        // class_type: Generator.Generated.DataTypeEnum
        size += 4;

        // race: Generator.Generated.DataTypeEnum
        size += 4;

        // area: Generator.Generated.DataTypeEnum
        size += 4;

        return size;
    }

}

