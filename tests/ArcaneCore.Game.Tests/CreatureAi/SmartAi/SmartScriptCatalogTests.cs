using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.SmartAi.SmartRows;

namespace ArcaneCore.Game.Tests.CreatureAi.SmartAi;

/// <summary>
/// What <see cref="SmartScriptCatalog"/> accepts and why it refuses the rest (docs/integration/smartai-slice2-20261010.md). The AzerothCore rules cited are
/// SmartScriptMgr.cpp (the load order: identity, source type, event / action / target for the source, then the parameter checks) and SmartScriptMgr.h
/// (SMART_SCRIPT_TYPE_*, SmartAIEventMask). A refused row is listed in <see cref="SmartScriptCatalog.Rejected"/>, never silently dropped.
/// </summary>
public sealed class SmartScriptCatalogTests
{
    private const int Wolf = 299;

    public static TheoryData<string, SmartScriptRow[], string> RefusedRows() => new()
    {
        { "entryorguid 0", [Make(0, 0, 0, 4, 22, a1: 1)], "entryorguid 0" },
        { "source type 3", [Make(Wolf, 3, 0, 4, 22, a1: 1)], "source type 3 is not implemented" },
        { "source type 4", [Make(Wolf, 4, 0, 4, 22, a1: 1)], "source type 4 is not implemented" },
        { "source type 5", [Make(Wolf, 5, 0, 4, 22, a1: 1)], "source type 5 is not implemented" },
        { "source type 6", [Make(Wolf, 6, 0, 4, 22, a1: 1)], "source type 6 is not implemented" },
        { "source type 7", [Make(Wolf, 7, 0, 4, 22, a1: 1)], "source type 7 is not implemented" },
        { "source type 8", [Make(Wolf, 8, 0, 4, 22, a1: 1)], "source type 8 is not implemented" },
        { "source type 10", [Make(Wolf, 10, 0, 4, 22, a1: 1)], "source type 10 is invalid" },
        { "source type 255", [Make(Wolf, 255, 0, 4, 22, a1: 1)], "source type 255 is invalid" },
        { "negative guid, area trigger", [Make(-5, 2, 0, 46, 1, 7)], "negative entryorguid -5" },
        { "negative guid, timed action list", [Make(-5, 9, 0, 0, 22, a1: 1)], "negative entryorguid -5" },
        { "creature event AC forbids (area trigger 46)", [Make(Wolf, 0, 0, 46, 22, a1: 1)], "forbids event 46" },
        { "creature event AC forbids (quest 47)", [Make(Wolf, 0, 0, 47, 22, a1: 1)], "forbids event 47" },
        { "creature event with no hook (3)", [Make(Wolf, 0, 0, 3, 22, a1: 1)], "event 3 has no ArcaneCore hook" },
        { "GO event AC forbids (25 RESET)", [Make(Wolf, 1, 0, 25, 22, a1: 1)], "forbids event 25" },
        { "GO event AC forbids (0 UPDATE_IC)", [Make(Wolf, 1, 0, 0, 22, a1: 1)], "forbids event 0" },
        { "GO event valid but unhooked (8 SPELLHIT)", [Make(Wolf, 1, 0, 8, 22, a1: 1)], "event 8 has no ArcaneCore hook for source type 1" },
        { "GO event valid but unhooked (11 RESPAWN)", [Make(Wolf, 1, 0, 11, 22, a1: 1)], "event 11 has no ArcaneCore hook for source type 1" },
        { "GO action 12 SUMMON_CREATURE", [Make(Wolf, 1, 0, 60, 12, a1: 5)], "action 12 is not supported for source type 1" },
        { "GO action 69 MOVE_TO_POS", [Make(Wolf, 1, 0, 60, 69, a1: 5)], "action 69 is not supported for source type 1" },
        { "GO target 2 VICTIM", [Make(Wolf, 1, 0, 60, 22, 2, a1: 1)], "target 2 is not supported for source type 1" },
        { "area trigger action 11 CAST", [Make(100, 2, 0, 46, 11, 7, a1: 5)], "action 11 is not supported for source type 2" },
        { "area trigger target 1 SELF", [Make(100, 2, 0, 46, 1, 1, a1: 9001)], "target 1 is not supported for source type 2" },
        { "area trigger target 9 CREATURE_RANGE", [Make(100, 2, 0, 46, 1, 9, a1: 9001)], "range targets search around a base object" },
        { "area trigger event 4", [Make(100, 2, 0, 4, 1, 7, a1: 9001)], "forbids event 4 for source type 2" },
        { "creature action 41", [Make(Wolf, 0, 0, 4, 41)], "action 41 is not supported for source type 0" },
        { "creature target 13", [Make(Wolf, 0, 0, 4, 11, 13, a1: 5)], "target 13 is not supported for source type 0" },
        { "GOSSIP_HELLO filter 2", [Make(Wolf, 1, 0, 64, 22, e1: 2, a1: 1)], "GOSSIP_HELLO filter 2" },
        { "GOSSIP_HELLO filter 3", [Make(Wolf, 1, 0, 64, 22, e1: 3, a1: 1)], "GOSSIP_HELLO filter 3 is not 0 or 1" },
        { "80 with target 0", [Make(Wolf, 0, 0, 4, 80, 0, a1: 50), ListRow(50, 0, SmartAction.SetEventPhase, a1: 1)], "needs a target" },
        { "80 with allowOverride 2", [Make(Wolf, 0, 0, 4, 80, 1, a1: 50, a3: 2), ListRow(50, 0, SmartAction.SetEventPhase, a1: 1)], "allowOverride 2" },
        { "87 with every list 0", [Make(Wolf, 0, 0, 4, 87, 1)], "names no timed action list" },
        { "87 naming a list without rows", [Make(Wolf, 0, 0, 4, 87, 1, a1: 50, a2: 51), ListRow(50, 0, SmartAction.SetEventPhase, a1: 1)], "list 51" },
        { "88 with min above max", [Make(Wolf, 0, 0, 4, 88, 1, a1: 52, a2: 50), ListRow(50, 0, SmartAction.SetEventPhase, a1: 1)], "min above max" },
        { "88 over a range with no list", [Make(Wolf, 0, 0, 4, 88, 1, a1: 60, a2: 70), ListRow(50, 0, SmartAction.SetEventPhase, a1: 1)], "holds no usable timed action list" },
        { "80 calling a list with no rows", [Make(Wolf, 0, 0, 4, 80, 1, a1: 50)], "list 50, which has no usable rows" },
        { "a list row with a link", [Make(50, 9, 0, 0, 22, a1: 1, link: 1), Make(Wolf, 0, 0, 4, 80, 1, a1: 50)], "has a link" },
        { "a list row with delay min above max", [Make(50, 9, 0, 0, 22, e1: 5, e2: 1, a1: 1), Make(Wolf, 0, 0, 4, 80, 1, a1: 50)], "delay min 5 above max 1" },
        { "a list row with repeat min above max", [Make(50, 9, 0, 0, 22, e3: 5, e4: 1, a1: 1), Make(Wolf, 0, 0, 4, 80, 1, a1: 50)], "repeat min 5 above max 1" },
    };

    [Theory]
    [MemberData(nameof(RefusedRows))]
    public void Catalog_RejectsEveryRowItCannotRun_WithAReason(string rule, SmartScriptRow[] rows, string reasonFragment)
    {
        var catalog = new SmartScriptCatalog(rows);

        // The offending row is the first one given; the rows after it exist only to make it a fair case (the list it calls, and so on).
        SmartScriptRow offender = rows[0];
        SmartScriptRejection rejection = Assert.Single(catalog.Rejected, r => r.EntryOrGuid == offender.EntryOrGuid && r.SourceType == offender.SourceType && r.Id == offender.Id);
        Assert.Contains(reasonFragment, rejection.Reason, StringComparison.Ordinal);

        // A refused row reaches no index.
        Assert.Equal(0, catalog.Count);
        Assert.Equal(0, catalog.GameObjectRowCount);
        Assert.Equal(0, catalog.AreaTriggerRowCount);
        Assert.False(catalog.HasGameObjectRows);
        Assert.Empty(catalog.ForAreaTrigger(100));
        Assert.Empty(catalog.For(Wolf, 0));
        Assert.Empty(catalog.ForGameObject(Wolf, 0));
        Assert.True(rejection.Reason.Length > 0, $"{rule}: the rejection carries a reason");
    }

    [Fact]
    public void Catalog_AcceptsOneValidRowOfEachSource_WithNothingRejected()
    {
        var catalog = new SmartScriptCatalog(
        [
            CreatureRow(Wolf, 0, SmartEvent.Aggro, SmartAction.Cast, SmartTarget.ActionInvoker, a1: 5),
            ObjectRow(7, 0, SmartEvent.GossipHello, SmartAction.Cast, SmartTarget.ActionInvoker, e1: 1, a1: 5),
            ObjectRow(7, 1, SmartEvent.GossipHello, SmartAction.Cast, SmartTarget.ActionInvoker, e1: 0, a1: 5),
            TriggerRow(100, 0, SmartAction.Talk, SmartTarget.ClosestCreature, a1: 9001, t1: 301),
            ListRow(50, 0, SmartAction.Cast, SmartTarget.ActionInvoker, a1: 5),
            CreatureRow(Wolf, 1, SmartEvent.Evade, SmartAction.CallTimedActionList, SmartTarget.Self, a1: 50),
            CreatureRow(Wolf, 2, SmartEvent.Evade, SmartAction.CallRandomTimedActionList, SmartTarget.Self, a1: 50, a2: 0, a3: 0),
            CreatureRow(Wolf, 3, SmartEvent.Evade, SmartAction.CallRandomRangeTimedActionList, SmartTarget.Self, a1: 40, a2: 60),
        ]);
        Assert.Empty(catalog.Rejected);
        Assert.Equal(4, catalog.Count);
        Assert.Equal(2, catalog.GameObjectRowCount);
        Assert.Equal(1, catalog.AreaTriggerRowCount);
        Assert.Equal(1, catalog.TimedActionListRowCount);
    }

    [Fact]
    public void Catalog_NullArgumentsThrow_AndTheEmptyCatalogHasNothing()
    {
        Assert.Throws<ArgumentNullException>(() => new SmartScriptCatalog(null!));
        Assert.Throws<ArgumentNullException>(() => SmartScriptSupport.Check(null!, _ => true));
        Assert.Empty(SmartScriptCatalog.Empty.Rejected);
        Assert.False(SmartScriptCatalog.Empty.HasGameObjectRows);
        Assert.Empty(SmartScriptCatalog.Empty.ForGameObject(1, 1));
        Assert.Empty(SmartScriptCatalog.Empty.ForAreaTrigger(1));
        Assert.Empty(SmartScriptCatalog.Empty.TimedActionList(1));
        Assert.False(SmartScriptCatalog.Empty.HasTimedActionList(1));
    }

    [Fact]
    public void Catalog_IndexesGameObjectAreaTriggerAndTimedListRows_SpawnRowsReplaceEntryRows()
    {
        var catalog = new SmartScriptCatalog(
        [
            ObjectRow(5, 0, SmartEvent.Update, SmartAction.SetEventPhase, a1: 1),
            ObjectRow(5, 1, SmartEvent.Update, SmartAction.SetEventPhase, a1: 2),
            ObjectRow(-9, 0, SmartEvent.Update, SmartAction.SetEventPhase, a1: 3),
            TriggerRow(100, 0, SmartAction.Talk, SmartTarget.ActionInvoker, a1: 9001),
            TriggerRow(101, 0, SmartAction.Talk, SmartTarget.ActionInvoker, a1: 9001),
            TriggerRow(100, 1, SmartAction.Talk, SmartTarget.ActionInvoker, a1: 9002),
            ListRow(7, 2, SmartAction.SetEventPhase, a1: 2),
            ListRow(7, 1, SmartAction.SetEventPhase, a1: 1),
            CreatureRow(Wolf, 0, SmartEvent.Aggro, SmartAction.SetEventPhase, a1: 1),
            CreatureRow(-4, 0, SmartEvent.Aggro, SmartAction.SetEventPhase, a1: 2),
            CreatureRow(Wolf, 5, SmartEvent.Aggro, SmartAction.SetEventPhase, a1: 3) with { EventFlags = 0x080 }, // DEBUG_ONLY: skipped, as AzerothCore does
        ]);

        Assert.Empty(catalog.Rejected);
        Assert.Equal((2, 3, 3, 2), (catalog.Count, catalog.GameObjectRowCount, catalog.AreaTriggerRowCount, catalog.TimedActionListRowCount));
        Assert.True(catalog.HasGameObjectRows);

        Assert.Equal([1u, 2u], catalog.ForGameObject(5, 0).Select(r => r.ActionParam1));
        Assert.Equal([1u, 2u], catalog.ForGameObject(5, 8).Select(r => r.ActionParam1)); // spawn 8 has no rows of its own
        Assert.Equal(3u, Assert.Single(catalog.ForGameObject(5, 9)).ActionParam1);       // spawn 9 replaces the entry's rows
        Assert.Equal(3u, Assert.Single(catalog.ForGameObject(6, 9)).ActionParam1);       // ... whatever its entry
        Assert.Empty(catalog.ForGameObject(6, 0));

        Assert.Equal([0, 1], catalog.ForAreaTrigger(100).Select(r => (int)r.Id));
        Assert.Single(catalog.ForAreaTrigger(101));
        Assert.Empty(catalog.ForAreaTrigger(102));

        Assert.Equal([1, 2], catalog.TimedActionList(7).Select(r => (int)r.Id)); // id order, not the order given
        Assert.True(catalog.HasTimedActionList(7));
        Assert.False(catalog.HasTimedActionList(8));

        // The creature index is untouched by the other sources, and the DEBUG_ONLY row is in neither list.
        Assert.Equal(1u, Assert.Single(catalog.For(Wolf, 0)).ActionParam1);
        Assert.Equal(2u, Assert.Single(catalog.For(Wolf, 4)).ActionParam1);
        Assert.Empty(catalog.For(5, 0));
    }

    [Fact]
    public void Catalog_ACallToAListWhoseOnlyRowIsRefused_IsRefusedToo()
    {
        // The list row's action 41 is refused, so the list has no usable row and the 80 that calls it is refused with it.
        var catalog = new SmartScriptCatalog(
        [
            Make(50, 9, 0, 0, 41),
            CreatureRow(Wolf, 0, SmartEvent.Aggro, SmartAction.CallTimedActionList, SmartTarget.Self, a1: 50),
            CreatureRow(Wolf, 1, SmartEvent.Evade, SmartAction.SetEventPhase, a1: 1),
        ]);
        Assert.Equal(2, catalog.Rejected.Count);
        Assert.Contains(catalog.Rejected, r => r.SourceType == 9 && r.Reason.Contains("action 41", StringComparison.Ordinal));
        Assert.Contains(catalog.Rejected, r => r.SourceType == 0 && r.Id == 0 && r.Reason.Contains("list 50", StringComparison.Ordinal));
        Assert.Equal(1u, Assert.Single(catalog.For(Wolf, 0)).ActionParam1);
        Assert.False(catalog.HasTimedActionList(50));
    }

    [Fact]
    public void Catalog_WithReferences_RejectsMissingTemplatesSpawnsTriggersAndConditions()
    {
        var refs = new SmartScriptReferences(
            CreatureEntries: new HashSet<uint> { 299 }, CreatureGuids: new HashSet<uint> { 1 },
            GameObjectEntries: new HashSet<uint> { 5 }, GameObjectGuids: new HashSet<uint> { 9 },
            AreaTriggers: new HashSet<uint> { 100 }, Conditions: new HashSet<uint> { 10 });
        SmartScriptRow[] rows =
        [
            CreatureRow(299, 0, SmartEvent.Aggro, SmartAction.SetEventPhase, a1: 1, condition: 10),   // all references exist
            CreatureRow(300, 0, SmartEvent.Aggro, SmartAction.SetEventPhase, a1: 1),                  // no creature template 300
            CreatureRow(-1, 0, SmartEvent.Aggro, SmartAction.SetEventPhase, a1: 1),                   // spawn 1 exists
            CreatureRow(-2, 0, SmartEvent.Aggro, SmartAction.SetEventPhase, a1: 1),                   // no creature spawn 2
            CreatureRow(299, 1, SmartEvent.Aggro, SmartAction.SetEventPhase, a1: 1, condition: 11),   // no condition 11
            ObjectRow(5, 0, SmartEvent.Update, SmartAction.SetEventPhase, a1: 1),
            ObjectRow(6, 0, SmartEvent.Update, SmartAction.SetEventPhase, a1: 1),                 // no GO template 6
            ObjectRow(-9, 0, SmartEvent.Update, SmartAction.SetEventPhase, a1: 1),
            ObjectRow(-8, 0, SmartEvent.Update, SmartAction.SetEventPhase, a1: 1),                // no GO spawn 8
            TriggerRow(100, 0, SmartAction.Talk, SmartTarget.ActionInvoker, a1: 9001),
            TriggerRow(101, 0, SmartAction.Talk, SmartTarget.ActionInvoker, a1: 9001),            // no area trigger 101
            ListRow(50, 0, SmartAction.SetEventPhase, a1: 1, condition: 10),                       // a list id needs no template
            ListRow(51, 0, SmartAction.SetEventPhase, a1: 1, condition: 12),                       // no condition 12
        ];
        var catalog = new SmartScriptCatalog(rows, refs);

        Assert.Equal(
            [
                (300, 0, "creature template 300"), (-2, 0, "creature spawn 2"), (299, 1, "condition 11"),
                (6, 0, "gameobject template 6"), (-8, 0, "gameobject spawn 8"), (101, 0, "area trigger 101"), (51, 0, "condition 12"),
            ],
            catalog.Rejected.Select(r => (r.EntryOrGuid, (int)r.Id, FindFragment(r.Reason))));
        Assert.Equal((2, 2, 1, 1), (catalog.Count, catalog.GameObjectRowCount, catalog.AreaTriggerRowCount, catalog.TimedActionListRowCount));
        Assert.Equal(10u, catalog.For(299, 0).Single().ConditionId);

        // Without references the same rows are all accepted: the check needs the sets, and a catalog made without them (a unit test) skips it.
        Assert.Empty(new SmartScriptCatalog(rows).Rejected);

        static string FindFragment(string reason) => new[] { "creature template 300", "creature spawn 2", "condition 11", "gameobject template 6",
            "gameobject spawn 8", "area trigger 101", "condition 12" }.Single(f => reason.Contains(f, StringComparison.Ordinal));
    }

    [Fact]
    public void SupportTables_MatchTheEngineEnums()
    {
        SmartScriptSource[] sources = Enum.GetValues<SmartScriptSource>();
        Assert.Equal([0, 1, 2, 9], sources.Select(s => (int)s));

        HashSet<byte> events = [.. Enum.GetValues<SmartEvent>().Select(e => (byte)e)];
        HashSet<byte> actions = [.. Enum.GetValues<SmartAction>().Where(a => a != SmartAction.None).Select(a => (byte)a)];
        HashSet<byte> targets = [.. Enum.GetValues<SmartTarget>().Select(t => (byte)t)];

        // Everything the support tables allow is something the engine can run, and everything the engine knows is allowed for some source.
        Assert.True(events.SetEquals(sources.SelectMany(s => SmartScriptSupport.Events(s))), "events");
        Assert.True(actions.SetEquals(sources.SelectMany(s => SmartScriptSupport.Actions(s))), "actions");
        Assert.True(targets.SetEquals(sources.SelectMany(s => SmartScriptSupport.Targets(s))), "targets");

        // A timed action list has no events of its own and runs its owner's actions and targets; the sources keep their own sets.
        Assert.Empty(SmartScriptSupport.Events(SmartScriptSource.TimedActionList));
        Assert.Equal(SmartScriptSupport.Actions(SmartScriptSource.Creature), SmartScriptSupport.Actions(SmartScriptSource.TimedActionList));
        Assert.Equal(SmartScriptSupport.Targets(SmartScriptSource.Creature), SmartScriptSupport.Targets(SmartScriptSource.TimedActionList));
        Assert.Equal<byte>([1, 37, 59, 60, 61, 63, 64], SmartScriptSupport.Events(SmartScriptSource.GameObject).Order());
        Assert.Equal<byte>([46, 61], SmartScriptSupport.Events(SmartScriptSource.AreaTrigger).Order());
        Assert.Equal<byte>([1, 80, 87, 88], SmartScriptSupport.Actions(SmartScriptSource.AreaTrigger).Order());
        Assert.Equal<byte>([7, 19, 21], SmartScriptSupport.Targets(SmartScriptSource.AreaTrigger).Order());
        Assert.Equal<byte>([0, 1, 7, 8, 9, 11, 17, 18, 19, 21], SmartScriptSupport.Targets(SmartScriptSource.GameObject).Order());

        // Source types the engine does not run have no table at all.
        Assert.Empty(SmartScriptSupport.Events((SmartScriptSource)3));
        Assert.Empty(SmartScriptSupport.Actions((SmartScriptSource)3));
        Assert.Empty(SmartScriptSupport.Targets((SmartScriptSource)3));

        // The engine's enum values are the AzerothCore numbers (SmartScriptMgr.h), written out so a renumbering shows up here.
        Assert.Equal((37, 46, 63, 64), ((int)SmartEvent.AiInit, (int)SmartEvent.AreaTriggerOnTrigger, (int)SmartEvent.JustCreated, (int)SmartEvent.GossipHello));
        Assert.Equal((80, 87, 88), ((int)SmartAction.CallTimedActionList, (int)SmartAction.CallRandomTimedActionList, (int)SmartAction.CallRandomRangeTimedActionList));
    }
}
