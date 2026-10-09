using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public void SendEventSpell_AnEventAlreadyRunning_IsNotReportedUnsupported_AMissingOneIs()
    {
        // StartDbScript is also false for an event already running for the same caster and target (RelayScriptRunner.IsRunning); cmangos
        // Map::ScriptsStart (Maps/Map.cpp:2181-2193) skips that as a success. Only an event without dbscripts_on_event rows is unsupported.
        const uint spellId = 970002;
        const uint missingSpellId = 970003;
        const uint eventId = 9044;
        const uint missingEventId = 9045;
        using var kit = new SpellTestKit(
            SpellTestKit.Spell(spellId, SpellTestKit.Effect(SpellEffectName.SendEvent, 1, misc: (int)eventId)),
            SpellTestKit.Spell(missingSpellId, SpellTestKit.Effect(SpellEffectName.SendEvent, 1, misc: (int)missingEventId)));
        RelayScriptStep delayed = new(eventId, 5000, 0, 8, 2044, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var ai = new CreatureAiContent([], []) { DbScripts = new DbScriptCatalog([(DbScriptKind.Event, delayed)]) };
        Map map = kit.World.GetMap(0);
        var creatures = new CreatureMapSystem(map, new CreatureContent([], [], [], [], [], ai),
            aiServices: new CreatureAiServices { ScriptQuests = new Quests() });
        map.AddUpdater(creatures);
        var logger = new CapturingLogger();
        var spells = new SpellSystem(kit.Store, () => kit.Now, spellbook: kit.Spellbook, random: new Random(1), logger: logger);
        (Player player, _) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(1, creatures.PendingDbScriptSteps);
        Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(1, creatures.PendingDbScriptSteps); // the second start was declined: the event is still running
        Assert.DoesNotContain(logger.Lines, l => l.Contains("is not implemented yet", StringComparison.Ordinal));

        Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(player, missingSpellId, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Contains(logger.Lines, l => l.Contains($"send event {missingEventId} is not implemented yet", StringComparison.Ordinal));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }
}
