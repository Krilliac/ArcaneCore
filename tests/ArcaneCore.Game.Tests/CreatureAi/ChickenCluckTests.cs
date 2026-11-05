using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>The Chicken (entry 620, npc_chicken_cluck): a /chicken has a 1 in 50 chance to start the Cluck! offer, a /cheer from a player with the quest complete always does, 20 s later it resets.</summary>
public sealed class ChickenCluckTests
{
    private sealed class CompleteQuests : IScriptQuestEvents
    {
        public bool Complete { get; set; }

        public bool IsQuestComplete(Player player, uint questId) => Complete && questId == ChickenCluckAI.QuestCluck;

        public void AreaExploredOrEventHappens(Player player, uint questId)
        {
        }

        public void FailQuest(Player player, uint questId)
        {
        }

        public void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source)
        {
        }

        public void GroupEventFailHappens(Player player, uint questId)
        {
        }

        public IReadOnlyList<Player> GroupMembersOf(Player player) => [];
    }

    private static (WorldRuntime World, Creature Chicken, Player Player, ChickenCluckAI Ai) Rig(CompleteQuests quests)
    {
        CreatureContent content = new([Template(ChickenCluckAI.Entry)], [Spawn(1, ChickenCluckAI.Entry, 0, 0)], [], [], [], new CreatureAiContent([], []));
        (WorldRuntime world, Map _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { ScriptQuests = quests });
        (Player player, FakeSession _) = AddPlayer(world, 1, 2, 0);
        Creature chicken = Assert.Single(system.Creatures);
        world.RunTick(100);
        return (world, chicken, player, Assert.IsType<ChickenCluckAI>(chicken.AI));
    }

    [Fact]
    public void ItIsAPlainCritter_BeforeAnyEmote()
    {
        (WorldRuntime world, Creature chicken, Player _, ChickenCluckAI ai) = Rig(new CompleteQuests());
        using (world)
        {
            Assert.False(ai.Offering);
            Assert.Equal(ChickenCluckAI.FactionChicken, chicken.FactionTemplate);
        }
    }

    [Fact]
    public void ACheer_WithoutTheQuestComplete_DoesNothing()
    {
        (WorldRuntime world, Creature chicken, Player player, ChickenCluckAI ai) = Rig(new CompleteQuests { Complete = false });
        using (world)
        {
            ai.OnReceiveEmote(player, ChickenCluckAI.TextEmoteCheer);
            Assert.False(ai.Offering);
            Assert.Equal(ChickenCluckAI.FactionChicken, chicken.FactionTemplate);
        }
    }

    [Fact]
    public void ACheer_WithTheQuestComplete_OffersAtOnce_AndTheOfferResetsAfterTwentySeconds()
    {
        (WorldRuntime world, Creature chicken, Player player, ChickenCluckAI ai) = Rig(new CompleteQuests { Complete = true });
        using (world)
        {
            ai.OnReceiveEmote(player, ChickenCluckAI.TextEmoteCheer);
            Assert.True(ai.Offering);
            Assert.Equal(ChickenCluckAI.FactionFriendly, chicken.FactionTemplate);

            Run(world, 19_000);
            Assert.True(ai.Offering);

            Run(world, 2_000);
            Assert.False(ai.Offering);
            Assert.Equal(ChickenCluckAI.FactionChicken, chicken.FactionTemplate);
        }
    }

    [Fact]
    public void AChickenEmote_OffersOnlyOnceTheRandomDrawWins()
    {
        (WorldRuntime world, Creature chicken, Player player, ChickenCluckAI ai) = Rig(new CompleteQuests());
        using (world)
        {
            int emotes = 0;
            while (!ai.Offering && emotes < 5000)
            {
                ai.OnReceiveEmote(player, ChickenCluckAI.TextEmoteChicken);
                emotes++;
            }

            Assert.True(ai.Offering, "5000 /chicken emotes never won the random draw");
            Assert.Equal(ChickenCluckAI.FactionFriendly, chicken.FactionTemplate);
        }
    }

    [Fact]
    public void ACritterTypeChicken_KeepsTheCritterBehaviour_ItFleesFromAHitAndDoesNotRetaliate()
    {
        CreatureContent content = new([Template(ChickenCluckAI.Entry) with { CreatureType = 8 }], [Spawn(1, ChickenCluckAI.Entry, 0, 0)], [], [], [], new CreatureAiContent([], []));
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { ScriptQuests = new CompleteQuests() });
        using (world)
        {
            (Player player, FakeSession _) = AddPlayer(world, 1, 2, 0);
            Creature chicken = Assert.Single(system.Creatures);
            Assert.IsAssignableFrom<CritterAI>(Assert.IsType<ChickenCluckAI>(chicken.AI));
            world.RunTick(100);

            map.Combat.DealDamage(player, chicken, 1, direct: false);

            Assert.Equal(MovementGeneratorType.Fleeing, chicken.Motion.CurrentType);
            Assert.Null(chicken.Combat.Victim);
            Assert.False(chicken.AI!.AttackStart(player));
        }
    }

    [Fact]
    public void AnEmoteOtherThanChickenOrCheer_IsIgnored()
    {
        (WorldRuntime world, Creature _, Player player, ChickenCluckAI ai) = Rig(new CompleteQuests { Complete = true });
        using (world)
        {
            for (int i = 0; i < 200; i++)
            {
                ai.OnReceiveEmote(player, 7);
            }

            Assert.False(ai.Offering);
        }
    }
}
