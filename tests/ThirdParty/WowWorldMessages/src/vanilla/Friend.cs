using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

using FriendStatusType = OneOf.OneOf<Friend.FriendStatusAfk, Friend.FriendStatusDnd, Friend.FriendStatusOnline, Friend.FriendStatusUnknown3, FriendStatus>;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class Friend {
    public class FriendStatusAfk {
        public required Vanilla.Area Area { get; set; }
        public required Vanilla.Class ClassType { get; set; }
        public required uint Level { get; set; }
    }
    public class FriendStatusDnd {
        public required Vanilla.Area Area { get; set; }
        public required Vanilla.Class ClassType { get; set; }
        public required uint Level { get; set; }
    }
    public class FriendStatusOnline {
        public required Vanilla.Area Area { get; set; }
        public required Vanilla.Class ClassType { get; set; }
        public required uint Level { get; set; }
    }
    public class FriendStatusUnknown3 {
        public required Vanilla.Area Area { get; set; }
        public required Vanilla.Class ClassType { get; set; }
        public required uint Level { get; set; }
    }
    public required ulong Guid { get; set; }
    public required FriendStatusType Status { get; set; }
    internal FriendStatus StatusValue => Status.Match(
        _ => Vanilla.FriendStatus.Afk,
        _ => Vanilla.FriendStatus.Dnd,
        _ => Vanilla.FriendStatus.Online,
        _ => Vanilla.FriendStatus.Unknown3,
        v => v
    );

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteULong(Guid, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)StatusValue, cancellationToken).ConfigureAwait(false);

        if (Status.Value is Friend.FriendStatusOnline friendStatusOnline) {
            await w.WriteUInt((uint)friendStatusOnline.Area, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(friendStatusOnline.Level, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt((uint)friendStatusOnline.ClassType, cancellationToken).ConfigureAwait(false);

        }
        else if (Status.Value is Friend.FriendStatusAfk friendStatusAfk) {
            await w.WriteUInt((uint)friendStatusAfk.Area, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(friendStatusAfk.Level, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt((uint)friendStatusAfk.ClassType, cancellationToken).ConfigureAwait(false);

        }
        else if (Status.Value is Friend.FriendStatusUnknown3 friendStatusUnknown3) {
            await w.WriteUInt((uint)friendStatusUnknown3.Area, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(friendStatusUnknown3.Level, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt((uint)friendStatusUnknown3.ClassType, cancellationToken).ConfigureAwait(false);

        }
        else if (Status.Value is Friend.FriendStatusDnd friendStatusDnd) {
            await w.WriteUInt((uint)friendStatusDnd.Area, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt(friendStatusDnd.Level, cancellationToken).ConfigureAwait(false);

            await w.WriteUInt((uint)friendStatusDnd.ClassType, cancellationToken).ConfigureAwait(false);

        }

    }

    public static async Task<Friend> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var guid = await r.ReadULong(cancellationToken).ConfigureAwait(false);

        FriendStatusType status = (Vanilla.FriendStatus)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        if (status.Value is Vanilla.FriendStatus.Online) {
            var area = (Vanilla.Area)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            var level = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            var classType = (Vanilla.Class)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            status = new FriendStatusOnline {
                Area = area,
                ClassType = classType,
                Level = level,
            };
        }
        else if (status.Value is Vanilla.FriendStatus.Afk) {
            var area = (Vanilla.Area)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            var level = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            var classType = (Vanilla.Class)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            status = new FriendStatusAfk {
                Area = area,
                ClassType = classType,
                Level = level,
            };
        }
        else if (status.Value is Vanilla.FriendStatus.Unknown3) {
            var area = (Vanilla.Area)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            var level = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            var classType = (Vanilla.Class)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            status = new FriendStatusUnknown3 {
                Area = area,
                ClassType = classType,
                Level = level,
            };
        }
        else if (status.Value is Vanilla.FriendStatus.Dnd) {
            var area = (Vanilla.Area)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            var level = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            var classType = (Vanilla.Class)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

            status = new FriendStatusDnd {
                Area = area,
                ClassType = classType,
                Level = level,
            };
        }

        return new Friend {
            Guid = guid,
            Status = status,
        };
    }

    internal int Size() {
        var size = 0;

        // guid: Generator.Generated.DataTypeGuid
        size += 8;

        // status: Generator.Generated.DataTypeEnum
        size += 1;

        if (Status.Value is Friend.FriendStatusOnline friendStatusOnline) {
            // area: Generator.Generated.DataTypeEnum
            size += 4;

            // level: Generator.Generated.DataTypeLevel32
            size += 4;

            // class_type: Generator.Generated.DataTypeEnum
            size += 4;

        }
        else if (Status.Value is Friend.FriendStatusAfk friendStatusAfk) {
            // area: Generator.Generated.DataTypeEnum
            size += 4;

            // level: Generator.Generated.DataTypeLevel32
            size += 4;

            // class_type: Generator.Generated.DataTypeEnum
            size += 4;

        }
        else if (Status.Value is Friend.FriendStatusUnknown3 friendStatusUnknown3) {
            // area: Generator.Generated.DataTypeEnum
            size += 4;

            // level: Generator.Generated.DataTypeLevel32
            size += 4;

            // class_type: Generator.Generated.DataTypeEnum
            size += 4;

        }
        else if (Status.Value is Friend.FriendStatusDnd friendStatusDnd) {
            // area: Generator.Generated.DataTypeEnum
            size += 4;

            // level: Generator.Generated.DataTypeLevel32
            size += 4;

            // class_type: Generator.Generated.DataTypeEnum
            size += 4;

        }

        return size;
    }

}

