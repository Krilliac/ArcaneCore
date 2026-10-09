using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Naxxramas;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Features;
using ArcaneCore.World.Instances;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Instances;

/// <summary>
/// Naxxramas' Frostwyrm Lair trigger on ClassicDB content: the database row teleports, the instance script only vetoes it until the
/// four wings are cleared (mangos-classic naxxramas.cpp instance_naxxramas::DoHandleAreaTrigger). TeleportHandlers.HandleAreaTrigger
/// evaluates the row with every world feature that is an <see cref="IAreaTriggerGate"/>; this runs that same evaluation.
/// </summary>
public sealed class NaxxramasFrostwyrmGateTests
{
    // ClassicDB z2815 areatrigger_teleport 4156 "Naxxramas (Entrance)": level 51, quest 9378, map 533 at 3498.28,-5349.9,144.968.
    // The quest requirement belongs to the quest feature's gate and is left out here.
    private static readonly AreaTriggerTeleport Frostwyrm =
        new(4156, "Naxxramas (Entrance)", "", 51, 533, 3498.28f, -5349.9f, 144.968f, 0f);

    [Fact]
    public void InstanceFeature_IsADiscoveredAreaTriggerGate()
    {
        Assert.Contains(typeof(InstanceFeature), WorldFeatures.FeatureTypes);
        Assert.True(typeof(IAreaTriggerGate).IsAssignableFrom(typeof(InstanceFeature)));
    }

    [Fact]
    public void FrostwyrmRow_IsRefusedUntilTheFourWingsAreCleared()
    {
        using var world = new WorldRuntime(new WorldRuntimeOptions { UpdateCompressionThreshold = 0, AutosaveIntervalMs = 0 },
            new NullSaves(), NullLogger<WorldRuntime>.Instance);
        Map map = world.GetMap(533);
        var raid = new NaxxramasInstance(map);
        raid.Initialize();
        map.AddUpdater(raid);
        Player player = CreatePlayer(533);
        world.AddPlayer(player);
        world.RunTick(0);
        var gates = new IAreaTriggerGate[] { new InstanceFeature(null!, null!, NullLoggerFactory.Instance) };

        AreaTriggerVerdict verdict = AreaTriggerRequirements.Evaluate(player, Frostwyrm, null, gates);
        Assert.False(verdict.Allowed);
        Assert.Null(verdict.Message); // DoHandleAreaTrigger returns true without a text

        // Three of the four wings are not enough.
        foreach (uint boss in new uint[] { NaxxramasInstance.Maexxna, NaxxramasInstance.Loatheb, 8 })
            raid.SetData(boss, EncounterState.Done);
        Assert.False(AreaTriggerRequirements.Evaluate(player, Frostwyrm, null, gates).Allowed);

        raid.SetData(12, EncounterState.Done);
        Assert.True(AreaTriggerRequirements.Evaluate(player, Frostwyrm, null, gates).Allowed);

        // Another trigger in the same instance is not this gate's business.
        raid.SetData(12, EncounterState.Fail);
        Assert.True(AreaTriggerRequirements.Evaluate(player, Frostwyrm with { Id = 4157 }, null, gates).Allowed);
    }

    private static Player CreatePlayer(uint mapId)
    {
        var character = new CharacterRecord
        {
            Id = 1, AccountId = 1, Name = "Naxx", Race = 1, Class = 1, Gender = 0, Level = 60,
            MapId = mapId, ZoneId = 3456, X = 3005f, Y = -3434f, Z = 304f,
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
