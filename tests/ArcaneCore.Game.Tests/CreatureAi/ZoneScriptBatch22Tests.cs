using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>Zone scripts batch 22: npc_garments_of_quests and npc_redemption_target (npcs_special.cpp).</summary>
public sealed class ZoneScriptBatch22Tests
{
    private sealed class Quests : IScriptQuestEvents
    {
        public HashSet<uint> Incomplete { get; } = [];
        public List<uint> TalkedTo { get; } = [];
        public void AreaExploredOrEventHappens(Player player, uint questId) { }
        public void FailQuest(Player player, uint questId) { }
        public void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source) { }
        public void GroupEventFailHappens(Player player, uint questId) { }
        public IReadOnlyList<Player> GroupMembersOf(Player player) => [];
        public bool IsQuestIncomplete(Player player, uint questId) => Incomplete.Contains(questId);
        public void TalkedToCreature(Player player, uint creatureEntry, ObjectGuid source) => TalkedTo.Add(creatureEntry);
    }

    [Fact]
    public void GarmentsOfTheLight_HealThenFortitude_GivesTheCredit_OnlyOnTheQuest()
    {
        CreatureContent content = new([Template(GarmentsOfQuestsAI.EntryRoberts, b => { b.Name = "Guard Roberts"; b.Faction = 12; })],
            [Spawn(1, GarmentsOfQuestsAI.EntryRoberts, 0f, 0f, 0f)], [], [], []);
        var quests = new Quests();
        (WorldRuntime world, Map _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { ScriptQuests = quests });
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 2f, 0f);
            Creature roberts = Assert.Single(system.Creatures);
            var ai = Assert.IsType<GarmentsOfQuestsAI>(roberts.AI);
            Run(world, 200);
            Assert.Equal(StandState.Kneel, roberts.StandState);
            Assert.True(roberts.Health < roberts.MaxHealth);

            ai.OnSpellHit(player, new SpellInfo { Id = GarmentsOfQuestsAI.SpellLesserHealR2 });
            Assert.False(ai.IsHealed); // not on the quest

            quests.Incomplete.Add(5624);
            ai.OnSpellHit(player, new SpellInfo { Id = GarmentsOfQuestsAI.SpellFortitudeR1 });
            Assert.False(ai.IsHealed); // Fortitude first does nothing
            ai.OnSpellHit(player, new SpellInfo { Id = GarmentsOfQuestsAI.SpellLesserHealR2 });
            Assert.True(ai.IsHealed);
            Assert.Equal(StandState.Stand, roberts.StandState);
            Assert.Empty(quests.TalkedTo);
            ai.OnSpellHit(player, new SpellInfo { Id = GarmentsOfQuestsAI.SpellFortitudeR1 });
            Assert.True(ai.CanRun);
            Assert.Equal([GarmentsOfQuestsAI.EntryRoberts], quests.TalkedTo);
        }
    }

    [Fact]
    public void RedemptionTarget_SymbolOfLife_RevivesHim_ThenHeOffersQuests_AndLiesDownAfterTwoMinutes()
    {
        CreatureContent content = new([Template(RedemptionTargetAI.NpcHenzeFaulk, b => { b.Name = "Henze Faulk"; b.Faction = 12; b.NpcFlags = (uint)NpcFlags.QuestGiver; })],
            [Spawn(1, RedemptionTargetAI.NpcHenzeFaulk, 0f, 0f, 0f)], [], [], []);
        (WorldRuntime world, Map _, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 2f, 0f);
            Creature henze = Assert.Single(system.Creatures);
            var ai = Assert.IsType<RedemptionTargetAI>(henze.AI);
            ai.OnRespawn();
            Assert.Equal(StandState.Dead, henze.StandState);
            Assert.Equal(0u, henze.NpcFlags & (uint)NpcFlags.QuestGiver);

            ai.OnSpellHit(player, new SpellInfo { Id = 1 });
            Assert.False(ai.Revived);
            ai.OnSpellHit(player, new SpellInfo { Id = RedemptionTargetAI.SpellSymbolOfLife });
            Assert.True(ai.Revived);
            Assert.Equal(StandState.Stand, henze.StandState);
            Run(world, 4500);
            Assert.NotEqual(0u, henze.NpcFlags & (uint)NpcFlags.QuestGiver);

            Run(world, 120_000, step: 1000);
            Assert.False(ai.Revived);
            Assert.Equal(StandState.Dead, henze.StandState);
            Assert.Equal(0u, henze.NpcFlags & (uint)NpcFlags.QuestGiver);
        }
    }
}
