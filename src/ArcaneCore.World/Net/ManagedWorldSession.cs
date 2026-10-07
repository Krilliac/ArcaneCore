using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    private bool _managed;
    private readonly Queue<ManagedSessionPacket> _managedPackets = new();
    private int _managedPacketBytes;
    private readonly TaskCompletionSource _managedClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool IsManaged => _managed;
    internal ManagedActionBudget? ManagedBudget { get; set; }

    internal static async Task<WorldSession> CreateManagedAsync(Account owner, int? characterId,
        IServiceProvider services, OpcodeTable opcodes, WorldRuntime world, SessionRegistry registry,
        WorldSessionOptions options, ILogger logger, CancellationToken cancellationToken = default)
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
            IReadOnlyList<ManagedPlayerbot> registrations = await services.GetRequiredService<IManagedPlayerbotStore>()
                .LoadAllAsync(cancellationToken).ConfigureAwait(false);
            if (!registrations.Any(bot => bot.AccountId == owner.Id && bot.CharacterId == id && bot.AccountName == owner.Username))
                throw new InvalidOperationException("managed-registration-refused");
        }
        if (registry.Find(owner.Id) is not null) throw new InvalidOperationException("managed-owner-online");
        var session = new WorldSession(Stream.Null, "managed-playerbot", services, opcodes, world, registry, options, logger)
        {
            _managed = true, AccountId = owner.Id, AccountName = owner.Username, Security = AccountSecurity.Player,
        };
        IBanStore? bans = services.GetService<IBanStore>();
        if (bans is not null && await session.IsBannedAsync(bans, owner.Id).ConfigureAwait(false))
            throw new InvalidOperationException("managed-owner-banned");
        session.Settings = await services.GetRequiredService<IAccountDataStore>().GetAsync(owner.Id).ConfigureAwait(false);
        session._state = SessionState.CharacterSelect;
        registry.Register(session);
        return session;
    }

    internal async Task<bool> DispatchManagedSessionAsync(WorldOpcode opcode, byte[] payload)
    {
        if (!_managed || _kick.IsCancellationRequested || payload.Length > MaxClientPacketSize - 4
            || opcode is not (WorldOpcode.CmsgCharCreate or WorldOpcode.CmsgPlayerLogin or WorldOpcode.CmsgCharEnum)
            || !_opcodes.TryGet(opcode, out OpcodeHandler? handler) || handler.Session is null
            || !handler.AllowsState(_state)) return false;
        await handler.Session(this, payload).ConfigureAwait(false);
        return !_kick.IsCancellationRequested;
    }

    internal bool TryManagedAction(WorldOpcode opcode, byte[] payload)
    {
        if (!_managed || !World.IsWorldThread || _kick.IsCancellationRequested || _state != SessionState.InWorld
            || Player is not { } player || player.IsQuestSettlementPending
            || payload.Length > MaxClientPacketSize - 4
            || !_opcodes.TryGet(opcode, out OpcodeHandler? handler) || handler.World is null
            || !handler.AllowsState(_state)
            || (player.Map is null && opcode != WorldOpcode.MsgMoveWorldportAck)) return false;
        if (ManagedBudget is { } budget && !budget.TryTake()) return false;
        handler.World(this, player, payload);
        return _state == SessionState.InWorld;
    }

    private void CaptureManagedPacket(WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        lock (_sendLock)
        {
            if (_state == SessionState.Closed) return;
            // Bounded transport: no socket writer or unconsumed unbounded channel exists for bots.
            while (_managedPackets.Count > 0 && (_managedPackets.Count >= 128 || _managedPacketBytes + payload.Length > 1_048_576))
                _managedPacketBytes -= _managedPackets.Dequeue().Payload.Length;
            _managedPackets.Enqueue(new ManagedSessionPacket(opcode, payload.ToArray()));
            _managedPacketBytes += payload.Length;
        }
    }

    internal IReadOnlyList<ManagedSessionPacket> DrainManagedPackets()
    {
        lock (_sendLock)
        {
            ManagedSessionPacket[] packets = _managedPackets.ToArray();
            _managedPackets.Clear();
            _managedPacketBytes = 0;
            return packets;
        }
    }

    internal IReadOnlyList<ManagedSessionPacket> DrainManagedPackets(params WorldOpcode[] onlyOpcodes)
    {
        var selected = new HashSet<WorldOpcode>(onlyOpcodes);
        lock (_sendLock)
        {
            var result = new List<ManagedSessionPacket>();
            int count = _managedPackets.Count;
            for (int i = 0; i < count; i++)
            {
                ManagedSessionPacket packet = _managedPackets.Dequeue();
                if (selected.Contains(packet.Opcode))
                { result.Add(packet); _managedPacketBytes -= packet.Payload.Length; }
                else _managedPackets.Enqueue(packet);
            }
            return result;
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
            _managedPackets.Clear();
            _managedPacketBytes = 0;
            _outbound.Writer.TryComplete();
        }
        _registry.Unregister(this);
        DiscardQueuedPackets();
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
