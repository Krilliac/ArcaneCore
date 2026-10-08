using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

using GuildMemberStatusType = OneOf.OneOf<GuildMember.GuildMemberStatusOffline, GuildMemberStatus>;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class GuildMember {
    public class GuildMemberStatusOffline {
        public required float TimeOffline { get; set; }
    }
    public required ulong Guid { get; set; }
    public required GuildMemberStatusType Status { get; set; }
    internal GuildMemberStatus StatusValue => Status.Match(
        _ => Vanilla.GuildMemberStatus.Offline,
        v => v
    );
    public required string Name { get; set; }
    public required uint Rank { get; set; }
    public required byte Level { get; set; }
    public required Vanilla.Class ClassType { get; set; }
    public required Vanilla.Area Area { get; set; }
    public required string PublicNote { get; set; }
    public required string OfficerNote { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)StatusValue, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(Name, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(Rank, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(Level, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)ClassType, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)Area, cancellationToken).ConfigureAwait(false);

        if (Status.Value is GuildMember.GuildMemberStatusOffline guildMemberStatusOffline) {
            await w.WriteFloat(guildMemberStatusOffline.TimeOffline, cancellationToken).ConfigureAwait(false);

        }

        await w.WriteCString(PublicNote, cancellationToken).ConfigureAwait(false);

        await w.WriteCString(OfficerNote, cancellationToken).ConfigureAwait(false);

    }

    public static async Task<GuildMember> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        GuildMemberStatusType status = (Vanilla.GuildMemberStatus)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var name = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var rank = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var level = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var classType = (Vanilla.Class)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var area = (Vanilla.Area)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        if (status.Value is Vanilla.GuildMemberStatus.Offline) {
            var timeOffline = await r.ReadFloat(cancellationToken).ConfigureAwait(false);

            status = new GuildMemberStatusOffline {
                TimeOffline = timeOffline,
            };
        }

        var publicNote = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        var officerNote = await r.ReadCString(cancellationToken).ConfigureAwait(false);

        return new GuildMember {
            Guid = guid,
            Status = status,
            Name = name,
            Rank = rank,
            Level = level,
            ClassType = classType,
            Area = area,
            PublicNote = publicNote,
            OfficerNote = officerNote,
        };
    }

    internal int Size() {
        var size = 0;

        // guid: Generator.Generated.DataTypeGuid
        size += 8;

        // status: Generator.Generated.DataTypeEnum
        size += 1;

        // name: Generator.Generated.DataTypeCstring
        size += Name.Length + 1;

        // rank: Generator.Generated.DataTypeInteger
        size += 4;

        // level: Generator.Generated.DataTypeLevel
        size += 1;

        // class_type: Generator.Generated.DataTypeEnum
        size += 1;

        // area: Generator.Generated.DataTypeEnum
        size += 4;

        if (Status.Value is GuildMember.GuildMemberStatusOffline guildMemberStatusOffline) {
            // time_offline: Generator.Generated.DataTypeFloatingPoint
            size += 4;

        }

        // public_note: Generator.Generated.DataTypeCstring
        size += PublicNote.Length + 1;

        // officer_note: Generator.Generated.DataTypeCstring
        size += OfficerNote.Length + 1;

        return size;
    }

}

