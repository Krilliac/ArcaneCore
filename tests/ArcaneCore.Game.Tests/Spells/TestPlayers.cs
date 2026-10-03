using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>Players of other classes and power types than the warriors <see cref="TestWorld.CreatePlayer"/> makes.</summary>
internal static class TestPlayers
{
    public static Player Add(SpellTestKit kit, uint guid, Class cls, PowerType power, float x = 0, float y = 0, uint maxPower = 100)
    {
        var session = new FakeSession((int)guid);
        var character = new CharacterRecord
        {
            Id = (int)guid,
            AccountId = session.AccountId,
            Name = $"P{guid}",
            Race = (byte)Race.Human,
            Class = (byte)cls,
            Gender = (byte)Gender.Male,
            Level = 1,
            MapId = 0,
            ZoneId = 12,
            X = x == 0 ? guid : x,
            Y = y == 0 ? guid : y,
            Z = 83.5f,
        };
        var appearance = new PlayerAppearance(49, 1, power, 60, maxPower, 60, maxPower, maxPower, 400);
        var player = new Player(character, appearance, session);
        kit.World.AddPlayer(player);
        kit.World.RunTick(0);
        session.Clear();
        return player;
    }
}
