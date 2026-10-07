using ArcaneCore.Game;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;

namespace ArcaneCore.MockClient.Scenarios;

/// <summary>Optional normal corpse-loot proof for a completed combat target.</summary>
internal static class StartingZoneLoot
{
    private const int PlayerMoneyField = 0x0498;

    internal sealed record LootSlot(byte Slot, uint ItemId, uint Count, uint DisplayId,
        uint RandomSuffix, int RandomProperty, byte SlotType);

    internal sealed record LootWindow(ulong Guid, byte Type, uint Gold, IReadOnlyList<LootSlot> Items);

    internal sealed record LootEvidence(ulong Target, uint GoldObserved, uint GoldCollected,
        IReadOnlyList<LootSlot> ItemsCollected);

    internal static LootWindow ParseWindow(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new PacketReader(body);
            ulong guid = reader.ReadUInt64();
            byte type = reader.ReadByte();
            if (guid == 0 || type != 1) throw new MockProtocolException("loot response had an invalid GUID or non-corpse type");
            uint gold = reader.ReadUInt32();
            byte count = reader.ReadByte();
            if (count > 32) throw new MockProtocolException("loot response exceeded 32 items");
            var items = new List<LootSlot>(count);
            var slots = new HashSet<byte>();
            for (int index = 0; index < count; index++)
            {
                byte slot = reader.ReadByte();
                if (!slots.Add(slot)) throw new MockProtocolException("loot response repeated an item slot");
                uint item = reader.ReadUInt32();
                uint itemCount = reader.ReadUInt32();
                uint display = reader.ReadUInt32();
                uint suffix = reader.ReadUInt32();
                int property = reader.ReadInt32();
                byte slotType = reader.ReadByte();
                if (item == 0 || itemCount == 0) throw new MockProtocolException("loot response contained an empty item");
                items.Add(new LootSlot(slot, item, itemCount, display, suffix, property, slotType));
            }

            if (reader.Remaining != 0) throw new MockProtocolException("loot response had trailing bytes");
            return new LootWindow(guid, type, gold, items.AsReadOnly());
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new MockProtocolException("malformed loot response");
        }
    }

    internal static async Task<LootEvidence> RunAsync(ScenarioConnection connection, ulong player,
        ulong corpse, (float X, float Y, float Z) playerPosition, uint clientTime,
        CancellationToken cancellationToken)
    {
        if (corpse == 0) throw new MockProtocolException("loot corpse GUID is empty");
        var budget = new StartingZoneCombat.CombatBudget();
        await StartingZoneLootApproach.ApproachAsync(connection, budget, corpse, playerPosition,
            clientTime, cancellationToken).ConfigureAwait(false);
        LootWindow window = await OpenAsync(connection, budget, corpse, cancellationToken).ConfigureAwait(false);

        var collected = new List<LootSlot>();
        foreach (LootSlot item in window.Items.Where(item => item.SlotType == 0))
        {
            await connection.SendAsync(WorldOpcode.CmsgAutostoreLootItem, [item.Slot], cancellationToken).ConfigureAwait(false);
            bool removed = false, pushed = false;
            while (!removed || !pushed)
            {
                WorldFrame frame = await budget.ReadAsync(connection, cancellationToken).ConfigureAwait(false);
                if (frame.Opcode == (ushort)WorldOpcode.SmsgLootRemoved)
                {
                    if (frame.Payload.Length != 1 || frame.Payload[0] != item.Slot)
                        throw new MockProtocolException("loot removed packet did not match the requested slot");
                    removed = true;
                }
                else if (frame.Opcode == (ushort)WorldOpcode.SmsgItemPushResult)
                {
                    ItemPush push = ParseItemPush(frame.Payload);
                    if (push.PlayerGuid != player || push.ItemId != item.ItemId || push.Count != item.Count)
                        throw new MockProtocolException("item push did not match the looted item");
                    pushed = true;
                }
            }

            collected.Add(item);
        }

        uint collectedGold = 0;
        if (window.Gold > 0)
        {
            uint beforeMoney = MoneyBaseline(connection, player);
            ulong expectedMoney = (ulong)beforeMoney + window.Gold;
            if (expectedMoney > uint.MaxValue) throw new MockProtocolException("loot money delta overflowed the player money field");
            await connection.SendAsync(WorldOpcode.CmsgLootMoney, [], cancellationToken).ConfigureAwait(false);
            collectedGold = await WaitForMoneyAsync(connection, player, beforeMoney, window.Gold, budget, cancellationToken).ConfigureAwait(false);
        }

        await connection.SendAsync(WorldOpcode.CmsgLootRelease, ScenarioWire.Guid(corpse), cancellationToken).ConfigureAwait(false);
        byte[] release = await ReadUntilAsync(connection, budget, WorldOpcode.SmsgLootReleaseResponse, cancellationToken).ConfigureAwait(false);
        if (release.Length != 9 || BitConverter.ToUInt64(release, 0) != corpse || release[8] != 1)
            throw new MockProtocolException("loot release response did not match the corpse");
        return new LootEvidence(corpse, window.Gold, collectedGold, collected.AsReadOnly());
    }

    internal static async Task<uint> WaitForMoneyAsync(ScenarioConnection connection, ulong player, uint beforeMoney,
        uint gold, StartingZoneCombat.CombatBudget budget, CancellationToken cancellationToken)
    {
        ulong expectedMoney = (ulong)beforeMoney + gold;
        if (expectedMoney > uint.MaxValue) throw new MockProtocolException("loot money delta overflowed the player money field");
        bool clearSeen = false;
        while (!clearSeen || !connection.FieldsOf(player).TryGetValue(PlayerMoneyField, out uint currentMoney)
            || currentMoney != expectedMoney)
        {
            WorldFrame frame = await budget.ReadAsync(connection, cancellationToken).ConfigureAwait(false);
            if (frame.Opcode == (ushort)WorldOpcode.SmsgLootMoneyNotify)
            {
                if (frame.Payload.Length != 4 || new PacketReader(frame.Payload).ReadUInt32() != gold)
                    throw new MockProtocolException("loot money notify did not match loot gold");
            }
            else if (frame.Opcode == (ushort)WorldOpcode.SmsgLootClearMoney)
            {
                if (frame.Payload.Length != 0) throw new MockProtocolException("loot clear-money packet had a body");
                clearSeen = true;
            }
        }

        return gold;
    }

    internal static uint MoneyBaseline(ScenarioConnection connection, ulong player)
    {
        IReadOnlyDictionary<int, uint> fields = connection.FieldsOf(player);
        if (fields.TryGetValue(PlayerMoneyField, out uint money)) return money;
        if (!fields.TryGetValue(UpdateFields.ObjectFieldType, out uint type)
            || (type & TypeMask.Player) == 0
            || connection.PositionOf(player) is null)
            throw new MockProtocolException("player money baseline was unknown and the observed object was not a positioned player");
        return 0;
    }

    internal static async Task<LootWindow> OpenAsync(ScenarioConnection connection,
        StartingZoneCombat.CombatBudget budget, ulong corpse, CancellationToken cancellationToken)
    {
        await connection.SendAsync(WorldOpcode.CmsgLoot, ScenarioWire.Guid(corpse), cancellationToken).ConfigureAwait(false);
        while (true)
        {
            WorldFrame frame = await budget.ReadAsync(connection, cancellationToken).ConfigureAwait(false);
            if (frame.Opcode == (ushort)WorldOpcode.SmsgLootResponse)
            {
                LootWindow window = ParseWindow(frame.Payload);
                if (window.Guid != corpse || window.Type != 1)
                    throw new MockProtocolException("loot response did not match the requested corpse window");
                return window;
            }
            if (frame.Opcode == (ushort)WorldOpcode.SmsgLootReleaseResponse)
            {
                if (frame.Payload.Length != 9 || BitConverter.ToUInt64(frame.Payload, 0) != corpse || frame.Payload[8] != 1)
                    throw new MockProtocolException("malformed corpse loot-open refusal");
                throw new MockProtocolException("corpse loot open was refused");
            }
        }
    }

    private static async Task<byte[]> ReadUntilAsync(ScenarioConnection connection, StartingZoneCombat.CombatBudget budget,
        WorldOpcode opcode, CancellationToken cancellationToken)
    {
        while (true)
        {
            WorldFrame frame = await budget.ReadAsync(connection, cancellationToken).ConfigureAwait(false);
            if (frame.Opcode == (ushort)opcode) return frame.Payload;
        }
    }

    internal static ItemPush ParseItemPush(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new PacketReader(body);
            ulong player = reader.ReadUInt64();
            uint source = reader.ReadUInt32();
            uint creation = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            _ = reader.ReadByte(); _ = reader.ReadUInt32();
            uint item = reader.ReadUInt32();
            _ = reader.ReadUInt32(); _ = reader.ReadUInt32();
            uint count = reader.ReadUInt32();
            if (body.Length != 41 || player == 0 || source != 0 || creation != 0 || item == 0 || count == 0 || reader.Remaining != 0)
                throw new MockProtocolException("malformed item push result");
            return new ItemPush(player, item, count);
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new MockProtocolException("malformed item push result");
        }
    }

    internal readonly record struct ItemPush(ulong PlayerGuid, uint ItemId, uint Count);
}
