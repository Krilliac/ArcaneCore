using System.Collections.Concurrent;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Tests;

/// <summary>A session that records what the world sends it.</summary>
internal sealed class FakeSession(int accountId = 1) : IPlayerSession
{
    public ConcurrentQueue<(WorldOpcode Opcode, byte[] Payload)> Sent { get; } = new();

    public bool Kicked { get; private set; }

    public int AccountId { get; } = accountId;

    public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload) => Sent.Enqueue((opcode, payload.ToArray()));

    public void ProcessWorldPackets(Player player)
    {
    }

    public void Kick() => Kicked = true;

    public (WorldOpcode Opcode, byte[] Payload) Next()
        => Sent.TryDequeue(out var packet) ? packet : throw new InvalidOperationException("no packet sent");

    public void Clear() => Sent.Clear();
}

/// <summary>Records snapshots instead of saving them.</summary>
internal sealed class RecordingSaveQueue : ICharacterSaveQueue
{
    public List<CharacterState> Saved { get; } = [];

    public void Enqueue(CharacterState state) => Saved.Add(state);
}

internal static class TestWorld
{
    public static WorldRuntime CreateRuntime(RecordingSaveQueue? saves = null, int autosaveMs = 0)
        => new(
            new WorldRuntimeOptions { UpdateCompressionThreshold = 0, AutosaveIntervalMs = autosaveMs },
            saves ?? new RecordingSaveQueue(),
            NullLogger<WorldRuntime>.Instance);

    public static Player CreatePlayer(uint guid, float x, float y, IPlayerSession session, uint mapId = 0)
    {
        var character = new CharacterRecord
        {
            Id = (int)guid,
            AccountId = session.AccountId,
            Name = $"P{guid}",
            Race = (byte)Race.Human,
            Class = (byte)Class.Warrior,
            Gender = (byte)Gender.Male,
            Level = 1,
            MapId = mapId,
            ZoneId = 12,
            X = x,
            Y = y,
            Z = 83.5f,
        };

        var appearance = new PlayerAppearance(
            DisplayId: 49, FactionTemplate: 1, PowerType.Rage, BaseHealth: 60, BaseMana: 0,
            MaxHealth: 60, MaxPower: 1000, StartPower: 0, NextLevelXp: 400);
        return new Player(character, appearance, session);
    }
}
