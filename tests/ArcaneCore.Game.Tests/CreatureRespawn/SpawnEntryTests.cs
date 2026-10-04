using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureRespawn;

/// <summary>
/// A spawn that can become several creature entries (cmangos creature_spawn_entry, ObjectMgr.cpp:1826-1869 and Creature::LoadFromDB /
/// ResetEntry, Entities/Creature.cpp:1640-1660, 636-655; vmangos Creature.cpp:830-841 and :1936-1944): an entry is chosen when the object
/// loads and again at every respawn; the GUID keeps the entry it was created with and the client sees the new entry in the next create block.
/// </summary>
public sealed class SpawnEntryTests
{
    private const uint EntryA = 5001;
    private const uint EntryB = 5002;

    private static CreatureTemplate Variant(uint entry, string name, string aiName = "")
        => Template(entry, t =>
        {
            t.Name = name;
            t.AIName = aiName;
        });

    private static (WorldRuntime World, CreatureMapSystem System, Player Player, FakeSession Session) Start(
        CreatureContent content, CreatureOptions? options = null, CreatureAiServices? services = null, int seed = 1)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var system = new CreatureMapSystem(map, content, options, random: new Random(seed), aiServices: services ?? new CreatureAiServices());
        map.AddUpdater(system);
        var session = new FakeSession(1);
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        world.RunTick(50);
        session.Clear();
        return (world, system, player, session);
    }

    private static CreatureContent TwoVariants(uint spawnEntry = 0, params CreatureTemplate[] extra)
        => Content([Variant(EntryA, "Variant A"), Variant(EntryB, "Variant B"), .. extra], [Spawn(1, spawnEntry, 10, 0)],
            spawnEntries: [(1u, EntryA), (1u, EntryB)]);

    [Fact]
    public void ASpawnWithEntryZero_SpawnsOneOfItsEntries_InsteadOfBeingSkipped()
    {
        (WorldRuntime w, CreatureMapSystem system, _, _) = Start(TwoVariants());
        using WorldRuntime world = w;

        Creature creature = Assert.Single(system.Creatures);

        Assert.Contains(creature.Template.Entry, new[] { EntryA, EntryB });
        Assert.Equal(creature.Template.Entry, creature.Entry);
        Assert.Equal(1u, creature.Spawn!.Guid);
    }

    [Fact]
    public void TheChoiceFollowsTheSeed_AndBothEntriesComeUpAcrossLoads()
    {
        var seen = new Dictionary<int, uint>();
        for (int seed = 0; seed < 40; seed++)
        {
            (WorldRuntime w, CreatureMapSystem system, _, _) = Start(TwoVariants(), seed: seed);
            using WorldRuntime world = w;
            seen[seed] = Assert.Single(system.Creatures).Template.Entry;
        }

        Assert.Contains(EntryA, seen.Values);
        Assert.Contains(EntryB, seen.Values);

        // The same seed gives the same entry (deterministic, no wall clock).
        (WorldRuntime w2, CreatureMapSystem again, _, _) = Start(TwoVariants(), seed: 7);
        using WorldRuntime world2 = w2;
        Assert.Equal(seen[7], Assert.Single(again.Creatures).Template.Entry);
    }

    [Fact]
    public void EveryRespawn_ChoosesAgain_TheGuidKeepsItsEntry_AndTheAiFollowsTheNewTemplate()
    {
        CreatureContent content = Content(
            [Variant(EntryA, "Variant A", RecorderName), Variant(EntryB, "Variant B")],
            [Spawn(1, 0, 10, 0)],
            spawnEntries: [(1u, EntryA), (1u, EntryB)]);
        (WorldRuntime w, CreatureMapSystem system, _, _) = Start(content, services: new CreatureAiServices { Factory = RecorderFactory() });
        using WorldRuntime world = w;
        Creature wolf = Assert.Single(system.Creatures);
        ObjectGuid guid = wolf.Guid;

        var entries = new HashSet<uint> { wolf.Template.Entry };
        for (int i = 0; i < 60; i++)
        {
            system.KillCreature(wolf);
            system.ForceRespawn(wolf);

            Assert.Equal(guid, wolf.Guid); // vmangos/cmangos keep the GUID the object was created with
            Assert.Same(wolf, system.FindCreature(guid));
            Assert.Equal(wolf.Template.Entry, wolf.Entry);
            Assert.Equal(wolf.Template.Entry == EntryA, wolf.AI is RecorderAI); // vmangos: CSTATE_INIT_AI_ON_RESPAWN, cmangos ResetEntry -> AIM_Initialize
            entries.Add(wolf.Template.Entry);
        }

        Assert.Equal([EntryA, EntryB], entries.Order());
    }

    [Fact]
    public void TheClientSeesTheChosenEntryInTheCreateBlockOfTheRespawn()
    {
        (WorldRuntime w, CreatureMapSystem system, _, FakeSession session) = Start(TwoVariants());
        using WorldRuntime world = w;
        Creature wolf = Assert.Single(system.Creatures);
        var seen = new HashSet<uint>();

        for (int i = 0; i < 40 && seen.Count < 2; i++)
        {
            system.KillCreature(wolf);
            system.ForceRespawn(wolf);
            world.RunTick(50);
            ParsedBlock create = Assert.Single(DrainBlocks(session), b => b.Type == ObjectUpdateType.CreateObject && b.Guids.Contains(wolf.Guid.Value));
            seen.Add(create.Values[UpdateFields.ObjectFieldEntry]);
            Assert.Equal(wolf.Template.Entry, create.Values[UpdateFields.ObjectFieldEntry]);
        }

        Assert.Equal([EntryA, EntryB], seen.Order());
    }

    [Fact]
    public void AnEntryWithoutATemplate_IsNeverChosen_AndASpawnWithNoUsableEntryIsSkipped()
    {
        CreatureContent partly = Content([Variant(EntryA, "Variant A")], [Spawn(1, 0, 10, 0)], spawnEntries: [(1u, EntryA), (1u, 9999u)]);
        (WorldRuntime w1, CreatureMapSystem system, _, _) = Start(partly);
        using WorldRuntime world1 = w1;
        Creature wolf = Assert.Single(system.Creatures);
        for (int i = 0; i < 30; i++)
        {
            system.KillCreature(wolf);
            system.ForceRespawn(wolf);
            Assert.Equal(EntryA, wolf.Template.Entry);
        }

        CreatureContent none = Content([Variant(EntryA, "Variant A")], [Spawn(1, 0, 10, 0)], spawnEntries: [(1u, 9998u), (1u, 9999u)]);
        (WorldRuntime w2, CreatureMapSystem empty, _, _) = Start(none);
        using WorldRuntime world2 = w2;
        Assert.Empty(empty.Creatures);
    }

    [Fact]
    public void ASpawnWithoutEntries_KeepsItsFixedEntry_AsBefore()
    {
        CreatureContent content = Content([Variant(EntryA, "Variant A")], [Spawn(1, EntryA, 10, 0)]);
        (WorldRuntime w, CreatureMapSystem system, _, _) = Start(content);
        using WorldRuntime world = w;
        Creature wolf = Assert.Single(system.Creatures);

        system.KillCreature(wolf);
        system.ForceRespawn(wolf);

        Assert.Equal(EntryA, wolf.Template.Entry);
    }

    [Fact]
    public void WithAlternateEntriesOff_AnEntryZeroSpawnIsSkippedAsBefore()
    {
        var options = new CreatureOptions();
        options.Respawn.AlternateEntries = false;
        (WorldRuntime w, CreatureMapSystem system, _, _) = Start(TwoVariants(), options);
        using WorldRuntime world = w;

        Assert.Empty(system.Creatures);
        Assert.True(new CreatureOptions().Respawn.AlternateEntries);
    }
}
