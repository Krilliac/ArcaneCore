using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class SendEventDbScriptTests
{
    private sealed class Quests : IScriptQuestEvents
    {
        public List<uint> Credits { get; } = [];
        public void AreaExploredOrEventHappens(Player player, uint questId) { }
        public void FailQuest(Player player, uint questId) { }
        public void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source) => Credits.Add(creatureEntry);
        public void GroupEventFailHappens(Player player, uint questId) { }
        public IReadOnlyList<Player> GroupMembersOf(Player player) => [];
    }

    [Fact]
    public void SendEventSpell_StartsItsEventDbScript()
    {
        const uint spellId = 970001;
        const uint eventId = 9042;
        using var kit = new SpellTestKit(SpellTestKit.Spell(spellId,
            SpellTestKit.Effect(SpellEffectName.SendEvent, 1, misc: (int)eventId)));
        RelayScriptStep credit = new(eventId, 0, 0, 8, 2044, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var ai = new CreatureAiContent([], [])
        {
            DbScripts = new DbScriptCatalog([(DbScriptKind.Event, credit)]),
        };
        var quests = new Quests();
        Map map = kit.World.GetMap(0);
        var creatures = new CreatureMapSystem(map, new CreatureContent([], [], [], [], [], ai),
            aiServices: new CreatureAiServices { ScriptQuests = quests });
        map.AddUpdater(creatures);
        (Player player, _) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk,
            kit.System.CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal([2044u], quests.Credits);
    }
}
