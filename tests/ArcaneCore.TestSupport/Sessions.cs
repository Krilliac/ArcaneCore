using System.Collections.Concurrent;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;

namespace ArcaneCore.TestSupport;

/// <summary>A session that discards everything the world sends it and never reports a logout or kick.</summary>
public sealed class NullPlayerSession(int accountId = 1, AccountSecurity security = AccountSecurity.Player) : IPlayerSession
{
    public int AccountId { get; } = accountId;

    public AccountSecurity Security { get; } = security;

    public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
    }

    public void ProcessWorldPackets(Player player)
    {
    }

    public void Kick()
    {
    }

    public void OnLoggedOut()
    {
    }
}

/// <summary>A session that records what the world sends it.</summary>
public sealed class RecordingPlayerSession(int accountId = 1, AccountSecurity security = AccountSecurity.Player) : IPlayerSession
{
    public ConcurrentQueue<(WorldOpcode Opcode, byte[] Payload)> Sent { get; } = new();

    public bool Kicked { get; private set; }

    public int LoggedOutCount { get; private set; }

    public int AccountId { get; } = accountId;

    public AccountSecurity Security { get; } = security;

    public bool WantsUpdateObjects { get; set; } = true;

    public bool IsPlayerbot { get; set; }

    public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload) => Sent.Enqueue((opcode, payload.ToArray()));

    public void ProcessWorldPackets(Player player)
    {
    }

    public void Kick() => Kicked = true;

    public void OnLoggedOut() => LoggedOutCount++;

    public (WorldOpcode Opcode, byte[] Payload) Next()
        => Sent.TryDequeue(out var packet) ? packet : throw new InvalidOperationException("no packet sent");

    public void Clear() => Sent.Clear();
}
