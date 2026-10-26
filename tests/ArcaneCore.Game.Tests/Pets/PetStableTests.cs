using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>The hunter pet stable (vmangos NPCHandler.cpp SendStablePet, HandleStablePet, HandleUnstablePet, HandleBuyStableSlot, HandleStableSwapPet).</summary>
public sealed class PetStableTests
{
    private const uint BeastEntry = 5221;
    private static readonly ObjectGuid StableMaster = ObjectGuid.WithEntry(HighGuid.Unit, 9984, 77);

    private sealed class MemoryStableStore : IPetStableStore
    {
        public byte Slots { get; set; }
        public Dictionary<byte, PersistentPetSnapshot> Stabled { get; } = [];
        public PersistentPetSnapshot? Current { get; set; }
        public bool Fail { get; set; }

        public Task<PetStableState> LoadAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult(new PetStableState(Slots, Stabled.Select(p => new StabledPet(p.Key, p.Value)).ToArray()));

        /// <summary>When set, the next slot write waits on it (and fails if it faults) so a test can act while the write is in flight.</summary>
        public TaskCompletionSource? SlotGate { get; set; }

        public async Task SetPurchasedSlotsAsync(int characterId, byte slots, CancellationToken cancellationToken = default)
        {
            if (SlotGate is { } gate)
            {
                SlotGate = null;
                await gate.Task;
            }

            await SetPurchasedSlotsCoreAsync(slots);
        }

        private Task SetPurchasedSlotsCoreAsync(byte slots)
        {
            if (Fail) return Task.FromException(new InvalidOperationException("down"));
            Slots = slots;
            return Task.CompletedTask;
        }

        public Task StableAsync(int characterId, byte slot, PersistentPetSnapshot active, CancellationToken cancellationToken = default)
        {
            if (Fail) return Task.FromException(new InvalidOperationException("down"));
            Stabled.Add(slot, active with { IsCurrent = false });
            Current = null;
            return Task.CompletedTask;
        }

        public Task<PersistentPetSnapshot?> UnstableAsync(int characterId, uint petNumber, PersistentPetSnapshot? active, CancellationToken cancellationToken = default)
        {
            var hit = Stabled.FirstOrDefault(p => p.Value.PetNumber == petNumber);
            if (hit.Value is null) return Task.FromResult<PersistentPetSnapshot?>(null);
            Stabled.Remove(hit.Key);
            if (active is not null) Stabled[hit.Key] = active with { IsCurrent = false };
            Current = hit.Value with { IsCurrent = true };
            return Task.FromResult<PersistentPetSnapshot?>(Current);
        }
    }

    private static PetTestKit Kit() => new(extraTemplates:
    [
        CreatureTestKit.Template(BeastEntry, b =>
        {
            b.MinLevel = 20;
            b.MaxLevel = 20;
            b.MinLevelHealth = 100;
            b.MaxLevelHealth = 100;
            b.Faction = 14;
        }) with { CreatureType = SummonService.CreatureTypeBeast, TypeFlags = SummonService.TypeFlagTameable, Family = 1 },
    ]);

    private static (PetStableController Stable, MemoryStableStore Store) Stable(PetTestKit kit, byte slots = 0)
    {
        var store = new MemoryStableStore { Slots = slots };
        return (new PetStableController(kit.Service) { Store = store, IsStableMaster = (_, npc) => npc == StableMaster }, store);
    }

    private static (Player Hunter, FakeSession Session) Hunter(PetTestKit kit)
    {
        (Player hunter, FakeSession session) = kit.AddPlayer(1);
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        hunter.Level = 30;
        return (hunter, session);
    }

    private static Creature Tame(PetTestKit kit, Player hunter)
    {
        Creature beast = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(BeastEntry)!, 3, 0, 0, 0);
        return Assert.IsType<Creature>(kit.Service.TameCreature(hunter, beast, 1515));
    }

    private static StableResult LastResult(FakeSession session)
        => (StableResult)session.Sent.Last(p => p.Opcode == WorldOpcode.SmsgStableResult).Payload.Single();

    [Fact]
    public void Prefetch_ReservesStabledPetNumbers_SoANewTameDoesNotReuseThem()
    {
        using PetTestKit kit = Kit();
        (PetStableController stable, MemoryStableStore store) = Stable(kit, slots: 1);
        (Player hunter, _) = Hunter(kit);
        store.Stabled[1] = new PersistentPetSnapshot(1, 500, BeastEntry, 20, 0, 100, 0, 0, 1, [], []);

        stable.Prefetch(hunter);
        Creature pet = Tame(kit, hunter);

        Assert.True(pet.Summon!.Charm!.PetNumber > 500);
        Assert.Single(stable.CachedState(1)!.Pets);
    }

    [Fact]
    public void BuySlot_ChargesTheDbcPrice_UpToTwoSlots()
    {
        using PetTestKit kit = Kit();
        (PetStableController stable, MemoryStableStore store) = Stable(kit);
        (Player hunter, FakeSession session) = Hunter(kit);
        int saves = 0;
        stable.MoneyChanged += _ => saves++;
        hunter.Money = 50_400;

        stable.HandleBuySlot(hunter, StableMaster);
        Assert.Equal(StableResult.SuccessBuySlot, LastResult(session));
        Assert.Equal((49_900u, (byte)1, 1), (hunter.Money, store.Slots, saves));

        stable.HandleBuySlot(hunter, StableMaster);
        Assert.Equal(StableResult.ErrMoney, LastResult(session)); // the second slot is 5 gold
        Assert.Equal(49_900u, hunter.Money);

        hunter.Money = 50_000;
        stable.HandleBuySlot(hunter, StableMaster);
        Assert.Equal((StableResult.SuccessBuySlot, 0u, (byte)2), (LastResult(session), hunter.Money, store.Slots));

        hunter.Money = 1_000_000;
        stable.HandleBuySlot(hunter, StableMaster);
        Assert.Equal((StableResult.ErrStable, 1_000_000u), (LastResult(session), hunter.Money));
    }

    [Fact]
    public void BuySlot_GivesTheMoneyBack_WhenTheSlotIsNotSaved()
    {
        using PetTestKit kit = Kit();
        (PetStableController stable, MemoryStableStore store) = Stable(kit);
        (Player hunter, FakeSession session) = Hunter(kit);
        hunter.Money = 500;
        store.Fail = true;

        stable.HandleBuySlot(hunter, StableMaster);

        Assert.Equal((StableResult.ErrStable, 500u, (byte)0), (LastResult(session), hunter.Money, store.Slots));
    }

    [Fact]
    public void BuySlot_FailedWrite_LeavesTheMoneyUntouchedWhileInFlightAndAfter()
    {
        using PetTestKit kit = Kit();
        (PetStableController stable, MemoryStableStore store) = Stable(kit);
        (Player hunter, FakeSession session) = Hunter(kit);
        hunter.Money = ArcaneCore.Game.Economy.EconomyOptions.MaxMoney;
        var gate = new TaskCompletionSource();
        store.SlotGate = gate;

        stable.HandleBuySlot(hunter, StableMaster);
        // Deducted only once the write commits: a save taken while it is in flight still holds the full amount.
        Assert.Equal(ArcaneCore.Game.Economy.EconomyOptions.MaxMoney, hunter.Money);

        gate.SetException(new InvalidOperationException("down"));

        Assert.Equal((StableResult.ErrStable, ArcaneCore.Game.Economy.EconomyOptions.MaxMoney, (byte)0), (LastResult(session), hunter.Money, store.Slots));
    }

    [Fact]
    public void BuySlot_MoneySpentWhileTheWriteIsInFlight_TakesTheSlotBack()
    {
        using PetTestKit kit = Kit();
        (PetStableController stable, MemoryStableStore store) = Stable(kit);
        (Player hunter, FakeSession session) = Hunter(kit);
        hunter.Money = 500;
        var gate = new TaskCompletionSource();
        store.SlotGate = gate;

        stable.HandleBuySlot(hunter, StableMaster);
        hunter.Money = 100;
        gate.SetResult();

        Assert.Equal((StableResult.ErrMoney, 100u, (byte)0), (LastResult(session), hunter.Money, store.Slots));
        Assert.Equal((byte)0, stable.CachedState(1)?.PurchasedSlots ?? 0);
    }

    [Fact]
    public void BuySlot_LoggedOutWhileTheWriteIsInFlight_TakesTheSlotBackWithoutTouchingThePlayer()
    {
        using PetTestKit kit = Kit();
        (PetStableController stable, MemoryStableStore store) = Stable(kit);
        (Player hunter, _) = Hunter(kit);
        hunter.Money = 500;
        var gate = new TaskCompletionSource();
        store.SlotGate = gate;
        int saves = 0;
        stable.MoneyChanged += _ => saves++;

        stable.HandleBuySlot(hunter, StableMaster);
        kit.Map.RemoveObject(hunter);
        stable.Forget(1);
        gate.SetResult();

        Assert.Equal((500u, (byte)0, 0), (hunter.Money, store.Slots, saves));
    }

    [Fact]
    public void StableThenUnstable_RoundTripsTheSummonedPet()
    {
        using PetTestKit kit = Kit();
        (PetStableController stable, MemoryStableStore store) = Stable(kit, slots: 1);
        (Player hunter, FakeSession session) = Hunter(kit);
        Creature pet = Tame(kit, hunter);
        uint number = pet.Summon!.Charm!.PetNumber;

        stable.HandleStable(hunter, StableMaster);

        Assert.Equal(StableResult.SuccessStable, LastResult(session));
        Assert.True(hunter.PetGuid.IsEmpty);
        Assert.Equal(number, store.Stabled[1].PetNumber);
        Assert.False(kit.Service.TryGetCachedCurrentPet(hunter, out _));

        // The list: no current pet, one stabled pet in client slot 2 (stable slot 1 + 1).
        session.Clear();
        stable.HandleList(hunter, StableMaster);
        byte[] list = session.Sent.Single(p => p.Opcode == WorldOpcode.MsgListStabledPets).Payload;
        Assert.Equal(StableMaster.Value, BitConverter.ToUInt64(list, 0));
        Assert.Equal((byte)1, list[8]);
        Assert.Equal((byte)1, list[9]);
        Assert.Equal(number, BitConverter.ToUInt32(list, 10));
        Assert.Equal(BeastEntry, BitConverter.ToUInt32(list, 14));
        Assert.Equal((byte)2, list[^1]);

        stable.HandleUnstable(hunter, StableMaster, number);

        Assert.Equal(StableResult.SuccessUnstable, LastResult(session));
        Assert.False(hunter.PetGuid.IsEmpty);
        Assert.Empty(store.Stabled);
        Assert.Equal(number, kit.Service.CaptureCurrentPet(hunter)!.PetNumber);
    }

    [Fact]
    public void Unstable_IsRefusedWhileAPetIsOut_ButSwapTradesPlaces()
    {
        using PetTestKit kit = Kit();
        (PetStableController stable, MemoryStableStore store) = Stable(kit, slots: 2);
        (Player hunter, FakeSession session) = Hunter(kit);
        uint first = Tame(kit, hunter).Summon!.Charm!.PetNumber;
        stable.HandleStable(hunter, StableMaster);
        uint second = Tame(kit, hunter).Summon!.Charm!.PetNumber;

        stable.HandleUnstable(hunter, StableMaster, first);
        Assert.Equal(StableResult.ErrStable, LastResult(session));
        Assert.Equal(second, kit.Service.CaptureCurrentPet(hunter)!.PetNumber);

        stable.HandleSwap(hunter, StableMaster, first);

        Assert.Equal(StableResult.SuccessUnstable, LastResult(session));
        Assert.Equal(first, kit.Service.CaptureCurrentPet(hunter)!.PetNumber);
        Assert.Equal(second, store.Stabled[1].PetNumber);
    }

    [Fact]
    public void Stable_IsRefused_WithoutAFreeSlotOrAStableMaster()
    {
        using PetTestKit kit = Kit();
        (PetStableController stable, _) = Stable(kit, slots: 0);
        (Player hunter, FakeSession session) = Hunter(kit);
        Tame(kit, hunter);

        stable.HandleStable(hunter, StableMaster);
        Assert.Equal(StableResult.ErrStable, LastResult(session));
        Assert.False(hunter.PetGuid.IsEmpty);

        session.Clear();
        stable.HandleList(hunter, ObjectGuid.WithEntry(HighGuid.Unit, 1, 2));
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.MsgListStabledPets);
    }

    [Fact]
    public void TheOwnGuid_IsAStableMaster_OnlyAfterTheGmCommand()
    {
        using PetTestKit kit = Kit();
        (PetStableController stable, _) = Stable(kit, slots: 1);
        (Player hunter, FakeSession session) = Hunter(kit);
        Tame(kit, hunter);

        stable.HandleStable(hunter, hunter.Guid);
        Assert.Equal(StableResult.ErrStable, LastResult(session));
        Assert.False(hunter.PetGuid.IsEmpty);

        stable.OpenAsGm(hunter);
        Assert.Contains(session.Sent, p => p.Opcode == WorldOpcode.MsgListStabledPets);
        stable.HandleStable(hunter, hunter.Guid);
        Assert.Equal(StableResult.SuccessStable, LastResult(session));

        stable.Forget(1);
        session.Clear();
        stable.HandleBuySlot(hunter, hunter.Guid);
        Assert.Equal(StableResult.ErrStable, LastResult(session));
    }
}