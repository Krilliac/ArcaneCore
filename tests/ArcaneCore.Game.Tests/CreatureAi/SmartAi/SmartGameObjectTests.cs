using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureAi.SmartAi.SmartRows;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi.SmartAi;

/// <summary>
/// Source type 1 (game object) with AzerothCore SmartGameObjectAI semantics (SmartAI.cpp:1459-1490, SmartScript.cpp), wired as the fallback object AI of the
/// map's <see cref="GameObjectMapSystem"/>: events 1, 37, 59, 60, 61, 63 and 64, a game object's own TALK, CAST and timed action lists
/// (docs/integration/smartai-slice2-20261010.md).
/// </summary>
public sealed class SmartGameObjectTests
{
    private const uint Entry = SmartRig.ObjectEntry;

    private sealed class CountingAi : IGameObjectAi
    {
        public int Updates { get; private set; }

        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs) => Updates++;
    }

    private static GameObjectSpawn Go(uint guid, uint entry, float x, float y) => GameObjectTestKit.GoSpawn(guid, entry, x, y);

    private static SmartRig Start(IEnumerable<SmartScriptRow> rows, IEnumerable<GameObjectSpawn> spawns,
        bool conditions = false, IEnumerable<CreatureSpawn>? creatures = null)
        => SmartRig.Start(rows, conditions, creatures, spawns);

    private static SmartRig StartOne(IEnumerable<SmartScriptRow> rows, bool conditions = false, float objectX = 0, float objectY = 2,
        IEnumerable<CreatureSpawn>? creatures = null)
        => Start(rows, [Go(1, Entry, objectX, objectY)], conditions, creatures);

    private static List<(GameObject Source, uint Spell, Unit Target)> Casts(SmartRig rig)
        => [.. rig.ObjectSpells.Casts.Select(c => (c.Source, c.Spell, c.Target))];

    [Fact]
    public void GameObjectWithRows_GetsTheSmartAi_UnlessACSharpAiIsRegistered()
    {
        using SmartRig rig = Start(
            [ObjectRow((int)Entry, 0, SmartEvent.Update, SmartAction.Cast, SmartTarget.ClosestPlayer, a1: 1)],
            [Go(1, Entry, 0, 10), Go(2, SmartRig.ObjectEntry2, 0, 12)]);
        GameObject withRows = rig.ObjectBySpawn(1);
        GameObject withoutRows = rig.ObjectBySpawn(2);

        Assert.True(rig.Objects!.FallbackAi!.HandlesAny);
        Assert.IsType<SmartGameObjectAi>(rig.Objects.AiFor(withRows));
        Assert.Null(rig.Objects.AiFor(withoutRows)); // the opt-in is the rows themselves: an object without rows has no smart AI

        rig.World.RunTick(10);
        Assert.Equal([(withRows, 1u, (Unit)rig.Player)], Casts(rig));

        // A C# AI registered for the entry always wins, and the smart script is no longer run for it.
        var registered = new CountingAi();
        rig.Objects.RegisterAi(Entry, registered);
        Assert.Same(registered, rig.Objects.AiFor(withRows));
        rig.ObjectSpells.Casts.Clear();
        rig.World.RunTick(10);
        Assert.Equal(1, registered.Updates);
        Assert.Empty(rig.ObjectSpells.Casts);

        rig.Objects.UnregisterAi(Entry);
        Assert.IsType<SmartGameObjectAi>(rig.Objects.AiFor(withRows));
    }

    [Fact]
    public void GameObjectWithoutAnyRows_HasNoFallbackWork()
    {
        // Rows of other sources only: no game object has a smart script, and the map does not walk its objects for one.
        using SmartRig rig = StartOne([CreatureRow(WolfEntry, 0, SmartEvent.Aggro, SmartAction.SetEventPhase, a1: 1)]);
        GameObject go = rig.ObjectBySpawn(1);
        Assert.False(rig.Objects!.FallbackAi!.HandlesAny);
        Assert.False(rig.Objects.FallbackAi.Handles(go));
        Assert.Null(rig.Objects.AiFor(go));
        rig.World.RunTick(10);
        Assert.Empty(rig.ObjectSpells.Casts);
    }

    [Fact]
    public void AiInitThenJustCreated_FireOnceWhenTheScriptStarts()
    {
        using SmartRig rig = Start(
        [
            ObjectRow((int)Entry, 0, SmartEvent.AiInit, SmartAction.SetEventPhase, a1: 4),
            ObjectRow((int)Entry, 1, SmartEvent.JustCreated, SmartAction.IncEventPhase, a1: 3),
        ], [Go(1, Entry, 0, 10), Go(2, Entry, 0, 12)]);
        rig.World.RunTick(50);

        // AI_INIT first (phase 4), then JUST_CREATED adds 3: had they run in the other order, or the other row not at all, the phase would differ.
        Assert.Equal(7u, rig.ObjectScript(rig.ObjectBySpawn(1)).Phase);
        Assert.Equal(7u, rig.ObjectScript(rig.ObjectBySpawn(2)).Phase); // each object has its own script

        // Later updates do not raise them again.
        rig.World.RunTick(50);
        rig.World.RunTick(1000);
        Assert.Equal(7u, rig.ObjectScript(rig.ObjectBySpawn(1)).Phase);
        Assert.Equal(7u, rig.ObjectScript(rig.ObjectBySpawn(2)).Phase);
    }

    [Fact]
    public void GossipHello_FiresOnPlayerUse_FiltersZeroAndOne_AndTheUseContinues()
    {
        using SmartRig rig = Start(
        [
            ObjectRow((int)Entry, 0, SmartEvent.GossipHello, SmartAction.Cast, SmartTarget.ActionInvoker, e1: 0, a1: 1),
            ObjectRow((int)Entry, 1, SmartEvent.GossipHello, SmartAction.Cast, SmartTarget.ActionInvoker, e1: 1, a1: 2),
            ObjectRow((int)Entry, 2, SmartEvent.GossipHello, SmartAction.Cast, SmartTarget.ActionInvoker, e1: 2, a1: 3), // report use: refused at load
        ], [Go(1, Entry, 0, 2), Go(2, SmartRig.ObjectEntry2, 0, 3)]);
        Assert.Contains(rig.Catalog.Rejected, r => r.Id == 2 && r.Reason.Contains("filter 2", StringComparison.Ordinal));

        int used = 0;
        rig.Objects!.Used += (_, _) => used++;
        GameObject go = rig.ObjectBySpawn(1);
        GameObject plain = rig.ObjectBySpawn(2);

        // The control: the same kind of object with no rows. The script never takes the use over (SmartGameObjectAI::GossipHello returns false).
        GameObjectUseResult control = rig.Objects.Use(rig.Player, plain.Guid);
        int controlUsed = used;
        Assert.Equal(GameObjectUseResult.Ok, control);
        Assert.Equal(1, controlUsed);

        Assert.Equal(control, rig.Objects.Use(rig.Player, go.Guid));
        Assert.Equal(controlUsed + 1, used);
        Assert.Equal([(go, 1u, (Unit)rig.Player), (go, 2u, rig.Player)], Casts(rig)); // filters 0 and 1 both fire for a player use, in row order
        Assert.Null(rig.ObjectSpells.Casts[0].Caster);

        // A creature using the object is not a player: no GOSSIP_HELLO.
        rig.ObjectSpells.Casts.Clear();
        rig.Objects.UseByUnit(rig.Wolf, go);
        Assert.Empty(rig.ObjectSpells.Casts);
    }

    [Fact]
    public void Respawn_ResetsTheScript_NotRepeatableFiresAgainAndThePhaseReturnsToZero()
    {
        // AzerothCore calls AI()->Reset() on respawn of an object spawned by default (spawntimesecs >= 0) to clear one-time events
        // (GameObject.cpp:656-665); the same instance respawns here.
        // The goober template carries the noDamageImmune column, as a goober with a non-negative spawn time must to despawn at all (GameObject.cpp:985-991).
        const uint despawning = SmartRig.ObjectEntryDespawning;
        using SmartRig rig = Start(
        [
            Make((int)despawning, 1, 0, (byte)SmartEvent.GossipHello, (byte)SmartAction.Cast, (byte)SmartTarget.ActionInvoker, a1: 1, flags: (uint)SmartEventFlags.NotRepeatable),
            ObjectRow((int)despawning, 1, SmartEvent.GossipHello, SmartAction.SetEventPhase, a1: 3),
        ], [GameObjectTestKit.GoSpawn(1, despawning, 0, 2, spawnTimeSeconds: 60)]);
        GameObject go = rig.ObjectBySpawn(1);
        Assert.True(go.IsSpawned);
        SmartScript script = rig.ObjectScript(go);

        rig.Objects!.UseByUnit(rig.Player, go);
        rig.Objects.UseByUnit(rig.Player, go);
        Assert.Equal([(go, 1u, (Unit)rig.Player)], Casts(rig)); // NOT_REPEATABLE: once
        Assert.Equal(3u, script.Phase);

        Assert.True(rig.Objects.DespawnForRespawn(go));
        rig.World.RunTick(10);
        rig.World.RunTick(10);
        Assert.False(go.IsSpawned);
        rig.Objects.SetRespawnIn(go, 1);
        rig.World.RunTick(1100);
        Assert.True(go.IsSpawned);
        Assert.Same(script, rig.ObjectScript(go));
        Assert.Equal(0u, script.Phase);

        rig.Objects.UseByUnit(rig.Player, go);
        Assert.Equal(2, Casts(rig).Count); // fires again after the respawn
        Assert.Equal(3u, script.Phase);
    }

    [Fact]
    public void Respawn_OfAnObjectNotSpawnedByDefault_DoesNotResetTheScript()
    {
        // A negative spawntimesecs object is not m_spawnedByDefault: AzerothCore returns before AI()->Reset() (GameObject.cpp:656-665),
        // so NOT_REPEATABLE and the phase carry across a respawn a script or event brings about.
        using SmartRig rig = Start(
        [
            Make((int)Entry, 1, 0, (byte)SmartEvent.GossipHello, (byte)SmartAction.Cast, (byte)SmartTarget.ActionInvoker, a1: 1, flags: (uint)SmartEventFlags.NotRepeatable),
            ObjectRow((int)Entry, 1, SmartEvent.GossipHello, SmartAction.SetEventPhase, a1: 3),
        ], [GameObjectTestKit.GoSpawn(1, Entry, 0, 2, spawnTimeSeconds: -60)]);
        GameObject go = rig.ObjectBySpawn(1);
        rig.Objects!.ForceRespawn(go); // a negative spawn time starts down
        Assert.True(go.IsSpawned);
        SmartScript script = rig.ObjectScript(go);

        rig.Objects.UseByUnit(rig.Player, go);
        Assert.Equal([(go, 1u, (Unit)rig.Player)], Casts(rig));
        Assert.Equal(3u, script.Phase);

        Assert.True(rig.Objects.DespawnForRespawn(go));
        rig.World.RunTick(10);
        rig.World.RunTick(10);
        Assert.False(go.IsSpawned);
        rig.Objects.SetRespawnIn(go, 1);
        rig.World.RunTick(1100);
        Assert.True(go.IsSpawned);
        Assert.Same(script, rig.ObjectScript(go));
        Assert.Equal(3u, script.Phase); // no reset

        rig.Objects.UseByUnit(rig.Player, go);
        Assert.Single(Casts(rig)); // NOT_REPEATABLE still spent
    }

    [Fact]
    public void UpdateOutOfCombatAndUpdate_RunOnTheirTimers()
    {
        using SmartRig rig = StartOne(
        [
            ObjectRow((int)Entry, 0, SmartEvent.UpdateOutOfCombat, SmartAction.Cast, SmartTarget.ClosestPlayer, 1000, 1000, 3000, 3000, a1: 10),
            ObjectRow((int)Entry, 1, SmartEvent.Update, SmartAction.Cast, SmartTarget.ClosestPlayer, 500, 500, 2000, 2000, a1: 20),
        ], objectY: 10);
        IEnumerable<uint> Spells() => rig.ObjectSpells.Casts.Select(c => c.Spell);

        rig.World.RunTick(499);
        Assert.Empty(Spells());
        rig.World.RunTick(2);                       // 501 ms: UPDATE (first run after 500)
        Assert.Equal([20u], Spells());
        rig.World.RunTick(501);                     // 1002 ms: UPDATE_OOC (first run after 1000; a game object is never in combat)
        Assert.Equal([20u, 10u], Spells());
        rig.World.RunTick(1500);                    // 2502 ms: UPDATE again, 2000 ms after its first run
        Assert.Equal([20u, 10u, 20u], Spells());
        rig.World.RunTick(1501);                    // 4003 ms: UPDATE_OOC again, 3000 ms after its first run
        Assert.Equal(2, Spells().Count(s => s == 10));
        Assert.All(rig.ObjectSpells.Casts, c => Assert.Same(rig.Player, c.Target));
    }

    [Fact]
    public void Cast_GoesThroughTheObjectSpellSeam_AtUnitTargetsOnly()
    {
        const uint flags = (uint)(SmartCastFlags.AuraNotPresent | SmartCastFlags.Triggered | SmartCastFlags.InterruptPrevious);
        using SmartRig rig = StartOne(
        [
            ObjectRow((int)Entry, 0, SmartEvent.Update, SmartAction.Cast, SmartTarget.Self, a1: 1),                               // the object itself is no unit
            ObjectRow((int)Entry, 1, SmartEvent.Update, SmartAction.Cast, SmartTarget.ClosestPlayer, a1: 2, a2: flags),           // cast flags are ignored on a game object
            ObjectRow((int)Entry, 2, SmartEvent.Update, SmartAction.Cast, SmartTarget.ClosestCreature, a1: 3, t1: SmartRig.Speaker),
        ], objectY: 10, creatures: [Spawn(2, SmartRig.Speaker, 5, 5)]);
        Creature speaker = rig.Creatures.Creatures.Single(c => c.Entry == SmartRig.Speaker);
        GameObject go = rig.ObjectBySpawn(1);

        rig.World.RunTick(10);
        Assert.Equal([(go, 2u, (Unit)rig.Player), (go, 3u, speaker)], Casts(rig));
        Assert.All(rig.ObjectSpells.Casts, c => Assert.Null(c.Caster));
    }

    [Fact]
    public void Cast_WithoutTheObjectSpellSeam_DoesNothing()
    {
        using SmartRig rig = StartOne([ObjectRow((int)Entry, 0, SmartEvent.Update, SmartAction.Cast, SmartTarget.ClosestPlayer, a1: 2)], objectY: 10);
        rig.Objects!.Spells = null;
        rig.World.RunTick(10);
        Assert.Empty(rig.ObjectSpells.Casts);

        // The seam is the only reason: with it back, the next update casts.
        rig.Objects.Spells = rig.ObjectSpells;
        rig.World.RunTick(10);
        Assert.Single(rig.ObjectSpells.Casts);
    }

    [Fact]
    public void Talk_IsSpokenByTheFirstNonPetCreatureTarget()
    {
        // CREATURE_RANGE (9) over every creature within 50 yards of the object, nearest first: a pet at 2, the speaker at 5, the wolf at about 11.
        using SmartRig rig = StartOne(
            [ObjectRow((int)Entry, 0, SmartEvent.GossipHello, SmartAction.Talk, SmartTarget.CreatureRange, a1: 9001, t1: 0, t2: 0, t3: 50)],
            objectY: 10, creatures: [Spawn(2, SmartRig.Grark, 0, 12), Spawn(3, SmartRig.Speaker, 0, 15)]);
        Creature pet = rig.Creatures.Creatures.Single(c => c.Entry == SmartRig.Grark);
        Creature speaker = rig.Creatures.Creatures.Single(c => c.Entry == SmartRig.Speaker);
        pet.Summon = new SummonLinks(SummonKind.Pet, rig.Player.Guid, 0, TotemSlots.None, 0);
        GameObject go = rig.ObjectBySpawn(1);

        rig.Session.Clear();
        rig.Objects!.UseByUnit(rig.Player, go);
        MonsterChat say = ParseMonsterChat(Assert.Single(CreatureAiTestSupport.Packets(rig.Session, WorldOpcode.SmsgMessagechat)));
        Assert.Equal(speaker.Template.Name, say.Name);                 // not the pet, which is nearer
        Assert.NotEqual(pet.Template.Name, say.Name);
        Assert.Contains(rig.Player.Name, say.Message, StringComparison.Ordinal); // the text target is the last invoker, the player who used it
    }

    [Fact]
    public void Talk_WithAPlayerTargetOrNoCreatureInRange_IsSpokenByNobody()
    {
        using SmartRig rig = StartOne(
        [
            ObjectRow((int)Entry, 0, SmartEvent.GossipHello, SmartAction.Talk, SmartTarget.ClosestPlayer, a1: 9001),
            ObjectRow((int)Entry, 1, SmartEvent.GossipHello, SmartAction.Talk, SmartTarget.ClosestCreature, a1: 9001, t1: SmartRig.Speaker), // none spawned
            ObjectRow((int)Entry, 2, SmartEvent.GossipHello, SmartAction.Talk, SmartTarget.Self, a1: 9001),
        ]);
        rig.Session.Clear();
        rig.Objects!.UseByUnit(rig.Player, rig.ObjectBySpawn(1));
        Assert.Empty(CreatureAiTestSupport.Packets(rig.Session, WorldOpcode.SmsgMessagechat));
    }

    [Fact]
    public void SpawnRows_ReplaceEntryRows()
    {
        using SmartRig rig = Start(
        [
            ObjectRow((int)Entry, 0, SmartEvent.Update, SmartAction.Cast, SmartTarget.ClosestPlayer, a1: 1),
            ObjectRow(-2, 0, SmartEvent.Update, SmartAction.Cast, SmartTarget.ClosestPlayer, a1: 2),
        ], [Go(1, Entry, 0, 10), Go(2, Entry, 0, 12)]);
        GameObject first = rig.ObjectBySpawn(1);
        GameObject second = rig.ObjectBySpawn(2);

        rig.World.RunTick(10);
        Assert.Equal(1u, Assert.Single(rig.ObjectSpells.Casts, c => ReferenceEquals(c.Source, first)).Spell);   // the entry's row
        Assert.Equal(2u, Assert.Single(rig.ObjectSpells.Casts, c => ReferenceEquals(c.Source, second)).Spell);  // its own row, not both
        Assert.Equal(2, rig.ObjectSpells.Casts.Count);
    }

    [Fact]
    public void TimedActionList_OnTheObjectItself()
    {
        using SmartRig rig = StartOne(
        [
            ObjectRow((int)Entry, 0, SmartEvent.GossipHello, SmartAction.CallTimedActionList, SmartTarget.Self, a1: 60, a2: 2),
            ListRow(60, 0, SmartAction.Cast, SmartTarget.ActionInvoker, delay: 200, a1: 30),
        ]);
        GameObject go = rig.ObjectBySpawn(1);
        SmartScript script = rig.ObjectScript(go);
        Assert.Empty(script.TimedActionList);

        Assert.Equal(GameObjectUseResult.Ok, rig.Objects!.Use(rig.Player, go.Guid));
        SmartHolder row = Assert.Single(script.TimedActionList);
        Assert.Equal(SmartEvent.Update, row.Event);   // timer type 2: always
        Assert.Same(rig.Player, script.LastInvoker);
        Assert.Empty(rig.ObjectSpells.Casts);

        rig.World.RunTick(150);
        Assert.Empty(rig.ObjectSpells.Casts);
        rig.World.RunTick(60);                        // 210 ms after the call
        Assert.Equal([(go, 30u, (Unit)rig.Player)], Casts(rig)); // the list ran on the object, with the player who used it as invoker
        rig.World.RunTick(10);
        Assert.Empty(script.TimedActionList);
    }

    [Theory]
    [InlineData(500f, 317u, true)]    // 317: the player (target) dead or beyond 60 yards of the object (source): 500 yards off holds
    [InlineData(10f, 317u, false)]    // 10 yards off does not
    [InlineData(500f, 1317u, false)]  // -3 NOT over 317
    [InlineData(10f, 1317u, true)]
    public void ConditionedGossipHello_UsesThePlayerAsTargetAndTheObjectAsSource(float objectX, uint conditionId, bool runs)
    {
        using SmartRig rig = StartOne(
            [ObjectRow((int)Entry, 0, SmartEvent.GossipHello, SmartAction.Cast, SmartTarget.ActionInvoker, a1: 1, condition: conditionId)],
            conditions: true, objectX: objectX, objectY: 0);
        GameObject go = rig.ObjectBySpawn(1);

        rig.Objects!.UseByUnit(rig.Player, go);
        if (runs)
        {
            Assert.Equal([(go, 1u, (Unit)rig.Player)], Casts(rig));
        }
        else
        {
            Assert.Empty(rig.ObjectSpells.Casts);
        }
    }

    [Fact]
    public void ConditionedGossipHello_WithoutAnEvaluatorOrWithAMissingCondition_FailsClosed()
    {
        using SmartRig noEvaluator = StartOne(
            [ObjectRow((int)Entry, 0, SmartEvent.GossipHello, SmartAction.Cast, SmartTarget.ActionInvoker, a1: 1, condition: 1317)], conditions: false);
        noEvaluator.Objects!.UseByUnit(noEvaluator.Player, noEvaluator.ObjectBySpawn(1));
        Assert.Empty(noEvaluator.ObjectSpells.Casts);

        using SmartRig missing = StartOne(
            [ObjectRow((int)Entry, 0, SmartEvent.GossipHello, SmartAction.Cast, SmartTarget.ActionInvoker, a1: 1, condition: 999)], conditions: true);
        missing.Objects!.UseByUnit(missing.Player, missing.ObjectBySpawn(1));
        Assert.Empty(missing.ObjectSpells.Casts);
    }
}
