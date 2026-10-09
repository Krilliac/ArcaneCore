using ArcaneCore.Kernel.WorldData.SpawnGroups;

namespace ArcaneCore.Game.Maps.SpawnGroups;

/// <summary>What a <see cref="SpawnGroupState"/> needs to know about the spawns of its map (the creature or game object system).</summary>
internal interface ISpawnGroupHost
{
    /// <summary>Whether the spawn's respawn time is still in the future (cmangos <c>GetObjectRespawnTime(guid) &gt; now</c>).</summary>
    bool IsRespawnPending(uint spawnGuid);

    /// <summary>Forget the spawn's respawn time (cmangos <c>SaveObjectRespawnTime(guid, now)</c>).</summary>
    void ClearRespawn(uint spawnGuid);

    /// <summary>Give <paramref name="spawnGuid"/> the respawn time <paramref name="fromGuid"/> has (a chanced group respawns as one).</summary>
    void CopyRespawn(uint fromGuid, uint spawnGuid);

    /// <summary>The spawn row's own entry (<c>creature.id</c> / <c>gameobject.id</c>; 0 for none), when it has a template.</summary>
    uint OwnEntry(uint spawnGuid);

    /// <summary>One of the spawn's <c>creature_spawn_entry</c> / <c>gameobject_spawn_entry</c> entries, uniformly (0 when it has none).</summary>
    uint RandomSpawnEntry(uint spawnGuid);

    /// <summary>Whether the spawn has <c>*_spawn_entry</c> rows at all (cmangos SpawnGroupDbGuids::RandomEntry).</summary>
    bool HasSpawnEntries(uint spawnGuid);

    /// <summary>Whether the group's condition holds now (<c>spawn_group.WorldState</c>); false when it cannot be decided.</summary>
    bool ConditionHolds(SpawnGroupDefinition group);

    /// <summary>Take the group's live members out of the world now, ready to come back at the next spawn (SPAWN_GROUP_DESPAWN_ON_COND_FAIL).</summary>
    void DespawnMembers(SpawnGroupState group);
}

/// <summary>
/// One cmangos spawn group on one map (Maps/SpawnGroup.cpp SpawnGroup::Spawn, GetEligibleEntry, RemoveObject; the definition rules of
/// ObjectMgr::LoadSpawnGroups, Globals/ObjectMgr.cpp:1120-1540): which of the group's database spawns are in the world now and which entry
/// each became. Re-implemented from the cmangos behaviour; no code is copied.
/// <para>
/// The group spawns members until <see cref="MaxCount"/> are out. A member on its respawn time is skipped (a group of one waits until every
/// member is off it, the "rare mob" rule); members are shuffled before the count is cut; a member with a <c>Chance</c> rolls it once per
/// group life. A member's entry is its <c>*_spawn_entry</c> pick, else its own <c>id</c>, else one of the group's <c>spawn_group_entry</c>
/// rows: the <c>MinCount</c> entries first, then the explicitly chanced rows by a 1..100 roll, then the equally chanced (Chance 0) rows in
/// random order, never past an entry's <c>MaxCount</c>. A creature group in a dungeon, once full, only ever brings back the members it chose.
/// </para>
/// <para>
/// Not modelled: squads (<c>spawn_group_squad</c>, not in classic-db z2815), <c>RespawnOverrideMin/Max</c> (not in z2815), the world-state
/// expressions, formations and linked groups. A member for which no entry can be found is not spawned (cmangos keeps it in the group with
/// entry 0 and its creation fails, which blocks the slot).
/// </para>
/// World thread only.
/// </summary>
internal sealed class SpawnGroupState
{
    private readonly Dictionary<uint, uint> _objects = [];
    private readonly Dictionary<uint, bool> _chosenSpawns = [];
    private readonly Dictionary<uint, uint> _chosenEntries = [];
    private readonly SpawnGroupRandomEntry[] _equallyChanced;
    private readonly SpawnGroupRandomEntry[] _explicitlyChanced;

    /// <param name="definition">The group as loaded.</param>
    /// <param name="members">The members that are spawns of this map (cmangos drops a guid without spawn data).</param>
    /// <param name="randomEntries">The group entries that have a template (cmangos drops the others at load).</param>
    /// <param name="dungeon">Whether the map is a dungeon (entries chosen once; no group cooldown).</param>
    public SpawnGroupState(SpawnGroupDefinition definition, IReadOnlyList<SpawnGroupMember> members, IReadOnlyList<SpawnGroupRandomEntry> randomEntries, bool dungeon)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(randomEntries);
        Definition = definition;
        Members = members;
        RandomEntries = randomEntries;
        IsDungeon = dungeon;
        HasChancedSpawns = members.Any(m => m.Chance != 0);
        MaxCount = EffectiveMaxCount(definition.MaxCount, members.Count, randomEntries);
        _equallyChanced = [.. randomEntries.Where(e => e.Chance == 0)];
        _explicitlyChanced = [.. randomEntries.Where(e => e.Chance != 0)];
    }

    public SpawnGroupDefinition Definition { get; }

    public uint Id => Definition.Id;

    public IReadOnlyList<SpawnGroupMember> Members { get; }

    public IReadOnlyList<SpawnGroupRandomEntry> RandomEntries { get; }

    public bool IsDungeon { get; }

    public bool HasChancedSpawns { get; }

    /// <summary>
    /// A creature group in a dungeon keeps the members it chose once it has <see cref="MaxCount"/> of them, and its members take the entry
    /// recorded for them (cmangos <c>m_chosenEntries</c>, held in memory only; SpawnGroup.cpp:263-270, 404-406). Game objects always pick.
    /// </summary>
    private bool ChoosesEntriesOnce => IsDungeon && Definition.Type == SpawnGroupType.Creature;

    /// <summary>
    /// The most members in the world at once. A stored 0 is derived as cmangos does after loading (ObjectMgr.cpp:1460-1475): every member
    /// when an entry row has Chance 0, else the smaller of the members and the summed entry <c>MaxCount</c>s, and every member when that is
    /// 0 and the group has no entry rows.
    /// </summary>
    public int MaxCount { get; }

    /// <summary>The members in the world now (spawned, or chosen while their grid is not loaded) and the entry each became.</summary>
    public IReadOnlyDictionary<uint, uint> Objects => _objects;

    public bool Has(SpawnGroupFlags flag) => (Definition.Flags & flag) != 0;

    public bool TryGetEntry(uint spawnGuid, out uint entry) => _objects.TryGetValue(spawnGuid, out entry);

    internal static int EffectiveMaxCount(uint stored, int members, IReadOnlyList<SpawnGroupRandomEntry> entries)
    {
        if (stored != 0)
        {
            return (int)Math.Min(stored, int.MaxValue);
        }

        long summed = 0;
        bool equallyChanced = false;
        foreach (SpawnGroupRandomEntry entry in entries)
        {
            summed += entry.MaxCount;
            equallyChanced |= entry.Chance == 0;
        }

        int max = equallyChanced ? members : (int)Math.Min(summed, members);
        return max == 0 && entries.Count == 0 ? members : max;
    }

    /// <summary>
    /// A member left the world (its corpse decayed, it despawned, its grid unloaded): cmangos SpawnGroup::RemoveObject. When the last one goes on
    /// an open-world map, a group with chanced members rolls them again next time and every member takes the last one's respawn time.
    /// </summary>
    public void Remove(uint spawnGuid, ISpawnGroupHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!_objects.Remove(spawnGuid) || IsDungeon || _objects.Count > 0 || !HasChancedSpawns)
        {
            return;
        }

        _chosenSpawns.Clear();
        foreach (SpawnGroupMember member in Members)
        {
            if (member.Guid != spawnGuid)
            {
                host.CopyRespawn(spawnGuid, member.Guid);
            }
        }
    }

    /// <summary>Forget every member (the map's spawns were removed wholesale, e.g. a despawn of the group).</summary>
    public void Clear() => _objects.Clear();

    /// <summary>
    /// cmangos SpawnGroup::Spawn(false, <paramref name="ignoreRespawnTime"/>): choose the members (and their entries) that come into the world
    /// now and record them. Returns them; the host creates those whose grid is loaded.
    /// </summary>
    public IReadOnlyList<(uint Guid, uint Entry)> Spawn(ISpawnGroupHost host, Random random, bool ignoreRespawnTime = false)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(random);
        if (Has(SpawnGroupFlags.DespawnOnConditionFail) && _objects.Count > 0 && !host.ConditionHolds(Definition))
        {
            host.DespawnMembers(this);
            return [];
        }

        if (_objects.Count >= MaxCount || !host.ConditionHolds(Definition))
        {
            return [];
        }

        if (HasChancedSpawns && _chosenSpawns.Count >= MaxCount)
        {
            return [];
        }

        if (!ignoreRespawnTime && !ChoosesEntriesOnce && !AnyMemberReady(host))
        {
            return []; // the per-update cost of a group waiting for respawn times: one pass, no allocation
        }

        var eligible = new List<SpawnGroupMember>(Members.Count);
        if (ChoosesEntriesOnce && _chosenEntries.Count == MaxCount)
        {
            eligible.AddRange(Members.Where(m => _chosenEntries.ContainsKey(m.Guid))); // once picked, only reuse
        }
        else
        {
            eligible.AddRange(Members);
        }

        var valid = new Dictionary<uint, uint>();
        var minimum = new SortedDictionary<uint, uint>();
        foreach (SpawnGroupRandomEntry entry in RandomEntries)
        {
            valid[entry.Entry] = entry.MaxCount > 0 ? entry.MaxCount : uint.MaxValue;
            if (entry.MinCount > 0)
            {
                minimum[entry.Entry] = entry.MinCount;
            }
        }

        foreach ((uint guid, uint entry) in _objects)
        {
            eligible.RemoveAll(m => m.Guid == guid);
            if (valid.Count > 0)
            {
                valid[entry] = valid.GetValueOrDefault(entry) is > 0 and var count ? count - 1 : 0;
            }

            Consume(minimum, entry);
        }

        for (int i = 0; i < eligible.Count;)
        {
            if (host.IsRespawnPending(eligible[i].Guid))
            {
                if (!ignoreRespawnTime)
                {
                    if (MaxCount == 1)
                    {
                        return []; // rare mob case: nothing comes back until every member is off its respawn time
                    }

                    eligible.RemoveAt(i);
                    continue;
                }

                host.ClearRespawn(eligible[i].Guid);
            }

            i++;
        }

        Shuffle(eligible, random);
        if (HasChancedSpawns)
        {
            for (int i = 0; i < eligible.Count;)
            {
                SpawnGroupMember member = eligible[i];
                if (member.Chance != 0)
                {
                    if (!_chosenSpawns.TryGetValue(member.Guid, out bool spawn))
                    {
                        spawn = random.Next(100) < member.Chance; // roll_chance_i
                        _chosenSpawns[member.Guid] = spawn;
                    }

                    if (!spawn)
                    {
                        eligible.RemoveAt(i);
                        continue;
                    }
                }
                else
                {
                    _chosenSpawns[member.Guid] = true;
                }

                i++;
            }
        }

        int room = Math.Max(0, MaxCount - _objects.Count);
        if (eligible.Count > room)
        {
            eligible.RemoveRange(room, eligible.Count - room);
        }

        if (ChoosesEntriesOnce)
        {
            // Static and self-contained random entries first, so the group entries are shared out among the rest (SpawnGroup.cpp:369-390).
            foreach (SpawnGroupMember member in eligible.Where(m => host.HasSpawnEntries(m.Guid) || host.OwnEntry(m.Guid) != 0))
            {
                Choose(member.Guid);
            }

            foreach (SpawnGroupMember member in eligible.Where(m => !host.HasSpawnEntries(m.Guid) && host.OwnEntry(m.Guid) == 0))
            {
                Choose(member.Guid);
            }
        }

        var spawned = new List<(uint Guid, uint Entry)>(eligible.Count);
        foreach (SpawnGroupMember member in eligible)
        {
            uint entry;
            if (ChoosesEntriesOnce)
            {
                entry = _chosenEntries.GetValueOrDefault(member.Guid);
            }
            else
            {
                entry = PickEntry(member.Guid);
                Erase(entry);
            }

            if (entry == 0)
            {
                continue;
            }

            _objects[member.Guid] = entry;
            spawned.Add((member.Guid, entry));
        }

        return spawned;

        void Choose(uint guid)
        {
            uint chosen = PickEntry(guid);
            _chosenEntries[guid] = chosen;
            Erase(chosen);
        }

        uint PickEntry(uint guid)
        {
            if (host.HasSpawnEntries(guid))
            {
                return host.RandomSpawnEntry(guid);
            }

            uint own = host.OwnEntry(guid);
            return own != 0 ? own : EligibleEntry(valid, minimum, random);
        }

        void Erase(uint entry)
        {
            if (entry == 0)
            {
                return;
            }

            if (valid.TryGetValue(entry, out uint count) && count > 0)
            {
                valid[entry] = count - 1;
            }

            Consume(minimum, entry);
        }
    }

    /// <summary>
    /// Whether a member outside the world is off its respawn time; false as well for a group of one with any member still on it (the
    /// "rare mob" rule below), so such a group costs one pass over its members per update while it waits.
    /// </summary>
    private bool AnyMemberReady(ISpawnGroupHost host)
    {
        bool ready = false;
        foreach (SpawnGroupMember member in Members)
        {
            if (_objects.ContainsKey(member.Guid))
            {
                continue;
            }

            if (!host.IsRespawnPending(member.Guid))
            {
                ready = true;
            }
            else if (MaxCount == 1)
            {
                return false;
            }
        }

        return ready;
    }

    /// <summary>cmangos SpawnGroup::GetEligibleEntry over what is still allowed (<paramref name="valid"/>) and still owed (<paramref name="minimum"/>).</summary>
    private uint EligibleEntry(Dictionary<uint, uint> valid, SortedDictionary<uint, uint> minimum, Random random)
    {
        if (RandomEntries.Count == 0)
        {
            return 0;
        }

        if (minimum.Count > 0)
        {
            return minimum.Keys.ElementAt(random.Next(minimum.Count));
        }

        if (_explicitlyChanced.Length > 0)
        {
            int roll = random.Next(1, 101);
            foreach (SpawnGroupRandomEntry entry in _explicitlyChanced)
            {
                if (valid.GetValueOrDefault(entry.Entry) > 0)
                {
                    if (roll <= entry.Chance)
                    {
                        return entry.Entry;
                    }

                    roll -= (int)entry.Chance;
                }
            }
        }

        if (_equallyChanced.Length == 0)
        {
            return 0;
        }

        SpawnGroupRandomEntry[] order = [.. _equallyChanced];
        Shuffle(order, random);
        foreach (SpawnGroupRandomEntry entry in order)
        {
            if (valid.GetValueOrDefault(entry.Entry) > 0)
            {
                return entry.Entry;
            }
        }

        return 0;
    }

    private static void Consume(SortedDictionary<uint, uint> minimum, uint entry)
    {
        if (minimum.TryGetValue(entry, out uint left))
        {
            if (left <= 1)
            {
                minimum.Remove(entry);
            }
            else
            {
                minimum[entry] = left - 1;
            }
        }
    }

    private static void Shuffle<T>(IList<T> list, Random random)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
