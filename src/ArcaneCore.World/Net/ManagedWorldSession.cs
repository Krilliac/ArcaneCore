using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Bots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Net;

internal sealed record ManagedSessionPacket(WorldOpcode Opcode, byte[] Payload);

internal sealed class ManagedActionBudget(int remaining)
{
    internal int Remaining { get; private set; } = remaining;
    internal bool TryTake()
    {
        if (Remaining <= 0) return false;
        Remaining--;
        return true;
    }
}

/// <summary>Trusted, socketless transport for registered server-owned P0 players.</summary>
public sealed partial class WorldSession
{
    [System.Runtime.CompilerServices.InlineArray(16)]
    private struct ManagedOpcodeMask
    {
        private ulong _element0;

        public void Set(WorldOpcode opcode)
        {
            uint code = (uint)opcode;
            if (code < 1024)
            {
                this[(int)(code >> 6)] |= 1UL << (int)(code & 63);
            }
        }

        public readonly bool Contains(WorldOpcode opcode)
        {
            uint code = (uint)opcode;
            return code >= 1024 || (this[(int)(code >> 6)] & (1UL << (int)(code & 63))) != 0;
        }

        public readonly bool ContainsAny(WorldOpcode[] opcodes)
        {
            for (int i = 0; i < opcodes.Length; i++)
            {
                if (Contains(opcodes[i]))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private bool _managed;
    private readonly Queue<ManagedSessionPacket> _managedPackets = new();
    private ManagedOpcodeMask _managedOpcodeMask;
    private int _managedPacketBytes;
    private readonly TaskCompletionSource _managedClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool IsManaged => _managed;

    /// <summary>A managed session of a playerbot: managed and not the server console's (account 0, WorldSession.Console.cs). Bot QA reports only these.</summary>
    internal bool IsPlayerbot => _managed && AccountId != 0;

    bool IPlayerSession.IsPlayerbot => IsPlayerbot;
    internal ManagedActionBudget? ManagedBudget { get; set; }

    /// <summary>True while <c>Playerbots.Chaos.PlayerbotChaosEngine</c> is dispatching a synthetic probe on this session.</summary>
    internal bool IsInChaosProbe { get; set; }

    /// <summary>
    /// Scenario harness tap: sees every packet this managed session captures, before the bounded drain queue can evict
    /// it. Called under the send lock on the sending thread; it must be cheap and must not send.
    /// </summary>
    internal Action<WorldOpcode, byte[]>? ManagedPacketObserver { get; set; }

    /// <summary>Test tap: sees every client opcode a managed action dispatches to its handler (<see cref="TryManagedAction"/>), before the handler runs.</summary>
    internal Action<WorldOpcode>? ManagedDispatchObserver { get; set; }

    internal static async Task<WorldSession> CreateManagedAsync(Account owner, int? characterId,
        IServiceProvider services, OpcodeTable opcodes, WorldRuntime world, SessionRegistry registry,
        WorldSessionOptions options, ILogger logger, CancellationToken cancellationToken = default, Guid? botId = null)
    {
        Account? current = await services.GetRequiredService<IAccountStore>()
            .FindByUsernameAsync(owner.Username, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Id != owner.Id || current.Status != AccountStatus.Active
            || current.Security != AccountSecurity.Player || current.SessionKey is not null)
            throw new InvalidOperationException("managed-owner-refused");
        if (characterId is { } id)
        {
            CharacterRecord? character = await services.GetRequiredService<ICharacterStore>()
                .GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
            if (character?.AccountId != owner.Id) throw new InvalidOperationException("managed-character-refused");
            IManagedPlayerbotStore botStore = services.GetRequiredService<IManagedPlayerbotStore>();
            bool registered = botId is { } bid
                ? await botStore.FindAsync(bid, cancellationToken).ConfigureAwait(false) is { } found
                    && found.AccountId == owner.Id && found.CharacterId == id && found.AccountName == owner.Username
                : (await botStore.LoadAllAsync(cancellationToken).ConfigureAwait(false))
                    .Any(bot => bot.AccountId == owner.Id && bot.CharacterId == id && bot.AccountName == owner.Username);
            if (!registered)
                throw new InvalidOperationException("managed-registration-refused");
        }
        if (registry.Find(owner.Id) is not null) throw new InvalidOperationException("managed-owner-online");
        var session = new WorldSession(Stream.Null, "managed-playerbot", services, opcodes, world, registry, options, logger)
        {
            _managed = true, AccountId = owner.Id, AccountName = owner.Username, Security = AccountSecurity.Player,
        };
        IBanStore? bans = services.GetService<IBanStore>();
        if (bans is not null && await session.IsBannedAsync(bans, owner.Id, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("managed-owner-banned");
        session.Settings = await services.GetRequiredService<IAccountDataStore>().GetAsync(owner.Id).ConfigureAwait(false);
        session._state = SessionState.CharacterSelect;
        registry.Register(session);
        return session;
    }

    internal async Task<bool> DispatchManagedSessionAsync(WorldOpcode opcode, byte[] payload)
    {
        if (!_managed || _kick.IsCancellationRequested || !IsCurrentSession || payload.Length > MaxClientPacketSize - 4
            || opcode is not (WorldOpcode.CmsgCharCreate or WorldOpcode.CmsgPlayerLogin or WorldOpcode.CmsgCharEnum)
            || !_opcodes.TryGet(opcode, out OpcodeHandler? handler) || handler.Session is null
            || !handler.AllowsState(_state)) return false;
        CapturePacket(true, opcode, payload);
        await handler.Session(this, payload).ConfigureAwait(false);
        return !_kick.IsCancellationRequested && IsCurrentSession;
    }

    internal bool TryManagedAction(WorldOpcode opcode, byte[] payload)
    {
        if (!_managed || !World.IsWorldThread || _kick.IsCancellationRequested || !IsCurrentSession || _state != SessionState.InWorld
            || Player is not { } player || player.IsQuestSettlementPending
            || payload.Length > MaxClientPacketSize - 4
            || !_opcodes.TryGet(opcode, out OpcodeHandler? handler) || handler.World is null
            || !handler.AllowsState(_state)
            || (player.Map is null && opcode != WorldOpcode.MsgMoveWorldportAck))
        {
            NoteManagedRejected(opcode, payload.Length); // report only: the refusal is unchanged (a stripped dev opcode is dropped and flagged there)
            return false;
        }

        if (ManagedBudget is { } budget && !budget.TryTake()) return false;
        HeuristicsManagedPacket(player, opcode, payload);
        BotPolicy?.BeforeAction(this, player, opcode);
        CapturePacket(true, opcode, payload);
        ManagedDispatchObserver?.Invoke(opcode);
        _currentPacketReceivedMs = (uint)Kernel.Logging.Clock.Milliseconds();
        try
        {
            handler.World(this, player, payload);
        }
        catch (Exception ex) when (NoteManagedFault(opcode, payload.Length, ex))
        {
            throw; // never reached: the filter only reports and returns false, so the exception propagates exactly as before
        }

        return _state == SessionState.InWorld && IsCurrentSession;
    }

    /// <summary>
    /// A managed bot's own movement applied by the server (<c>World:Playerbots:MovementPackets</c> = false): the gate and budget of
    /// <see cref="TryManagedAction"/>, then <see cref="MovementHandlers.MoveManagedBot"/> instead of the opcode's handler.
    /// </summary>
    internal bool TryManagedMovement(WorldOpcode opcode, in MovementInfo movement)
    {
        if (!_managed || !World.IsWorldThread || _kick.IsCancellationRequested || !IsCurrentSession || _state != SessionState.InWorld
            || Player is not { } player || player.IsQuestSettlementPending || player.Map is null) return false;
        if (ManagedBudget is { } budget && !budget.TryTake()) return false;
        return MovementHandlers.MoveManagedBot(this, player, opcode, movement) && _state == SessionState.InWorld && IsCurrentSession;
    }

    private ManagedBotRegistry? _botRegistry;
    private bool _botRegistryRead;

    /// <summary>
    /// The playerbot pack's session policy (docs/design/hot-reload-tiers.md, section 4.1), or null for a real client, or while no
    /// playerbot pack is attached. Read live from the core's registry on each use, so a detach or a pack swap takes effect at once.
    /// </summary>
    private IManagedBotSessionPolicy? BotPolicy
    {
        get
        {
            if (!_managed) return null;
            if (!_botRegistryRead)
            {
                _botRegistry = Services.GetService<ManagedBotRegistry>();
                _botRegistryRead = true;
            }

            return _botRegistry?.SessionPolicy;
        }
    }

    /// <summary>
    /// True when this is a playerbot session and the pack's policy says lean (<c>World:Playerbots:ClientFidelity</c> = Lean). With no
    /// pack attached the session does the full client work: always correct, only dearer.
    /// </summary>
    private bool IsLeanBot => IsPlayerbot && BotPolicy is { Lean: true };

    /// <summary>A scenario tap, an outbound observer or a packet capture is attached: it must see every packet.</summary>
    private bool HasPacketObserver => ManagedPacketObserver is not null || _outboundPacketObserver is not null
        || Volatile.Read(ref _packetCapture) is not null || HasTaps;

    /// <summary>
    /// Whether the map builds SMSG_UPDATE_OBJECT blocks for this session (<see cref="IPlayerSession.WantsUpdateObjects"/>): always for a
    /// real client and in Full fidelity, and for a lean bot only while an observer is attached to it. World thread.
    /// </summary>
    public bool WantsUpdateObjects => !IsLeanBot || HasPacketObserver;

    private long _leanRingPacketsSkipped;

    /// <summary>Packets this session's lean intake dropped (the per-session share of <see cref="BotLeanStats.RingPackets"/>, which is process-wide). Any thread.</summary>
    internal long LeanRingPacketsSkipped => Volatile.Read(ref _leanRingPacketsSkipped);

    private void CaptureManagedPacket(WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        if (_state == SessionState.Closed) { NoteSendAfterClose(opcode); return; }
        // Lean bot: nothing reads this packet back, so it is neither copied nor queued (the ring is not read for other opcodes).
        if (IsLeanBot && BotPolicy is { } policy && !policy.KeepsPacket(opcode) && !HasPacketObserver)
        {
            BotLeanStats.SkippedRingPacket(payload.Length);
            Interlocked.Increment(ref _leanRingPacketsSkipped);
            return;
        }

        lock (_sendLock)
        {
            if (_state == SessionState.Closed) { NoteSendAfterClose(opcode); return; }
            byte[] copy = payload.ToArray();
            CapturePacket(false, opcode, payload);
            ManagedPacketObserver?.Invoke(opcode, copy);
            _outboundPacketObserver?.Observe(opcode, copy);
            // Bounded transport: no socket writer or unconsumed unbounded channel exists for bots.
            while (_managedPackets.Count > 0 && (_managedPackets.Count >= 128 || _managedPacketBytes + payload.Length > 1_048_576))
                _managedPacketBytes -= _managedPackets.Dequeue().Payload.Length;
            _managedPackets.Enqueue(new ManagedSessionPacket(opcode, copy));
            _managedOpcodeMask.Set(opcode);
            _managedPacketBytes += payload.Length;
        }
    }

    internal IReadOnlyList<ManagedSessionPacket> DrainManagedPackets()
    {
        lock (_sendLock)
        {
            if (_managedPackets.Count == 0)
            {
                return [];
            }

            ManagedSessionPacket[] packets = _managedPackets.ToArray();
            _managedPackets.Clear();
            _managedOpcodeMask = default;
            _managedPacketBytes = 0;
            return packets;
        }
    }

    internal IReadOnlyList<ManagedSessionPacket> DrainManagedPackets(WorldOpcode onlyOpcode)
    {
        lock (_sendLock)
        {
            int count = _managedPackets.Count;
            if (count == 0 || !_managedOpcodeMask.Contains(onlyOpcode))
            {
                return [];
            }

            _managedOpcodeMask = default;
            List<ManagedSessionPacket>? result = null;
            for (int i = 0; i < count; i++)
            {
                ManagedSessionPacket packet = _managedPackets.Dequeue();
                if (packet.Opcode == onlyOpcode)
                {
                    (result ??= []).Add(packet);
                    _managedPacketBytes -= packet.Payload.Length;
                }
                else
                {
                    _managedPackets.Enqueue(packet);
                    _managedOpcodeMask.Set(packet.Opcode);
                }
            }

            return result ?? (IReadOnlyList<ManagedSessionPacket>)[];
        }
    }

    internal IReadOnlyList<ManagedSessionPacket> DrainManagedPackets(WorldOpcode op1, WorldOpcode op2)
    {
        lock (_sendLock)
        {
            int count = _managedPackets.Count;
            if (count == 0 || (!_managedOpcodeMask.Contains(op1) && !_managedOpcodeMask.Contains(op2)))
            {
                return [];
            }

            _managedOpcodeMask = default;
            List<ManagedSessionPacket>? result = null;
            for (int i = 0; i < count; i++)
            {
                ManagedSessionPacket packet = _managedPackets.Dequeue();
                if (packet.Opcode == op1 || packet.Opcode == op2)
                {
                    (result ??= []).Add(packet);
                    _managedPacketBytes -= packet.Payload.Length;
                }
                else
                {
                    _managedPackets.Enqueue(packet);
                    _managedOpcodeMask.Set(packet.Opcode);
                }
            }

            return result ?? (IReadOnlyList<ManagedSessionPacket>)[];
        }
    }

    internal IReadOnlyList<ManagedSessionPacket> DrainManagedPackets(WorldOpcode op1, WorldOpcode op2, WorldOpcode op3)
    {
        lock (_sendLock)
        {
            int count = _managedPackets.Count;
            if (count == 0 || (!_managedOpcodeMask.Contains(op1) && !_managedOpcodeMask.Contains(op2) && !_managedOpcodeMask.Contains(op3)))
            {
                return [];
            }

            _managedOpcodeMask = default;
            List<ManagedSessionPacket>? result = null;
            for (int i = 0; i < count; i++)
            {
                ManagedSessionPacket packet = _managedPackets.Dequeue();
                if (packet.Opcode == op1 || packet.Opcode == op2 || packet.Opcode == op3)
                {
                    (result ??= []).Add(packet);
                    _managedPacketBytes -= packet.Payload.Length;
                }
                else
                {
                    _managedPackets.Enqueue(packet);
                    _managedOpcodeMask.Set(packet.Opcode);
                }
            }

            return result ?? (IReadOnlyList<ManagedSessionPacket>)[];
        }
    }

    internal IReadOnlyList<ManagedSessionPacket> DrainManagedPackets(WorldOpcode op1, WorldOpcode op2, WorldOpcode op3, WorldOpcode op4)
    {
        lock (_sendLock)
        {
            int count = _managedPackets.Count;
            if (count == 0 || (!_managedOpcodeMask.Contains(op1) && !_managedOpcodeMask.Contains(op2)
                && !_managedOpcodeMask.Contains(op3) && !_managedOpcodeMask.Contains(op4)))
            {
                return [];
            }

            _managedOpcodeMask = default;
            List<ManagedSessionPacket>? result = null;
            for (int i = 0; i < count; i++)
            {
                ManagedSessionPacket packet = _managedPackets.Dequeue();
                if (packet.Opcode == op1 || packet.Opcode == op2 || packet.Opcode == op3 || packet.Opcode == op4)
                {
                    (result ??= []).Add(packet);
                    _managedPacketBytes -= packet.Payload.Length;
                }
                else
                {
                    _managedPackets.Enqueue(packet);
                    _managedOpcodeMask.Set(packet.Opcode);
                }
            }

            return result ?? (IReadOnlyList<ManagedSessionPacket>)[];
        }
    }

    internal IReadOnlyList<ManagedSessionPacket> DrainManagedPackets(WorldOpcode op1, WorldOpcode op2, WorldOpcode op3, WorldOpcode op4, WorldOpcode op5)
    {
        lock (_sendLock)
        {
            int count = _managedPackets.Count;
            if (count == 0 || (!_managedOpcodeMask.Contains(op1) && !_managedOpcodeMask.Contains(op2)
                && !_managedOpcodeMask.Contains(op3) && !_managedOpcodeMask.Contains(op4) && !_managedOpcodeMask.Contains(op5)))
            {
                return [];
            }

            _managedOpcodeMask = default;
            List<ManagedSessionPacket>? result = null;
            for (int i = 0; i < count; i++)
            {
                ManagedSessionPacket packet = _managedPackets.Dequeue();
                if (packet.Opcode == op1 || packet.Opcode == op2 || packet.Opcode == op3 || packet.Opcode == op4 || packet.Opcode == op5)
                {
                    (result ??= []).Add(packet);
                    _managedPacketBytes -= packet.Payload.Length;
                }
                else
                {
                    _managedPackets.Enqueue(packet);
                    _managedOpcodeMask.Set(packet.Opcode);
                }
            }

            return result ?? (IReadOnlyList<ManagedSessionPacket>)[];
        }
    }

    internal IReadOnlyList<ManagedSessionPacket> DrainManagedPackets(params WorldOpcode[] onlyOpcodes)
    {
        // Bots drain several times per think: no set and no list unless something matches (54 MB per 30 s on live).
        lock (_sendLock)
        {
            int count = _managedPackets.Count;
            if (count == 0 || !_managedOpcodeMask.ContainsAny(onlyOpcodes))
            {
                return [];
            }

            _managedOpcodeMask = default;
            List<ManagedSessionPacket>? result = null;
            for (int i = 0; i < count; i++)
            {
                ManagedSessionPacket packet = _managedPackets.Dequeue();
                if (Array.IndexOf(onlyOpcodes, packet.Opcode) >= 0)
                {
                    (result ??= []).Add(packet);
                    _managedPacketBytes -= packet.Payload.Length;
                }
                else
                {
                    _managedPackets.Enqueue(packet);
                    _managedOpcodeMask.Set(packet.Opcode);
                }
            }
            return result ?? (IReadOnlyList<ManagedSessionPacket>)[];
        }
    }

    internal Task ManagedClosed => _managedClosed.Task;

    internal void CloseManaged()
    {
        if (!_managed) throw new InvalidOperationException("not-managed");
        lock (_sendLock)
        {
            if (_state == SessionState.Closed) return;
            _state = SessionState.Closed;
            Volatile.Write(ref _closedAtTick, Environment.TickCount64);
            _packetCapture?.Stop(); // the writer drains in the background; the lock is not held for the disk
            _packetCapture = null;
            _managedPackets.Clear();
            _managedOpcodeMask = default;
            _managedPacketBytes = 0;
            _outbound.Writer.TryComplete();
        }
        _registry.Unregister(this);
        DiscardQueuedPackets();
        EndTaps();
        void Remove()
        {
            try
            {
                if (Player is { } player) { World.RemovePlayer(player); Player = null; }
                _managedClosed.TrySetResult();
            }
            catch (Exception ex) { _managedClosed.TrySetException(ex); }
        }
        if (World.IsWorldThread) Remove(); else World.Post(Remove);
    }
}
