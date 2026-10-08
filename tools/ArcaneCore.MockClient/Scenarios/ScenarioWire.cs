using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Game;

namespace ArcaneCore.MockClient.Scenarios;

/// <summary>
/// Independent request encodings and bounded response decoders for the synthetic build 5875 fixture.
/// Layout reference: gtker/wow_messages, commit 70abb9deff0bb63440d8aeb4386b820653e8a176,
/// wow_message_parser/wowm/world/{character_screen,quest,gameobject,login_logout}.
/// Reward and kill bodies: vmangos/core, commit 4b3d241cffe245a1f68da11380bce96c23db48c0,
/// src/game/Server/Packets/Quest.cpp. These decoders implement the build 5875 field layout.
/// No production packet builder or reader participates in these assertions.
/// </summary>
internal static partial class ScenarioWire
{
    internal const int MaximumInflatedBytes = 1024 * 1024;

    internal static byte[] CharacterCreate(string name)
    {
        byte[] text = Encoding.ASCII.GetBytes(name);
        byte[] payload = new byte[text.Length + 10];
        text.CopyTo(payload, 0);
        payload[text.Length + 1] = 1; // human
        payload[text.Length + 2] = 1; // warrior; gender and six appearance/outfit bytes are zero
        return payload;
    }

    internal static byte[] UInt32(uint value)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, value);
        return payload;
    }

    internal static byte[] Guid(ulong value)
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, value);
        return payload;
    }

    internal static byte[] GuidQuest(ulong guid, uint quest)
    {
        byte[] payload = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, guid);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), quest);
        return payload;
    }

    internal static byte[] GuidQuestChoice(ulong guid, uint quest, uint choice)
    {
        byte[] payload = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, guid);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), quest);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), choice);
        return payload;
    }

    internal static byte[] Ping(uint sequence, uint latency)
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), latency);
        return payload;
    }

    internal static IReadOnlyList<MockCharacter> CharacterList(byte[] payload)
    {
        var cursor = new WireCursor(payload);
        int count = cursor.Byte();
        if (count > 10)
        {
            throw new MockProtocolException("Synthetic character enumeration exceeded ten characters.");
        }

        var characters = new List<MockCharacter>(count);
        for (int index = 0; index < count; index++)
        {
            ulong guid = cursor.UInt64();
            string name = cursor.CString(12);
            byte race = cursor.Byte();
            byte characterClass = cursor.Byte();
            byte gender = cursor.Byte();
            byte[] appearance = cursor.Bytes(5);
            byte level = cursor.Byte();
            uint zone = cursor.UInt32();
            uint map = cursor.UInt32();
            float x = cursor.Single();
            float y = cursor.Single();
            float z = cursor.Single();
            uint guild = cursor.UInt32();
            uint flags = cursor.UInt32();
            byte firstLogin = cursor.Byte();
            uint petDisplay = cursor.UInt32();
            uint petLevel = cursor.UInt32();
            uint petFamily = cursor.UInt32();
            var equipment = new MockEquipment[20];
            for (int slot = 0; slot < equipment.Length; slot++)
            {
                equipment[slot] = new MockEquipment(cursor.UInt32(), cursor.Byte());
            }

            characters.Add(new MockCharacter(guid, name, race, characterClass, gender, appearance,
                level, zone, map, x, y, z, guild, flags, firstLogin, petDisplay, petLevel, petFamily, equipment));
        }

        cursor.End();
        return characters.AsReadOnly();
    }

    internal static MockLocation LoginLocation(byte[] payload)
    {
        var cursor = new WireCursor(payload);
        var location = new MockLocation(cursor.UInt32(), cursor.Single(), cursor.Single(), cursor.Single(), cursor.Single());
        cursor.End();
        return location;
    }

    /// <summary>
    /// The map's ships as a player entering the map gets them before its self create (vmangos Map::Add: SendInitTransports, then
    /// SendInitSelf; ArcaneCore TransportSystem.OnPlayerAdding): the has-transport byte set and nothing but game object creates. A self
    /// packet (it carries the player) or an ordinary update is not one.
    /// </summary>
    internal static bool IsMapTransportsPacket(byte[] body)
    {
        try
        {
            var cursor = new WireCursor(body);
            uint count = cursor.UInt32();
            if (count is 0 or > 128 || cursor.Byte() != 1)
            {
                return false;
            }

            for (uint index = 0; index < count; index++)
            {
                if (cursor.Byte() is not (2 or 3))
                {
                    return false;
                }

                _ = cursor.PackedGuid();
                if (cursor.Byte() != TypeId.GameObject)
                {
                    return false;
                }

                _ = ReadMovement(cursor);
                _ = ReadFields(cursor);
            }

            cursor.End();
            return true;
        }
        catch (MockProtocolException)
        {
            return false;
        }
    }

    internal static MockSelfCreate SelfCreate(byte[] body)
    {
        var cursor = new WireCursor(body);
        uint count = cursor.UInt32();
        Require(count is > 0 and <= 128, "Synthetic initial update exceeded the block limit.");
        Require(cursor.Byte() == 0, "Synthetic self create cannot contain a transport.");
        MockSelfCreate? self = null;
        for (uint index = 0; index < count; index++)
        {
            byte type = cursor.Byte();
            Require(type is 2 or 3, "Initial update must contain create blocks.");
            ulong guid = cursor.PackedGuid();
            byte objectType = cursor.Byte();
            if (objectType != 4)
            {
                _ = ReadMovement(cursor);
                _ = ReadFields(cursor);
                continue;
            }

            Require(type == 2, "Initial self update must use CREATE_OBJECT.");
            Require(self is null, "Synthetic initial update contains more than one self player.");
            self = ReadSelfCreate(cursor, guid);
        }

        cursor.End();
        return self ?? throw new MockProtocolException("Synthetic initial update lacks its self player.");
    }

    private static MockSelfCreate ReadSelfCreate(WireCursor cursor, ulong guid)
    {
        Require(cursor.Byte() == 0x71, "Self create must advertise SELF, ALL, LIVING and HAS_POSITION.");
        Require(cursor.UInt32() == 0, "Unmoving fixture must not advertise optional movement fields.");
        _ = cursor.UInt32(); // movement clock is dynamic
        float x = cursor.Single();
        float y = cursor.Single();
        float z = cursor.Single();
        float orientation = cursor.Single();
        Require(cursor.UInt32() == 0, "Unmoving fixture must have zero fall time.");
        for (int index = 0; index < 6; index++)
        {
            float speed = cursor.Single();
            Require(float.IsFinite(speed) && speed > 0, "Self create speed must be finite and positive.");
        }

        Require(cursor.UInt32() == 1, "Self create ALL trailer must equal one.");
        IReadOnlyDictionary<int, uint> values = ReadFields(cursor);
        Require(values.TryGetValue(0, out uint low) && values.TryGetValue(1, out uint high)
            && low == (uint)guid && high == (uint)(guid >> 32),
            "Self create object fields must contain its full GUID.");
        Require(values.GetValueOrDefault(2) == 0x19, "Self create object field type must be player.");
        Require(float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) && float.IsFinite(orientation),
            "Self create location must be finite.");
        return new MockSelfCreate(guid, x, y, z, orientation, values);
    }

    internal static IReadOnlyList<MockFieldUpdate> FieldUpdates(byte[] body)
    {
        var cursor = new WireCursor(body);
        uint count = cursor.UInt32();
        Require(count is > 0 and <= 128, "Object update exceeded the block limit.");
        ReadHasTransport(cursor);
        var updates = new List<MockFieldUpdate>();
        for (uint index = 0; index < count; index++)
        {
            byte type = cursor.Byte();
            if (type is 4 or 5)
            {
                uint guidCount = cursor.UInt32();
                Require(guidCount <= 128, "Object update exceeded the GUID list limit.");
                for (uint guidIndex = 0; guidIndex < guidCount; guidIndex++)
                {
                    _ = cursor.PackedGuid();
                }

                continue;
            }

            Require(type <= 3, "Object update contains an unknown block type.");
            ulong guid = cursor.PackedGuid();
            MockPosition? position = null;
            byte? objectType = null;
            if (type is 2 or 3)
            {
                objectType = cursor.Byte();
            }

            if (type != 0)
            {
                position = ReadMovement(cursor);
            }

            if (type != 1)
            {
                IReadOnlyDictionary<int, uint> values = ReadFields(cursor);
                if (objectType is TypeId.Unit or TypeId.Player)
                {
                    // Full creates establish zero for omitted values. Incremental updates
                    // cannot establish that baseline for an object we have not seen created.
                    var initial = new Dictionary<int, uint>(values);
                    initial.TryAdd(UpdateFields.UnitFieldTarget, 0);
                    initial.TryAdd(UpdateFields.UnitFieldTarget + 1, 0);
                    initial.TryAdd(UpdateFields.UnitFieldFlags, 0);
                    values = initial;
                }
                updates.Add(new MockFieldUpdate(guid, values, position));
            }
        }

        cursor.End();
        return updates.AsReadOnly();
    }

    internal static MockQuestgiverStatus QuestgiverStatus(byte[] payload)
    {
        var cursor = new WireCursor(payload);
        var result = new MockQuestgiverStatus(cursor.UInt64(), cursor.UInt32());
        cursor.End();
        return result;
    }

    internal static MockQuestDetails QuestDetails(byte[] payload)
    {
        var cursor = new WireCursor(payload);
        ulong guid = cursor.UInt64();
        uint quest = cursor.UInt32();
        string title = cursor.CString(1024);
        string details = cursor.CString(8192);
        string objectives = cursor.CString(4096);
        uint activateAccept = cursor.UInt32();
        MockQuestReward[] choices = ReadRewards(cursor, 6);
        MockQuestReward[] rewards = ReadRewards(cursor, 4);
        int money = unchecked((int)cursor.UInt32());
        uint spell = cursor.UInt32();
        uint emoteCount = cursor.UInt32();
        Require(emoteCount <= 4, "Quest details exceed four vanilla emotes.");
        var emotes = new MockQuestEmote[(int)emoteCount];
        for (int index = 0; index < emotes.Length; index++)
        {
            emotes[index] = new MockQuestEmote(cursor.UInt32(), cursor.UInt32());
        }

        cursor.End();
        return new MockQuestDetails(guid, quest, title, details, objectives, activateAccept, choices, rewards, money, spell, emotes);
    }

    internal static MockQuestOfferReward QuestOfferReward(byte[] payload)
    {
        var cursor = new WireCursor(payload);
        ulong guid = cursor.UInt64();
        uint quest = cursor.UInt32();
        string title = cursor.CString(1024);
        string text = cursor.CString(8192);
        uint enableNext = cursor.UInt32();
        uint emoteCount = cursor.UInt32();
        Require(emoteCount <= 4, "Quest offer reward emote count exceeds its vanilla limit.");
        var emotes = new MockQuestEmote[(int)emoteCount];
        for (int index = 0; index < emotes.Length; index++)
        {
            // Offer reward reverses the emote/delay order used by quest details.
            uint delay = cursor.UInt32();
            emotes[index] = new MockQuestEmote(cursor.UInt32(), delay);
        }

        MockQuestReward[] choices = ReadRewards(cursor, 6);
        MockQuestReward[] rewards = ReadRewards(cursor, 4);
        int money = unchecked((int)cursor.UInt32());
        uint flags = cursor.UInt32();
        uint spell = cursor.UInt32();
        cursor.End();
        return new MockQuestOfferReward(guid, quest, title, text, enableNext, emotes, choices, rewards, money, flags, spell);
    }

    internal static MockQuestComplete QuestComplete(byte[] payload)
    {
        var cursor = new WireCursor(payload);
        uint quest = cursor.UInt32();
        uint type = cursor.UInt32();
        uint experience = cursor.UInt32();
        uint money = cursor.UInt32();
        uint rewardCount = cursor.UInt32();
        Require(rewardCount <= 4, "Quest completion reward count exceeds its vanilla limit.");
        var rewards = new MockQuestCompletedReward[(int)rewardCount];
        for (int index = 0; index < rewards.Length; index++)
        {
            rewards[index] = new MockQuestCompletedReward(cursor.UInt32(), cursor.UInt32());
        }

        cursor.End();
        return new MockQuestComplete(quest, type, experience, money, rewards);
    }

    internal static MockQuestKill QuestKill(byte[] payload)
    {
        var cursor = new WireCursor(payload);
        var result = new MockQuestKill(cursor.UInt32(), cursor.UInt32(), cursor.UInt32(), cursor.UInt32(), cursor.UInt64());
        cursor.End();
        return result;
    }

    private static MockQuestReward[] ReadRewards(WireCursor cursor, uint maximum)
    {
        uint count = cursor.UInt32();
        Require(count <= maximum, "Quest reward count exceeds its vanilla limit.");
        var rewards = new MockQuestReward[(int)count];
        for (int index = 0; index < rewards.Length; index++)
        {
            rewards[index] = new MockQuestReward(cursor.UInt32(), cursor.UInt32(), cursor.UInt32());
        }

        return rewards;
    }

    private static IReadOnlyDictionary<int, uint> ReadFields(WireCursor cursor)
    {
        int maskCount = cursor.Byte();
        Require(maskCount <= 64, "Object field mask exceeds the decoder limit.");
        uint[] masks = new uint[maskCount];
        for (int index = 0; index < masks.Length; index++)
        {
            masks[index] = cursor.UInt32();
        }

        var values = new Dictionary<int, uint>();
        for (int word = 0; word < masks.Length; word++)
        {
            for (int bit = 0; bit < 32; bit++)
            {
                if ((masks[word] & (1u << bit)) != 0)
                {
                    values.Add((word * 32) + bit, cursor.UInt32());
                }
            }
        }

        return values;
    }

    internal static MockMonsterMove MonsterMove(byte[] payload)
    {
        var cursor = new WireCursor(payload);
        ulong guid = cursor.PackedGuid();
        Require(guid != 0, "Monster move must identify a mover.");
        MockPosition start = new(cursor.Single(), cursor.Single(), cursor.Single());
        Require(start.IsFinite, "Monster move start must be finite.");
        uint splineId = cursor.UInt32();
        byte type = cursor.Byte();
        if (type == 1)
        {
            cursor.End();
            return new MockMonsterMove(guid, start, null, splineId, 0, 0, IsStop: true, Linear: false);
        }

        Require(type is 0 or 2 or 3 or 4, "Monster move has an unknown move type.");
        if (type == 2)
        {
            MockPosition facing = new(cursor.Single(), cursor.Single(), cursor.Single());
            Require(facing.IsFinite, "Monster move facing spot must be finite.");
        }
        else if (type == 3)
        {
            Require(cursor.UInt64() != 0, "Monster move facing target must identify a target.");
        }
        else if (type == 4)
        {
            float angle = cursor.Single();
            Require(float.IsFinite(angle), "Monster move facing angle must be finite.");
        }

        _ = cursor.UInt32(); // spline flags
        uint duration = cursor.UInt32();
        uint count = cursor.UInt32();
        Require(count is >= 1 and <= 128, "Monster move point count is outside the bounded decoder limit.");
        MockPosition destination = new(cursor.Single(), cursor.Single(), cursor.Single());
        Require(destination.IsFinite, "Monster move destination must be finite.");
        for (uint i = 1; i < count; i++) _ = cursor.UInt32();
        cursor.End();
        return new MockMonsterMove(guid, start, destination, splineId, duration, count, IsStop: false, Linear: count == 1);
    }

    internal static IReadOnlyList<ulong> RemovedGuids(byte[] body)
    {
        var cursor = new WireCursor(body);
        uint count = cursor.UInt32();
        Require(count is > 0 and <= 128, "Object update exceeded the block limit.");
        ReadHasTransport(cursor);
        var removed = new List<ulong>();
        for (uint index = 0; index < count; index++)
        {
            byte type = cursor.Byte();
            if (type is 4 or 5)
            {
                uint guidCount = cursor.UInt32();
                Require(guidCount <= 128, "Object update exceeded the GUID list limit.");
                for (uint guidIndex = 0; guidIndex < guidCount; guidIndex++) removed.Add(cursor.PackedGuid());
                continue;
            }

            Require(type <= 3, "Object update contains an unknown block type.");
            _ = cursor.PackedGuid();
            if (type is 2 or 3) _ = cursor.Byte();
            if (type != 0) _ = ReadMovement(cursor);
            if (type != 1) _ = ReadFields(cursor);
        }

        cursor.End();
        return removed;
    }

    /// <summary>
    /// The u8 after the block count (vmangos UpdateData::BuildPacket, m_hasTransport): a flag, 1 on the server's ship packets
    /// (Map::SendInitTransports, GenericTransport::SendCreateUpdateToMap / SendOutOfRangeUpdateToMap; ArcaneCore TransportPackets) and on
    /// the self packet of a player aboard. Nothing else follows it, so the blocks decode the same either way; another value is refused.
    /// </summary>
    private static void ReadHasTransport(WireCursor cursor)
        => Require(cursor.Byte() is 0 or 1, "Object update has-transport byte must be 0 or 1.");

    private static MockPosition? ReadMovement(WireCursor cursor)
    {
        byte flags = cursor.Byte();
        Require((flags & 0x80) == 0, "Object movement contains an unknown update flag.");
        MockPosition? position = null;
        if ((flags & 0x20) != 0)
        {
            uint movement = cursor.UInt32();
            Require((movement & 0x00400000) == 0, "Synthetic object must not advertise an unsupported spline.");
            _ = cursor.UInt32();
            position = new MockPosition(cursor.Single(), cursor.Single(), cursor.Single());
            _ = cursor.Single(); // orientation
            Require(position.IsFinite, "Object movement position must be finite.");
            if ((movement & 0x02000000) != 0)
            {
                _ = cursor.Bytes(24); // full transport GUID plus position/orientation
            }

            if ((movement & 0x00200000) != 0)
            {
                _ = cursor.Single();
            }

            _ = cursor.UInt32(); // fall time
            if ((movement & 0x00002000) != 0)
            {
                _ = cursor.Bytes(16);
            }

            if ((movement & 0x04000000) != 0)
            {
                _ = cursor.Single();
            }

            _ = cursor.Bytes(24); // six movement speeds
        }
        else if ((flags & 0x40) != 0)
        {
            position = new MockPosition(cursor.Single(), cursor.Single(), cursor.Single());
            _ = cursor.Single();
            Require(position.IsFinite, "Object position must be finite.");
        }

        if ((flags & 0x08) != 0)
        {
            _ = cursor.UInt32();
        }

        if ((flags & 0x10) != 0)
        {
            _ = cursor.UInt32();
        }

        if ((flags & 0x04) != 0)
        {
            _ = cursor.PackedGuid();
        }

        if ((flags & 0x02) != 0)
        {
            _ = cursor.UInt32();
        }

        return position;
    }

    internal static byte[] InflateUpdate(byte[] payload)
    {
        Require(payload.Length > 4, "Compressed update lacks its size or stream.");
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        Require(size is >= 5 and <= MaximumInflatedBytes, "Compressed update exceeds the one MiB decoder limit.");
        using var input = new MemoryStream(payload, 4, payload.Length - 4, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        byte[] body = new byte[(int)size];
        zlib.ReadExactly(body);
        Require(zlib.ReadByte() == -1, "Compressed update expands beyond its declared size.");
        return body;
    }

    internal static MockQuestQuery QuestQuery(byte[] payload)
    {
        var cursor = new WireCursor(payload);
        uint id = cursor.UInt32();
        uint method = cursor.UInt32();
        int level = unchecked((int)cursor.UInt32());
        uint[] header = new uint[12];
        for (int index = 0; index < header.Length; index++)
        {
            header[index] = cursor.UInt32();
        }

        uint[] rewards = new uint[20];
        for (int index = 0; index < rewards.Length; index++)
        {
            rewards[index] = cursor.UInt32();
        }

        uint pointMap = cursor.UInt32();
        float pointX = cursor.Single();
        float pointY = cursor.Single();
        uint pointOption = cursor.UInt32();
        string title = cursor.CString(1024);
        string objectives = cursor.CString(4096);
        string details = cursor.CString(8192);
        string endText = cursor.CString(4096);
        var requirements = new MockQuestObjective[4];
        for (int index = 0; index < requirements.Length; index++)
        {
            requirements[index] = new MockQuestObjective(cursor.UInt32(), cursor.UInt32(), cursor.UInt32(), cursor.UInt32());
        }

        string[] objectiveTexts = new string[4];
        for (int index = 0; index < objectiveTexts.Length; index++)
        {
            objectiveTexts[index] = cursor.CString(4096);
        }

        cursor.End();
        return new MockQuestQuery(id, method, level, header, rewards, pointMap, pointX, pointY, pointOption,
            title, objectives, details, endText, requirements, objectiveTexts);
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new MockProtocolException(message);
        }
    }

    private sealed class WireCursor(byte[] payload)
    {
        private int _offset;

        internal byte Byte()
        {
            Ensure(1);
            return payload[_offset++];
        }

        internal uint UInt32()
        {
            Ensure(4);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(_offset, 4));
            _offset += 4;
            return value;
        }

        internal ulong UInt64()
        {
            Ensure(8);
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(_offset, 8));
            _offset += 8;
            return value;
        }

        internal float Single() => BitConverter.UInt32BitsToSingle(UInt32());

        internal byte[] Bytes(int count)
        {
            Ensure(count);
            byte[] value = payload.AsSpan(_offset, count).ToArray();
            _offset += count;
            return value;
        }

        internal ulong PackedGuid()
        {
            byte mask = Byte();
            ulong value = 0;
            for (int index = 0; index < 8; index++)
            {
                if ((mask & (1 << index)) != 0)
                {
                    value |= (ulong)Byte() << (index * 8);
                }
            }

            return value;
        }

        internal string CString(int maximumBytes)
        {
            int end = Array.IndexOf(payload, (byte)0, _offset);
            Require(end >= _offset && end - _offset <= maximumBytes, "Response string is unterminated or oversized.");
            string text = new UTF8Encoding(false, true).GetString(payload, _offset, end - _offset);
            _offset = end + 1;
            return text;
        }

        internal void End() => Require(_offset == payload.Length, $"Response has {payload.Length - _offset} trailing bytes.");

        private void Ensure(int count) => Require(count >= 0 && payload.Length - _offset >= count,
            $"Response is truncated at byte {_offset}.");
    }
}

internal sealed record MockEquipment(uint DisplayId, byte InventoryType);

internal sealed record MockCharacter(ulong Guid, string Name, byte Race, byte Class, byte Gender, byte[] Appearance,
    byte Level, uint Zone, uint Map, float X, float Y, float Z, uint Guild, uint Flags, byte FirstLogin,
    uint PetDisplay, uint PetLevel, uint PetFamily, IReadOnlyList<MockEquipment> Equipment);

internal sealed record MockLocation(uint Map, float X, float Y, float Z, float Orientation);

internal sealed record MockSelfCreate(ulong Guid, float X, float Y, float Z, float Orientation, IReadOnlyDictionary<int, uint> Fields);

internal sealed record MockQuestObjective(uint CreatureOrGameObject, uint CreatureCount, uint ItemId, uint ItemCount);

internal sealed record MockQuestQuery(uint Id, uint Method, int Level, uint[] Header, uint[] Rewards,
    uint PointMap, float PointX, float PointY, uint PointOption, string Title, string Objectives, string Details,
    string EndText, MockQuestObjective[] Requirements, string[] ObjectiveTexts);

internal sealed record MockFieldUpdate(ulong Guid, IReadOnlyDictionary<int, uint> Fields, MockPosition? Position = null);

internal sealed record MockPosition(float X, float Y, float Z)
{
    internal bool IsFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z);
}

internal sealed record MockMonsterMove(ulong Guid, MockPosition Start, MockPosition? Destination, uint SplineId,
    uint DurationMs, uint PointCount, bool IsStop, bool Linear);

internal sealed record MockQuestgiverStatus(ulong Guid, uint Status);

internal sealed record MockQuestReward(uint ItemId, uint Count, uint DisplayId);

internal sealed record MockQuestEmote(uint Id, uint Delay);

internal sealed record MockQuestDetails(ulong Guid, uint QuestId, string Title, string Details, string Objectives,
    uint ActivateAccept, MockQuestReward[] Choices, MockQuestReward[] Rewards, int Money, uint Spell, MockQuestEmote[] Emotes);

internal sealed record MockQuestOfferReward(ulong Guid, uint QuestId, string Title, string Text, uint EnableNext,
    MockQuestEmote[] Emotes, MockQuestReward[] Choices, MockQuestReward[] Rewards, int Money, uint Flags, uint Spell);

internal sealed record MockQuestCompletedReward(uint ItemId, uint Count);

internal sealed record MockQuestComplete(uint QuestId, uint Type, uint Experience, uint Money, MockQuestCompletedReward[] Rewards);

internal sealed record MockQuestKill(uint QuestId, uint CreatureId, uint Count, uint RequiredCount, ulong Guid);
