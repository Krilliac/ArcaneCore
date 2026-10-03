using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Reputation.ReputationFixtures;

namespace ArcaneCore.Game.Tests.Reputation;

/// <summary>
/// The quest NPC lookup with the reputation reaction source: reputation-faction NPCs and
/// contested guards resolve (and become hostile from reputation), unknown data still fails closed.
/// </summary>
public sealed class ReputationLookupTests : IDisposable
{
    private readonly WorldRuntime _world = TestWorld.CreateRuntime();
    private readonly Player _player;
    private readonly Creature _creature;
    private readonly ReputationService _service = new(Factions);
    private readonly CreatureQuestLookup _lookup;

    public ReputationLookupTests()
    {
        _player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        _world.AddPlayer(_player);
        var template = new CreatureTemplate { Entry = 900310, Name = "Synthetic questgiver", Faction = StormwindNpc.Id, NpcFlags = 2 };
        var spawn = new CreatureSpawn { Guid = 900320, Entry = template.Entry, MapId = 0, X = 0, Y = 0, Z = _player.Z };
        _creature = new Creature(spawn.Guid, template, spawn, CreatureContent.Empty, new Random(1));
        _player.Map!.AddObject(_creature);
        _world.RunTick(5);
        _service.Track(_player, _service.Create(_player, CharacterReputationData.Empty));
        _lookup = new CreatureQuestLookup(Templates, _service);
    }

    [Fact]
    public void ReputationFactionNpc_Resolves_WithItsFaction_AndTurnsHostileFromReputation()
    {
        NpcInfo info = Assert.IsType<NpcInfo>(_lookup.Find(_player, _creature.Guid));
        Assert.False(info.IsHostile);
        Assert.Equal(Stormwind, info.FactionId);

        _service.SetReputation(_player, Stormwind, -3001);
        Assert.True(_lookup.Find(_player, _creature.Guid)!.IsHostile);
        _service.SetReputation(_player, Stormwind, -3000);
        Assert.False(_lookup.Find(_player, _creature.Guid)!.IsHostile);

        // Without the reputation source the same NPC stays unresolvable (the previous fail-closed rule).
        Assert.Null(new CreatureQuestLookup(Templates).Find(_player, _creature.Guid));
    }

    [Fact]
    public void ContestedGuard_IsHostileOnlyToContestedPlayers()
    {
        _creature.FactionTemplate = ContestedGuard.Id;
        Assert.False(_lookup.Find(_player, _creature.Guid)!.IsHostile);
        _player.Flags |= PlayerFlags.ContestedPvp;
        Assert.True(_lookup.Find(_player, _creature.Guid)!.IsHostile);
    }

    [Theory]
    [InlineData(999u)]  // unknown template
    [InlineData(7u)]    // faction absent from Faction.dbc
    public void UnknownData_FailsClosed(uint factionTemplate)
    {
        _creature.FactionTemplate = factionTemplate;
        Assert.Null(_lookup.Find(_player, _creature.Guid));
    }

    [Fact]
    public void UntrackedPlayer_CannotResolveReputationNpcs_ButStillResolvesPlainOnes()
    {
        _service.Untrack(_player);
        Assert.Null(_lookup.Find(_player, _creature.Guid));
        _creature.FactionTemplate = NeutralNpc.Id;
        Assert.False(_lookup.Find(_player, _creature.Guid)!.IsHostile);
        _creature.FactionTemplate = HostileNpc.Id;
        Assert.True(_lookup.Find(_player, _creature.Guid)!.IsHostile);
    }

    public void Dispose() => _world.Dispose();
}
