using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Pets;

/// <summary>SMSG_STABLE_RESULT codes (vmangos StableResultCode, NPCHandler.h).</summary>
public enum StableResult : byte
{
    ErrMoney = 0x01,
    ErrStable = 0x06,
    SuccessStable = 0x08,
    SuccessUnstable = 0x09,
    SuccessBuySlot = 0x0A,
}

/// <summary>
/// The hunter pet stable (vmangos NPCHandler.cpp HandleListStabledPetsOpcode, SendStablePet, HandleStablePet, HandleUnstablePet,
/// HandleBuyStableSlot, HandleStableSwapPet). World thread only: the stable of an online hunter is loaded on first use and kept until logout,
/// and every write goes through <see cref="SummonService.QueueSerializedWriteAsync{T}"/> so it is ordered with the pet's other saves. While a
/// load or write is in flight a second request is refused (STABLE_ERR_STABLE) rather than queued, so the cached state is never raced.
/// </summary>
public sealed class PetStableController(SummonService summons, ILogger? logger = null)
{
    /// <summary>vmangos MAX_PET_STABLES (PET_SAVE_FIRST_STABLE_SLOT 1 .. PET_SAVE_LAST_STABLE_SLOT 2).</summary>
    public const int MaxStableSlots = 2;

    /// <summary>StableSlotPrices.dbc (build 5875): slot 1 is 5 silver, slot 2 is 5 gold.</summary>
    public static IReadOnlyList<uint> SlotPrices { get; } = [500, 50_000];

    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly Dictionary<int, Entry> _entries = [];
    private readonly HashSet<int> _gmStables = [];

    /// <summary>The characters-database stable; without one every request is refused.</summary>
    public IPetStableStore? Store { get; set; }

    /// <summary>Runs a continuation on the world thread (WorldRuntime.Post).</summary>
    public Action<Action> Post { get; set; } = static action => action();

    /// <summary>True when the player can use the stable master <c>npc</c> (vmangos GetNPCIfCanInteractWith(guid, UNIT_NPC_FLAG_STABLEMASTER)); unset fails closed.</summary>
    public Func<Player, ObjectGuid, bool>? IsStableMaster { get; set; }

    /// <summary>Raised on the world thread after the player's money changed (the world saves it).</summary>
    public event Action<Player>? MoneyChanged;

    /// <summary>The cached stable of an online character (tests and diagnostics).</summary>
    public PetStableState? CachedState(int characterId) => _entries.GetValueOrDefault(characterId)?.State;

    /// <summary>Drop a character's cached stable (logout).</summary>
    public void Forget(int characterId)
    {
        _entries.Remove(characterId);
        _gmStables.Remove(characterId);
    }

    /// <summary>
    /// The .stable command (vmangos HandleStableCommand: SendStablePet(own guid)): the player's own guid then stands in for a stable master
    /// until logout (vmangos CheckStableMaster accepts the own guid only for a player who has the command).
    /// </summary>
    public void OpenAsGm(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        _gmStables.Add(Id(player));
        HandleList(player, player.Guid);
    }

    /// <summary>
    /// Load a hunter's stable at login so its pet numbers are reserved before the hunter can tame again: pet numbers are only reserved when a
    /// stored pet loads, and a fresh tame after a restart must not reuse a stabled pet's number (vmangos seeds GeneratePetNumber from the
    /// whole character_pet table, stabled pets included).
    /// </summary>
    public void Prefetch(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.Class == Class.Hunter && Store is not null)
        {
            WithState(player, failWhenBusy: false, static _ => { });
        }
    }

    /// <summary>MSG_LIST_STABLED_PETS from the client: u64 stable master.</summary>
    public void HandleList(Player player, ObjectGuid npc)
    {
        if (!Allowed(player, npc))
        {
            return;
        }

        WithState(player, failWhenBusy: false, state => player.Session.Send(WorldOpcode.MsgListStabledPets, BuildList(player, npc, state)));
    }

    /// <summary>CMSG_STABLE_PET: the current pet, summoned or dismissed, goes into the first free bought slot.</summary>
    public void HandleStable(Player player, ObjectGuid npc)
    {
        if (!player.IsAlive || !Allowed(player, npc))
        {
            Send(player, StableResult.ErrStable);
            return;
        }

        WithState(player, failWhenBusy: true, state =>
        {
            int id = Id(player);
            Creature? live = LivePet(player);
            PersistentPetSnapshot? active = live is not null ? summons.CaptureCurrentPet(player)
                : summons.TryGetCachedCurrentPet(player, out PersistentPetSnapshot cached) ? cached : null;
            byte slot = FreeSlot(state);
            if (active is null || (live is not null && live.Summon?.Kind != SummonKind.Pet) || slot == 0 || Store is not { } store)
            {
                Send(player, StableResult.ErrStable);
                return;
            }

            if (live is not null)
            {
                summons.ForgetPetForSpiritHealer(player);
                summons.Unsummon(live);
            }

            summons.ReplaceCachedPet(id, null);
            PetStableState next = state with { Pets = [.. state.Pets, new StabledPet(slot, active with { IsCurrent = false })] };
            Write(player, id, async ct => { await store.StableAsync(id, slot, active, ct).ConfigureAwait(false); return true; },
                _ => { SetState(id, next); Send(player, StableResult.SuccessStable); },
                () => summons.ReplaceCachedPet(id, active));
        });
    }

    /// <summary>CMSG_UNSTABLE_PET: u64 stable master, u32 pet number. Refused while a pet is summoned; a dismissed current pet takes the freed slot.</summary>
    public void HandleUnstable(Player player, ObjectGuid npc, uint petNumber) => TakeOut(player, npc, petNumber, swap: false);

    /// <summary>CMSG_STABLE_SWAP_PET: u64 stable master, u32 pet number. The current pet, summoned or not, goes into the slot of the one taken out.</summary>
    public void HandleSwap(Player player, ObjectGuid npc, uint petNumber) => TakeOut(player, npc, petNumber, swap: true);

    /// <summary>CMSG_BUY_STABLE_SLOT: the next slot for its StableSlotPrices.dbc price, up to <see cref="MaxStableSlots"/>.</summary>
    public void HandleBuySlot(Player player, ObjectGuid npc)
    {
        if (!Allowed(player, npc))
        {
            Send(player, StableResult.ErrStable);
            return;
        }

        WithState(player, failWhenBusy: true, state =>
        {
            if (state.PurchasedSlots >= MaxStableSlots || Store is not { } store)
            {
                Send(player, StableResult.ErrStable);
                return;
            }

            uint price = SlotPrices[state.PurchasedSlots];
            if (player.Money < price)
            {
                Send(player, StableResult.ErrMoney);
                return;
            }

            int id = Id(player);
            byte slots = (byte)(state.PurchasedSlots + 1);
            // Charged only once the slot is saved, so a failed write has nothing to refund and a save taken while the write is in
            // flight never holds the deducted money. If the player can no longer pay at commit (spent it, logged out, or a quest
            // settlement holds the character), the saved slot is taken back instead.
            Write(player, id, async ct => { await store.SetPurchasedSlotsAsync(id, slots, ct).ConfigureAwait(false); return true; },
                _ =>
                {
                    if (!TryCharge(player, price))
                    {
                        TakeSlotBack(player, id, store, state.PurchasedSlots);
                        return;
                    }

                    SetState(id, state with { PurchasedSlots = slots });
                    Send(player, StableResult.SuccessBuySlot);
                },
                static () => { });
        });
    }

    /// <summary>vmangos SendStablePet: u64 npc, u8 count, u8 bought slots, then per pet u32 number, u32 entry, u32 level, cstring name, u32 loyalty, u8 slot (1 = current).</summary>
    public byte[] BuildList(Player player, ObjectGuid npc, PetStableState state)
    {
        var w = new PacketWriter(64);
        w.WriteUInt64(npc.Value);
        byte count = 0;
        var pets = new PacketWriter(64);
        if (LivePet(player) is { IsAlive: true, Summon: { Kind: SummonKind.Pet, Charm: { } charm } } live)
        {
            WritePet(pets, charm.PetNumber, live.Entry, live.Level, charm.Name, PetLoyalty.Level(live), 1);
            count++;
        }
        else if (summons.TryGetCachedCurrentPet(player, out PersistentPetSnapshot current))
        {
            WritePet(pets, current.PetNumber, current.Entry, current.Level, current.Name, current.LoyaltyLevel, 1);
            count++;
        }

        foreach (StabledPet stabled in state.Pets.OrderBy(p => p.Slot))
        {
            PersistentPetSnapshot pet = stabled.Pet;
            WritePet(pets, pet.PetNumber, pet.Entry, pet.Level, pet.Name, pet.LoyaltyLevel, (byte)(stabled.Slot + 1));
            count++;
        }

        w.WriteByte(count);
        w.WriteByte(state.PurchasedSlots);
        w.WriteBytes(pets.ToArray());
        return w.ToArray();
    }

    private static void WritePet(PacketWriter w, uint number, uint entry, uint level, string name, uint loyalty, byte slot)
    {
        w.WriteUInt32(number);
        w.WriteUInt32(entry);
        w.WriteUInt32(level);
        w.WriteCString(name);
        w.WriteUInt32(loyalty);
        w.WriteByte(slot);
    }

    private void TakeOut(Player player, ObjectGuid npc, uint petNumber, bool swap)
    {
        if (!Allowed(player, npc))
        {
            Send(player, StableResult.ErrStable);
            return;
        }

        WithState(player, failWhenBusy: true, state =>
        {
            int id = Id(player);
            StabledPet? stabled = state.Pets.FirstOrDefault(p => p.Pet.PetNumber == petNumber);
            Creature? live = LivePet(player);
            if (stabled is null || Store is not { } store || (live is not null && (!swap || live.Summon?.Kind != SummonKind.Pet)))
            {
                Send(player, StableResult.ErrStable);
                return;
            }

            PersistentPetSnapshot? active = live is not null ? summons.CaptureCurrentPet(player)
                : summons.TryGetCachedCurrentPet(player, out PersistentPetSnapshot cached) ? cached : null;
            if (live is not null && active is null)
            {
                Send(player, StableResult.ErrStable);
                return;
            }

            if (live is not null)
            {
                summons.ForgetPetForSpiritHealer(player);
                summons.Unsummon(live);
            }

            summons.ReplaceCachedPet(id, null);
            Write(player, id, ct => store.UnstableAsync(id, petNumber, active, ct),
                taken =>
                {
                    if (taken is null)
                    {
                        _entries.Remove(id); // the database disagreed with the cache: reload on next use
                        summons.ReplaceCachedPet(id, active);
                        Send(player, StableResult.ErrStable);
                        return;
                    }

                    List<StabledPet> rest = [.. state.Pets.Where(p => p.Pet.PetNumber != petNumber)];
                    if (active is not null)
                    {
                        rest.Add(new StabledPet(stabled.Slot, active with { IsCurrent = false }));
                    }

                    SetState(id, state with { Pets = rest });
                    summons.ReplaceCachedPet(id, taken);

                    // vmangos: a dead stabled pet comes out as the dismissed current pet (PET_SAVE_NOT_IN_SLOT) without being summoned.
                    if (taken.Health > 0 && player.IsInWorld && player.PetGuid.IsEmpty)
                    {
                        summons.RestoreCurrentPet(player, taken);
                    }

                    Send(player, StableResult.SuccessUnstable);
                },
                () => summons.ReplaceCachedPet(id, active));
        });
    }

    /// <summary>Deduct <paramref name="price"/> through the settlement-guarded setter, clamped to 0..MaxMoney; false (untouched) when the player cannot pay now.</summary>
    private bool TryCharge(Player player, uint price)
    {
        if (!player.IsInWorld || !player.CanMutateQuestSettlementState || player.Money < price)
        {
            return false;
        }

        player.Money = (uint)Math.Clamp((long)player.Money - price, 0, ArcaneCore.Game.Economy.EconomyOptions.MaxMoney);
        MoneyChanged?.Invoke(player);
        return true;
    }

    /// <summary>Undo a saved slot purchase that could not be charged; the cached stable is reloaded on next use.</summary>
    private void TakeSlotBack(Player player, int id, IPetStableStore store, byte before)
    {
        _entries.TryGetValue(id, out Entry? entry);
        if (entry is not null)
        {
            entry.Busy = true;
            entry.State = null;
        }

        if (player.IsInWorld)
        {
            Send(player, StableResult.ErrMoney);
        }

        _ = RevertSlotsAsync(id, entry, store, before);
    }

    private async Task RevertSlotsAsync(int id, Entry? entry, IPetStableStore store, byte slots)
    {
        try
        {
            await summons.QueueSerializedWriteAsync(id, async ct => { await store.SetPurchasedSlotsAsync(id, slots, ct).ConfigureAwait(false); return true; }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("pet.stable", ex);
            _logger.LogFault("pet-stable", ex, "pet stable slot rollback failed for character {CharacterId}", id);
        }

        Post(() =>
        {
            if (entry is not null)
            {
                entry.Busy = false;
            }
        });
    }

    private bool Allowed(Player player, ObjectGuid npc)
        => player.Class == Class.Hunter && player.IsInWorld
            && (npc == player.Guid ? _gmStables.Contains(Id(player)) : IsStableMaster is { } check && check(player, npc));

    private static int Id(Player player) => checked((int)player.Guid.Low);

    private static Creature? LivePet(Player player)
        => !player.PetGuid.IsEmpty && player.Map?.FindObject(player.PetGuid) is Creature { Summon: not null } pet && pet.OwnerGuid == player.Guid ? pet : null;

    private static byte FreeSlot(PetStableState state)
    {
        for (byte slot = 1; slot <= Math.Min(state.PurchasedSlots, (byte)MaxStableSlots); slot++)
        {
            if (!state.Pets.Any(p => p.Slot == slot))
            {
                return slot;
            }
        }

        return 0;
    }

    private static void Send(Player player, StableResult result) => player.Session.Send(WorldOpcode.SmsgStableResult, [(byte)result]);

    private void SetState(int id, PetStableState state)
    {
        if (_entries.TryGetValue(id, out Entry? entry))
        {
            entry.State = state;
        }
    }

    private void WithState(Player player, bool failWhenBusy, Action<PetStableState> next)
    {
        int id = Id(player);
        if (!_entries.TryGetValue(id, out Entry? entry))
        {
            entry = new Entry();
            _entries[id] = entry;
        }

        if (entry.Busy)
        {
            if (failWhenBusy) Send(player, StableResult.ErrStable);
            return;
        }

        if (entry.State is { } state)
        {
            next(state);
            return;
        }

        if (Store is not { } store)
        {
            if (failWhenBusy) Send(player, StableResult.ErrStable);
            return;
        }

        entry.Busy = true;
        _ = LoadAsync(player, id, entry, store, next);
    }

    private async Task LoadAsync(Player player, int id, Entry entry, IPetStableStore store, Action<PetStableState> next)
    {
        PetStableState? loaded = null;
        try
        {
            loaded = await summons.QueueSerializedWriteAsync(id, ct => store.LoadAsync(id, ct)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("pet.stable", ex);
            AnomalyLedger.Shared.Report("pet", "stable-fault", $"character {id}", ex.Message);
            _logger.LogError(ex, "pet stable load failed for character {CharacterId}", id);
        }

        Post(() =>
        {
            entry.Busy = false;
            if (loaded is null)
            {
                Send(player, StableResult.ErrStable);
                return;
            }

            entry.State = loaded;
            foreach (StabledPet stabled in loaded.Pets)
            {
                summons.ReservePetNumber(stabled.Pet.PetNumber);
            }

            if (player.IsInWorld && ReferenceEquals(_entries.GetValueOrDefault(id), entry))
            {
                next(loaded);
            }
        });
    }

    private void Write<T>(Player player, int id, Func<CancellationToken, Task<T>> write, Action<T> succeeded, Action failed)
    {
        Entry entry = _entries[id];
        entry.Busy = true;
        _ = WriteAsync(player, id, entry, write, succeeded, failed);
    }

    private async Task WriteAsync<T>(Player player, int id, Entry entry, Func<CancellationToken, Task<T>> write, Action<T> succeeded, Action failed)
    {
        T result = default!;
        bool ok = false;
        try
        {
            result = await summons.QueueSerializedWriteAsync(id, write).ConfigureAwait(false);
            ok = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("pet.stable", ex);
            AnomalyLedger.Shared.Report("pet", "stable-fault", $"character {id}", ex.Message);
            _logger.LogError(ex, "pet stable write failed for character {CharacterId}", id);
        }

        Post(() =>
        {
            entry.Busy = false;
            if (ok)
            {
                succeeded(result);
                return;
            }

            entry.State = null; // reload from the database on next use
            failed();
            Send(player, StableResult.ErrStable);
        });
    }

    private sealed class Entry
    {
        public PetStableState? State { get; set; }
        public bool Busy { get; set; }
    }
}