using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Naxxramas;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Instances;

/// <summary>
/// The world-side wiring of Naxxramas' Military/Construct/Frostwyrm part: <see cref="InstanceFeature"/> as the area-trigger listener
/// (trigger 4112 starts Kel'Thuzad's encounter) and as the gate on ClassicDB's 4156 row once the Four Horsemen fall through their own
/// four-death count.
/// </summary>
public sealed class NaxxramasPartTwoGateTests
{
    private static readonly AreaTriggerTeleport Frostwyrm =
        new(4156, "Naxxramas (Entrance)", "", 51, 533, 3498.28f, -5349.9f, 144.968f, 0f);

    [Fact]
    public void FourHorsemenDeathsAndThaddius_WithTheOtherWings_OpenTheFrostwyrmRow()
    {
        using var world = new WorldRuntime(new WorldRuntimeOptions { UpdateCompressionThreshold = 0, AutosaveIntervalMs = 0 },
            new NullSaves(), NullLogger<WorldRuntime>.Instance);
        Map map = world.GetMap(533);
        var raid = new NaxxramasInstance(map);
        raid.Initialize();
        map.AddUpdater(raid);
        Player player = CreatePlayer();
        world.AddPlayer(player);
        world.RunTick(0);
        var feature = new InstanceFeature(null!, null!, NullLoggerFactory.Instance);
        var gates = new IAreaTriggerGate[] { feature };

        raid.SetData(NaxxramasInstance.Maexxna, EncounterState.Done);
        raid.SetData(NaxxramasInstance.Loatheb, EncounterState.Done);
        raid.SetData(NaxxramasInstance.Thaddius, EncounterState.Done);
        foreach (uint horseman in NaxxramasInstance.HorsemenEntries.Take(3)) raid.RecordHorsemanDeath(horseman);
        Assert.False(AreaTriggerRequirements.Evaluate(player, Frostwyrm, null, gates).Allowed);

        raid.RecordHorsemanDeath(NaxxramasInstance.HorsemenEntries[3]);
        Assert.Equal(EncounterState.Done, raid.GetData(NaxxramasInstance.Horsemen));
        Assert.True(AreaTriggerRequirements.Evaluate(player, Frostwyrm, null, gates).Allowed);
    }

    [Fact]
    public void KelThuzadTrigger_ThroughTheListener_StartsTheEncounter()
    {
        using var world = new WorldRuntime(new WorldRuntimeOptions { UpdateCompressionThreshold = 0, AutosaveIntervalMs = 0 },
            new NullSaves(), NullLogger<WorldRuntime>.Instance);
        Map map = world.GetMap(533);
        var raid = new NaxxramasInstance(map);
        raid.Initialize();
        map.AddUpdater(raid);
        Player player = CreatePlayer();
        world.AddPlayer(player);
        world.RunTick(0);
        IAreaTriggerListener listener = new InstanceFeature(null!, null!, NullLoggerFactory.Instance);

        listener.OnAreaTrigger(player, 4112);

        Assert.Equal(EncounterState.InProgress, raid.GetData(NaxxramasInstance.KelThuzad));
    }

    private static Player CreatePlayer()
    {
        var character = new CharacterRecord
        {
            Id = 1, AccountId = 1, Name = "Naxx", Race = 1, Class = 1, Gender = 0, Level = 60,
            MapId = 533, ZoneId = 3456, X = 3005f, Y = -3434f, Z = 304f,
        };
        var appearance = new PlayerAppearance(
            DisplayId: 49, FactionTemplate: 1, PowerType.Rage, BaseHealth: 60, BaseMana: 0,
            MaxHealth: 60, MaxPower: 1000, StartPower: 0, NextLevelXp: 400);
        return new Player(character, appearance, new NullSession());
    }

    private sealed class NullSaves : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state)
        {
        }
    }

    private sealed class NullSession : IPlayerSession
    {
        public int AccountId => 1;

        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(ArcaneCore.Protocol.WorldOpcode opcode, ReadOnlySpan<byte> payload)
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
}
