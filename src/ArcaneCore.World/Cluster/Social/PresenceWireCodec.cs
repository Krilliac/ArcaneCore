using System.Text;
using ArcaneCore.Kernel.Cluster.Social;

namespace ArcaneCore.World.Cluster.Social;

/// <summary>Request kinds of the presence protocol carried in the peer link's request payload (first byte). Values are wire values: append only.</summary>
internal enum PresenceOp : byte
{
    Register = 1,
    Upsert = 2,
    Remove = 3,
    DrainKicks = 4,
    FindByGuid = 5,
    FindByName = 6,
    FindByAccount = 7,
    FindByGuild = 8,
    FindByGroup = 9,
    QueryWho = 10,
    Snapshot = 11,
    Count = 12,
    Route = 13,
    JoinChannel = 14,
    LeaveChannel = 15,
    DrainGateway = 16,
    DrainWorker = 17,
    Logout = 18,
}

/// <summary>
/// Encoding of the presence protocol over the peer link's request/answer payload. Request: op byte then the op's fields; reply: status byte (0 ok, 1 error) then the
/// result or, for an error, a message string. Integers little-endian (<see cref="BinaryWriter"/>), strings length-prefixed UTF-8, lists an int32 count (at most
/// <see cref="MaxListCount"/>). Decoders throw <see cref="InvalidDataException"/> or <see cref="EndOfStreamException"/> on a frame that does not fit.
/// Pure functions except <see cref="Dispatch"/>, which calls the holder service; thread-safe.
/// </summary>
internal static class PresenceWireCodec
{
    public const int MaxListCount = 4096;

    private const byte StatusOk = 0;
    private const byte StatusError = 1;

    // ---- framing -------------------------------------------------------------------------------------------------------------------------------

    public static byte[] EncodeRequest(PresenceOp op, Action<BinaryWriter>? write = null)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            w.Write((byte)op);
            write?.Invoke(w);
        }

        return stream.ToArray();
    }

    public static byte[] EncodeReply(Action<BinaryWriter>? write = null)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(StatusOk);
            write?.Invoke(w);
        }

        return stream.ToArray();
    }

    public static byte[] EncodeError(string message)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(StatusError);
            w.Write(message.Length <= 200 ? message : message[..200]);
        }

        return stream.ToArray();
    }

    /// <summary>Opens a reply for reading; throws <see cref="InvalidOperationException"/> carrying the authority's message when the status is an error.</summary>
    public static BinaryReader OpenReply(byte[] reply)
    {
        var r = new BinaryReader(new MemoryStream(reply, writable: false), Encoding.UTF8);
        byte status = r.ReadByte();
        if (status != StatusOk)
        {
            throw new InvalidOperationException("the presence authority refused the request: " + r.ReadString());
        }

        return r;
    }

    private static int ReadCount(BinaryReader r)
    {
        int count = r.ReadInt32();
        if (count < 0 || count > MaxListCount)
        {
            throw new InvalidDataException($"list of {count} items (at most {MaxListCount})");
        }

        return count;
    }

    private static void WriteNullableString(BinaryWriter w, string? value)
    {
        w.Write(value is not null);
        if (value is not null)
        {
            w.Write(value);
        }
    }

    private static string? ReadNullableString(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;

    // ---- entries -------------------------------------------------------------------------------------------------------------------------------

    public static void WriteEntry(BinaryWriter w, RealmPresenceEntry e)
    {
        w.Write(e.CharacterGuid);
        w.Write(e.CharacterName);
        w.Write(e.AccountId);
        w.Write(e.WorkerId);
        w.Write(e.GatewayId);
        w.Write(e.MapId);
        w.Write(e.ZoneId);
        w.Write(e.AreaId);
        w.Write(e.Level);
        w.Write(e.Class);
        w.Write(e.Race);
        w.Write(e.Gender);
        w.Write(e.GuildId);
        w.Write(e.GroupId);
        w.Write(e.PresenceVersion);
        w.Write(e.SessionGeneration);
        w.Write(e.UpdatedAtUtc.UtcTicks);
        WriteNullableString(w, e.GuildName);
        w.Write(e.IsAfk);
        w.Write(e.IsDnd);
        WriteNullableString(w, e.StatusMessage);
    }

    public static RealmPresenceEntry ReadEntry(BinaryReader r)
    {
        ulong guid = r.ReadUInt64();
        string name = r.ReadString();
        int account = r.ReadInt32();
        string worker = r.ReadString();
        string gateway = r.ReadString();
        uint map = r.ReadUInt32();
        uint zone = r.ReadUInt32();
        uint area = r.ReadUInt32();
        byte level = r.ReadByte();
        byte cls = r.ReadByte();
        byte race = r.ReadByte();
        byte gender = r.ReadByte();
        int guild = r.ReadInt32();
        int group = r.ReadInt32();
        long version = r.ReadInt64();
        long generation = r.ReadInt64();
        long ticks = r.ReadInt64();
        string? guildName = ReadNullableString(r);
        bool afk = r.ReadBoolean();
        bool dnd = r.ReadBoolean();
        string? status = ReadNullableString(r);
        return new RealmPresenceEntry(
            guid, name, account, worker, gateway, map, zone, area, level, cls, race, gender, guild, group, version, generation,
            new DateTimeOffset(ticks, TimeSpan.Zero), guildName, afk, dnd, status);
    }

    public static void WriteNullableEntry(BinaryWriter w, RealmPresenceEntry? e)
    {
        w.Write(e is not null);
        if (e is not null)
        {
            WriteEntry(w, e);
        }
    }

    public static RealmPresenceEntry? ReadNullableEntry(BinaryReader r) => r.ReadBoolean() ? ReadEntry(r) : null;

    public static void WriteEntries(BinaryWriter w, IReadOnlyList<RealmPresenceEntry> entries)
    {
        w.Write(entries.Count);
        foreach (RealmPresenceEntry e in entries)
        {
            WriteEntry(w, e);
        }
    }

    public static IReadOnlyList<RealmPresenceEntry> ReadEntries(BinaryReader r)
    {
        int count = ReadCount(r);
        var list = new List<RealmPresenceEntry>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(ReadEntry(r));
        }

        return list;
    }

    public static void WriteUpsertReply(BinaryWriter w, PresenceUpsertReply reply)
    {
        w.Write((byte)reply.Result);
        WriteNullableEntry(w, reply.Current);
    }

    public static PresenceUpsertReply ReadUpsertReply(BinaryReader r)
    {
        var result = (PresenceMutationResult)r.ReadByte();
        return new PresenceUpsertReply(result, ReadNullableEntry(r));
    }

    // ---- kicks ---------------------------------------------------------------------------------------------------------------------------------

    public static void WriteKicks(BinaryWriter w, IReadOnlyList<PresenceKick> kicks)
    {
        w.Write(kicks.Count);
        foreach (PresenceKick k in kicks)
        {
            w.Write(k.WorkerId);
            w.Write(k.AccountId);
            w.Write(k.CharacterGuid);
            w.Write(k.SessionGeneration);
        }
    }

    public static IReadOnlyList<PresenceKick> ReadKicks(BinaryReader r)
    {
        int count = ReadCount(r);
        var list = new List<PresenceKick>(count);
        for (int i = 0; i < count; i++)
        {
            string worker = r.ReadString();
            int account = r.ReadInt32();
            ulong guid = r.ReadUInt64();
            long generation = r.ReadInt64();
            list.Add(new PresenceKick(worker, account, guid, generation));
        }

        return list;
    }

    // ---- who filter ----------------------------------------------------------------------------------------------------------------------------

    public static void WriteWho(BinaryWriter w, RealmWhoFilter f)
    {
        w.Write(f.MinLevel);
        w.Write(f.MaxLevel);
        w.Write(f.ClassMask is not null);
        w.Write(f.ClassMask ?? 0u);
        w.Write(f.RaceMask is not null);
        w.Write(f.RaceMask ?? 0u);
        IReadOnlyList<uint> zones = f.ZoneIds ?? [];
        w.Write(zones.Count);
        foreach (uint z in zones)
        {
            w.Write(z);
        }

        IReadOnlyList<string> terms = f.SearchTerms ?? [];
        w.Write(terms.Count);
        foreach (string t in terms)
        {
            w.Write(t);
        }

        w.Write(f.MaxResults);
    }

    public static RealmWhoFilter ReadWho(BinaryReader r)
    {
        byte min = r.ReadByte();
        byte max = r.ReadByte();
        bool hasClass = r.ReadBoolean();
        uint classMask = r.ReadUInt32();
        bool hasRace = r.ReadBoolean();
        uint raceMask = r.ReadUInt32();
        int zoneCount = ReadCount(r);
        var zones = new List<uint>(zoneCount);
        for (int i = 0; i < zoneCount; i++)
        {
            zones.Add(r.ReadUInt32());
        }

        int termCount = ReadCount(r);
        var terms = new List<string>(termCount);
        for (int i = 0; i < termCount; i++)
        {
            terms.Add(r.ReadString());
        }

        int maxResults = r.ReadInt32();
        return new RealmWhoFilter(min, max, hasClass ? classMask : null, hasRace ? raceMask : null, zones, terms, maxResults);
    }

    // ---- social messages -----------------------------------------------------------------------------------------------------------------------

    public static void WriteMessage(BinaryWriter w, ClusterSocialMessage m)
    {
        w.Write(m.MessageId.ToByteArray());
        w.Write((byte)m.Channel);
        w.Write(m.SenderGuid);
        w.Write(m.SenderName);
        w.Write(m.SenderAccountId);
        w.Write(m.SenderSessionGeneration);
        w.Write(m.OriginWorkerId);
        w.Write(m.OriginGatewayId);
        w.Write(m.SenderSequence);
        w.Write(m.PayloadText);
        w.Write(m.TargetCharacterGuid is not null);
        w.Write(m.TargetCharacterGuid ?? 0UL);
        WriteNullableString(w, m.TargetCharacterName);
        w.Write(m.TargetGroupId is not null);
        w.Write(m.TargetGroupId ?? 0);
        w.Write(m.TargetGuildId is not null);
        w.Write(m.TargetGuildId ?? 0);
        WriteNullableString(w, m.TargetChannelName);
        w.Write(m.CreatedAtUtc.UtcTicks);
    }

    public static ClusterSocialMessage ReadMessage(BinaryReader r)
    {
        var id = new Guid(r.ReadBytes(16));
        var channel = (SocialRouteChannel)r.ReadByte();
        ulong senderGuid = r.ReadUInt64();
        string senderName = r.ReadString();
        int account = r.ReadInt32();
        long generation = r.ReadInt64();
        string worker = r.ReadString();
        string gateway = r.ReadString();
        long sequence = r.ReadInt64();
        string text = r.ReadString();
        bool hasGuid = r.ReadBoolean();
        ulong targetGuid = r.ReadUInt64();
        string? targetName = ReadNullableString(r);
        bool hasGroup = r.ReadBoolean();
        int group = r.ReadInt32();
        bool hasGuild = r.ReadBoolean();
        int guild = r.ReadInt32();
        string? channelName = ReadNullableString(r);
        long ticks = r.ReadInt64();
        return new ClusterSocialMessage(
            id, channel, senderGuid, senderName, account, generation, worker, gateway, sequence, text,
            hasGuid ? targetGuid : null, targetName, hasGroup ? group : null, hasGuild ? guild : null, channelName,
            new DateTimeOffset(ticks, TimeSpan.Zero));
    }

    public static void WriteDeliveries(BinaryWriter w, IReadOnlyList<RoutedSocialDelivery> deliveries)
    {
        w.Write(deliveries.Count);
        foreach (RoutedSocialDelivery d in deliveries)
        {
            w.Write(d.MessageId.ToByteArray());
            w.Write((byte)d.Channel);
            w.Write(d.RecipientGuid);
            w.Write(d.RecipientName);
            w.Write(d.RecipientAccountId);
            w.Write(d.RecipientSessionGeneration);
            w.Write(d.TargetWorkerId);
            w.Write(d.TargetGatewayId);
            w.Write(d.SenderGuid);
            w.Write(d.SenderName);
            w.Write(d.DeliverySequence);
            w.Write(d.PayloadText);
        }
    }

    public static IReadOnlyList<RoutedSocialDelivery> ReadDeliveries(BinaryReader r)
    {
        int count = ReadCount(r);
        var list = new List<RoutedSocialDelivery>(count);
        for (int i = 0; i < count; i++)
        {
            var id = new Guid(r.ReadBytes(16));
            var channel = (SocialRouteChannel)r.ReadByte();
            ulong recipient = r.ReadUInt64();
            string recipientName = r.ReadString();
            int account = r.ReadInt32();
            long generation = r.ReadInt64();
            string worker = r.ReadString();
            string gateway = r.ReadString();
            ulong sender = r.ReadUInt64();
            string senderName = r.ReadString();
            long sequence = r.ReadInt64();
            string text = r.ReadString();
            list.Add(new RoutedSocialDelivery(id, channel, recipient, recipientName, account, generation, worker, gateway, sender, senderName, sequence, text));
        }

        return list;
    }

    // ---- holder side ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>Decodes one request, runs it against <paramref name="service"/> and encodes the reply.</summary>
    public static byte[] Dispatch(PresenceHolderService service, ReadOnlyMemory<byte> request)
    {
        using var r = new BinaryReader(new MemoryStream(request.ToArray(), writable: false), Encoding.UTF8);
        var op = (PresenceOp)r.ReadByte();
        IRealmPresenceDirectory dir = service.Directory;
        IClusterSocialRouter router = service.Router;
        switch (op)
        {
            case PresenceOp.Register:
            {
                PresenceUpsertReply reply = service.Register(ReadEntry(r));
                return EncodeReply(w => WriteUpsertReply(w, reply));
            }

            case PresenceOp.Upsert:
            {
                PresenceUpsertReply reply = service.Upsert(ReadEntry(r));
                return EncodeReply(w => WriteUpsertReply(w, reply));
            }

            case PresenceOp.Remove:
            {
                ulong guid = r.ReadUInt64();
                long version = r.ReadInt64();
                long generation = r.ReadInt64();
                string worker = r.ReadString();
                PresenceMutationResult result = service.Remove(guid, version, generation, worker.Length == 0 ? null : worker);
                return EncodeReply(w => w.Write((byte)result));
            }

            case PresenceOp.DrainKicks:
            {
                IReadOnlyList<PresenceKick> kicks = service.DrainKicks(r.ReadString());
                return EncodeReply(w => WriteKicks(w, kicks));
            }

            case PresenceOp.FindByGuid:
            {
                RealmPresenceEntry? found = dir.FindByGuid(r.ReadUInt64());
                return EncodeReply(w => WriteNullableEntry(w, found));
            }

            case PresenceOp.FindByName:
            {
                RealmPresenceEntry? found = dir.FindByName(r.ReadString());
                return EncodeReply(w => WriteNullableEntry(w, found));
            }

            case PresenceOp.FindByAccount:
            {
                IReadOnlyList<RealmPresenceEntry> found = dir.FindByAccount(r.ReadInt32());
                return EncodeReply(w => WriteEntries(w, found));
            }

            case PresenceOp.FindByGuild:
            {
                IReadOnlyList<RealmPresenceEntry> found = dir.FindByGuild(r.ReadInt32());
                return EncodeReply(w => WriteEntries(w, found));
            }

            case PresenceOp.FindByGroup:
            {
                IReadOnlyList<RealmPresenceEntry> found = dir.FindByGroup(r.ReadInt32());
                return EncodeReply(w => WriteEntries(w, found));
            }

            case PresenceOp.QueryWho:
            {
                IReadOnlyList<RealmPresenceEntry> found = dir.QueryWho(ReadWho(r));
                return EncodeReply(w => WriteEntries(w, found));
            }

            case PresenceOp.Snapshot:
            {
                IReadOnlyList<RealmPresenceEntry> all = dir.Snapshot();
                return EncodeReply(w => WriteEntries(w, all));
            }

            case PresenceOp.Count:
            {
                int count = dir.Count;
                return EncodeReply(w => w.Write(count));
            }

            case PresenceOp.Route:
            {
                SocialRouteResult routed = router.Route(ReadMessage(r));
                return EncodeReply(w =>
                {
                    w.Write((byte)routed.Status);
                    WriteDeliveries(w, routed.Deliveries);
                });
            }

            case PresenceOp.JoinChannel:
            {
                string channel = r.ReadString();
                ulong guid = r.ReadUInt64();
                long generation = r.ReadInt64();
                bool ok = router.JoinChannel(channel, guid, generation);
                return EncodeReply(w => w.Write(ok));
            }

            case PresenceOp.LeaveChannel:
            {
                string channel = r.ReadString();
                ulong guid = r.ReadUInt64();
                long generation = r.ReadInt64();
                bool ok = router.LeaveChannel(channel, guid, generation);
                return EncodeReply(w => w.Write(ok));
            }

            case PresenceOp.DrainGateway:
            {
                IReadOnlyList<RoutedSocialDelivery> drained = router.DrainGatewayDeliveries(r.ReadString());
                return EncodeReply(w => WriteDeliveries(w, drained));
            }

            case PresenceOp.DrainWorker:
            {
                IReadOnlyList<RoutedSocialDelivery> drained = router.DrainWorkerDeliveries(r.ReadString());
                return EncodeReply(w => WriteDeliveries(w, drained));
            }

            case PresenceOp.Logout:
            {
                ulong guid = r.ReadUInt64();
                long version = r.ReadInt64();
                long generation = r.ReadInt64();
                string worker = r.ReadString();
                PresenceMutationResult result = router.LogoutWithOrderedFlush(guid, version, generation, worker, out IReadOnlyList<RoutedSocialDelivery> flushed);
                return EncodeReply(w =>
                {
                    w.Write((byte)result);
                    WriteDeliveries(w, flushed);
                });
            }

            default:
                throw new InvalidDataException($"unknown presence op {(byte)op}");
        }
    }
}
