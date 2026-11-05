using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Battlegrounds;

namespace ArcaneCore.World.Cluster.Social;

/// <summary>
/// Encoding of the battleground queue traffic carried in the presence protocol (<see cref="PresenceOp.BattlegroundSubmit"/> and the three ops after it): the queue commands of
/// a worker's players (<see cref="BattlegroundCommand"/>, worker to holder) and the notices for them (<see cref="BattlegroundNotice"/>, holder to worker). Each value starts
/// with a tag byte; integers are little-endian (<see cref="BinaryWriter"/>), text is length-prefixed UTF-8, lists an int32 count (at most <see cref="MaxListCount"/>).
/// The tag and field layouts are wire values pinned by <c>BattlegroundWireCodecTests</c>: append only. Readers check every count and enum and throw
/// <see cref="InvalidDataException"/> on a value that does not fit. Pure functions; thread-safe.
/// </summary>
internal static class BattlegroundWireCodec
{
    /// <summary>Most items in one list.</summary>
    public const int MaxListCount = 4096;

    /// <summary>Most members one group join lists (a raid is 40).</summary>
    public const int MaxGroupMembers = 40;

    private const byte CmdJoin = 1;
    private const byte CmdPort = 2;
    private const byte CmdStatus = 3;
    private const byte CmdLogin = 4;
    private const byte CmdLogout = 5;

    private const byte NoticeStatus = 1;
    private const byte NoticeGroupJoined = 2;
    private const byte NoticeJoinError = 3;
    private const byte NoticeQueueLimit = 4;
    private const byte NoticePrepareToPort = 5;
    private const byte NoticeEnter = 6;

    // ---- members and places ---------------------------------------------------------------------------------------------------------------------

    private static void WriteGuid(BinaryWriter w, ObjectGuid guid) => w.Write(guid.Value);

    private static ObjectGuid ReadGuid(BinaryReader r) => new(r.ReadUInt64());

    private static void WriteTeam(BinaryWriter w, Team team) => w.Write((byte)team);

    private static Team ReadTeam(BinaryReader r)
    {
        byte value = r.ReadByte();
        return Enum.IsDefined((Team)value) ? (Team)value : throw new InvalidDataException($"team {value} is not a team");
    }

    private static T ReadEnum<T>(BinaryReader r, string what)
        where T : struct, Enum
    {
        byte value = r.ReadByte();
        T parsed = (T)Enum.ToObject(typeof(T), value);
        return Enum.IsDefined(parsed) ? parsed : throw new InvalidDataException($"{what} {value} is not defined");
    }

    private static int ReadCount(BinaryReader r, int max)
    {
        int count = r.ReadInt32();
        if (count < 0 || count > max)
        {
            throw new InvalidDataException($"list of {count} items (at most {max})");
        }

        return count;
    }

    private static void WriteMember(BinaryWriter w, BattlegroundMember m)
    {
        WriteGuid(w, m.Guid);
        WriteTeam(w, m.Team);
        w.Write(m.Level);
        w.Write(m.InWorld);
        w.Write(m.Deserter);
    }

    private static BattlegroundMember ReadMember(BinaryReader r)
        => new(ReadGuid(r), ReadTeam(r), r.ReadUInt32(), r.ReadBoolean(), r.ReadBoolean());

    private static void WriteEntryPoint(BinaryWriter w, BattlegroundEntryPoint? point)
    {
        w.Write(point is not null);
        if (point is { } p)
        {
            w.Write(p.MapId);
            w.Write(p.X);
            w.Write(p.Y);
            w.Write(p.Z);
            w.Write(p.Orientation);
            w.Write(p.ZoneId);
        }
    }

    private static BattlegroundEntryPoint? ReadEntryPoint(BinaryReader r)
        => r.ReadBoolean() ? new BattlegroundEntryPoint(r.ReadUInt32(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadUInt32()) : null;

    // ---- commands -------------------------------------------------------------------------------------------------------------------------------

    public static void WriteCommand(BinaryWriter w, BattlegroundCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command)
        {
            case BattlegroundJoinCommand join:
                w.Write(CmdJoin);
                WriteMember(w, join.Request.Requester);
                w.Write((byte)join.Request.Type);
                w.Write(join.Request.DesiredInstanceId);
                w.Write(join.Request.AsGroup);
                w.Write(join.Request.QueuedAtPortal);
                if (join.Request.Group.Count > MaxGroupMembers)
                {
                    throw new ArgumentException($"a group join lists {join.Request.Group.Count} members (at most {MaxGroupMembers})", nameof(command));
                }

                w.Write(join.Request.Group.Count);
                foreach (BattlegroundMember member in join.Request.Group)
                {
                    WriteMember(w, member);
                }

                WriteEntryPoint(w, join.EntryPoint);
                break;

            case BattlegroundPortCommand port:
                w.Write(CmdPort);
                WriteGuid(w, port.Request.Player);
                w.Write(port.Request.MapId);
                w.Write(port.Request.Action);
                w.Write(port.Request.Level);
                w.Write(port.Request.Deserter);
                break;

            case BattlegroundStatusCommand status:
                w.Write(CmdStatus);
                WriteGuid(w, status.Player);
                w.Write(status.Level);
                break;

            case BattlegroundLoginCommand login:
                w.Write(CmdLogin);
                WriteMember(w, login.Member);
                break;

            case BattlegroundLogoutCommand logout:
                w.Write(CmdLogout);
                WriteGuid(w, logout.Player);
                break;

            default:
                throw new ArgumentException($"battleground command {command.GetType().Name} has no wire form", nameof(command));
        }
    }

    public static BattlegroundCommand ReadCommand(BinaryReader r)
    {
        byte tag = r.ReadByte();
        switch (tag)
        {
            case CmdJoin:
            {
                BattlegroundMember requester = ReadMember(r);
                var type = ReadEnum<BattlegroundType>(r, "battleground type");
                uint desired = r.ReadUInt32();
                bool asGroup = r.ReadBoolean();
                bool atPortal = r.ReadBoolean();
                int count = ReadCount(r, MaxGroupMembers);
                var group = new List<BattlegroundMember>(count);
                for (int i = 0; i < count; i++)
                {
                    group.Add(ReadMember(r));
                }

                return new BattlegroundJoinCommand(new BattlegroundJoinRequest(requester, type, desired, asGroup, atPortal, group), ReadEntryPoint(r));
            }

            case CmdPort:
                return new BattlegroundPortCommand(new BattlegroundPortRequest(ReadGuid(r), r.ReadUInt32(), r.ReadByte(), r.ReadUInt32(), r.ReadBoolean()));

            case CmdStatus:
                return new BattlegroundStatusCommand(ReadGuid(r), r.ReadUInt32());

            case CmdLogin:
                return new BattlegroundLoginCommand(ReadMember(r));

            case CmdLogout:
                return new BattlegroundLogoutCommand(ReadGuid(r));

            default:
                throw new InvalidDataException($"unknown battleground command tag {tag}");
        }
    }

    // ---- notices --------------------------------------------------------------------------------------------------------------------------------

    public static void WriteNotice(BinaryWriter w, BattlegroundNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        switch (notice)
        {
            case BattlegroundStatusNotice n:
                w.Write(NoticeStatus);
                WriteGuid(w, n.Player);
                w.Write(n.QueueSlot);
                w.Write(n.Subject.MapId);
                w.Write(n.Subject.Bracket);
                w.Write(n.Subject.ClientInstanceId);
                w.Write((byte)n.Status);
                w.Write(n.Time1);
                w.Write(n.Time2);
                break;

            case BattlegroundGroupJoinedNotice n:
                w.Write(NoticeGroupJoined);
                WriteGuid(w, n.Player);
                w.Write(n.Result);
                break;

            case BattlegroundJoinErrorNotice n:
                w.Write(NoticeJoinError);
                WriteGuid(w, n.Player);
                w.Write((byte)n.Error);
                break;

            case BattlegroundQueueLimitNotice n:
                w.Write(NoticeQueueLimit);
                WriteGuid(w, n.Player);
                w.Write(n.Limit);
                break;

            case BattlegroundPrepareToPortNotice n:
                w.Write(NoticePrepareToPort);
                WriteGuid(w, n.Player);
                break;

            case BattlegroundEnterNotice n:
                w.Write(NoticeEnter);
                WriteGuid(w, n.Player);
                w.Write(n.MapId);
                w.Write(n.InstanceId);
                WriteTeam(w, n.Team);
                w.Write(n.X);
                w.Write(n.Y);
                w.Write(n.Z);
                w.Write(n.Orientation);
                break;

            default:
                throw new ArgumentException($"battleground notice {notice.GetType().Name} has no wire form", nameof(notice));
        }
    }

    public static BattlegroundNotice ReadNotice(BinaryReader r)
    {
        byte tag = r.ReadByte();
        switch (tag)
        {
            case NoticeStatus:
            {
                ObjectGuid player = ReadGuid(r);
                uint slot = r.ReadUInt32();
                var subject = new BattlegroundStatusSubject(r.ReadUInt32(), r.ReadByte(), r.ReadUInt32());
                var status = ReadEnum<BattlegroundStatus>(r, "battleground status");
                return new BattlegroundStatusNotice(player, slot, subject, status, r.ReadUInt32(), r.ReadUInt32());
            }

            case NoticeGroupJoined:
                return new BattlegroundGroupJoinedNotice(ReadGuid(r), r.ReadUInt32());

            case NoticeJoinError:
            {
                ObjectGuid player = ReadGuid(r);
                return new BattlegroundJoinErrorNotice(player, ReadEnum<BattlegroundJoinError>(r, "join error"));
            }

            case NoticeQueueLimit:
                return new BattlegroundQueueLimitNotice(ReadGuid(r), r.ReadUInt32());

            case NoticePrepareToPort:
                return new BattlegroundPrepareToPortNotice(ReadGuid(r));

            case NoticeEnter:
            {
                ObjectGuid player = ReadGuid(r);
                uint map = r.ReadUInt32();
                uint instance = r.ReadUInt32();
                Team team = ReadTeam(r);
                return new BattlegroundEnterNotice(player, map, instance, team, r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            }

            default:
                throw new InvalidDataException($"unknown battleground notice tag {tag}");
        }
    }

    // ---- lists of (worker, item) ----------------------------------------------------------------------------------------------------------------

    public static void WriteCommands(BinaryWriter w, IReadOnlyList<BattlegroundQueuedCommand> items)
    {
        w.Write(items.Count);
        foreach (BattlegroundQueuedCommand item in items)
        {
            w.Write(item.WorkerId);
            WriteCommand(w, item.Command);
        }
    }

    public static IReadOnlyList<BattlegroundQueuedCommand> ReadCommands(BinaryReader r)
    {
        int count = ReadCount(r, MaxListCount);
        var items = new List<BattlegroundQueuedCommand>(count);
        for (int i = 0; i < count; i++)
        {
            string worker = r.ReadString();
            items.Add(new BattlegroundQueuedCommand(worker, ReadCommand(r)));
        }

        return items;
    }

    public static void WriteNotices(BinaryWriter w, IReadOnlyList<BattlegroundQueuedNotice> items)
    {
        w.Write(items.Count);
        foreach (BattlegroundQueuedNotice item in items)
        {
            w.Write(item.WorkerId);
            WriteNotice(w, item.Notice);
        }
    }

    public static IReadOnlyList<BattlegroundQueuedNotice> ReadNotices(BinaryReader r)
    {
        int count = ReadCount(r, MaxListCount);
        var items = new List<BattlegroundQueuedNotice>(count);
        for (int i = 0; i < count; i++)
        {
            string worker = r.ReadString();
            items.Add(new BattlegroundQueuedNotice(worker, ReadNotice(r)));
        }

        return items;
    }

    /// <summary>The bytes one (worker, command) list item takes: what a batch is measured with against the link's payload limit.</summary>
    public static int EncodedSize(string workerId, BattlegroundCommand command) => Measure(w =>
    {
        w.Write(workerId);
        WriteCommand(w, command);
    });

    /// <summary>The bytes one (worker, notice) list item takes.</summary>
    public static int EncodedSize(string workerId, BattlegroundNotice notice) => Measure(w =>
    {
        w.Write(workerId);
        WriteNotice(w, notice);
    });

    private static int Measure(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            write(w);
        }

        return (int)stream.Length;
    }
}
