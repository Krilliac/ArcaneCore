using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
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

    // --- the tap decides (vmangos Unit::Kill, Unit.cpp:987-1000, then Group::RewardGroupAtKill, Group.cpp:2360-2409) -------------

    private static Group GroupOf(uint id, params Player[] members)
    {
        var group = new Group(id) { IsCreated = true, LeaderGuid = members[0].Guid };
        foreach (Player member in members)
        {
            group.AddMemberSlot(member.Guid, member.Name);
        }

        return group;
    }

    private static Func<Player, RewardGroup?> Resolver(params Group[] groups)
        => player => groups.FirstOrDefault(g => g.IsMember(player.Guid)) is { } group
            ? new RewardGroup([.. group.Members.Select(m => m.Guid)], group.IsRaid) : null;

    [Fact]
    public void TheTappersGroup_GetsTheReputation_NotTheGroupOfWhoeverLandsTheKillingBlow()
    {
        Player tapper = AddPlayer(1, 0, 0);
        Player tapperFriend = AddPlayer(2, 0, 10);
        Player deadFriend = AddPlayer(3, 10, 0);
        deadFriend.Health = 0; // dead inside reward distance still counts
        Player killer = AddPlayer(4, 5, 0);
        Player killerFriend = AddPlayer(5, 0, 5);
        Group tapGroup = GroupOf(1, tapper, tapperFriend, deadFriend);
        Group killerGroup = GroupOf(2, killer, killerFriend);
        Creature victim = Victim(c => { c.LootTapPlayerGuid = tapper.Guid; c.LootTapGroup = tapGroup; });

        IReadOnlyList<Player> recipients = ReputationKillCredit.AwardKill(_service, killer, victim, Resolver(tapGroup, killerGroup), 74);

        Assert.Equal([tapper, tapperFriend, deadFriend], recipients);
        Assert.Equal(10, _service.GetReputation(tapper, BootyBay));
        Assert.Equal(10, _service.GetReputation(deadFriend, BootyBay));
        Assert.Equal(0, _service.GetReputation(killer, BootyBay));
        Assert.Equal(0, _service.GetReputation(killerFriend, BootyBay));
    }

    [Fact]
    public void ATapperWhoLeftTheGroupAfterTheTap_StillGainsWithTheGroupOfTheTap()
    {
        Player tapper = AddPlayer(1, 0, 0);
        Player stayed = AddPlayer(2, 0, 10);
        Player farAway = AddPlayer(3, 300, 0);
        Group tapGroup = GroupOf(1, stayed, farAway); // the tapper left after tagging
        Creature victim = Victim(c => { c.LootTapPlayerGuid = tapper.Guid; c.LootTapGroup = tapGroup; });

        IReadOnlyList<Player> recipients = ReputationKillCredit.AwardKill(_service, stayed, victim, Resolver(tapGroup), 74);

        Assert.Equal([stayed, tapper], recipients); // members first, the tapper last (Group.cpp:2386-2405)
        Assert.Equal(0, _service.GetReputation(farAway, BootyBay));
    }

    [Fact]
    public void ASoloTapper_IsTheOnlyRecipient_AndADisbandedTapGroupFallsBackToTheTappersGroupNow()
    {
        Player tapper = AddPlayer(1, 0, 0);
        Player killer = AddPlayer(2, 5, 0);
        Group killerGroup = GroupOf(2, killer, AddPlayer(3, 0, 5));
        Creature solo = Victim(c => c.LootTapPlayerGuid = tapper.Guid);
        Assert.Equal([tapper], ReputationKillCredit.AwardKill(_service, killer, solo, Resolver(killerGroup), 74));

        Group disbanded = GroupOf(1, tapper, AddPlayer(4, 0, 6));
        disbanded.Clear();
        Player newFriend = AddPlayer(5, 6, 0);
        Group now = GroupOf(3, tapper, newFriend);
        Creature victim = Victim(c => { c.LootTapPlayerGuid = tapper.Guid; c.LootTapGroup = disbanded; });
        Assert.Equal([tapper, newFriend], ReputationKillCredit.AwardKill(_service, killer, victim, Resolver(now, killerGroup), 74));
    }

    [Fact]
    public void WithoutATap_TheKillerAndHisGroupGain_AndAnOfflineTapperIsReplacedByTheKiller()
    {
        Player killer = AddPlayer(1, 0, 0);
        Player friend = AddPlayer(2, 0, 5);
        Group group = GroupOf(1, killer, friend);
        Assert.Equal([killer, friend], ReputationKillCredit.AwardKill(_service, killer, Victim(), Resolver(group), 74));

        // vmangos keeps the killer as pPlayerTap when the original recipient is not online, with the tap group still rewarded.
        Player member = AddPlayer(3, 0, 7);
        Group tapGroup = GroupOf(2, member);
        Creature victim = Victim(c => { c.LootTapPlayerGuid = ObjectGuid.Player(424242); c.LootTapGroup = tapGroup; });
        Assert.Equal([member, killer], ReputationKillCredit.AwardKill(_service, killer, victim, Resolver(group, tapGroup), 74));
    }
}
