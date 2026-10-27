using System.Collections.Concurrent;
using System.Reflection;
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
    private static readonly TimeSpan HangBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Stands in for the world thread: the controller's store work (loads and writes) runs on the thread pool and posts its continuation here, and the test
    /// runs it on its own thread while it waits (so the assertions only ever see settled state; the wait is a hang bound, not a timing).
    /// </summary>
    private sealed class WorldPump
    {
        private readonly ConcurrentQueue<Action> _posts = new();

        public void Post(Action action) => _posts.Enqueue(action);

        public async Task UntilAsync(Func<bool> done)
        {
            using CancellationTokenSource timeout = new(HangBound);
            while (true)
            {
                while (_posts.TryDequeue(out Action? action)) action();
                if (done()) return;
                if (timeout.IsCancellationRequested) throw new TimeoutException("the stable never reached the awaited state");
                await Task.Delay(1).ConfigureAwait(false);
            }
        }
    }

    private sealed class MemoryStableStore : IPetStableStore
    {
        public byte Slots { get; set; }
        public Dictionary<byte, PersistentPetSnapshot> Stabled { get; } = [];
        public PersistentPetSnapshot? Current { get; set; }
        public bool Fail { get; set; }
        public List<string>? Log { get; set; }
        public Task? UnstableGate { get; set; }

        public Task<PetStableState> LoadAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult(new PetStableState(Slots, Stabled.Select(p => new StabledPet(p.Key, p.Value)).ToArray()));

        public Task SetPurchasedSlotsAsync(int characterId, byte slots, CancellationToken cancellationToken = default)
        {
            if (Fail) return Task.FromException(new InvalidOperationException("down"));
            Slots = slots;
            return Task.CompletedTask;
        }

        public Task StableAsync(int characterId, byte slot, PersistentPetSnapshot active, CancellationToken cancellationToken = default)
        {
            if (Fail) return Task.FromException(new InvalidOperationException("down"));
            if (Log is { } log) lock (log) log.Add("stable");
            Stabled.Add(slot, active with { IsCurrent = false });
            Current = null;
            return Task.CompletedTask;
        }

        public async Task<PersistentPetSnapshot?> UnstableAsync(int characterId, uint petNumber, PersistentPetSnapshot? active, CancellationToken cancellationToken = default)
        {
            if (UnstableGate is { } gate) await gate.ConfigureAwait(false);
            var hit = Stabled.FirstOrDefault(p => p.Value.PetNumber == petNumber);
            if (hit.Value is null) return null;
            Stabled.Remove(hit.Key);
            if (active is not null) Stabled[hit.Key] = active with { IsCurrent = false };
            Current = hit.Value with { IsCurrent = true };
            return Current;
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

    private static (PetStableController Stable, MemoryStableStore Store, WorldPump Pump) Stable(PetTestKit kit, byte slots = 0)
    {
        var store = new MemoryStableStore { Slots = slots };
        var pump = new WorldPump();
        return (new PetStableController(kit.Service) { Store = store, IsStableMaster = (_, npc) => npc == StableMaster, Post = pump.Post }, store, pump);
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

    /// <summary>Waits for the stable's answer to the request just made (the caller cleared the session before it) and returns it.</summary>
    private static async Task<StableResult> ResultAsync(WorldPump pump, FakeSession session)
    {
        await pump.UntilAsync(() => session.Sent.Any(p => p.Opcode == WorldOpcode.SmsgStableResult));
        return LastResult(session);
    }

    [Fact]
    public async Task Prefetch_ReservesStabledPetNumbers_SoANewTameDoesNotReuseThem()
    {
        using PetTestKit kit = Kit();
        (PetStableController stable, MemoryStableStore store, WorldPump pump) = Stable(kit, slots: 1);
        (Player hunter, _) = Hunter(kit);
        store.Stabled[1] = new PersistentPetSnapshot(1, 500, BeastEntry, 20, 0, 100, 0, 0, 1, [], []);

        stable.Prefetch(hunter);
        await pump.UntilAsync(() => stable.CachedState(1) is not null);
        Creature pet = Tame(kit, hunter);

        Assert.True(pet.Summon!.Charm!.PetNumber > 500);
        Assert.Single(stable.CachedState(1)!.Pets);
    }

    [Fact]
    public async Task BuySlot_ChargesTheDbcPrice_UpToTwoSlots()
    {
        using PetTestKit kit = Kit();
        (PetStableController stable, MemoryStableStore store, WorldPump pump) = Stable(kit);
        (Player hunter, FakeSession session) = Hunter(kit);
        int saves = 0;
        stable.MoneyChanged += _ => saves++;
        hunter.Money = 50_400;

        session.Clear();
        stable.HandleBuySlot(hunter, StableMaster);
        Assert.Equal(StableResult.SuccessBuySlot, await ResultAsync(pump, session));
        Assert.Equal((49_900u, (byte)1, 1), (hunter.Money, store.Slots, saves));

        session.Clear();
        stable.HandleBuySlot(hunter, StableMaster);
        Assert.Equal(StableResult.ErrMoney, await ResultAsync(pump, session)); // the second slot is 5 gold
        Assert.Equal(49_900u, hunter.Money);

        hunter.Money = 50_000;
        session.Clear();
        stable.HandleBuySlot(hunter, StableMaster);
        Assert.Equal(StableResult.SuccessBuySlot, await ResultAsync(pump, session));
        Assert.Equal((0u, (byte)2), (hunter.Money, store.Slots));

        hunter.Money = 1_000_000;
        session.Clear();
        stable.HandleBuySlot(hunter, StableMaster);
        Assert.Equal(StableResult.ErrStable, await ResultAsync(pump, session));
        Assert.Equal(1_000_000u, hunter.Money);
    }

    [Fact]
    public async Task BuySlot_GivesTheMoneyBack_WhenTheSlotIsNotSaved()
    {
        using PetTestKit kit = Kit();
        (PetStableController stable, MemoryStableStore store, WorldPump pump) = Stable(kit);
        (Player hunter, FakeSession session) = Hunter(kit);
        hunter.Money = 500;
        store.Fail = true;

        session.Clear();
        stable.HandleBuySlot(hunter, StableMaster);

        Assert.Equal(StableResult.ErrStable, await ResultAsync(pump, session));
        Assert.Equal((500u, (byte)0), (hunter.Money, store.Slots));
    }

    [Fact]
    public async Task StableThenUnstable_RoundTripsTheSummonedPet()
    {
        using PetTestKit kit = Kit();
        (PetStableController stable, MemoryStableStore store, WorldPump pump) = Stable(kit, slots: 1);
        (Player hunter, FakeSession session) = Hunter(kit);
        Creature pet = Tame(kit, hunter);
        uint number = pet.Summon!.Charm!.PetNumber;

        session.Clear();
        stable.HandleStable(hunter, StableMaster);

        Assert.Equal(StableResult.SuccessStable, await ResultAsync(pump, session));
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

        session.Clear();
        stable.HandleUnstable(hunter, StableMaster, number);

        Assert.Equal(StableResult.SuccessUnstable, await ResultAsync(pump, session));
        Assert.False(hunter.PetGuid.IsEmpty);
        Assert.Empty(store.Stabled);
        Assert.Equal(number, kit.Service.CaptureCurrentPet(hunter)!.PetNumber);
    }

    [Fact]
    public async Task Unstable_IsRefusedWhileAPetIsOut_ButSwapTradesPlaces()
    {
        using PetTestKit kit = Kit();
        (PetStableController stable, MemoryStableStore store, WorldPump pump) = Stable(kit, slots: 2);
        (Player hunter, FakeSession session) = Hunter(kit);
        uint first = Tame(kit, hunter).Summon!.Charm!.PetNumber;
        session.Clear();
        stable.HandleStable(hunter, StableMaster);
        Assert.Equal(StableResult.SuccessStable, await ResultAsync(pump, session));
        uint second = Tame(kit, hunter).Summon!.Charm!.PetNumber;

        session.Clear();
        stable.HandleUnstable(hunter, StableMaster, first);
        Assert.Equal(StableResult.ErrStable, await ResultAsync(pump, session));
        Assert.Equal(second, kit.Service.CaptureCurrentPet(hunter)!.PetNumber);

        session.Clear();
        stable.HandleSwap(hunter, StableMaster, first);

        Assert.Equal(StableResult.SuccessUnstable, await ResultAsync(pump, session));
        Assert.Equal(first, kit.Service.CaptureCurrentPet(hunter)!.PetNumber);
        Assert.Equal(second, store.Stabled[1].PetNumber);
    }

    [Fact]
    public async Task Stable_IsRefused_WithoutAFreeSlotOrAStableMaster()
    {
        using PetTestKit kit = Kit();
        (PetStableController stable, _, WorldPump pump) = Stable(kit, slots: 0);
        (Player hunter, FakeSession session) = Hunter(kit);
        Tame(kit, hunter);

        session.Clear();
        stable.HandleStable(hunter, StableMaster);
        Assert.Equal(StableResult.ErrStable, await ResultAsync(pump, session));
        Assert.False(hunter.PetGuid.IsEmpty);

        session.Clear();
        stable.HandleList(hunter, ObjectGuid.WithEntry(HighGuid.Unit, 1, 2));
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.MsgListStabledPets);
    }

    [Fact]
    public async Task TheOwnGuid_IsAStableMaster_OnlyAfterTheGmCommand()
    {
        using PetTestKit kit = Kit();
        (PetStableController stable, _, WorldPump pump) = Stable(kit, slots: 1);
        (Player hunter, FakeSession session) = Hunter(kit);
        Tame(kit, hunter);

        session.Clear();
        stable.HandleStable(hunter, hunter.Guid);
        Assert.Equal(StableResult.ErrStable, LastResult(session)); // refused before any store work
        Assert.False(hunter.PetGuid.IsEmpty);

        session.Clear();
        stable.OpenAsGm(hunter);
        await pump.UntilAsync(() => session.Sent.Any(p => p.Opcode == WorldOpcode.MsgListStabledPets));
        session.Clear();
        stable.HandleStable(hunter, hunter.Guid);
        Assert.Equal(StableResult.SuccessStable, await ResultAsync(pump, session));

        stable.Forget(1);
        session.Clear();
        stable.HandleBuySlot(hunter, hunter.Guid);
        Assert.Equal(StableResult.ErrStable, LastResult(session));
    }

    [Fact]
    public async Task Stable_BehindAGatedSave_SettlesInOrder_AndTheNextTameIsCached()
    {
        using PetTestKit kit = Kit();
        (PetStableController stable, MemoryStableStore store, WorldPump pump) = Stable(kit, slots: 1);
        (Player hunter, FakeSession session) = Hunter(kit);
        List<string> log = [];
        store.Log = log;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Service.SavePersistence = async (_, _) =>
        {
            lock (log) log.Add("save-begin");
            entered.TrySetResult();
            await gate.Task.ConfigureAwait(false);
            lock (log) log.Add("save-end");
        };

        uint first = Tame(kit, hunter).Summon!.Charm!.PetNumber; // queues the gated save
        await entered.Task.WaitAsync(HangBound);
        session.Clear();
        stable.HandleStable(hunter, StableMaster); // its stable load queues behind the gated save
        stable.HandleStable(hunter, StableMaster); // refused at once: the first request is still in flight
        Assert.Equal([StableResult.ErrStable], Results(session));
        Assert.False(hunter.PetGuid.IsEmpty);
        Assert.Empty(store.Stabled);

        // Let the stable's load and write run as far as they can while the save is still gated: they must still be waiting behind it.
        // A negative cannot be awaited for exactly; this delay can only make the check weaker on a starved pool (never a false failure),
        // and the exact ordering is asserted below once the gate opens.
        await Task.Delay(200);
        await pump.UntilAsync(() => true);
        lock (log) Assert.Equal(["save-begin"], log);
        Assert.Empty(store.Stabled);
        Assert.Equal([StableResult.ErrStable], Results(session));
        Assert.False(hunter.PetGuid.IsEmpty);

        gate.SetResult();
        await pump.UntilAsync(() => Results(session).Count == 2);

        Assert.Equal([StableResult.ErrStable, StableResult.SuccessStable], Results(session));
        Assert.Equal(["save-begin", "save-end", "stable"], log);
        Assert.True(hunter.PetGuid.IsEmpty);
        Assert.Equal(first, store.Stabled[1].PetNumber);
        Assert.False(kit.Service.TryGetCachedCurrentPet(hunter, out _));

        uint second = Tame(kit, hunter).Summon!.Charm!.PetNumber;
        await kit.Service.FlushCharacterAsync(1).WaitAsync(HangBound);
        Assert.NotEqual(first, second);
        Assert.True(kit.Service.TryGetCachedCurrentPet(hunter, out PersistentPetSnapshot cached));
        Assert.Equal(second, cached.PetNumber);
        Assert.Equal(first, Assert.Single(stable.CachedState(1)!.Pets).Pet.PetNumber);
    }

    [Fact]
    public async Task FailedAbandonDelete_IsNotRetriedOverAPetUnstabledAfterIt()
    {
        using PetTestKit kit = Kit();
        (PetStableController stable, MemoryStableStore store, WorldPump pump) = Stable(kit, slots: 1);
        (Player hunter, FakeSession session) = Hunter(kit);
        PersistentPetSnapshot dead = new(1, 500, BeastEntry, 20, 0, 0, 0, 0, 1, [], []); // health 0: comes out dismissed, not summoned
        store.Stabled[1] = dead with { IsCurrent = false };

        int deletesAfterRecovery = 0;
        bool storageDown = true;
        kit.Service.DeletePersistence = (_, _) =>
        {
            if (Volatile.Read(ref storageDown)) return Task.FromException(new IOException("database is locked"));
            Interlocked.Increment(ref deletesAfterRecovery);
            store.Current = null; // what the store does: DeleteAsync removes the character_pet row
            return Task.CompletedTask;
        };

        Creature abandoned = Tame(kit, hunter);
        kit.Service.QueueDeletePet(hunter);
        kit.Service.Unsummon(abandoned);
        await Assert.ThrowsAsync<IOException>(() => kit.Service.FlushCharacterAsync(1)).WaitAsync(HangBound); // the delete stays owed

        session.Clear();
        stable.HandleUnstable(hunter, StableMaster, 500);
        Assert.Equal(StableResult.SuccessUnstable, await ResultAsync(pump, session));
        Assert.NotNull(store.Current);

        Volatile.Write(ref storageDown, false);
        await kit.Service.FlushCharacterAsync(1).WaitAsync(HangBound); // the barrier that used to delete the unstabled pet
        await kit.Service.StopAsync().WaitAsync(HangBound);

        Assert.Equal(0, Volatile.Read(ref deletesAfterRecovery));
        Assert.NotNull(store.Current);
        Assert.True(kit.Service.TryGetCachedCurrentPet(hunter, out PersistentPetSnapshot cached));
        Assert.Equal(500u, cached.PetNumber);
    }

    [Fact]
    public async Task FailedAbandonDelete_RetryQueuedByABarrier_DoesNotDeleteThePetAnUnstableWroteMeanwhile()
    {
        using PetTestKit kit = Kit();
        (PetStableController stable, MemoryStableStore store, WorldPump pump) = Stable(kit, slots: 1);
        (Player hunter, FakeSession session) = Hunter(kit);
        PersistentPetSnapshot dead = new(1, 500, BeastEntry, 20, 0, 0, 0, 0, 1, [], []);
        store.Stabled[1] = dead with { IsCurrent = false };
        stable.Prefetch(hunter);
        await pump.UntilAsync(() => stable.CachedState(1) is not null); // the unstable below must queue its write at once, not behind a load

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unstableGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.UnstableGate = unstableGate.Task;
        int calls = 0;
        int deletesThatSucceeded = 0;
        kit.Service.DeletePersistence = async (_, _) =>
        {
            int call = Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            if (call <= 3) throw new IOException("database is locked"); // the abandon delete exhausts its three attempts
            Interlocked.Increment(ref deletesThatSucceeded);
            store.Current = null;
        };

        Creature abandoned = Tame(kit, hunter);
        kit.Service.QueueDeletePet(hunter); // D: in flight, will fail
        kit.Service.Unsummon(abandoned);
        await entered.Task.WaitAsync(HangBound);
        Task barrier = kit.Service.FlushCharacterAsync(1); // captures D as the tail, then retries the delete once D has failed

        session.Clear();
        stable.HandleUnstable(hunter, StableMaster, 500); // U: queued behind D, and behind nothing the barrier sees
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgStableResult);

        Task unstableTail = SaveTail(kit.Service, 1);
        release.SetResult(); // D fails; the barrier then queues its delete retry R behind U, whose store write is still gated
        while (ReferenceEquals(SaveTail(kit.Service, 1), unstableTail))
        {
            await Task.Delay(1).WaitAsync(HangBound); // polls for R's queue slot: a state, not a timing
        }

        unstableGate.SetResult(); // only now may U finish (and clear the owed delete), so R is guaranteed to have been queued first
        await pump.UntilAsync(() => session.Sent.Any(p => p.Opcode == WorldOpcode.SmsgStableResult) && barrier.IsCompleted);
        await barrier.WaitAsync(HangBound);
        await kit.Service.StopAsync().WaitAsync(HangBound);

        Assert.Equal(StableResult.SuccessUnstable, LastResult(session));
        Assert.Equal(0, Volatile.Read(ref deletesThatSucceeded));
        Assert.NotNull(store.Current);
        Assert.True(kit.Service.TryGetCachedCurrentPet(hunter, out PersistentPetSnapshot cached));
        Assert.Equal(500u, cached.PetNumber);
    }

    /// <summary>The current tail of a character's pet write queue (a private seam; the test needs to know when a retry has taken its slot).</summary>
    private static Task SaveTail(SummonService service, int characterId)
    {
        var tails = (Dictionary<int, Task>)typeof(SummonService).GetField("_saveTails", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        var gate = (Lock)typeof(SummonService).GetField("_cacheGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        using (gate.EnterScope()) return tails[characterId];
    }

    private static List<StableResult> Results(FakeSession session)
        => [.. session.Sent.Where(p => p.Opcode == WorldOpcode.SmsgStableResult).Select(p => (StableResult)p.Payload.Single())];
}
