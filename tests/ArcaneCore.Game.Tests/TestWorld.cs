using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Tests;

internal static class TestWorld
{
    /// <summary>
    /// A world for tests that drive ticks by hand. The command drain's wall-clock budget
    /// (<see cref="WorldRuntimeOptions.CommandTimeBudgetMs"/>) is off: a test tick must run every command posted before it
    /// however long each one takes on a loaded machine, or a command posted second (say a second worldport arrival) runs
    /// one tick late only when the first one was slow, and the test flakes under CI load.
    /// </summary>
    public static WorldRuntime CreateRuntime(RecordingSaveQueue? saves = null, int autosaveMs = 0)
        => new(
            new WorldRuntimeOptions { UpdateCompressionThreshold = 0, AutosaveIntervalMs = autosaveMs, CommandTimeBudgetMs = 0 },
            saves ?? new RecordingSaveQueue(),
            NullLogger<WorldRuntime>.Instance);

    public static Player CreatePlayer(uint guid, float x, float y, IPlayerSession session, uint mapId = 0, Race race = Race.Human)
    {
        var character = new CharacterRecord
        {
            Id = (int)guid,
            AccountId = session.AccountId,
            Name = $"P{guid}",
            Race = (byte)race,
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
        var player = new Player(character, appearance, session);
        // vmangos regenerates energy for every player (Player.cpp:2283) and every player has 100 maximum energy (Unit.cpp:8260), so a fresh
        // player sends hidden UNIT_FIELD_POWER4 updates until it is full. The fixture player is the one that has finished that burst, so
        // the tests that count what a tick sends see only what they are about.
        player.SetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Energy, player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Energy));
        return player;
    }
}
