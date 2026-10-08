using System.Reflection;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.BlackrockDepths;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// The dungeon objects as classic-db z2815 has them: locked (Lock.dbc rows copied from client 5875) and opened by the open-lock spell
/// (Spell::EffectOpenLock → SendLoot → GameObject::Use), which is how the vanilla client opens them; and the encounter paths that need more
/// than one object or one tick.
/// </summary>
public sealed partial class DungeonScriptExpansionTests
{
    // Lock.dbc 5875: 93 and 99 are a single skill case (type 2) of lock type 12 (spell 6477 "Opening") and 14 (8386 "Attacking"); 639
    // and 799 a single key case (type 1): items 11078 (Relic Coffer Key) and 11885.
    private static readonly LockEntry OpeningLock = SkillLock(93, 12);
    private static readonly LockEntry AttackingLock = SkillLock(99, (uint)LockType.OpenAttacking);
    private static readonly LockEntry CofferLock = KeyLock(639, 11078);
    private static readonly LockEntry BrazierLock = KeyLock(799, 11885);

    private static LockEntry SkillLock(uint id, uint lockType)
        => new(id, [0, 2, 0, 0, 0, 0, 0, 0], [0, lockType, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0]);

    private static LockEntry KeyLock(uint id, uint item)
        => new(id, [1, 0, 0, 0, 0, 0, 0, 0], [item, 0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 0, 0]);

    [Fact]
    public void SunkenTemple_StatuesOpenedWithTheOpeningSpell_RunTheStatueEvent()
    {
        uint[] entries = [148830, 148831, 148832, 148833, 148834, 148835];
        uint[] eventIds = [3094, 3095, 3097, 3098, 3099, 3100];
        GameObjectTemplate[] templates = [.. entries.Select((entry, i) => GameObjectTestKit.GoTemplate(entry, GameObjectType.Goober, (0, 93), (2, eventIds[i])))];
        GameObjectSpawn[] objects = [.. entries.Select((entry, i) => ObjectAt(200 + (uint)i, entry, 2))];
        var (fixture, player, map) = Enter(m => new SunkenTempleInstance(m), [], templates, objects, locks: [OpeningLock]);
        using (fixture)
        {
            GameObjectMapSystem gos = map.FindUpdater<GameObjectMapSystem>()!;
            var script = (SunkenTempleInstance)InstanceManager.InstanceDataOf(map)!;
            for (int i = 0; i < entries.Length; i++)
            {
                GameObject statue = gos.GameObjects.Single(g => g.Entry == entries[i]);
                Assert.Equal(GameObjectUseResult.Ok, gos.OpenLock(player, statue.Guid, (LockType)12));
                Assert.Equal(i + 1, script.StatuesActivated);
            }

            Assert.Equal(EncounterState.Special, script.GetData(SunkenTempleInstance.TypeAtalarion));
        }
    }

    [Fact]
    public void BlackrockDepths_MugsTakenWithTheOpeningSpell_ProvokePlugger()
    {
        const uint mug = 165738, boar = 165739;
        var (fixture, player, map) = Enter(m => new BlackrockDepthsInstance(m), [At(1, BlackrockDepthsInstance.NpcPlugger, 3)],
            [GameObjectTestKit.GoTemplate(mug, GameObjectType.Chest, (0, 93)), GameObjectTestKit.GoTemplate(boar, GameObjectType.Chest, (0, 93))],
            [ObjectAt(200, mug, 2), ObjectAt(201, mug, 2), ObjectAt(202, boar, 2)], locks: [OpeningLock]);
        using (fixture)
        {
            GameObjectMapSystem gos = map.FindUpdater<GameObjectMapSystem>()!;
            var script = (BlackrockDepthsInstance)InstanceManager.InstanceDataOf(map)!;
            foreach (GameObject go in gos.GameObjects.ToArray())
            {
                gos.OpenLock(player, go.Guid, (LockType)12); // the chest's loot needs the loot area; GOUse_go_bar_ale_mug runs before it
            }

            Assert.Equal(3, script.StolenAles);
            Assert.Equal(EncounterState.InProgress, script.GetData(BlackrockDepthsInstance.TypePlugger));
        }
    }

    [Fact]
    public void BlackrockDepths_KegsBrokenWithTheAttackingSpell_BringHurley()
    {
        var (fixture, player, map) = Enter(m => new BlackrockDepthsInstance(m), [At(1, BlackrockDepthsInstance.NpcPlugger, 3)],
            [GameObjectTestKit.GoTemplate(BlackrockDepthsInstance.GoBeerKeg, GameObjectType.Goober, (0, 99))],
            [ObjectAt(200, BlackrockDepthsInstance.GoBeerKeg, 2), ObjectAt(201, BlackrockDepthsInstance.GoBeerKeg, 2),
             ObjectAt(202, BlackrockDepthsInstance.GoBeerKeg, 2)],
            [9537, 9541], [AttackingLock]);
        using (fixture)
        {
            GameObjectMapSystem gos = map.FindUpdater<GameObjectMapSystem>()!;
            var script = (BlackrockDepthsInstance)InstanceManager.InstanceDataOf(map)!;
            foreach (GameObject keg in gos.GameObjects.ToArray())
            {
                Assert.Equal(GameObjectUseResult.Ok, gos.OpenLock(player, keg.Guid, LockType.OpenAttacking));
            }

            Assert.Equal(3, script.BrokenKegs);
            Assert.Equal(EncounterState.InProgress, script.GetData(BlackrockDepthsInstance.TypeHurley));
            Assert.Single(map.FindUpdater<CreatureMapSystem>()!.Creatures, c => c.Template.Entry == 9537);
        }
    }

    [Fact]
    public void BlackrockDepths_EveryRelicCofferDoorCounts_AndTheTwelfthWakesTheVault()
    {
        uint[] doors = BlackrockDepthsInstance.RelicCofferDoors;
        Assert.Equal(12, doors.Distinct().Count());
        var (fixture, player, map) = Enter(m => new BlackrockDepthsInstance(m), [],
            [.. doors.Select(d => GameObjectTestKit.GoTemplate(d, GameObjectType.Door, (1, 639)))],
            [.. doors.Select((d, i) => ObjectAt(300 + (uint)i, d, 2))], locks: [CofferLock]);
        using (fixture)
        {
            GameObjectMapSystem gos = map.FindUpdater<GameObjectMapSystem>()!;
            var script = (BlackrockDepthsInstance)InstanceManager.InstanceDataOf(map)!;
            for (int i = 0; i < doors.Length; i++)
            {
                GameObject door = gos.GameObjects.Single(g => g.Entry == doors[i]);
                Assert.Equal(GameObjectUseResult.Ok, gos.OpenLock(player, door.Guid, LockType.Open, keyItemId: 11078));
                Assert.Equal(i + 1, script.CofferDoorsOpened);
            }

            Assert.Equal(EncounterState.InProgress, script.GetData(BlackrockDepthsInstance.TypeVault));
        }
    }

    [Fact]
    public void BlackrockDepths_LightingBothShadowforgeBraziers_OpensTheGolemRoom()
    {
        uint[] braziers = BlackrockDepthsInstance.ShadowforgeBraziers;
        var (fixture, player, map) = Enter(m => new BlackrockDepthsInstance(m), [],
            [.. braziers.Select(b => GameObjectTestKit.GoTemplate(b, GameObjectType.Button, (1, 799))),
             GameObjectTestKit.GoTemplate(BlackrockDepthsInstance.GoGolemRoomNorth, GameObjectType.Door),
             GameObjectTestKit.GoTemplate(BlackrockDepthsInstance.GoGolemRoomSouth, GameObjectType.Door)],
            [ObjectAt(200, braziers[0], 2), ObjectAt(201, braziers[1], 2),
             ObjectAt(202, BlackrockDepthsInstance.GoGolemRoomNorth, 4), ObjectAt(203, BlackrockDepthsInstance.GoGolemRoomSouth, 4)],
            locks: [BrazierLock]);
        using (fixture)
        {
            GameObjectMapSystem gos = map.FindUpdater<GameObjectMapSystem>()!;
            var script = (BlackrockDepthsInstance)InstanceManager.InstanceDataOf(map)!;
            Assert.Equal(GameObjectUseResult.Ok, gos.OpenLock(player, gos.GameObjects.Single(g => g.Entry == braziers[0]).Guid, LockType.Open, 11885));
            Assert.Equal(EncounterState.InProgress, script.GetData(BlackrockDepthsInstance.TypeLyceum));
            Assert.Equal(GameObjectUseResult.Ok, gos.OpenLock(player, gos.GameObjects.Single(g => g.Entry == braziers[1]).Guid, LockType.Open, 11885));
            Assert.Equal(EncounterState.Done, script.GetData(BlackrockDepthsInstance.TypeLyceum));
            Assert.Equal(GameObjectState.Active, gos.GameObjects.Single(g => g.Entry == BlackrockDepthsInstance.GoGolemRoomNorth).State);
            Assert.Equal(GameObjectState.Active, gos.GameObjects.Single(g => g.Entry == BlackrockDepthsInstance.GoGolemRoomSouth).State);
        }
    }

    [Fact]
    public void Maraudon_AttackingTheLarvaSpewer_StopsTheLarvae()
    {
        var (fixture, player, map) = Enter(m => new MaraudonInstance(m), [],
            [GameObjectTestKit.GoTemplate(MaraudonInstance.GoLarvaSpewer, GameObjectType.Trap, (0, 99))],
            [ObjectAt(200, MaraudonInstance.GoLarvaSpewer, 2)], locks: [AttackingLock]);
        using (fixture)
        {
            GameObjectMapSystem gos = map.FindUpdater<GameObjectMapSystem>()!;
            var script = (MaraudonInstance)InstanceManager.InstanceDataOf(map)!;
            GameObject spewer = gos.GameObjects.Single();
            Assert.NotEqual(0u, script.LarvaSpewRemainingMs);

            Assert.Equal(GameObjectUseResult.Ok, gos.OpenLock(player, spewer.Guid, LockType.OpenAttacking));
            Assert.Equal(GameObjectLootState.Activated, spewer.LootState);
            fixture.Tick();

            Assert.Equal(EncounterState.Special, script.GetData(MaraudonInstance.TypeNoxxion));
            Assert.Equal(0u, script.LarvaSpewRemainingMs);
        }
    }

    [Fact]
    public void BlackrockDepths_RingOfLaw_SecondWaveFollowsAFirstWaveThatDiedBeforeItsSixteenSeconds()
    {
        uint[] ringMobs = [8925, 8926, 8927, 8928, 8933, 8932];
        var (fixture, player, map) = Enter(m => new BlackrockDepthsInstance(m), [At(1, BlackrockDepthsInstance.NpcPlugger, 3)],
            extraCreatureEntries: [10096, .. ringMobs]);
        using (fixture)
        {
            var script = (BlackrockDepthsInstance)InstanceManager.InstanceDataOf(map)!;
            CreatureMapSystem creatures = map.FindUpdater<CreatureMapSystem>()!;
            Assert.True(script.EnterRingOfLaw(player, player.X, player.Y, player.Z));
            Creature grimstone = creatures.Creatures.Single(c => c.Template.Entry == 10096);
            GrimstoneAI ai = Assert.IsType<GrimstoneAI>(grimstone.AI);
            MethodInfo reached = typeof(GrimstoneAI).GetMethod("WaypointReached", BindingFlags.Instance | BindingFlags.NonPublic)!;
            int Wave() => creatures.Creatures.Count(c => c.IsAlive && Array.IndexOf(ringMobs, c.Template.Entry) >= 0);

            void Run(int ms)
            {
                for (int t = 0; t < ms; t += 100)
                {
                    ai.OnUpdate(100);          // world ticks: an elapsed timer is at most one tick past
                }
            }

            Run(1000);                         // phase 0: the escort starts
            reached.Invoke(ai, [1u]);          // middle reached: 5 s
            Run(5000);                         // phase 1
            reached.Invoke(ai, [2u]);          // the wall: 5 s
            Run(5000);                         // phase 2: 2 s
            Run(2000);                         // phase 3: the east gate, 3 s
            Run(3000);                         // phase 4: 2.5 s
            Run(2500);                         // phase 5: the first wave, 16 s
            int first = Wave();
            Assert.True(first > 0);

            foreach (Creature mob in creatures.Creatures.Where(c => c.IsAlive && Array.IndexOf(ringMobs, c.Template.Entry) >= 0).ToArray())
            {
                map.Combat.Kill(player, mob);  // all dead well before 16 s: SummonedCreatureJustDied sets 5 s
            }

            Assert.Equal(0, Wave());
            Run(5000);                         // phase 6 (no case in npc_grimstoneAI): the elapsed timer is kept...
            Run(200);                          // ...so phase 7, the second wave, follows on the next tick
            Assert.Equal(first, Wave());
            Assert.Equal(EncounterState.InProgress, script.GetData(BlackrockDepthsInstance.TypeRingOfLaw));
        }
    }

    [Fact]
    public void BlackrockDepths_DoomrelsChallengeStartsTheTomb_AndAWipeGivesTheDwarfItsFactionBack()
    {
        const uint Anger = 9035;
        var (fixture, player, map) = Enter(m => new BlackrockDepthsInstance(m), [At(1, Anger, 3), At(2, DoomrelGossip.NpcDoomrel, 4)]);
        using (fixture)
        {
            var script = (BlackrockDepthsInstance)InstanceManager.InstanceDataOf(map)!;
            CreatureMapSystem creatures = map.FindUpdater<CreatureMapSystem>()!;
            Creature anger = creatures.Creatures.Single(c => c.Template.Entry == Anger);
            Creature doomrel = creatures.Creatures.Single(c => c.Template.Entry == DoomrelGossip.NpcDoomrel);
            uint templateFaction = anger.Template.Faction;
            Assert.NotEqual(754u, templateFaction);
            var gossip = new DoomrelGossip();
            NpcInfo info = new(doomrel.Guid, doomrel.Template.Entry, 2, NpcFlags.Gossip, map.MapId, doomrel.X, doomrel.Y, doomrel.Z, 0,
                true, false, false, false, 0);

            ScriptedGossipMenu menu = gossip.Hello(player, info)!;
            Assert.Equal(DoomrelGossip.NpcTextChallenge, menu.NpcTextId);
            ScriptedGossipItem line = Assert.Single(menu.Items);
            ScriptedGossipReply reply = gossip.SelectReply(player, info, line.Sender, line.Action);

            Assert.True(reply.Close);
            Assert.Equal(EncounterState.InProgress, script.GetData(BlackrockDepthsInstance.TypeTombOfSeven));
            Assert.Equal(754u, anger.FactionTemplate); // DoCallNextDwarf: the first dwarf turns on the party
            Assert.Empty(gossip.Hello(player, info)!.Items);

            creatures.EnterEvadeMode(anger);           // the wipe: the dwarf evades (FAIL) and walks home
            for (int i = 0; i < 100 && anger.IsEvading; i++)
            {
                fixture.Tick(100);
            }

            Assert.False(anger.IsEvading);
            Assert.Equal(templateFaction, anger.FactionTemplate); // TEMPFACTION_RESTORE_REACH_HOME
            Assert.Equal(EncounterState.Fail, script.GetData(BlackrockDepthsInstance.TypeTombOfSeven));
            Assert.Single(gossip.Hello(player, info)!.Items); // the challenge is offered again
        }
    }

    [Fact]
    public void GossipScriptChain_SendsAChoiceBackToTheScriptWhoseMenuWasShown()
    {
        var first = new FakeGossip(10, "first");
        var second = new FakeGossip(20, "second");
        var services = NpcGossipScriptChain.Of(first, second);
        using var fixture = new InstanceFixture();
        Player player = fixture.AddPlayer(1);
        NpcInfo Npc(uint entry) => new(new ObjectGuid(entry), entry, 0, NpcFlags.Gossip, 0, 0, 0, 0, 0, true, false, false, false, 0);

        Assert.Equal("second", services.Hello(player, Npc(20))!.Items.Single().Text);
        Assert.Equal(20u, services.SelectReply(player, Npc(20), 1, 7).NpcTextId);
        Assert.Equal("first", services.Hello(player, Npc(10))!.Items.Single().Text);
        Assert.Equal(10u, services.SelectReply(player, Npc(10), 1, 7).NpcTextId);
        Assert.Null(services.Hello(player, Npc(30)));
        Assert.Equal(0u, services.SelectReply(player, Npc(30), 1, 7).NpcTextId);
    }

    private sealed class FakeGossip(uint entry, string text) : INpcGossipScript
    {
        public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
            => npc.Entry == entry ? new ScriptedGossipMenu(false, 0, [new ScriptedGossipItem(0, text, 1, 7)]) : null;

        public uint Select(Player player, NpcInfo npc, uint sender, uint action) => npc.Entry == entry ? entry : 99;
    }
}
