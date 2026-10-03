using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Reputation.ReputationFixtures;

namespace ArcaneCore.Game.Tests.Reputation;

/// <summary>Kill reputation goes to every group member at reward distance, alive or dead (Group.cpp:2295-2409).</summary>
public sealed class ReputationKillCreditTests : IDisposable
{
    private const uint KillCreature = 7300;
    private const uint PlayerLevel = 10;

    private uint _nextGuid = 900300;

    private readonly WorldRuntime _world = TestWorld.CreateRuntime();
    private readonly ReputationService _service = new(Factions,
        [new ReputationOnKillEntry(KillCreature, BootyBay, 0, 7, false, 10, 0, false, 0, TeamDependent: false)], roll: () => 0.0);

    public void Dispose() => _world.Dispose();

    private Player AddPlayer(uint guid, float x, float y, bool track = true)
    {
        Player player = TestWorld.CreatePlayer(guid, x, y, new FakeSession((int)guid));
        player.Level = (byte)PlayerLevel;
        _world.AddPlayer(player);
        if (track)
        {
            _service.Track(player, _service.Create(player, CharacterReputationData.Empty));
        }

        return player;
    }

    private Creature Victim(Action<Creature>? configure = null)
    {
        var template = new CreatureTemplate { Entry = KillCreature, Name = "Pirate", Faction = 2, MinLevel = (byte)PlayerLevel, MaxLevel = (byte)PlayerLevel };
        uint guid = _nextGuid++;
        var victim = new Creature(guid, template, new CreatureSpawn { Guid = guid, Entry = KillCreature, MapId = 0, X = 0, Y = 0, Z = 83.5f },
            CreatureContent.Empty, new Random(1));
        victim.Level = (byte)PlayerLevel;
        configure?.Invoke(victim);
        _world.GetMap(0).AddObject(victim);
        return victim;
    }

    [Fact]
    public void GroupMembersAtRewardDistance_AllGain_AFarMemberDoesNot_ADeadOneStillDoes()
    {
        Player killer = AddPlayer(1, 10, 0);
        Player near = AddPlayer(2, 0, 70);
        Player far = AddPlayer(3, 80, 0);
        Player dead = AddPlayer(4, 0, 6);
        dead.Health = 0;
        Creature victim = Victim();
        var group = new RewardGroup([killer.Guid, near.Guid, far.Guid, dead.Guid], false);

        IReadOnlyList<Player> recipients = ReputationKillCredit.Award(_service, killer, victim, group, 74);

        Assert.Equal([killer, near, dead], recipients);
        Assert.Equal(10, _service.GetReputation(killer, BootyBay));
        Assert.Equal(10, _service.GetReputation(near, BootyBay));
        Assert.Equal(10, _service.GetReputation(dead, BootyBay)); // reputation needs no living member
        Assert.Equal(0, _service.GetReputation(far, BootyBay));
    }

    [Fact]
    public void ADeadKiller_StillCreditsTheGroup_AndASoloPlayerIsUnchanged()
    {
        Player killer = AddPlayer(1, 0, 0);
        Player friend = AddPlayer(2, 0, 5);
        killer.Health = 0;
        ReputationKillCredit.Award(_service, killer, Victim(), new RewardGroup([killer.Guid, friend.Guid], false), 74);
        Assert.Equal(10, _service.GetReputation(killer, BootyBay));
        Assert.Equal(10, _service.GetReputation(friend, BootyBay));

        Player solo = AddPlayer(5, 500, 0); // far away: a solo killer is the only recipient whatever the distance
        Assert.Equal([solo], ReputationKillCredit.Award(_service, solo, Victim(), null, 74));
        Assert.Equal(10, _service.GetReputation(solo, BootyBay));
    }

    [Fact]
    public void APetVictim_GivesNoReputation_PatchOnePointTen()
    {
        Player killer = AddPlayer(1, 0, 0);
        Creature pet = Victim(c => c.Summon = new SummonLinks(SummonKind.Pet, new ObjectGuid(77), 1, 0, 0));
        Assert.True(pet.IsPet);
        Assert.False(ReputationKillCredit.Gives(pet));
        Assert.Empty(ReputationKillCredit.Award(_service, killer, pet, null, 74));
        Assert.Equal(0, _service.GetReputation(killer, BootyBay));
    }

    [Fact]
    public void MembersWithoutLoadedStandings_GetNothing_AndTheOthersStillDo()
    {
        Player killer = AddPlayer(1, 0, 0);
        Player loading = AddPlayer(2, 0, 5, track: false);
        ReputationKillCredit.Award(_service, killer, Victim(), new RewardGroup([killer.Guid, loading.Guid], false), 74);
        Assert.Equal(10, _service.GetReputation(killer, BootyBay));
        Assert.Equal(0, _service.GetReputation(loading, BootyBay));
    }
}
