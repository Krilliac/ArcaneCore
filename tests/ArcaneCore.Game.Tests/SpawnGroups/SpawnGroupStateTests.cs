using ArcaneCore.Game.Maps.SpawnGroups;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using Xunit;
using static ArcaneCore.Game.Tests.SpawnGroups.ClassicDbSpawnGroupRows;

namespace ArcaneCore.Game.Tests.SpawnGroups;

/// <summary>
/// The choice rules of a cmangos spawn group (Maps/SpawnGroup.cpp SpawnGroup::Spawn, GetEligibleEntry, RemoveObject; the derived maximum of
/// ObjectMgr::LoadSpawnGroups, Globals/ObjectMgr.cpp:1460-1475) over classic-db z2815 groups (<see cref="ClassicDbSpawnGroupRows"/>).
/// </summary>
public sealed class SpawnGroupStateTests
{
    private sealed class Host : ISpawnGroupHost
    {
        public HashSet<uint> Pending { get; } = [];

        public Dictionary<uint, uint> Own { get; } = [];

        public Dictionary<uint, uint[]> SpawnEntries { get; } = [];

        public bool Condition { get; set; } = true;

        public int Despawns { get; private set; }

        public Random Random { get; } = new(3);

        public bool IsRespawnPending(uint spawnGuid) => Pending.Contains(spawnGuid);

        public void ClearRespawn(uint spawnGuid) => Pending.Remove(spawnGuid);

        public void CopyRespawn(uint fromGuid, uint spawnGuid)
        {
            if (Pending.Contains(fromGuid))
            {
                Pending.Add(spawnGuid);
            }
        }

        public uint OwnEntry(uint spawnGuid) => Own.GetValueOrDefault(spawnGuid);

        public uint RandomSpawnEntry(uint spawnGuid) => SpawnEntries.TryGetValue(spawnGuid, out uint[]? e) ? e[Random.Next(e.Length)] : 0;

        public bool HasSpawnEntries(uint spawnGuid) => SpawnEntries.ContainsKey(spawnGuid);

        public bool ConditionHolds(SpawnGroupDefinition group) => group.WorldStateCondition == 0 || Condition;

        public void DespawnMembers(SpawnGroupState group)
        {
            Despawns++;
            group.Clear();
        }
    }

    private static SpawnGroupState State(SpawnGroupDefinition group, bool dungeon = false)
        => new(group, group.Members, group.RandomEntries, dungeon);

    [Fact]
    public void TheDerivedMaximum_FollowsLoadSpawnGroups()
    {
        Assert.Equal(10, State(MustyTome).MaxCount);   // stored 0, an equally chanced entry: every member
        Assert.Equal(1, State(Ore).MaxCount);          // stored 1
        Assert.Equal(4, State(QirajiMajors).MaxCount); // stored 0, entry 15750 equally chanced
        Assert.Equal(7, SpawnGroupState.EffectiveMaxCount(0, 7, []));                                    // no entries: every member
        Assert.Equal(2, SpawnGroupState.EffectiveMaxCount(0, 5, [new(1, 0, 2, 50)]));                  // only chanced: summed MaxCount
        Assert.Equal(0, SpawnGroupState.EffectiveMaxCount(0, 5, [new(1, 0, 0, 50)]));                  // only chanced, no MaxCount: nothing
    }

    [Fact]
    public void MustyTome_AllTenSpotsAreFilled_ExactlyOneWithTheRealTome()
    {
        for (int seed = 0; seed < 30; seed++)
        {
            SpawnGroupState group = State(MustyTome);
            var host = new Host();

            IReadOnlyList<(uint Guid, uint Entry)> spawned = group.Spawn(host, new Random(seed));

            Assert.Equal(TomeSpawns.Order(), spawned.Select(s => s.Guid).Order());
            Assert.Equal(1, spawned.Count(s => s.Entry == RealTome));
            Assert.Equal(9, spawned.Count(s => s.Entry == FakeTome));
            Assert.Empty(group.Spawn(host, new Random(seed))); // full: nothing more
        }
    }

    [Fact]
    public void Spawn_OfAFullOrWaitingGroup_AllocatesNothing_AndARespawnOnlyItsResult()
    {
        // SpawnGroupState.Spawn runs for every group of a map every update (wave 17 allocation leftover: it captured its arguments in a
        // closure on every call, and rebuilt its eligible list and entry dictionaries for each respawn).
        SpawnGroupState group = State(MustyTome);
        var host = new Host();
        var random = new Random(5);
        group.Spawn(host, random);
        uint real = group.Objects.Single(o => o.Value == RealTome).Key;
        group.Remove(real, host);
        Assert.Single(group.Spawn(host, random)); // warm the scratch collections

        long before = GC.GetAllocatedBytesForCurrentThread();
        int returned = 0;
        for (int i = 0; i < 1000; i++)
        {
            returned += group.Spawn(host, random).Count; // full (counted, not asserted per call: Assert.Empty boxes an enumerator)
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(0, returned);

        group.Remove(real, host);
        host.Pending.Add(real); // waiting for its respawn time
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            returned += group.Spawn(host, random).Count;
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(0, returned);

        host.Pending.Clear();
        before = GC.GetAllocatedBytesForCurrentThread();
        IReadOnlyList<(uint Guid, uint Entry)> back = group.Spawn(host, random);
        long respawn = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal((real, RealTome), Assert.Single(back));
        Assert.True(respawn <= 128, $"a respawn allocates only the list it returns ({respawn} B)");
    }

    [Fact]
    public void MustyTome_TheRealTomeMovesWhenItsSpotIsTaken_AndNeverDoubles()
    {
        SpawnGroupState group = State(MustyTome);
        var host = new Host();
        var random = new Random(5);
        group.Spawn(host, random);
        var spots = new HashSet<uint>();
        for (int i = 0; i < 40; i++)
        {
            uint real = group.Objects.Single(o => o.Value == RealTome).Key;
            spots.Add(real);
            group.Remove(real, host); // looted: the spot is free (no respawn time in this host)
            IReadOnlyList<(uint Guid, uint Entry)> back = group.Spawn(host, random);

            Assert.Equal((real, RealTome), Assert.Single(back)); // the only free spot gets the only entry still allowed
        }

        Assert.Single(spots);
        Assert.Equal(1, group.Objects.Values.Count(e => e == RealTome));
    }

    [Fact]
    public void Ore_OneNodeAtATime_MithrilMostly_GoldAndTruesilverByTheirFivePercent()
    {
        var entries = new Dictionary<uint, int>();
        var spots = new HashSet<uint>();
        var random = new Random(11);
        SpawnGroupState group = State(Ore);
        var host = new Host();
        for (int i = 0; i < 2000; i++)
        {
            (uint guid, uint entry) = Assert.Single(group.Spawn(host, random));
            Assert.Single(group.Objects);
            entries[entry] = entries.GetValueOrDefault(entry) + 1;
            spots.Add(guid);
            group.Remove(guid, host);
        }

        Assert.Equal(OreSpawns.Order(), spots.Order());
        Assert.Equal([GoldVein, MithrilDeposit, TruesilverDeposit], entries.Keys.Order());
        Assert.InRange(entries[MithrilDeposit], 1700, 1900); // 90 percent
        Assert.InRange(entries[GoldVein], 50, 160);          // 5 percent each (rolls 1-5 and 6-10)
        Assert.InRange(entries[TruesilverDeposit], 50, 160);
    }

    [Fact]
    public void Balgaras_ARareOfOne_DoesNotComeBackWhileAnyMemberIsOnItsRespawnTime()
    {
        SpawnGroupState group = State(BalgarasTheFoul);
        var host = new Host();
        foreach (uint guid in BalgarasSpawns)
        {
            host.SpawnEntries[guid] = [Balgaras];
        }

        (uint first, uint entry) = Assert.Single(group.Spawn(host, new Random(1)));
        Assert.Equal(Balgaras, entry);

        host.Pending.Add(first); // killed: its corpse is gone, its respawn time runs
        group.Remove(first, host);
        Assert.Empty(group.Spawn(host, new Random(1))); // cmangos "rare mob case": six free spots, none used

        host.Pending.Remove(first);
        Assert.Single(group.Spawn(host, new Random(2)));
    }

    [Fact]
    public void QirajiMajors_TheMinCountEntryComesFirst_ThenTheEquallyChancedOne_AndOnlyUnderTheCondition()
    {
        var host = new Host { Condition = false };
        SpawnGroupState group = State(QirajiMajors);
        Assert.Empty(group.Spawn(host, new Random(1))); // Game Event 123 not active

        host.Condition = true;
        for (int seed = 0; seed < 20; seed++)
        {
            group = State(QirajiMajors);
            IReadOnlyList<(uint Guid, uint Entry)> spawned = group.Spawn(host, new Random(seed));
            Assert.Equal(4, spawned.Count);
            Assert.Equal(1, spawned.Count(s => s.Entry == MajorHealie));
            Assert.Equal(3, spawned.Count(s => s.Entry == QirajiMajor));
        }
    }

    [Fact]
    public void DespawnOnConditionFail_TakesTheMembersOut_OthersStay()
    {
        var host = new Host();
        SpawnGroupState kept = State(QirajiMajors);
        SpawnGroupState despawning = State(QirajiMajors with { Flags = SpawnGroupFlags.DespawnOnConditionFail });
        kept.Spawn(host, new Random(1));
        despawning.Spawn(host, new Random(1));

        host.Condition = false;
        kept.Spawn(host, new Random(1));
        despawning.Spawn(host, new Random(1));

        Assert.Equal(4, kept.Objects.Count);
        Assert.Empty(despawning.Objects);
        Assert.Equal(1, host.Despawns);
    }

    [Fact]
    public void OwnAndSpawnEntryMembers_KeepTheirEntry_TheGroupEntriesAreForTheRest()
    {
        var host = new Host();
        foreach ((uint guid, uint entry) in KargathSpawns)
        {
            host.Own[guid] = entry;
        }

        SpawnGroupState group = State(KargathExpeditionaryForce);
        IReadOnlyList<(uint Guid, uint Entry)> spawned = group.Spawn(host, new Random(1));

        Assert.Equal(KargathSpawns.OrderBy(s => s.Guid), spawned.OrderBy(s => s.Guid));
    }

    [Fact]
    public void AGroupWithAChancedMember_RollsOncePerGroupLife_AndComesBackOnlyAsAWhole()
    {
        // classic-db's two chanced rows (group 3090030 spawn 3090975, group 3090031 spawn 3090985) are 50 percent, in groups like this one.
        var definition = new SpawnGroupDefinition
        {
            Id = 3090030,
            Type = SpawnGroupType.Creature,
            MaxCount = 3,
            Members = [new(1, -1, 0), new(2, -1, 0), new(3090975, -1, 50)],
        };
        var host = new Host();
        host.Own[1] = host.Own[2] = host.Own[3090975] = 9;
        var outcomes = new HashSet<bool>();
        for (int seed = 0; seed < 40; seed++)
        {
            var group = new SpawnGroupState(definition, definition.Members, [], false);
            var random = new Random(seed);
            bool chanced = group.Spawn(host, random).Any(s => s.Guid == 3090975);
            outcomes.Add(chanced);
            Assert.Equal(chanced ? 3 : 2, group.Objects.Count);

            group.Remove(1, host);
            Assert.Empty(group.Spawn(host, random)); // every member has had its roll: nothing until the group is empty

            foreach (uint guid in group.Objects.Keys.ToArray())
            {
                group.Remove(guid, host);
            }

            Assert.NotEmpty(group.Spawn(host, random)); // a new life: rolled again
        }

        Assert.Equal([false, true], outcomes.Order());
    }
}
