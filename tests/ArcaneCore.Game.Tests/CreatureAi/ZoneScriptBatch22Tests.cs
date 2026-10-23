using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

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
        public List<uint> Killed { get; } = [];
        public void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source) => Killed.Add(creatureEntry);
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
    public void RestlessSoul_TaggedByEgansBlaster_GivesCredit_DiesAfterFiveSeconds_AndLeavesAFreedSoul()
    {
        CreatureContent content = new(
            [Template(RestlessSoulAI.Entry, b => { b.Name = "Restless Soul"; b.Faction = 21; }), Template(RestlessSoulAI.NpcFreedSoul, b => { b.Name = "Freed Soul"; b.Faction = 35; })],
            [Spawn(1, RestlessSoulAI.Entry, 0f, 0f, 0f)], [], [], []);
        var quests = new Quests();
        (WorldRuntime world, Map _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { ScriptQuests = quests });
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 2f, 0f);
            Creature soul = Assert.Single(system.Creatures);
            var ai = Assert.IsType<RestlessSoulAI>(soul.AI);
            ai.OnSpellHit(player, new SpellInfo { Id = RestlessSoulAI.SpellEganBlaster });
            Assert.False(ai.Tagged); // not on the quest

            quests.Incomplete.Add(RestlessSoulAI.QuestRestlessSoul);
            ai.OnSpellHit(player, new SpellInfo { Id = RestlessSoulAI.SpellEganBlaster });
            Assert.True(ai.Tagged);
            Run(world, 4000);
            Assert.True(soul.IsAlive);
            Run(world, 1500);
            Assert.False(soul.IsAlive);
            Assert.Equal([RestlessSoulAI.Entry], quests.Killed);
            Assert.NotNull(ai.FreedSoul);
            Assert.Equal(RestlessSoulAI.NpcFreedSoul, ai.FreedSoul!.Entry);
        }
    }

    [Fact]
    public void MelodiousRaptureScriptIsFoundByItsSpellId()
    {
        SpellScriptRegistry registry = SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly);
        Assert.IsType<MelodiousRaptureScript>(registry.Find(MelodiousRaptureScript.SpellMelodiousRapture));
    }
}