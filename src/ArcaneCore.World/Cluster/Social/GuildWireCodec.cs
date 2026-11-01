using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Cluster.Social;

/// <summary>What one <see cref="GuildLinkEvent"/> carries from the Social holder to a worker. Values are wire values: append only.</summary>
public enum GuildLinkEventKind : byte
{
    /// <summary>A packet for a character that is online on the receiving worker.</summary>
    Deliver = 1,

    /// <summary>The stored state of a guild (or its disbanding).</summary>
    Guild = 2,

    /// <summary>The state of a petition (or its removal).</summary>
    Petition = 3,

    /// <summary>The answer to a forwarded request that asked for one.</summary>
    Completed = 4,
}

/// <summary>
/// One thing the Social holder queues for a worker (<see cref="IGuildHolderTransport.DrainGuildEvents"/>). Immutable; build with the static factories.
/// </summary>
public sealed record GuildLinkEvent
{
    public GuildLinkEventKind Kind { get; init; }

    /// <summary>Deliver: the character's guid value.</summary>
    public ulong Character { get; init; }

    /// <summary>Deliver: the opcode.</summary>
    public WorldOpcode Opcode { get; init; }

    /// <summary>Deliver: the packet payload.</summary>
    public byte[] Payload { get; init; } = [];

    /// <summary>Guild and Petition: the guild or petition id.</summary>
    public int Id { get; init; }

    /// <summary>Guild: the state, null when the guild was disbanded.</summary>
    public GuildData? Guild { get; init; }

    /// <summary>Petition: the state, null when the petition is gone.</summary>
    public PetitionData? Petition { get; init; }

    /// <summary>Completed: the request the answer belongs to.</summary>
    public long RequestId { get; init; }

    /// <summary>Completed: the result of <see cref="GuildManager.Serve"/>.</summary>
    public bool Result { get; init; }

    public static GuildLinkEvent OfDeliver(ulong character, WorldOpcode opcode, byte[] payload)
        => new() { Kind = GuildLinkEventKind.Deliver, Character = character, Opcode = opcode, Payload = payload };

    public static GuildLinkEvent OfGuild(int id, GuildData? data) => new() { Kind = GuildLinkEventKind.Guild, Id = id, Guild = data };

    public static GuildLinkEvent OfPetition(int id, PetitionData? data) => new() { Kind = GuildLinkEventKind.Petition, Id = id, Petition = data };

    public static GuildLinkEvent OfCompleted(long requestId, bool result) => new() { Kind = GuildLinkEventKind.Completed, RequestId = requestId, Result = result };
}

/// <summary>
/// Encoding of guild requests and holder events for the peer link (carried inside <see cref="PresenceOp.GuildForward"/> and <see cref="PresenceOp.GuildDrain"/>). Little-endian
/// <see cref="BinaryWriter"/> fields, strings length-prefixed UTF-8, lists an int32 count. Decoders throw <see cref="InvalidDataException"/> or <see cref="EndOfStreamException"/>
/// on a frame that does not fit. Pure functions; thread-safe.
/// </summary>
internal static class GuildWireCodec
{
    private const int MaxList = 4096;
    private const int MaxBytes = 64 * 1024;
    private const int MaxValues = 64;

    // ---- requests ------------------------------------------------------------------------------------------------------------------------------

    public static void WriteRequest(BinaryWriter w, GuildRequest q)
    {
        w.Write((byte)q.Kind);
        w.Write(q.Actor.Value);
        w.Write(q.Text);
        w.Write(q.Text2);
        w.Write(q.A);
        w.Write(q.B);
        w.Write(q.Item);
        w.Write(q.Petition is not null);
        if (q.Petition is { } petition)
        {
            WritePetition(w, petition);
        }

        w.Write(q.Id);
        w.Write(q.Values is not null);
        if (q.Values is { } values)
        {
            w.Write(values.Count);
            foreach (int v in values)
            {
                w.Write(v);
            }
        }
    }

    public static GuildRequest ReadRequest(BinaryReader r)
    {
        var kind = (GuildRequestKind)r.ReadByte();
        var actor = new ObjectGuid(r.ReadUInt64());
        string text = r.ReadString();
        string text2 = r.ReadString();
        uint a = r.ReadUInt32();
        uint b = r.ReadUInt32();
        ulong item = r.ReadUInt64();
        PetitionData? petition = r.ReadBoolean() ? ReadPetition(r) : null;
        int id = r.ReadInt32();
        int[]? values = null;
        if (r.ReadBoolean())
        {
            int count = r.ReadInt32();
            if (count < 0 || count > MaxValues)
            {
                throw new InvalidDataException($"value list of {count} items (at most {MaxValues})");
            }

            values = new int[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = r.ReadInt32();
            }
        }

        return new GuildRequest(kind, actor, text, text2, a, b, item, petition, id, values);
    }

    // ---- replicas ------------------------------------------------------------------------------------------------------------------------------

    public static void WritePetition(BinaryWriter w, PetitionData p)
    {
        w.Write(p.Id);
        w.Write(p.OwnerId);
        w.Write(p.CharterItemId);
        w.Write(p.Name);
        w.Write(p.Signatures.Count);
        foreach (PetitionSignatureData s in p.Signatures)
        {
            w.Write(s.PlayerId);
            w.Write(s.AccountId);
        }
    }

    public static PetitionData ReadPetition(BinaryReader r)
    {
        int id = r.ReadInt32();
        int owner = r.ReadInt32();
        int item = r.ReadInt32();
        string name = r.ReadString();
        int count = ReadCount(r);
        var signatures = new PetitionSignatureData[count];
        for (int i = 0; i < count; i++)
        {
            signatures[i] = new PetitionSignatureData(r.ReadInt32(), r.ReadInt32());
        }

        return new PetitionData(id, owner, item, name, signatures);
    }

    public static void WriteGuild(BinaryWriter w, GuildData g)
    {
        w.Write(g.Id);
        w.Write(g.Name);
        w.Write(g.LeaderId);
        w.Write(g.Motd);
        w.Write(g.Info);
        w.Write(g.CreatedAt);
        w.Write(g.EmblemStyle);
        w.Write(g.EmblemColor);
        w.Write(g.BorderStyle);
        w.Write(g.BorderColor);
        w.Write(g.BackgroundColor);
        w.Write(g.Ranks.Count);
        foreach (GuildRankData rank in g.Ranks)
        {
            w.Write(rank.RankId);
            w.Write(rank.Name);
            w.Write(rank.Rights);
        }

        w.Write(g.Members.Count);
        foreach (GuildMemberData m in g.Members)
        {
            w.Write(m.CharacterId);
            w.Write(m.Rank);
            w.Write(m.PublicNote);
            w.Write(m.OfficerNote);
            w.Write(m.Level);
            w.Write(m.ZoneId);
            w.Write(m.LogoutTime);
        }
    }

    public static GuildData ReadGuild(BinaryReader r)
    {
        int id = r.ReadInt32();
        string name = r.ReadString();
        int leader = r.ReadInt32();
        string motd = r.ReadString();
        string info = r.ReadString();
        long created = r.ReadInt64();
        int style = r.ReadInt32();
        int color = r.ReadInt32();
        int borderStyle = r.ReadInt32();
        int borderColor = r.ReadInt32();
        int background = r.ReadInt32();
        int rankCount = ReadCount(r);
        var ranks = new GuildRankData[rankCount];
        for (int i = 0; i < rankCount; i++)
        {
            ranks[i] = new GuildRankData(r.ReadByte(), r.ReadString(), r.ReadUInt32());
        }

        int memberCount = ReadCount(r);
        var members = new GuildMemberData[memberCount];
        for (int i = 0; i < memberCount; i++)
        {
            members[i] = new GuildMemberData(r.ReadInt32(), r.ReadByte(), r.ReadString(), r.ReadString(), r.ReadByte(), r.ReadUInt32(), r.ReadInt64());
        }

        return new GuildData(id, name, leader, motd, info, created, style, color, borderStyle, borderColor, background, ranks, members);
    }

    // ---- events --------------------------------------------------------------------------------------------------------------------------------

    public static void WriteEvent(BinaryWriter w, GuildLinkEvent e)
    {
        w.Write((byte)e.Kind);
        switch (e.Kind)
        {
            case GuildLinkEventKind.Deliver:
                w.Write(e.Character);
                w.Write((ushort)e.Opcode);
                w.Write(e.Payload.Length);
                w.Write(e.Payload);
                break;
            case GuildLinkEventKind.Guild:
                w.Write(e.Id);
                w.Write(e.Guild is not null);
                if (e.Guild is { } guild)
                {
                    WriteGuild(w, guild);
                }

                break;
            case GuildLinkEventKind.Petition:
                w.Write(e.Id);
                w.Write(e.Petition is not null);
                if (e.Petition is { } petition)
                {
                    WritePetition(w, petition);
                }

                break;
            case GuildLinkEventKind.Completed:
                w.Write(e.RequestId);
                w.Write(e.Result);
                break;
            default:
                throw new InvalidDataException($"unknown guild event kind {(byte)e.Kind}");
        }
    }

    public static GuildLinkEvent ReadEvent(BinaryReader r)
    {
        var kind = (GuildLinkEventKind)r.ReadByte();
        switch (kind)
        {
            case GuildLinkEventKind.Deliver:
            {
                ulong character = r.ReadUInt64();
                var opcode = (WorldOpcode)r.ReadUInt16();
                int length = r.ReadInt32();
                if (length < 0 || length > MaxBytes)
                {
                    throw new InvalidDataException($"byte block of {length} B (at most {MaxBytes})");
                }

                byte[] payload = r.ReadBytes(length);
                if (payload.Length != length)
                {
                    throw new EndOfStreamException();
                }

                return GuildLinkEvent.OfDeliver(character, opcode, payload);
            }

            case GuildLinkEventKind.Guild:
            {
                int id = r.ReadInt32();
                return GuildLinkEvent.OfGuild(id, r.ReadBoolean() ? ReadGuild(r) : null);
            }

            case GuildLinkEventKind.Petition:
            {
                int id = r.ReadInt32();
                return GuildLinkEvent.OfPetition(id, r.ReadBoolean() ? ReadPetition(r) : null);
            }

            case GuildLinkEventKind.Completed:
            {
                long requestId = r.ReadInt64();
                return GuildLinkEvent.OfCompleted(requestId, r.ReadBoolean());
            }

            default:
                throw new InvalidDataException($"unknown guild event kind {(byte)kind}");
        }
    }

    public static void WriteEvents(BinaryWriter w, IReadOnlyList<GuildLinkEvent> events)
    {
        w.Write(events.Count);
        foreach (GuildLinkEvent e in events)
        {
            WriteEvent(w, e);
        }
    }

    public static IReadOnlyList<GuildLinkEvent> ReadEvents(BinaryReader r)
    {
        int count = ReadCount(r);
        var events = new List<GuildLinkEvent>(count);
        for (int i = 0; i < count; i++)
        {
            events.Add(ReadEvent(r));
        }

        return events;
    }

    /// <summary>The bytes <see cref="WriteEvent"/> writes for <paramref name="e"/> (what the holder checks against the link's payload limit when it builds a batch).</summary>
    public static int EncodedSize(GuildLinkEvent e)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteEvent(w, e);
        }

        return (int)stream.Length;
    }

    private static int ReadCount(BinaryReader r)
    {
        int count = r.ReadInt32();
        if (count < 0 || count > MaxList)
        {
            throw new InvalidDataException($"list of {count} items (at most {MaxList})");
        }

        return count;
    }
}
