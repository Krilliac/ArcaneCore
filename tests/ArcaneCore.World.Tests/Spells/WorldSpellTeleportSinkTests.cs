using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>The daemon's spell teleport sink with a non-player mover (vmangos Unit::NearTeleportTo, Objects/Unit.cpp:9801-9825).</summary>
public sealed class WorldSpellTeleportSinkTests
{
    private const uint Entry = 972950;
    private const float Z = 83.5f;

    [Fact]
    public void Teleport_OfACreature_StopsIt_AndItsObserversGetMsgMoveTeleport()
    {
        using var world = new WorldRuntime(
            new WorldRuntimeOptions { UpdateCompressionThreshold = 0, AutosaveIntervalMs = 0, CommandTimeBudgetMs = 0 },
            NullSaveQueue.Instance,
            NullLogger<WorldRuntime>.Instance);
        Map map = world.GetMap(0);
        var template = new CreatureTemplate
        {
            Entry = Entry, Name = "Mover", MinLevel = 20, MaxLevel = 20,
            MinLevelHealth = 100, MaxLevelHealth = 100, DisplayIds = [1], Faction = 14,
        };
        var system = new CreatureMapSystem(map, new CreatureContent([template], [], [], [], []), random: new Random(1));
        map.AddUpdater(system);
        Creature creature = system.SpawnTemporary(template, 40, 0, Z, 0);

        var session = new RecordingPlayerSession();
        var character = new CharacterRecord
        {
            Id = 1, AccountId = session.AccountId, Name = "Observer", Race = 1, Class = 1, Gender = 0, Level = 1, MapId = 0, ZoneId = 12, X = 0, Y = 0, Z = Z,
        };
        var observer = new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), session);
        world.AddPlayer(observer);
        world.RunTick(100);
        Assert.Contains(creature.Guid, observer.VisibleObjects);
        system.MoveStraight(creature, 60, 0, Z, run: false, finalOrientation: null);
        Assert.True(creature.IsMoving);
        session.Sent.Clear();

        // A creature mover never reaches the player teleport service.
        var sink = new WorldSpellTeleportSink(() => throw new InvalidOperationException("the teleport service is for players"));
        Assert.True(sink.Teleport(creature, 0, 10, 5, Z, 1f, keepCombat: true));

        Assert.False(creature.IsMoving);
        Assert.Equal((10f, 5f, 1f), (creature.X, creature.Y, creature.Orientation));
        Assert.Equal(2, session.Sent.Count(p => p.Opcode == WorldOpcode.MsgMoveTeleport)); // before and after the relocation
    }
}
