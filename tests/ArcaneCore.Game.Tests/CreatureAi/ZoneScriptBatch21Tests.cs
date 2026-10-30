using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts.World;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>Zone scripts batch 21: Stranglethorn Fever (vmangos npc_witch_doctor_unbagwa) and Short John Mithril's Gurubashi Arena run.</summary>
public sealed class ZoneScriptBatch21Tests
{
    private static List<Creature> Apes(CreatureMapSystem system) =>
        [.. system.Creatures.Where(c => c.IsAlive && c.Entry is WitchDoctorUnbagwaAI.NpcEnragedSilverbackGorilla or WitchDoctorUnbagwaAI.NpcKonda or WitchDoctorUnbagwaAI.NpcMokkTheSavage)];

    [Fact]
    public void StranglethornFever_ThreeWaves_ThenUnbagwaResets()
    {
        CreatureContent content = new(
            [
                Template(WitchDoctorUnbagwaAI.Entry, b => { b.Name = "Witch Doctor Unbagwa"; b.Faction = 35; b.NpcFlags = (uint)NpcFlags.QuestGiver; }),
                Template(WitchDoctorUnbagwaAI.NpcEnragedSilverbackGorilla, b => b.Faction = 16),
                Template(WitchDoctorUnbagwaAI.NpcKonda, b => b.Faction = 16),
                Template(WitchDoctorUnbagwaAI.NpcMokkTheSavage, b => b.Faction = 16),
            ],
            [Spawn(1, WitchDoctorUnbagwaAI.Entry, -13770f, -5f, 41.5f)], [], [], []);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, -13771f, -5f);
            Creature unbagwa = Assert.Single(system.Creatures);
            var ai = Assert.IsType<WitchDoctorUnbagwaAI>(unbagwa.AI);

            ai.OnQuestRewarded(player, 1);
            Assert.False(ai.EventStarted);
            ai.OnQuestRewarded(player, WitchDoctorUnbagwaAI.QuestStranglethornFever);
            Assert.True(ai.EventStarted);
            Assert.Equal(0u, unbagwa.NpcFlags & (uint)NpcFlags.QuestGiver);
            Assert.Equal(WitchDoctorUnbagwaAI.FactionEscortNeutralPassive, unbagwa.FactionTemplate);

            int[] expected = [3, 5, 6];
            uint[] boss = [0, WitchDoctorUnbagwaAI.NpcKonda, WitchDoctorUnbagwaAI.NpcMokkTheSavage];
            for (int wave = 0; wave < 3; wave++)
            {
                Run(world, 10_500);
                List<Creature> apes = Apes(system);
                Assert.Equal(expected[wave], apes.Count);
                if (boss[wave] != 0)
                {
                    Assert.Single(apes, a => a.Entry == boss[wave]);
                }

                foreach (Creature ape in apes)
                {
                    map.Combat.DealDamage(player, ape, ape.Health, direct: false);
                }

                Run(world, 200);
            }

            Assert.False(ai.EventStarted);
            Assert.NotEqual(0u, unbagwa.NpcFlags & (uint)NpcFlags.QuestGiver);
            Assert.NotEqual(WitchDoctorUnbagwaAI.FactionEscortNeutralPassive, unbagwa.FactionTemplate);
            Run(world, 20_000);
            Assert.Empty(Apes(system));
        }
    }

    [Fact]
    public void GurubashiArenaEvent_ShortJohnYells_RunsToTheFloor_LeavesTheChest_AndComesBack()
    {
        var aiContent = new CreatureAiContent([], [], new BroadcastTextCatalog(
        [
            new BroadcastText((uint)ShortJohnMithrilAI.YellBootyRun, "Arrr, Me Hearties!", string.Empty, 0, 0, 0, [0, 0, 0], [0, 0, 0]),
            new BroadcastText((uint)ShortJohnMithrilAI.YellBloodletting, "Let the Bloodletting Begin!", string.Empty, 0, 0, 0, [0, 0, 0], [0, 0, 0]),
        ]));
        CreatureContent content = new(
            [Template(ShortJohnMithrilAI.Entry, b => { b.Name = "Short John Mithril"; b.Faction = 35; b.NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.QuestGiver); })],
            [Spawn(1, ShortJohnMithrilAI.Entry, -13184.5f, 332.671f, 40.33f)], [], [], [], aiContent);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, -13220f, 290f);
            Creature shortJohn = Assert.Single(system.Creatures);
            var ai = Assert.IsType<ShortJohnMithrilAI>(shortJohn.AI);
            var data = Assert.IsType<EasternKingdomsWorldMapData>(WorldMapData.Create(map));
            data.Initialize();
            map.AddUpdater(data);
            data.OnCreatureCreate(shortJohn);

            data.QueueGameEvent(1, true, false);
            data.QueueGameEvent(ShortJohnMithrilAI.GameEventGurubashiArena, false, false);
            Run(world, 200);
            Assert.Equal(ShortJohnMithrilAI.RunStage.Idle, ai.Stage);

            data.QueueGameEvent(ShortJohnMithrilAI.GameEventGurubashiArena, true, false);
            Run(world, 200);
            Assert.Equal(ShortJohnMithrilAI.RunStage.Announced, ai.Stage);
            Assert.Equal(0u, shortJohn.NpcFlags & (uint)(NpcFlags.Gossip | NpcFlags.QuestGiver));

            for (int i = 0; i < 3000 && ai.Stage != ShortJohnMithrilAI.RunStage.ToLedge; i++)
            {
                player.Relocate(shortJohn.X, shortJohn.Y, shortJohn.Z, 0, 0);
                Run(world, 100);
            }

            Assert.Equal(ShortJohnMithrilAI.RunStage.ToLedge, ai.Stage);
            // This rig has no game object system or templates, so the chest summon returns null here.
            Assert.True(map.FindUpdater<GameObjectMapSystem>() is null || ai.Chest?.Entry == ShortJohnMithrilAI.GoArenaTreasureChest);

            for (int i = 0; i < 3000 && ai.Stage != ShortJohnMithrilAI.RunStage.Idle; i++)
            {
                Run(world, 100);
            }

            Assert.Equal(ShortJohnMithrilAI.RunStage.Idle, ai.Stage);
            Assert.True(Distance2D(shortJohn, -13184.5f, 332.671f) < 2f);
            Assert.NotEqual(0u, shortJohn.NpcFlags & (uint)NpcFlags.QuestGiver);
        }
    }

    [Fact]
    public void GurubashiArenaEvent_AnEvadeOnTheWayToTheFloor_ResumesTheRunFromTheLedge()
    {
        CreatureContent content = new(
            [Template(ShortJohnMithrilAI.Entry, b => { b.Name = "Short John Mithril"; b.Faction = 35; b.NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.QuestGiver); })],
            [Spawn(1, ShortJohnMithrilAI.Entry, -13184.5f, 332.671f, 40.33f)], [], [], [], new CreatureAiContent([], []));
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, -13220f, 290f);
            Creature shortJohn = Assert.Single(system.Creatures);
            var ai = Assert.IsType<ShortJohnMithrilAI>(shortJohn.AI);

            ai.StartBootyRun();
            for (int i = 0; i < 3000 && (ai.Stage != ShortJohnMithrilAI.RunStage.ToFloor || Distance2D(shortJohn, -13184.5f, 332.671f) < 10f); i++)
            {
                player.Relocate(shortJohn.X, shortJohn.Y, shortJohn.Z, 0, 0);
                Run(world, 100);
            }

            Assert.Equal(ShortJohnMithrilAI.RunStage.ToFloor, ai.Stage);
            system.EnterEvadeMode(shortJohn); // a fight on the way broke the spline; the evade walks him back to his ledge spawn
            for (int i = 0; i < 6000 && ai.Stage != ShortJohnMithrilAI.RunStage.Idle; i++)
            {
                player.Relocate(shortJohn.X, shortJohn.Y, shortJohn.Z, 0, 0);
                Run(world, 100);
            }

            Assert.Equal(ShortJohnMithrilAI.RunStage.Idle, ai.Stage); // the run finished instead of sticking at ToFloor with the flags off
            Assert.NotEqual(0u, shortJohn.NpcFlags & (uint)NpcFlags.QuestGiver);
        }
    }
}
