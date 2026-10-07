using ArcaneCore.Game;
using ArcaneCore.Game.Items;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using ArcaneCore.Game.Spells;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;

namespace ArcaneCore.MockClient.Scenarios;

/// <summary>Bounded normal item-use proof for private Human Warrior starter food.</summary>
internal static class StartingZoneFood
{
    private const uint FoodEntry = 117;
    private const uint FoodSpell = 433;

    internal sealed record FoodEvidence(ulong ItemGuid, byte Bag, byte Slot, uint StackBefore,
        uint StackAfter, uint HealthBefore, uint HealthAfter, bool SpellStarted, bool AuraObserved,
        bool AuraEnded, bool FoodCanceled, bool FullHealth);

    internal static async Task<FoodEvidence> RunAsync(ScenarioConnection connection, ulong character,
        StartingZoneCombat.CombatBudget budget, CancellationToken cancellationToken, bool emitProgress = true,
        (ulong Guid, byte Slot, uint Stack)? expectedFood = null)
    {
        IReadOnlyDictionary<int, uint> playerFields = connection.FieldsOf(character);
        (ulong itemGuid, byte slot, uint stackBefore) = FindFood(playerFields, character, connection.FieldsOf);
        if (expectedFood is { } expected) RequireFoodIdentity((itemGuid, slot, stackBefore), expected);
        uint healthBefore = playerFields.GetValueOrDefault(UpdateFields.UnitFieldHealth);
        uint maxHealth = playerFields.GetValueOrDefault(UpdateFields.UnitFieldMaxhealth);
        if (healthBefore == 0 || maxHealth == 0)
            throw new MockProtocolException("food proof requires observed live player health");
        if (healthBefore >= maxHealth)
            throw new MockProtocolException("food proof requires damaged player health");
        if (stackBefore == 0)
            throw new MockProtocolException("food item has no remaining stack");

        bool spellStarted = false;
        bool sawAura = false;
        uint stackAfter = stackBefore;
        uint healthAfter = healthBefore;
        var elapsed = Stopwatch.StartNew();
        string phase = "sit-send";
        byte? castResult = null;
        byte? inventoryResult = null;
        CancellationToken cleanupToken = default;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        void Progress(string state, string? cancellationOrigin = null)
        {
            if (!emitProgress) return;
            IReadOnlyDictionary<int, uint> current = connection.FieldsOf(character);
            Console.WriteLine("starting-zone food-progress=" + JsonSerializer.Serialize(new
            {
                Phase = state, ElapsedMs = elapsed.ElapsedMilliseconds, ItemGuid = itemGuid, Slot = slot,
                StackBefore = stackBefore, StackAfter = stackAfter, HealthBefore = healthBefore,
                HealthAfter = healthAfter, MaxHealth = maxHealth, SpellStarted = spellStarted, AuraObserved = sawAura,
                AuraActive = HasFoodAura(current), CastResult = castResult, InventoryResult = inventoryResult,
                PlayerFlags = current.TryGetValue(UpdateFields.UnitFieldFlags, out uint flags) ? (uint?)flags : null,
                CancellationOrigin = cancellationOrigin, budget.Frames, budget.Bytes,
            }));
        }
        try
        {
            Progress(phase);
            byte[] sit = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(sit, 1);
            await connection.SendAsync(WorldOpcode.CmsgStandstatechange, sit, deadline.Token).ConfigureAwait(false);
            phase = "item-send";
            Progress(phase);
            await connection.SendAsync(WorldOpcode.CmsgUseItem, BuildUseItemPayload(slot), deadline.Token).ConfigureAwait(false);
            phase = "observe";
            Progress(phase);
            while (true)
            {
                WorldFrame frame = await budget.ReadAsync(connection, deadline.Token).ConfigureAwait(false);
                if (frame.Opcode == (ushort)WorldOpcode.SmsgSpellStart)
                {
                    bool started = IsOwnFoodStart(frame.Payload, character);
                    if (started && !spellStarted)
                    {
                        spellStarted = true;
                        Progress("spell-start");
                    }
                }
                if (frame.Opcode == (ushort)WorldOpcode.SmsgCastResult && frame.Payload.Length >= 5
                    && BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload) == FoodSpell)
                {
                    castResult = frame.Payload[4];
                    Progress("cast-result");
                    if (castResult != 0)
                        throw new MockProtocolException($"starter food cast was refused: result={castResult}");
                }
                if (frame.Opcode == (ushort)WorldOpcode.SmsgInventoryChangeFailure && frame.Payload.Length > 0
                    && frame.Payload[0] != 0)
                {
                    inventoryResult = frame.Payload[0];
                    // EQUIP_ERR_NONE (59) only clears the client's gray item state; a separate
                    // cast result carries the rejection (vmangos SpellHandler.cpp:127-135).
                    bool reset = inventoryResult == (byte)InventoryResult.None;
                    Progress(reset ? "inventory-reset" : "inventory-refusal");
                    if (!reset)
                        throw new MockProtocolException($"starter food inventory request was refused: result={inventoryResult}; item={itemGuid}; slot={slot}");
                }

                IReadOnlyDictionary<int, uint> itemFields = connection.FieldsOf(itemGuid);
                stackAfter = itemFields.GetValueOrDefault(UpdateFields.ItemFieldStackCount);
                if (stackAfter < stackBefore - 1)
                    throw new MockProtocolException("one food command consumed more than one item");
                playerFields = connection.FieldsOf(character);
                healthAfter = playerFields.GetValueOrDefault(UpdateFields.UnitFieldHealth, healthAfter);
                maxHealth = playerFields.GetValueOrDefault(UpdateFields.UnitFieldMaxhealth, maxHealth);
                bool active = HasFoodAura(playerFields);
                sawAura |= active;
                if (healthAfter == 0)
                    throw new MockProtocolException("player died during the food observation");
                if (spellStarted && sawAura && stackAfter == stackBefore - 1 && healthAfter > healthBefore
                    && (healthAfter >= maxHealth || !active))
                    break;
            }

            Progress("observation-complete");
            bool activeBeforeStand = HasFoodAura(connection.FieldsOf(character));
            phase = "stand-send";
            byte[] stand = new byte[4];
            await connection.SendAsync(WorldOpcode.CmsgStandstatechange, stand, deadline.Token).ConfigureAwait(false);
            using var cancelBound = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            cancelBound.CancelAfter(TimeSpan.FromSeconds(2));
            cleanupToken = cancelBound.Token;
            phase = "stand-removal";
            await budget.DrainQuietAsync(connection, cancelBound.Token).ConfigureAwait(false);
            while (HasFoodAura(connection.FieldsOf(character)))
                _ = await budget.ReadAsync(connection, cancelBound.Token).ConfigureAwait(false);
            Progress("complete");
            return new FoodEvidence(itemGuid, InventorySlots.Bag0, slot, stackBefore, stackAfter,
                healthBefore, healthAfter, spellStarted, sawAura, AuraEnded: true,
                FoodCanceled: activeBeforeStand, FullHealth: healthAfter >= maxHealth);
        }
        catch (OperationCanceledException)
        {
            string origin = cancellationToken.IsCancellationRequested ? "parent-scenario"
                : deadline.IsCancellationRequested ? "food-observation-45s"
                : cleanupToken.IsCancellationRequested ? "stand-removal-2s" : "transport-operation";
            Progress(phase + "-cancelled", origin);
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new MockProtocolException($"StarterFoodCanceled: phase={phase}; origin={origin}; "
                + $"elapsedMs={elapsed.ElapsedMilliseconds}; spellStarted={spellStarted}; auraObserved={sawAura}; "
                + $"stack={stackBefore}->{stackAfter}; health={healthBefore}->{healthAfter}");
        }
    }

    internal static byte[] BuildUseItemPayload(byte slot)
    {
        if (slot < InventorySlots.ItemStart || slot >= InventorySlots.ItemStart + 16)
            throw new MockProtocolException("starter food requires an actual backpack slot");
        var writer = new PacketWriter(5);
        writer.WriteByte(InventorySlots.Bag0);
        writer.WriteByte(slot);
        writer.WriteByte(0);
        SpellCastTargets.ForSelf().Write(writer);
        return writer.ToArray();
    }

    internal static void RequireFoodIdentity((ulong Guid, byte Slot, uint Stack) current,
        (ulong Guid, byte Slot, uint Stack) expected)
    {
        if (current != expected)
            throw new MockProtocolException("Observed food differs from the validated item, slot or stack.");
    }

    internal static bool IsOwnFoodStart(ReadOnlySpan<byte> body, ulong character)
        => StartingZoneCombat.IsSpellStart(body, FoodSpell, character);

    internal static bool HasFoodAura(IReadOnlyDictionary<int, uint> fields)
    {
        for (int slot = 0; slot < 32; slot++)
            if (fields.GetValueOrDefault(UpdateFields.UnitFieldAura + slot) == FoodSpell)
                return true;
        return false;
    }

    internal static (ulong Guid, byte Slot, uint Stack) FindFood(IReadOnlyDictionary<int, uint> fields,
        ulong character, Func<ulong, IReadOnlyDictionary<int, uint>> resolveFields)
    {
        for (int index = 0; index < 16; index++)
        {
            int field = UpdateFields.PlayerFieldPackSlot1 + (index * 2);
            ulong guid = fields.GetValueOrDefault(field) | ((ulong)fields.GetValueOrDefault(field + 1) << 32);
            if (guid == 0)
                continue;

            IReadOnlyDictionary<int, uint> itemFields = resolveFields(guid);
            if (itemFields.GetValueOrDefault(UpdateFields.ObjectFieldEntry) == FoodEntry)
            {
                ulong owner = itemFields.GetValueOrDefault(UpdateFields.ItemFieldOwner)
                    | ((ulong)itemFields.GetValueOrDefault(UpdateFields.ItemFieldOwner + 1) << 32);
                if (owner != character)
                    throw new MockProtocolException("starter food pointer resolved to another character's item");
                byte slot = checked((byte)(InventorySlots.ItemStart + index));
                return (guid, slot, itemFields.GetValueOrDefault(UpdateFields.ItemFieldStackCount));
            }
        }

        throw new MockProtocolException("starter food item 117 was not observed in backpack fields");
    }
}
